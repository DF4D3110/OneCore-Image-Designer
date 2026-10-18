#Requires -Version 5.1
<#
.SYNOPSIS
    Assemble a COMPONENTS hive from an image's manifests + servicing packages.

.DESCRIPTION
    Runs the three OCID planners (components / deployments+catalogs / VersionedIndex),
    merges their K/V lines with a generated root-value plan, then writes the hive with
    `cbs-write`.  Everything is derived from files on the image; no servicing API is
    called.

    Optional comparison: pass -DiffAgainst <truth dump from cbs-dump> to get a
    `cbs-diff` report of what the image-derived plan still misses.

.EXAMPLE
    .\assemble-cbs-store.ps1 -ImageRoot Q:\ -Arch x86 `
        -StackVersion "6.2.8061.0 (fbl_core1_soc.110803-1830)" `
        -Markers E:\ocid_verify\deployments_full.csv `
        -Installed E:\ocid_verify\packages_state.txt `
        -DiffAgainst E:\ocid_verify\truth_all.txt
#>
[CmdletBinding()]
param(
    [string] $ImageRoot = 'Q:\',
    [string] $Arch,
    [Parameter(Mandatory = $true)][string] $StackVersion,
    [string] $OutDir = 'E:\ocid_verify\assemble',
    [string] $HiveName = 'COMPONENTS.hiv',
    [string] $Manifests,
    [string] $Packages,
    [string] $WinSxs,
    [string] $Markers,
    [string] $Installed,
    [string] $Claims,
    [string] $Flags,
    [string] $Sessions,
    [string] $Buckets,
    [string] $Cache,
    [string] $Ct,
    [string] $VersionFlags,
    [string] $Mcp,
 [string] $SkeletonFamilies,
    [string] $Override,
    [string] $Pending = '',
    [string] $Time = '',
    [switch] $RootMinimal,
 [switch] $Ccpinterface,
 [switch] $InstallersShortcut,
    [string[]] $StackEntries = @(),
    [string] $RootInfo,
    [switch] $Transactional,
    [switch] $SkipFileVerify,
    [switch] $SkipStoreVerify,
    [string] $DiffAgainst,
    [string] $DcmDictionary,
    [string] $FileFlags,
    [string] $Exe
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $ImageRoot).Path.TrimEnd('\')
if (-not $Manifests) { $Manifests = Join-Path $root 'Windows\WinSxS\Manifests' }
if (-not $Packages)  { $Packages  = Join-Path $root 'Windows\servicing\Packages' }
if (-not $WinSxs)    { $WinSxs    = Join-Path $root 'Windows\WinSxS' }

# 16299+ 的组件清单是 DCM 压缩容器，字典在 servicing stack 的 wcp.dll 资源里。
# 不给 $DcmDictionary 时按镜像里的 servicing stack 自动找一份。
if (-not $DcmDictionary) {
    $stackDir = Get-ChildItem (Join-Path $root 'Windows\WinSxS') -Directory -Filter '*_microsoft-windows-servicingstack_*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($stackDir) {
        $cand = Get-ChildItem $stackDir.FullName -Filter 'wcp.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cand) { $DcmDictionary = $cand.FullName }
    }
}
$dcmArgs = @(); if ($DcmDictionary) { $dcmArgs = @('--dcm-dictionary', $DcmDictionary) }

if (-not $Arch) {
    $kernel = Join-Path $root 'Windows\System32\ntoskrnl.exe'
    if (Test-Path $kernel) {
        $m = [IO.File]::ReadAllBytes($kernel)
        $pe = [BitConverter]::ToInt32($m, 0x3C)
        $machine = [BitConverter]::ToUInt16($m, $pe + 4)
        $Arch = switch ($machine) {
            0x014c { 'x86' }
            0x01c4 { 'arm' }
            0x8664 { 'amd64' }
            0xaa64 { 'arm64' }
            default { throw ("unknown PE machine 0x{0:x4} for {1}" -f $machine, $kernel) }
        }
    } else {
        throw "cannot detect architecture (no $kernel); pass -Arch"
    }
}

