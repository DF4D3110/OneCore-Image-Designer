<#
.SYNOPSIS
    把 OCID 重建的 COMPONENTS 台账打进一张可开机镜像（VHDX），一条命令到底。

.DESCRIPTION
    这是 P1「串联」的最后一公里：前面 ocid-assemble.ps1 负责"从包集/镜像算出台账"，
    本脚本负责"把台账变成能开机的盘"。流程：

        1. （可选）用 diskpart 新建动态 VHDX（GPT：ESP + MSR + NTFS）
        2. dism /Apply-Image 把 install.wim 应用进去
        3. bcdboot 写 UEFI 引导
        4. 备份镜像里的原版 COMPONENTS，换成 OCID 重建的那份
        5. 验证：reg load 后 cbs-scan / cbs-verify-store；dism /Get-Packages 换前后对比
        6. 分离磁盘，打印镜像路径与验证结论

    三个镜像（8061 x86 / 8061 ARM / 8419 ARM / 16299 amd64）都用它验证过；
    台帐必须已经由 ocid-assemble.ps1 产出（默认 <OutDir>\final\COMPONENTS.hiv）。

.PARAMETER Wim
    源 install.wim（新建盘模式必需）。

.PARAMETER Index
    wim 的映像索引，默认 1。

.PARAMETER BaseVhd
    已有 VHD/VHDX（差分盘链路也可以，但父盘要一起拷）。给了它就不 apply wim，
    只做第 4/5/6 步（换台账 + 验证）。用于"参考镜像已经在盘上"的场景。

.PARAMETER VhdPath
    输出 VHDX 路径（新建模式）或工作副本路径（BaseVhd 模式）。

.PARAMETER SizeGB
    虚拟盘大小，默认 30 GB（动态分配）。

.PARAMETER LedgerHive
    OCID 重建的 COMPONENTS hive。缺省时去 <OutDir>\final\COMPONENTS.hiv 找。

.PARAMETER OutDir
    ocid-assemble.ps1 的输出目录（用于找台账、写验证日志）。

.PARAMETER Verify
    默认开：reg load 后跑 cbs-scan / cbs-verify-store，并做 dism /Get-Packages 换前/换后对比。

.PARAMETER KeepMounted
    结束时不离线（调试用）。

.EXAMPLE
    .\Build-OcidImage.ps1 -Wim 'G:\sources\install.wim' -VhdPath 'E:\ocid_verify\p5\ocid-8061-x86.vhdx' `
        -LedgerHive 'E:\ocid_verify\regX86f\final\COMPONENTS.hiv'

.EXAMPLE
    # ARM unstaged：从现成差分盘拷一份再换台账
    .\Build-OcidImage.ps1 -BaseVhd 'E:\ocid_verify\p5\arm-unstaged.vhd' -VhdPath 'E:\ocid_verify\p5\arm-check.vhd' `
        -LedgerHive 'E:\ocid_verify\oneARM\final\COMPONENTS.hiv'
#>
[CmdletBinding()]
param(
    [string] $Wim,
    [int] $Index = 1,
    [string] $BaseVhd,
    [Parameter(Mandatory = $true)][string] $VhdPath,
    [int] $SizeGB = 30,
    [string] $LedgerHive,
    [string] $OutDir = '',
    [string] $Exe = '',
    [switch] $KeepMounted,
    [switch] $NoVerify
)

$ErrorActionPreference = 'Stop'
$script:fail = @()

function Step([string] $text) { Write-Host "[image] $text" }
function Warn([string] $text) { Write-Host "[image] WARN $text" -ForegroundColor Yellow; $script:fail += $text }
function Die([string] $text) { Write-Host "[image] FAIL $text" -ForegroundColor Red; throw $text }

# ---------- 工具路径 ----------
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Exe)) { $Exe = Join-Path $root 'Output\x86\ocid_manifest.exe' }
if (-not (Test-Path $Exe)) { Die "找不到 ocid_manifest.exe: $Exe（先跑 build-servicing.ps1）" }
if ([string]::IsNullOrWhiteSpace($LedgerHive)) {
    if ([string]::IsNullOrWhiteSpace($OutDir)) { Die '需要 -LedgerHive 或 -OutDir' }
    $LedgerHive = Join-Path $OutDir 'final\COMPONENTS.hiv'
}
if (-not (Test-Path $LedgerHive)) { Die "找不到台账: $LedgerHive" }
if (-not [string]::IsNullOrWhiteSpace($Wim) -and -not (Test-Path $Wim)) { Die "找不到 wim: $Wim" }
if (-not [string]::IsNullOrWhiteSpace($BaseVhd) -and -not (Test-Path $BaseVhd)) { Die "找不到 BaseVhd: $BaseVhd" }

