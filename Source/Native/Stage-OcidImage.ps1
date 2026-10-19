#Requires -Version 5.1
<#
.SYNOPSIS
    Stage a Windows image from a package tree with OCID's own native pipe:
    CSI 落位（place-ops -> csi-place）→ CSI/CBS 台账组装（assemble-cbs-store）→ 换进镜像 → 自洽校验。

.DESCRIPTION
    这条链把"包展开目录 + 清单"变成"镜像里的文件与 COMPONENTS 台账"，全程不调用系统服务栈：
      1) place-ops  把每份清单算成落位计划（JSONL）
      2) csi-place  执行计划：载荷 → 正本（复制）→ 落点（硬链接）→ 最后套 SDDL
      3) assemble-cbs-store.ps1  生成 DerivedData/CanonicalData/VersionedIndex（+ 现代镜像的 Drivers）
      4) 替换 <ImageRoot>\Windows\System32\config\COMPONENTS（自动备份原文件）
      5) reg load 校验：cbs-scan / cbs-verify-store

    现代镜像（16299+，清单是 DCM 压缩）用 -DcmDictionary 指向镜像里的 wcp.dll（脚本会自动探测）。

.EXAMPLE
    .\Stage-OcidImage.ps1 -ImageRoot 'E:\ocid_dism\mnt16299' -StackVersion '10.0.16299.15 (WinBuild.160101.0800)'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ImageRoot,
    [Parameter(Mandatory = $true)][string] $StackVersion,
    [string] $Arch,
    [string] $OutDir = 'E:\ocid_verify\stage',
    [string] $Manifests,
    [string] $Packages,
    [string] $WinSxs,
    [string] $Exe,
    [switch] $NoAcl,
    [switch] $SkipPlace,
    [switch] $SkipDrivers,
    [switch] $SkipLedger,
    [switch] $SkipSwap,
    [string] $Markers, [string] $Installed, [string] $Claims, [string] $Flags, [string] $Sessions,
    [string] $Buckets, [string] $Cache, [string] $Ct, [string] $VersionFlags, [string] $Mcp,
    [string] $SkeletonFamilies, [string] $RootInfo, [string] $Override, [string] $DiffAgainst,
    [string] $DcmDictionary, [string] $FileFlags, [string] $Pending, [string] $Time,
    [string] $DriverOsVersion = '6.2', [string] $DriverSigner, [int] $DriverSignerScore = 0,
    [string[]] $StackEntries = @(),
    [switch] $Transactional, [switch] $RootMinimal, [switch] $Ccpinterface,
    [switch] $InstallersShortcut, [switch] $NoConfiguration, [switch] $NonCanonicalData,
    [switch] $TransformSettings, [switch] $InstallersRoot
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here 'Output\x86\ocid_manifest.exe' }
if (-not (Test-Path $Exe)) { throw "找不到 $Exe（先跑 build-servicing.ps1）" }