if (-not $Exe) {
    $Exe = Join-Path $PSScriptRoot 'Output\x86\ocid_manifest.exe'
}
if (-not (Test-Path $Exe)) { throw "ocid_manifest.exe not found: $Exe (build first)" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($p in @($Manifests, $Packages)) {
    if (-not (Test-Path $p)) { throw "missing input directory: $p" }
}

function Invoke-Planner([string] $Label, [string[]] $Arguments) {
    Write-Host "[assemble] $Label"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE" }
}

$planComponents  = Join-Path $OutDir 'plan-components.txt'
$planDeployments = Join-Path $OutDir 'plan-deployments.txt'
$planVersioned   = Join-Path $OutDir 'plan-versioned.txt'
$planRoot        = Join-Path $OutDir 'plan-root.txt'
$planMerged      = Join-Path $OutDir 'plan-all.txt'
$hive            = Join-Path $OutDir $HiveName

$componentArgs = @('cbs-plan-components', $Manifests, $planComponents, '--staged', $WinSxs) + $dcmArgs
if ($FileFlags) { $componentArgs += @('--file-flags', $FileFlags) }
Invoke-Planner 'components' $componentArgs

$depArgs = @('cbs-plan-deployments', $Packages, $Manifests, $planDeployments) + $dcmArgs
if ($Markers)   { $depArgs += @('--markers', $Markers) }
if ($Installed) { $depArgs += @('--installed', $Installed) }
if ($Claims)    { $depArgs += @('--claims', $Claims) }
if ($Flags)     { $depArgs += @('--flags', $Flags) }
if (-not $Sessions) {
    # 会话日志是"那次安装到底选了哪个 .cat"的唯一证据（Sessions\<id>.xml 的 Resolve 顺序）；
    # 镜像里有就默认带上，没有就保持老的"最后声明"规则。
    $candidate = Join-Path $root 'Windows\servicing\Sessions'
    if (Test-Path $candidate) { $Sessions = $candidate }
}
if ($Sessions)  { $depArgs += @('--sessions', $Sessions) }
Invoke-Planner 'deployments' $depArgs

$verArgs = @('cbs-plan-versioned', $Manifests, $Packages, $WinSxs, $StackVersion, $planVersioned) + $dcmArgs
if ($Buckets) { $verArgs += @('--buckets', $Buckets) }
if ($Cache)   { $verArgs += @('--cache', $Cache) }
if ($Ct)      { $verArgs += @('--ct', $Ct) }
if ($VersionFlags) { $verArgs += @('--version-flags', $VersionFlags) }
if ($Mcp) { $verArgs += @('--mcp', $Mcp) }
if ($SkeletonFamilies) { $verArgs += @('--skeleton-families', $SkeletonFamilies) }
Invoke-Planner 'versioned' $verArgs

if ($RootInfo) {
    if (-not (Test-Path $RootInfo)) { throw "missing root info: $RootInfo" }
    $info = Get-Content -Raw -Path $RootInfo | ConvertFrom-Json
    if (-not $PSBoundParameters.ContainsKey('Pending') -and $info.pendingXmlIdentifier) { $Pending = $info.pendingXmlIdentifier }
    if (-not $PSBoundParameters.ContainsKey('Time')) {
        foreach ($e in $info.stackEntries) {
            if ($e.name -eq $StackVersion) { $Time = ($e.text -split ' ')[0]; break }
        }
    }
    if (-not $PSBoundParameters.ContainsKey('StackEntries')) {
        $derived = @()
        foreach ($e in $info.stackEntries) {
            if ($e.name -eq $StackVersion) { continue }
            if (-not $e.name) { continue }
            $derived += ('' + $e.name + '|' + $e.text)
        }
        $StackEntries = $derived
    }
    if (-not $PSBoundParameters.ContainsKey('RootMinimal') -and $null -eq $info.executionState) { $RootMinimal = [switch]$true }
    if (-not $PSBoundParameters.ContainsKey('Ccpinterface') -and $info.ccpinterface) { $Ccpinterface = [switch]$true }
    if (-not $PSBoundParameters.ContainsKey('InstallersShortcut') -and $info.installersShortcut) { $InstallersShortcut = [switch]$true }
}

$rootArgs = @('cbs-plan-root', $Arch, $StackVersion, $planRoot)
if ($Pending) { $rootArgs += @('--pending', $Pending) }
if ($Time)    { $rootArgs += @('--time', $Time) }
if ($RootMinimal) { $rootArgs += '--minimal' }
if ($Ccpinterface) { $rootArgs += '--ccpinterface' }
if ($InstallersShortcut) { $rootArgs += '--installers-shortcut' }
foreach ($entry in $StackEntries) {
    if ($entry) { $rootArgs += @('--stack-entry', $entry) }
}
Invoke-Planner 'root' $rootArgs

Write-Host '[assemble] merging plans'
$merged = @()
foreach ($p in @($planComponents, $planDeployments, $planVersioned, $planRoot)) {
    $merged += [IO.File]::ReadAllLines($p)
}
$sorted = $merged | Sort-Object -Unique
if ($Override -and (Test-Path $Override)) {
    # 覆盖表：把"只能从安装历史抄"的值按行替换/删除。
    # 行格式（TAB 分隔）：
    #   set    <相对路径> <值名> <类型> <hex>   替换该值（没有则新增）
    #   remove <相对路径> <值名>                删除该值
    #   add-key <相对路径>                      新增一个空键（真机偶有我们计划里没有的空键，
    #                                           例 8419 的 `Installers\Shortcut`）
    #   remove-key <相对路径>                   删除该键及其全部子键/值
    # 典型用途：组件 identity 里 8 条历史大小写、f256 多段拼接的安装顺序、
    # 根键的 ServicingStackVersions 时间串、servicing stack 部署的 payload。
    Write-Host ("[assemble] applying overrides: {0}" -f $Override)
    $dropSet = New-Object 'System.Collections.Generic.HashSet[string]'
    $dropKeySet = New-Object 'System.Collections.Generic.HashSet[string]'
    $addList = New-Object 'System.Collections.Generic.List[string]'
    foreach ($raw in [IO.File]::ReadAllLines($Override)) {
        if (-not $raw -or $raw.StartsWith('#')) { continue }
        $f = $raw -split "`t"
        if ($f.Count -lt 2) { continue }
        if ($f[0] -eq 'remove-key') {
            [void]$dropKeySet.Add($f[1])
            continue
        }
        if ($f[0] -eq 'add-key') {
            $addList.Add('K' + "`t" + $f[1])
            continue
        }
        if ($f.Count -lt 3) { continue }
        $prefix = 'V' + "`t" + $f[1] + "`t" + $f[2] + "`t"
        [void]$dropSet.Add($prefix)
        if ($f[0] -eq 'set') {
            if ($f.Count -lt 5) { continue }
            $addList.Add('V' + "`t" + $f[1] + "`t" + $f[2] + "`t" + $f[3] + "`t" + $f[4])
        }
    }
    $kept = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $sorted) {
        if ($line.Length -gt 2 -and $line[0] -eq 'K') {
            $parts = $line -split "`t", 2
            if ($parts.Count -ge 2) {
                $skipKey = $false
                foreach ($dk in $dropKeySet) {
                    if ($parts[1] -eq $dk -or $parts[1].StartsWith($dk + '\')) { $skipKey = $true; break }
                }
                if ($skipKey) { continue }
            }
        }
        if ($line.Length -gt 2 -and $line[0] -eq 'V') {
            $parts = $line -split "`t", 4
            if ($parts.Count -ge 4) {
                $key = 'V' + "`t" + $parts[1] + "`t" + $parts[2] + "`t"
                if ($dropSet.Contains($key)) { continue }
                $skipKey = $false
                foreach ($dk in $dropKeySet) {
                    if ($parts[1] -eq $dk -or $parts[1].StartsWith($dk + '\')) { $skipKey = $true; break }
                }
                if ($skipKey) { continue }
            }
        }
        $kept.Add($line)
    }
    foreach ($extra in $addList) { $kept.Add($extra) }
    $sorted = $kept | Sort-Object -Unique
    Write-Host ("[assemble] merged lines after overrides: {0}" -f $sorted.Count)
}
[IO.File]::WriteAllLines($planMerged, $sorted, (New-Object Text.UTF8Encoding($false)))
Write-Host ("[assemble] merged lines: {0}" -f $sorted.Count)

# 文件 + 台账的原子性靠顺序约定：先把文件落进 WinSxS，再提交台账。
# 提交前核对一遍 —— 每个 f! 值都必须在相应组件目录里找到文件；任何缺失就
# 放弃写台账（文件多了只是孤儿，台账不会被改成半截状态）。
if (-not $SkipFileVerify) {
    if (-not (Test-Path $WinSxs)) { throw "missing WinSxS directory: $WinSxs" }
    Write-Host "[assemble] verifying staged files against plan: $WinSxs"
    & $Exe cbs-verify-files $planMerged $WinSxs
    if ($LASTEXITCODE -ne 0) {
        throw "cbs-verify-files failed (exit $LASTEXITCODE): refusing to write the ledger; stage the missing files first"
    }
}

$writeArgs = @('cbs-write', $planMerged, $hive)
if ($Transactional) { $writeArgs += '--transactional' }
Invoke-Planner 'write' $writeArgs
Write-Host ("[assemble] hive: {0} ({1} bytes)" -f $hive, (Get-Item $hive).Length)

if ($DiffAgainst -or -not $SkipStoreVerify) {
    $mount = 'OCIDASM'
    $dump = Join-Path $OutDir 'hive-dump.txt'
    & reg unload "HKLM\$mount" 2>$null | Out-Null
    & reg load "HKLM\$mount" $hive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "reg load failed for $hive" }
    try {
        if (-not $SkipStoreVerify) {
            Write-Host "[assemble] self-consistency check (key/value names recomputed from record data)"
            & $Exe cbs-verify-store "HKLM\$mount"
            if ($LASTEXITCODE -ne 0) { throw "cbs-verify-store failed (exit $LASTEXITCODE): the assembled ledger is not self-consistent" }
        }
        if ($DiffAgainst) {
            Invoke-Planner 'dump' @('cbs-dump', "HKLM\$mount", $dump)
        }
    } finally {
        & reg unload "HKLM\$mount" | Out-Null
    }
    if ($DiffAgainst) {
        Write-Host "[assemble] diff against $DiffAgainst"
        & $Exe cbs-diff $dump $DiffAgainst --limit 8
        exit $LASTEXITCODE
    }
}

exit 0