$diskpart = Join-Path $env:SystemRoot 'System32\diskpart.exe'
if (-not (Test-Path $diskpart)) { Die '找不到 diskpart.exe' }

# ---------- 盘符选择 ----------
function Get-FreeLetter {
    $used = @(Get-Volume | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
    foreach ($c in ([char[]](('R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z')))) {
        if ($used -notcontains [string]$c) { return [string]$c }
    }
    Die '没有空闲盘符'
}
$winLetter = Get-FreeLetter
$espLetter = Get-FreeLetter

# ---------- 1. 建盘 / 拷贝基底 ----------
$work = $VhdPath
if (Test-Path $work) {
    Step "删除已存在的目标盘: $work"
    Remove-Item $work -Force
}
$newDisk = $false
if (-not [string]::IsNullOrWhiteSpace($BaseVhd)) {
    Step "复制基底盘 $BaseVhd -> $work"
    Copy-Item $BaseVhd $work -Force
}
else {
    if ([string]::IsNullOrWhiteSpace($Wim)) { Die '新建盘需要 -Wim' }
    $newDisk = $true
    $dp = Join-Path $env:TEMP ('ocid-disk-' + [guid]::NewGuid().ToString('N') + '.txt')
    @"
create vdisk file="$work" maximum=$($SizeGB * 1024) type=expandable
select vdisk file="$work"
attach vdisk
convert gpt
create partition efi size=260
format fs=fat32 quick label=System
assign letter=$espLetter
create partition msr size=16
create partition primary
format fs=ntfs quick label=Windows
assign letter=$winLetter
exit
"@ | Set-Content -Path $dp -Encoding ASCII
    Step "diskpart: 建 GPT 盘（ESP=$espLetter Windows=$winLetter）"
    $out = & $diskpart /s $dp 2>&1
    Remove-Item $dp -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { $out | ForEach-Object { Write-Host "  $_" }; Die "diskpart 失败（exit $LASTEXITCODE）" }
}

# BaseVhd 模式：attach 现有盘，盘符靠"挂载前后卷集合的差"反查
if (-not $newDisk) {
    $before = @(Get-Volume | ForEach-Object { $_.UniqueId })
    $dp = Join-Path $env:TEMP ('ocid-attach-' + [guid]::NewGuid().ToString('N') + '.txt')
    @"
select vdisk file="$work"
attach vdisk
exit
"@ | Set-Content -Path $dp -Encoding ASCII
    & $diskpart /s $dp 2>&1 | Out-Null
    Remove-Item $dp -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $newVols = @(Get-Volume | Where-Object { $before -notcontains $_.UniqueId -and $_.DriveLetter })
    $vol = $newVols | Where-Object { $_.FileSystemLabel -eq 'Windows' } | Select-Object -First 1
    if (-not $vol) { $vol = $newVols | Where-Object { Test-Path (Join-Path "$($_.DriveLetter):\" 'Windows') } | Select-Object -First 1 }
    if (-not $vol) { Die 'BaseVhd 挂载后找不到新出现的 Windows 卷' }
    $winLetter = [string]$vol.DriveLetter
    $espVol = $newVols | Where-Object { $_.DriveLetter -ne $winLetter } | Select-Object -First 1
    if ($espVol) { $espLetter = [string]$espVol.DriveLetter }
    Step "BaseVhd 已挂载：Windows=$winLetter ESP=$espLetter"
}

$winDir = "${winLetter}:\Windows"
if (-not (Test-Path $winDir)) { Die "目标卷里没有 Windows 目录: $winDir" }

# ---------- 2. 应用映像 ----------
if ($newDisk) {
    Step "dism /Apply-Image $Wim (index $Index) -> ${winLetter}:\"
    $log = Join-Path $env:TEMP 'ocid-apply.log'
    & (Join-Path $env:SystemRoot 'System32\dism.exe') /English /Apply-Image "/ImageFile:$Wim" "/Index:$Index" "/ApplyDir:${winLetter}:\" /LogPath:$log | Out-Null
    if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 20 | ForEach-Object { Write-Host "  $_" }; Die "dism /Apply-Image 失败（exit $LASTEXITCODE）" }

    # ---------- 3. bcdboot ----------
    Step "bcdboot（UEFI）"
    & (Join-Path $env:SystemRoot 'System32\bcdboot.exe') "$winDir" /s "${espLetter}:\" /f UEFI | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn "bcdboot 返回 $LASTEXITCODE" }
}

# ---------- 4. 换台账 ----------
$cfg = Join-Path $winDir 'System32\config'
$components = Join-Path $cfg 'COMPONENTS'
if (-not (Test-Path $components)) { Die "镜像里没有 COMPONENTS: $components" }
$dism = Join-Path $env:SystemRoot 'System32\dism.exe'

function Get-PackageStates([string] $winRoot) {
    $txt = & $dism /English /Image:"$winRoot" /Get-Packages 2>&1
    $list = @(); $cur = $null
    foreach ($l in $txt) {
        if ($l -match '^Package Identity : (.+)$') { $cur = $Matches[1].Trim() }
        elseif ($l -match '^State : (.+)$' -and $cur) { $list += "$cur|$($Matches[1].Trim())"; $cur = $null }
    }
    return $list
}

$beforePkgs = @()
if (-not $NoVerify) {
    Step 'dism /Get-Packages（替换前）'
    $beforePkgs = @(Get-PackageStates "${winLetter}:\")
    Step ("替换前包记录 {0} 条" -f $beforePkgs.Count)
}

$backup = "$components.orig-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
Step "备份原版台账 -> $(Split-Path -Leaf $backup)"
Move-Item $components $backup
Copy-Item $LedgerHive $components
Step ("台账已替换：{0:N0} 字节（备份 {1:N0} 字节）" -f (Get-Item $components).Length, (Get-Item $backup).Length)

# ---------- 5. 验证 ----------
if (-not $NoVerify) {
    $mountName = 'OCIDIMGCHK'
    & reg load "HKLM\$mountName" $components 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn "reg load 失败（exit $LASTEXITCODE），跳过台账验证" }
    else {
        try {
            Step 'cbs-scan（台账自洽）'
            $scan = & $Exe cbs-scan "HKLM\$mountName" 2>&1
            $scan | ForEach-Object { Write-Host "  $_" }
            if (-not ($scan -match 'result:\s*OK')) { Warn 'cbs-scan 未返回 OK' }

            Step 'cbs-verify-store（键名/值名可重算）'
            $vstore = & $Exe cbs-verify-store "HKLM\$mountName" 2>&1
            $vstore | ForEach-Object { Write-Host "  $_" }
            if (-not ($vstore -match 'result=OK')) { Warn 'cbs-verify-store 未返回 OK' }
        }
        finally {
            & reg unload "HKLM\$mountName" 2>&1 | Out-Null
        }
    }

    Step 'dism /Get-Packages（替换后对比）'
    $afterPkgs = @(Get-PackageStates "${winLetter}:\")
    if ($beforePkgs.Count -eq 0 -and $afterPkgs.Count -eq 0) {
        Warn 'dism /Get-Packages 没有返回任何包记录（DISM 被拒绝：常见原因是同签名的 VHD 还挂着，' +
             '报 "Offline image specified is the running system"；先把源盘 detach 再跑）；跳过包记录对比'
    }
    else {
        $diff = @(Compare-Object -ReferenceObject $beforePkgs -DifferenceObject $afterPkgs)
        if ($diff.Count -eq 0) {
            Step ("包记录完全一致（{0} 条）" -f $afterPkgs.Count)
        }
        else {
            Warn ("包记录有 {0} 处差异（台账与镜像包状态不一致，可能是覆盖表兜住的安装历史）" -f $diff.Count)
            $diff | Select-Object -First 20 | ForEach-Object { Write-Host ("    {0} {1}" -f $_.SideIndicator, $_.InputObject) }
        }
    }
}

# ---------- 6. 分离 ----------
if (-not $KeepMounted) {
    $dp = Join-Path $env:TEMP ('ocid-detach-' + [guid]::NewGuid().ToString('N') + '.txt')
    @"
select vdisk file="$work"
detach vdisk
exit
"@ | Set-Content -Path $dp -Encoding ASCII
    & $diskpart /s $dp 2>&1 | Out-Null
    Remove-Item $dp -Force -ErrorAction SilentlyContinue
    Step '已分离磁盘'
}
else {
    Step "保持挂载：$winLetter`:\"
}

if ($script:fail.Count -gt 0) {
    Write-Host ("[image] 有 {0} 项警告：" -f $script:fail.Count) -ForegroundColor Yellow
    $script:fail | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host ''
Write-Host "[image] 镜像就绪: $work"
Write-Host ("[image] 台账: {0}" -f $LedgerHive)