$root = (Resolve-Path $ImageRoot).Path.TrimEnd('\')
$winDir = Join-Path $root 'Windows'
if (-not (Test-Path $winDir)) { throw "镜像里没有 Windows 目录：$winDir" }
if (-not $Manifests) { $Manifests = Join-Path $winDir 'WinSxS\Manifests' }
if (-not $WinSxs)    { $WinSxs = Join-Path $winDir 'WinSxS' }
if (-not (Test-Path $Manifests)) { throw "找不到清单目录：$Manifests" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# ── 架构：没给就按 ntoskrnl.exe 的 PE Machine 探测（与 place-ops 自己用的规则一致）──
function Get-PeMachine([string] $path) {
    if (-not (Test-Path $path)) { return 0 }
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $peOff = $br.ReadUInt32()
        $fs.Position = $peOff + 4
        return [int]$br.ReadUInt16()
    } finally { $fs.Dispose() }
}
if (-not $Arch) {
    $machine = Get-PeMachine (Join-Path $winDir 'System32\ntoskrnl.exe')
    $Arch = switch ($machine) {
        0x014c { 'x86' } 0x01c4 { 'arm' } 0x8664 { 'amd64' } 0xaa64 { 'arm64' } default { '' }
    }
    if (-not $Arch) { throw "无法从 ntoskrnl.exe 探测架构（Machine=0x{0:X}），请用 -Arch 指定" -f $machine }
}
Write-Host "[stage] image=$root arch=$Arch stack=$StackVersion"

# ── 1) 落位计划 + 执行 ───────────────────────────────────────────────────
$placeJsonl = Join-Path $OutDir 'place.jsonl'
if (-not $SkipPlace) {
    $pkgArg = @()
    if ($Packages) { $pkgArg = @('--packages', $Packages) }
    $lines = & $Exe place-ops --windows $winDir @pkgArg --image-arch $Arch $Manifests 2>&1 |
             Where-Object { $_ -like '{*' }
    $lines | Set-Content -Path $placeJsonl -Encoding UTF8
    Write-Host ("[stage] place-ops lines={0}" -f @($lines).Count)
    if (@($lines).Count -eq 0) { throw "place-ops 没有产出任何落位计划（清单目录里没有 .manifest？）" }

    $aclArg = @()
    if ($NoAcl) { $aclArg = @('--no-acl') }
    & $Exe csi-place $placeJsonl @aclArg
    if ($LASTEXITCODE -ne 0) { throw "csi-place 失败（exit=$LASTEXITCODE）" }
} else { Write-Host '[stage] 跳过落位' }

# ── 1.5) 驱动包落进镜像（FileRepository + Windows\INF\oemN.inf + DriverDatabase）──
$driverPlan = Join-Path $OutDir 'drivers.jsonl'
$driverDbPlan = Join-Path $OutDir 'driverdb.txt'
if (-not $SkipDrivers) {
    if (-not $Packages) {
        Write-Host '[stage] 跳过驱动落位（没有 -Packages：驱动载荷不在清单目录里）'
    } else {
        $driverLines = & $Exe dmi-ops $Manifests --packages $Packages 2>&1 |
                       Where-Object { $_ -like '{*' }
        $driverLines | Set-Content -Path $driverPlan -Encoding UTF8
        Write-Host ("[stage] dmi-ops lines={0}" -f @($driverLines).Count)
        if (@($driverLines).Count -gt 0) {
            $dp = @('dmi-place', $driverPlan, $root, '--packages', $Packages,
                    '--plan-out', $driverDbPlan, '--os', $DriverOsVersion)
            if ($DriverSigner) { $dp += @('--signer', $DriverSigner) }
            if ($DriverSignerScore -ne 0) { $dp += @('--signer-score', [string]$DriverSignerScore) }
            & $Exe @dp
            if ($LASTEXITCODE -ne 0) { throw "dmi-place 失败（exit=$LASTEXITCODE）" }

            # DriverDatabase 写进离线 SYSTEM hive（--append：目标已存在，只挂载不覆盖）。
            $systemHive = Join-Path $winDir 'System32\config\SYSTEM'
            $writeArgs = @('cbs-write', $driverDbPlan, $systemHive, '--append')
            if ($Transactional) { $writeArgs += '--transactional' }
            & $Exe @writeArgs
            if ($LASTEXITCODE -ne 0) { throw "DriverDatabase 写入失败（exit=$LASTEXITCODE）" }
        }
    }
}

# ── 2) 台账组装 ─────────────────────────────────────────────────────────
$ledgerDir = Join-Path $OutDir 'ledger'
if (-not $SkipLedger) {
    $a = @{
        ImageRoot = ($root + '\'); StackVersion = $StackVersion; Arch = $Arch
        OutDir = $ledgerDir; Manifests = $Manifests; WinSxs = $WinSxs; Exe = $Exe
    }
    if ($Packages)   { $a.Packages = $Packages }
    if ($Markers)    { $a.Markers = $Markers }
    if ($Installed)  { $a.Installed = $Installed }
    if ($Claims)     { $a.Claims = $Claims }
    if ($Flags)      { $a.Flags = $Flags }
    if ($Sessions)   { $a.Sessions = $Sessions }
    if ($Buckets)    { $a.Buckets = $Buckets }
    if ($Cache)      { $a.Cache = $Cache }
    if ($Ct)         { $a.Ct = $Ct }
    if ($VersionFlags) { $a.VersionFlags = $VersionFlags }
    if ($Mcp)        { $a.Mcp = $Mcp }
    if ($SkeletonFamilies) { $a.SkeletonFamilies = $SkeletonFamilies }
    if ($Override)   { $a.Override = $Override }
    if ($RootInfo)   { $a.RootInfo = $RootInfo }
    if ($DiffAgainst){ $a.DiffAgainst = $DiffAgainst }
    if ($DcmDictionary) { $a.DcmDictionary = $DcmDictionary }
    if ($FileFlags)  { $a.FileFlags = $FileFlags }
    if ($Pending)    { $a.Pending = $Pending }
    if ($Time)       { $a.Time = $Time }
    if ($StackEntries.Count -gt 0) { $a.StackEntries = $StackEntries }
    if ($Transactional) { $a.Transactional = $true }
    if ($RootMinimal)   { $a.RootMinimal = $true }
    if ($Ccpinterface)  { $a.Ccpinterface = $true }
    if ($InstallersShortcut) { $a.InstallersShortcut = $true }
    if ($NoConfiguration)    { $a.NoConfiguration = $true }
    if ($NonCanonicalData)   { $a.NonCanonicalData = $true }
    if ($TransformSettings)  { $a.TransformSettings = $true }
    if ($InstallersRoot)     { $a.InstallersRoot = $true }
    & (Join-Path $here 'assemble-cbs-store.ps1') @a
    if ($LASTEXITCODE -ne 0) { throw "assemble-cbs-store 失败（exit=$LASTEXITCODE）" }
} else { Write-Host '[stage] 跳过台账' }

# ── 3) 换进镜像（先备份）────────────────────────────────────────────────
$hive = Join-Path $ledgerDir 'COMPONENTS.hiv'
$target = Join-Path $winDir 'System32\config\COMPONENTS'
if (-not $SkipSwap) {
    if (-not (Test-Path $hive)) { throw "没有产物 hive：$hive" }
    if (Test-Path $target) {
        $bak = "$target.bak." + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item $target $bak -Force
        Write-Host "[stage] 已备份原台账：$bak"
    }
    Copy-Item $hive $target -Force
    Write-Host "[stage] 台账已换入：$target"
}

# ── 4) 自洽校验（把换进去的台账挂起来扫）─────────────────────────────────
if (-not $SkipSwap) {
    $mount = 'OCIDSTAGE'
    & reg load "HKLM\$mount" $target | Out-Null
    try {
        & $Exe cbs-scan "HKLM\$mount"
        if ($LASTEXITCODE -ne 0) { throw 'cbs-scan 失败' }
        & $Exe cbs-verify-store "HKLM\$mount"
        if ($LASTEXITCODE -ne 0) { throw 'cbs-verify-store 失败' }
    } finally {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers()
        & reg unload "HKLM\$mount" | Out-Null
    }
}

Write-Host "[stage] 完成：$root"
