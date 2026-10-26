<#
    merge-overlays.ps1 - 把 UpdateDLL 在 staging 目录留下的 space 覆盖盘并回镜像。

    背景：ocid_imagegen apply 用 UpdateDLL 做落包时，池内每个 space 的数据本体写到
    <Dir>\{GUID}.vhdx（覆盖盘），镜像里的 space 本身仍是空的。本脚本把这些覆盖盘的
    文件系统内容按 GPT 分区类型搬回镜像内对应 space，然后删掉覆盖盘。

    用法：
        powershell -NoProfile -ExecutionPolicy Bypass -File merge-overlays.ps1 `
            -Image E:\out\Default.vhd `
            -Layout D:\...\DeviceLayout.xml `
            -Dir E:\out\staging `
            -Tool D:\...\Output\Imaging\ocid_imagegen.exe

    退出码：0 = 成功（或没有覆盖盘）；1 = 有 space 合并失败。
#>
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [Parameter(Mandatory = $true)][string]$Layout,
    [Parameter(Mandatory = $true)][string]$Dir,
    [Parameter(Mandatory = $true)][string]$Tool,
    [string]$OemDevicePlatform = '',
    [switch]$KeepOverlays,
    [switch]$Verbose2
)

$ErrorActionPreference = 'Continue'

function Say([string]$text) { Write-Output ("[merge " + (Get-Date -Format 'HH:mm:ss') + "] " + $text) }

# GPT 分区类型 -> 布局 space 名（10X 布局固定映射）
$spaceByType = @{
    '{57434f53-e3e3-4631-a5c5-26d2243873aa}' = 'EFIESPDisk'
    '{57434f53-8f45-405e-8a23-186d8a4330d3}' = 'MainOSDisk'
    '{57434f53-4df9-45b9-8e9e-2370f006457c}' = 'BSPDisk'
    '{57434f53-7fe0-4196-9b42-427b51643484}' = 'PreInstalledDisk'
    '{57434f53-23f2-44d5-a830-67bbdaa609f9}' = 'OSDataDisk'
    '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' = 'DataDisk'
    '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' = 'VIRT_EFIESPDisk'
}

