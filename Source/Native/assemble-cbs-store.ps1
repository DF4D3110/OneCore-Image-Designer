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
    [string] $Buckets,
    [string] $Cache,
    [string] $Ct,
    [string] $VersionFlags,
    [string] $Pending = '',
    [string] $Time = '',
    [string] $DiffAgainst,
    [string] $Exe
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $ImageRoot).Path.TrimEnd('\')
if (-not $Manifests) { $Manifests = Join-Path $root 'Windows\WinSxS\Manifests' }
if (-not $Packages)  { $Packages  = Join-Path $root 'Windows\servicing\Packages' }
if (-not $WinSxs)    { $WinSxs    = Join-Path $root 'Windows\WinSxS' }

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

Invoke-Planner 'components' @('cbs-plan-components', $Manifests, $planComponents, '--staged', $WinSxs)

$depArgs = @('cbs-plan-deployments', $Packages, $Manifests, $planDeployments)
if ($Markers)   { $depArgs += @('--markers', $Markers) }
if ($Installed) { $depArgs += @('--installed', $Installed) }
if ($Claims)    { $depArgs += @('--claims', $Claims) }
if ($Flags)     { $depArgs += @('--flags', $Flags) }
Invoke-Planner 'deployments' $depArgs

$verArgs = @('cbs-plan-versioned', $Manifests, $Packages, $WinSxs, $StackVersion, $planVersioned)
if ($Buckets) { $verArgs += @('--buckets', $Buckets) }
if ($Cache)   { $verArgs += @('--cache', $Cache) }
if ($Ct)      { $verArgs += @('--ct', $Ct) }
if ($VersionFlags) { $verArgs += @('--version-flags', $VersionFlags) }
Invoke-Planner 'versioned' $verArgs

$rootArgs = @('cbs-plan-root', $Arch, $StackVersion, $planRoot)
if ($Pending) { $rootArgs += @('--pending', $Pending) }
if ($Time)    { $rootArgs += @('--time', $Time) }
Invoke-Planner 'root' $rootArgs

Write-Host '[assemble] merging plans'
$merged = @()
foreach ($p in @($planComponents, $planDeployments, $planVersioned, $planRoot)) {
    $merged += [IO.File]::ReadAllLines($p)
}
$sorted = $merged | Sort-Object -Unique
[IO.File]::WriteAllLines($planMerged, $sorted, (New-Object Text.UTF8Encoding($false)))
Write-Host ("[assemble] merged lines: {0}" -f $sorted.Count)

Invoke-Planner 'write' @('cbs-write', $planMerged, $hive)
Write-Host ("[assemble] hive: {0} ({1} bytes)" -f $hive, (Get-Item $hive).Length)

if ($DiffAgainst) {
    $mount = 'OCIDASM'
    $dump = Join-Path $OutDir 'hive-dump.txt'
    & reg unload "HKLM\$mount" 2>$null | Out-Null
    & reg load "HKLM\$mount" $hive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "reg load failed for $hive" }
    try {
        Invoke-Planner 'dump' @('cbs-dump', "HKLM\$mount", $dump)
    } finally {
        & reg unload "HKLM\$mount" | Out-Null
    }
    Write-Host "[assemble] diff against $DiffAgainst"
    & $Exe cbs-diff $dump $DiffAgainst --limit 8
    exit $LASTEXITCODE
}

exit 0