# 调 ocid_imagegen 的统一入口：带超时，超时就杀掉子进程，避免整个流水线被内核挂死的调用拖住。
function Invoke-Tool([string[]]$arguments, [int]$timeoutSeconds = 900) {
    $tmpRoot = Join-Path (Split-Path -Parent $Dir) 'tmp'
    if (-not (Test-Path -LiteralPath $tmpRoot)) { New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null }
    $stamp = [guid]::NewGuid().ToString('N')
    $outFile = Join-Path $tmpRoot ("tool_" + $stamp + ".out")
    $errFile = Join-Path $tmpRoot ("tool_" + $stamp + ".err")
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Tool
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # Windows PowerShell 5.1 的 ProcessStartInfo 没有 ArgumentList，只能自己拼引号。
    $psi.Arguments = (($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stdout = $proc.StandardOutput.ReadToEndAsync()
    $stderr = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit($timeoutSeconds * 1000)
    if (-not $exited) {
        Say ("工具调用超时 " + $timeoutSeconds + "s，杀掉进程：" + ($arguments -join ' '))
        try { $proc.Kill() } catch { }
        Start-Sleep -Milliseconds 500
        return @{ Code = 124; Output = @(); TimedOut = $true }
    }
    $out = @()
    try { $out = @($stdout.Result -split "`r?`n" | Where-Object { $_ -ne '' }) } catch { }
    $errText = ''
    try { $errText = $stderr.Result } catch { }
    if ($Verbose2) {
        $out | ForEach-Object { Write-Host ("  " + $_) }
        if ($errText) { Write-Host ("  ERR " + $errText) }
    }
    return @{ Code = $proc.ExitCode; Output = $out; TimedOut = $false }
}

# 基座盘当前是否已挂载；挂载时返回 \\.\PhysicalDriveN（服务只认设备路径时要用）。
function Get-BaseDevice {
    $di = Get-DiskImage -ImagePath $Image -ErrorAction SilentlyContinue
    if (-not $di) { return $null }
    $d = $di | Get-Disk -ErrorAction SilentlyContinue
    if ($d) { return ('\\.\PhysicalDrive' + $d.Number) }
    return $null
}

# 服务对"已挂载的 VHD 文件"调 OpenVirtualHardDisk 会失败（0x800700E0），
# 所以清理 space 挂载一律走 --device；拿不到设备路径才退回 --vhd。
function Detach-PoolSpaces {
    $device = Get-BaseDevice
    if ($device) {
        $r = Invoke-Tool @('pool', 'detach', '--device', $device, '--layout', $Layout, '--dir', $Dir) 300
        if ($r.Code -eq 0) { return $r }
    }
    return Invoke-Tool @('pool', 'detach', '--vhd', $Image, '--layout', $Layout, '--dir', $Dir) 300
}

function Free-DriveLetter {
    $used = @()
    foreach ($pt in (Get-Partition -ErrorAction SilentlyContinue)) {
        foreach ($a in @($pt.AccessPaths)) { if ($a -match '^([A-Z]):\\$') { $used += [char]$Matches[1] } }
    }
    foreach ($ch in [char[]]'Q,R,U,V,W,X,Z,J,K,L,N,P,S,T,Y') {
        if ($used -notcontains $ch -and -not (Test-Path ($ch.ToString() + ':\'))) { return $ch.ToString() }
    }
    throw 'no free drive letter'
}
function Wait-Volume([string]$letter) {
    for ($i = 0; $i -lt 20; $i++) {
        if (Test-Path ($letter + ':\')) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Get-VolumeLetter([int]$disk, [int]$partition) {
    $paths = @()
    try { $paths = @((Get-Partition -DiskNumber $disk -PartitionNumber $partition -ErrorAction SilentlyContinue).AccessPaths) } catch { }
    foreach ($pth in $paths) {
        if ($pth -match '^([A-Z]):\\$') { return $Matches[1] }
    }
    return $null
}

function Ensure-Letter([int]$disk, [int]$partition, [string]$preferred) {
    $efi = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    $basic = '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
    for ($i = 0; $i -lt 15; $i++) {
        $existing = Get-VolumeLetter -disk $disk -partition $partition
        if ($existing -and (Wait-Volume $existing)) { return $existing }
        if ($preferred -and (Wait-Volume $preferred)) { return $preferred }
        $part = Get-Partition -DiskNumber $disk -PartitionNumber $partition -ErrorAction SilentlyContinue
        $isEfi = ($part -and ([string]$part.GptType).ToLower() -eq $efi)
        if ($isEfi -and -not $existing) {
            Set-Partition -DiskNumber $disk -PartitionNumber $partition -GptType $basic -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
        }
        if (-not $existing) {
            Add-PartitionAccessPath -DiskNumber $disk -PartitionNumber $partition -AccessPath ($preferred + ':\') -ErrorAction SilentlyContinue
            if (-not (Get-VolumeLetter -disk $disk -partition $partition)) {
                # 用户要求：临时文件都留在工作目录，不用 %TEMP%
                $tmpDir = Join-Path (Split-Path -Parent $Dir) 'tmp'
                if (-not (Test-Path -LiteralPath $tmpDir)) { New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null }
                $script = Join-Path $tmpDir ("dp_" + $disk + "_" + $partition + ".txt")
                @("select disk " + $disk, "select partition " + $partition, "assign letter=" + $preferred) | Set-Content -Path $script -Encoding ASCII
                & diskpart.exe /s $script | Out-Null
            }
        }
        $got = Get-VolumeLetter -disk $disk -partition $partition
        if ($got -and (Wait-Volume $got)) { return $got }
        Start-Sleep -Milliseconds 400
    }
    return $null
}
function Remove-Letter([int]$disk, [int]$partition, [string]$letter) {
    if ($letter) {
        Remove-PartitionAccessPath -DiskNumber $disk -PartitionNumber $partition -AccessPath ($letter + ':\') -ErrorAction SilentlyContinue
    }
}

# 1) 收集覆盖盘，并读 GPT 分区类型确定它属于哪个 space
$overlays = @(Get-ChildItem -LiteralPath $Dir -Filter *.vhdx -ErrorAction SilentlyContinue)
if ($overlays.Count -eq 0) {
    Say '没有覆盖盘，跳过'
    exit 0
}

# 布局里每个 space 的尺寸（用于消歧：EFI 类型同时对应 EFIESPDisk 与 VIRT_EFIESPDisk）
[System.Xml.XmlDocument] $layoutDoc = New-Object System.Xml.XmlDocument
$layoutDoc.Load($Layout)
$spaceSize = @{}
foreach ($poolNode in $layoutDoc.SelectNodes("//*[local-name()='StoragePool']")) {
    foreach ($storeNode in $poolNode.SelectNodes("./*[local-name()='Stores']/*[local-name()='Store']")) {
        $nm = $storeNode.SelectSingleNode("./*[local-name()='StoreType']")
        $sz = $storeNode.SelectSingleNode("./*[local-name()='SizeInSectors']")
        $sb = $storeNode.SelectSingleNode("./*[local-name()='SizeInBytes']")
        if ($nm -and ($sz -or $sb)) {
            if ($sb) { $spaceSize[$nm.InnerText] = [uint64]$sb.InnerText }
            else {
                $sec = $storeNode.SelectSingleNode("./*[local-name()='SectorSize']")
                $ss = 512
                if ($sec) { $ss = [uint64]$sec.InnerText }
                $spaceSize[$nm.InnerText] = [uint64]$sz.InnerText * $ss
            }
        }
    }
}

$plan = @()
foreach ($ov in $overlays) {
    Mount-DiskImage -ImagePath $ov.FullName -ErrorAction SilentlyContinue | Out-Null
    $disk = Get-DiskImage -ImagePath $ov.FullName -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue
    if (-not $disk) { Say ("跳过（挂不上）：" + $ov.Name); continue }
    $part = Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Select-Object -First 1
    $space = $null
    if ($part) { $space = $spaceByType[$part.GptType] }
    if ($part -and $disk) {
        $diskSize = [uint64]$disk.Size
        $mappedSize = $null
        if ($space -and $spaceSize.ContainsKey($space)) { $mappedSize = [uint64]$spaceSize[$space] }
        if ($null -eq $mappedSize -or $mappedSize -ne $diskSize) {
            foreach ($k in $spaceSize.Keys) {
                if ([uint64]$spaceSize[$k] -eq $diskSize) { $space = $k; break }
            }
        }
    }
    if (-not $space) { Say ("跳过（未知分区类型）：" + $ov.Name); Dismount-DiskImage -ImagePath $ov.FullName | Out-Null; continue }
    $plan += [pscustomobject]@{ File = $ov.FullName; Space = $space }
    Dismount-DiskImage -ImagePath $ov.FullName | Out-Null
}
if ($plan.Count -eq 0) { Say '没有可合并的覆盖盘'; exit 0 }

# 同一 space 若有多份覆盖盘（历史残留），只保留文件最大的那份
$dedup = @{}
foreach ($item in $plan) {
    if (-not $dedup.ContainsKey($item.Space)) { $dedup[$item.Space] = $item; continue }
    $cur = $dedup[$item.Space]
    if ((Get-Item -LiteralPath $item.File).Length -gt (Get-Item -LiteralPath $cur.File).Length) { $dedup[$item.Space] = $item }
}
if ($dedup.Count -ne $plan.Count) {
    Say ("同一 space 存在重复覆盖盘，去重后 " + $dedup.Count + " 个")
    $plan = @($dedup.Values)
}
$skipped = @($plan | Where-Object { $_.Space -eq 'VIRT_EFIESPDisk' })
if ($skipped.Count -gt 0) {
    Say 'VIRT_EFIESPDisk 跳过合并（移走覆盖盘后服务无法 attach），把覆盖盘备份到 overlays-keep'
    $keepEarly = Join-Path (Split-Path -Parent $Dir) 'overlays-keep'
    New-Item -ItemType Directory -Path $keepEarly -Force | Out-Null
    foreach ($s in $skipped) {
        $dst = Join-Path $keepEarly (Split-Path -Leaf $s.File)
        Copy-Item -LiteralPath $s.File -Destination $dst -Force -ErrorAction SilentlyContinue
        if (Test-Path $dst) { $s | Add-Member -NotePropertyName Keep -NotePropertyValue $dst -Force }
    }
    $plan = @($plan | Where-Object { $_.Space -ne 'VIRT_EFIESPDisk' })
}
Say ("覆盖盘 " + $plan.Count + " 个：" + (($plan | ForEach-Object { $_.Space }) -join ','))
if ($plan.Count -eq 0) { Say '没有需要合并的覆盖盘'; exit 0 }

# 2) 把覆盖盘挪出 staging，让池打开时回落到镜像内的真实 space
$keep = Join-Path (Split-Path -Parent $Dir) 'overlays-keep'
New-Item -ItemType Directory -Path $keep -Force | Out-Null
foreach ($p in $plan) {
    $target = Join-Path $keep (Split-Path -Leaf $p.File)
    if (Test-Path $target) { Remove-Item $target -Force -ErrorAction SilentlyContinue }
    Move-Item -LiteralPath $p.File -Destination $target -Force
    $p | Add-Member -NotePropertyName Keep -NotePropertyValue $target -Force
}

# 3) 先用 CLI 永久挂载基座盘拿设备路径：服务对 VHD 的 OpenVirtualHardDisk 不支持（VHDX 才行），
#    走设备路径时服务用 CreateFile 打开，VHD/VHDX 都可用。
# 清掉上一轮遗留的 space 挂载（必须在卸基座盘之前做，否则服务拿不到设备路径），
# 否则 AttachStorageSpace 会报 0x80070006。
$preDetach = Detach-PoolSpaces
if ($preDetach.Code -ne 0) { Say ("清理残留 space 挂载返回 " + $preDetach.Code + "（继续）") }
Invoke-Tool @('vhd', 'detach', $Image, '--force', '--kill') 180 | Out-Null
$attach = Invoke-Tool @('vhd', 'attach', $Image) 180
$device = $null
foreach ($line in @($attach.Output)) {
    if ([string]$line -match 'PhysicalDrive([0-9]+)') { $device = '\\.\PhysicalDrive' + $Matches[1]; break }
}
if ($attach.Code -ne 0 -or -not $device) {
    Say '基座盘挂载失败，恢复覆盖盘后退出'
    foreach ($p in $plan) { Move-Item -LiteralPath $p.Keep -Destination $p.File -Force -ErrorAction SilentlyContinue }
    exit 1
}
Say ('基座盘设备：' + $device)
$mount = Invoke-Tool @('pool', 'mount', '--device', $device, '--layout', $Layout, '--dir', $Dir)
if ($mount.Code -ne 0) {
    $mount = Invoke-Tool @('pool', 'mount', '--vhd', $Image, '--layout', $Layout, '--dir', $Dir)
}
if ($mount.Code -ne 0) {
    Say '池挂载失败，恢复覆盖盘后退出'
    foreach ($p in $plan) { Move-Item -LiteralPath $p.Keep -Destination $p.File -Force -ErrorAction SilentlyContinue }
    exit 1
}

# 4) 给每个 pool space 分配盘符（按 GPT 类型对应）
$destLetter = @{}
foreach ($p in $plan) { $destLetter[$p.Space] = $null }

$baseDisk = $null
try { $baseDisk = (Get-DiskImage -ImagePath $Image -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue).Number } catch { }

$wanted = @($plan | ForEach-Object { $_.Space } | Select-Object -Unique).Count
$deadline = (Get-Date).AddSeconds(90)
$found = 0
$firstDiag = $true
do {
    $found = 0
    if ($firstDiag) {
        $firstDiag = $false
        $diag = @(Get-Disk | Where-Object { $_.BusType -in 'File Backed Virtual','Spaces' } | ForEach-Object {
            $q = Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue | Select-Object -First 1
            "{0}:{1}:{2}" -f $_.Number, $_.BusType, $(if ($q) { $q.GptType } else { 'nopart' })
        })
        Say ("诊断 " + $diag.Count + " 块盘：" + ($diag -join ' | '))
        Say ("baseDisk=" + $baseDisk)
    }
    foreach ($d in (Get-Disk | Where-Object { $_.BusType -eq 'File Backed Virtual' -or $_.BusType -eq 'Spaces' })) {
        if ($null -ne $baseDisk -and $d.Number -eq $baseDisk) { continue }
        $part = Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $part) { continue }
        $space = $spaceByType[$part.GptType]
        if (-not $space) { continue }
        if ($destLetter.ContainsKey($space) -and -not $destLetter[$space]) {
            $letter = Ensure-Letter -disk $d.Number -partition $part.PartitionNumber -preferred (Free-DriveLetter)
            if ($letter) { $destLetter[$space] = @{ Letter = $letter; Disk = $d.Number; Part = $part.PartitionNumber }; $found++ }
        }
    }
    if ($found -lt $wanted) { Start-Sleep -Milliseconds 500 }
} while ($found -lt $wanted -and (Get-Date) -lt $deadline)
if ($found -lt $wanted) {
    foreach ($k in @($destLetter.Keys)) { if (-not $destLetter[$k]) { Say ("没有找到目标 space 卷：" + $k) } }
}

$failed = 0
foreach ($p in $plan) {
    $dst = $destLetter[$p.Space]
    if (-not $dst) { Say ("没有找到目标 space 卷：" + $p.Space); $failed++; continue }
    Mount-DiskImage -ImagePath $p.Keep -ErrorAction SilentlyContinue | Out-Null
    $sd = Get-DiskImage -ImagePath $p.Keep -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue
    if (-not $sd) { Say ("挂不上覆盖盘：" + $p.Space); $failed++; continue }
    $sp = Get-Partition -DiskNumber $sd.Number -ErrorAction SilentlyContinue | Select-Object -First 1
    $src = $null
    for ($k = 0; $k -lt 24 -and -not $src; $k++) {
        $src = Get-VolumeLetter -disk $sd.Number -partition $sp.PartitionNumber
        if (-not $src) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $src) { $src = Ensure-Letter -disk $sd.Number -partition $sp.PartitionNumber -preferred (Free-DriveLetter) }
    if (-not $src) { Say ("覆盖盘没有盘符：" + $p.Space); Dismount-DiskImage -ImagePath $p.Keep | Out-Null; $failed++; continue }
    robocopy ($src + ':\') ($dst.Letter + ':\') /E /B /COPY:DAT /DCOPY:T /SL /XJ /R:0 /W:0 /MT:8 /NFL /NDL /NP | Out-Null
    $rc = $LASTEXITCODE
    $missing = @(Get-ChildItem ($src + ':\') -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName.Substring(3) } |
        Where-Object { -not (Test-Path -LiteralPath ([System.IO.Path]::Combine($dst.Letter + ':\', $_))) })
    Say ("{0}: rc={1} 缺失={2}" -f $p.Space, $rc, $missing.Count)
    if ($missing.Count -gt 0) {
        $failed++
        $missing | Select-Object -First 5 | ForEach-Object { Say ("  缺：" + $_) }
    }
    Remove-Letter -disk $sd.Number -partition $sp.PartitionNumber -letter $src
    Dismount-DiskImage -ImagePath $p.Keep | Out-Null
}

# 4.5) space 目录骨架：ImageApp 会留下这些空目录（Data 是它的 staging 根，OSData 有 SystemData 日志目录），
#      并往 MainOS 的 Windows\ImageUpdate 写 DeviceLayout.xml / OEMDevicePlatform.xml。照参照镜像补齐。
$seedBySpace = @{
    'DataDisk' = @(
        'sandbox',
        'USERS', 'USERS\System', 'USERS\System\AppData', 'USERS\System\AppData\Local',
        'USERS\System\AppData\Local\UpdateStagingRoot',
        'USERS\System\AppData\Local\UpdateStagingRoot\SharedData',
        'USERS\System\AppData\Local\UpdateStagingRoot\SharedData\DuShared'
    )
    'OSDataDisk' = @('SystemData', 'SystemData\ETW', 'SystemData\NonETWLogs')
    'MainOSDisk' = @(
        'Data', 'Data\sandbox',
        'Data\USERS', 'Data\USERS\System', 'Data\USERS\System\AppData', 'Data\USERS\System\AppData\Local',
        'Data\USERS\System\AppData\Local\UpdateStagingRoot',
        'Data\USERS\System\AppData\Local\UpdateStagingRoot\SharedData',
        'Data\USERS\System\AppData\Local\UpdateStagingRoot\SharedData\DuShared'
    )
}
foreach ($space in $seedBySpace.Keys) {
    $dst = $destLetter[$space]
    if (-not $dst) { continue }
    $root = $dst.Letter + ':\'
    $made = 0
    foreach ($rel in $seedBySpace[$space]) {
        $full = $root + $rel
        if (-not (Test-Path -LiteralPath $full)) {
            New-Item -ItemType Directory -Path $full -Force | Out-Null
            if (Test-Path -LiteralPath $full) { $made++ }
        }
    }
    Say ("{0}: 目录骨架 +{1}" -f $space, $made)
}

# 4.6) MainOS\Windows\ImageUpdate：DeviceLayout.xml / OEMDevicePlatform.xml
$mainDst = $destLetter['MainOSDisk']
if ($mainDst) {
    $imageUpdate = $mainDst.Letter + ':\Windows\ImageUpdate'
    New-Item -ItemType Directory -Path $imageUpdate -Force | Out-Null
    $platformXml = $OemDevicePlatform
    if (-not $platformXml) { $platformXml = Join-Path (Split-Path -Parent $Layout) 'OEMDevicePlatform.xml' }
    $copied = @()
    foreach ($src in @($Layout, $platformXml)) {
        if ($src -and (Test-Path -LiteralPath $src)) {
            $target = Join-Path $imageUpdate (Split-Path -Leaf $src)
            Copy-Item -LiteralPath $src -Destination $target -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $target) { $copied += (Split-Path -Leaf $src) }
        }
    }
    Say ("MainOSDisk: Windows\ImageUpdate 写入 " + ($copied -join ','))
}

# 5) 收尾：卸 space（走设备路径）、卸基座盘、删覆盖盘
foreach ($k in $destLetter.Keys) {
    $v = $destLetter[$k]
    if ($v) { Remove-Letter -disk $v.Disk -partition $v.Part -letter $v.Letter }
}
$finalDetach = Detach-PoolSpaces
if ($finalDetach.Code -ne 0) {
    Say ("卸载 space 返回 " + $finalDetach.Code + "，重试一次")
    Start-Sleep -Seconds 3
    $finalDetach = Detach-PoolSpaces
}
Invoke-Tool @('vhd', 'detach', $Image, '--force', '--kill') 180 | Out-Null
Invoke-Tool @('vhd', 'detach', $Image, '--force', '--kill') 180 | Out-Null

if (-not $KeepOverlays -and $failed -eq 0) {
    foreach ($p in $plan) { Remove-Item -LiteralPath $p.Keep -Force -ErrorAction SilentlyContinue }
}
foreach ($s in $skipped) { Say ('保留覆盖盘（未合并）：' + $s.Space + ' -> ' + $s.File) }

if ($failed -gt 0) { Say ("完成，但有 " + $failed + " 个 space 未完全合并"); exit 1 }
Say '完成：所有 space 已合并回镜像'
exit 0
