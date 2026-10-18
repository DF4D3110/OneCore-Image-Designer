#Requires -Version 5.1
<#
.SYNOPSIS
    One-command OCID servicing assembly.

.DESCRIPTION
    Ties the pieces together so a single invocation goes from an image (plus an
    optional reference COMPONENTS hive) to a rebuilt COMPONENTS hive that can be
    compared against the reference:

      1. (optional) Extract-CbsInputs.ps1     - derive every "install history" input
                                                (markers/claims/flags/buckets/cache/
                                                ct/version_flags/mcp/skeleton/root_info)
                                                from a mounted reference hive.
      2. assemble-cbs-store.ps1 (pass 1)      - plan components/deployments/versioned/
                                                root, merge, transacted `cbs-write`,
                                                verify files + store.
      3. (optional) make-cbs-overrides.ps1    - turn the pass-1 `cbs-diff` residue into
                                                an override table.
      4. assemble-cbs-store.ps1 (pass 2)      - re-assemble with the override table and
                                                report the final diff (0/0 when the only
                                                remaining differences were install
                                                history that cannot be derived).

    Everything is derived from files and from explicitly supplied history inputs; no
    servicing API is called.

.PARAMETER ImageRoot
    Root of the reference image that supplies WinSxS\Manifests, servicing\Packages and
    the staged payload directories (e.g. `Q:\`, `P:\`, or a mounted WIM).

.PARAMETER StackVersion
    Servicing-stack bucket text, e.g. "6.2.8061.0 (fbl_core1_soc.110803-1830)".

.PARAMETER SourceHive
    Optional mounted reference COMPONENTS hive (e.g. `HKLM\OCIDCOMP`). When given, all
    history inputs are extracted automatically instead of passing the individual CSVs.

.PARAMETER InstalledHive
    Mounted SOFTWARE hive holding the CBS package states (used by the extractor).

.PARAMETER InputsDir
    Where extracted inputs are written/read. Defaults to `<OutDir>\inputs`.

.PARAMETER AutoOverrides
    After pass 1, generate an override table from the diff against `-DiffAgainst` and
    run pass 2 with it.

.EXAMPLE
    .\ocid-assemble.ps1 -ImageRoot Q:\ -SourceHive HKLM\OCIDCOMP `
        -InstalledHive HKLM\OCIDSOFT `
        -StackVersion "6.2.8061.0 (fbl_core1_soc.110803-1830)" `
        -OutDir E:\ocid_verify\one -AutoOverrides

.EXAMPLE
    # inputs already extracted once; re-assemble with an existing override table
    .\ocid-assemble.ps1 -ImageRoot P:\ -InputsDir E:\ocid_verify\inputsARM `
        -StackVersion "6.2.8061.0 (fbl_core1_soc.110803-1830)" `
        -Override E:\ocid_verify\overrides_arm2.csv -OutDir E:\ocid_verify\oneARM
#>
[CmdletBinding()]
param(
    [string] $ImageRoot = 'Q:\',
    [Parameter(Mandatory = $true)][string] $StackVersion,
    [string] $OutDir = 'E:\ocid_verify\ocid-assemble',
    [string] $Arch,

    # history inputs: either extract them from a mounted hive ...
    [string] $SourceHive,
    [string] $InstalledHive,
    [string] $SoftwareHivePath,
    [string] $ExtractDefaultBucket,
    [switch] $ForceExtract,
    [string] $InputsDir,

    # ... or pass them explicitly (same meaning as assemble-cbs-store.ps1)
    [string] $Markers,
    [string] $Installed,
    [string] $Claims,
    [string] $Flags,
    [string] $Buckets,
    [string] $Cache,
    [string] $Ct,
    [string] $VersionFlags,
    [string] $Mcp,
    [string] $SkeletonFamilies,
    [string] $Sessions,

    [string] $Override,
    [string] $DiffAgainst,
    [switch] $AutoOverrides,
    [switch] $Transactional,
    [switch] $SkipFileVerify,
    [switch] $SkipStoreVerify,
    [string] $HiveName = 'COMPONENTS.hiv',
    [string] $Exe
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) { $Exe = Join-Path $PSScriptRoot 'Output\x86\ocid_manifest.exe' }
if (-not (Test-Path $Exe)) { throw "ocid_manifest.exe not found: $Exe (build first)" }

$root = (Resolve-Path $ImageRoot).Path.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not $InputsDir) { $InputsDir = Join-Path $OutDir 'inputs' }

function Write-Step([string] $text) { Write-Host "[ocid] $text" }

# ---------------------------------------------------------------- 1. inputs
if ($SourceHive) {
    $needed = @()
    foreach ($p in @($Markers, $Installed, $Claims, $Flags, $Buckets, $Cache, $Ct, $VersionFlags, $Mcp, $SkeletonFamilies)) {
        if (-not $p) { $needed += 1 }
    }
    $extract = ($ForceExtract -or -not (Test-Path (Join-Path $InputsDir 'root_info.json')))
    if ($extract) {
        Write-Step "extracting install-history inputs from $SourceHive -> $InputsDir"
        $ep = @{
            Hive   = $SourceHive
            OutDir = $InputsDir
            Exe    = $Exe
        }
        if ($InstalledHive) { $ep['InstalledHive'] = $InstalledHive }
        if ($SoftwareHivePath) { $ep['SoftwareHivePath'] = $SoftwareHivePath }
        # the current stack bucket is the store's default bucket unless told otherwise
        if (-not $ExtractDefaultBucket) { $ExtractDefaultBucket = $StackVersion }
        if ($ExtractDefaultBucket) { $ep['DefaultBucket'] = $ExtractDefaultBucket }
        $LASTEXITCODE = 0
        & (Join-Path $PSScriptRoot 'Extract-CbsInputs.ps1') @ep
        if (-not (Test-Path (Join-Path $InputsDir 'truth.txt'))) { throw 'Extract-CbsInputs.ps1 produced no truth.txt' }
    } else {
        Write-Step "reusing inputs in $InputsDir (use -ForceExtract to redo)"
    }
    if (-not $Markers) { $Markers = Join-Path $InputsDir 'markers.csv' }
    if (-not $Installed) { $Installed = Join-Path $InputsDir 'packages_state.txt' }
    if (-not $Claims) { $Claims = Join-Path $InputsDir 'claims.csv' }
    if (-not $Flags) { $Flags = Join-Path $InputsDir 'flags.csv' }
    if (-not $Buckets) { $Buckets = Join-Path $InputsDir 'buckets.csv' }
    if (-not $Cache) { $Cache = Join-Path $InputsDir 'cache.csv' }
    if (-not $Ct) { $Ct = Join-Path $InputsDir 'ct.csv' }
    if (-not $VersionFlags) { $VersionFlags = Join-Path $InputsDir 'version_flags.csv' }
    if (-not $Mcp) { $Mcp = Join-Path $InputsDir 'mcp.csv' }
    if (-not $SkeletonFamilies) { $SkeletonFamilies = Join-Path $InputsDir 'skeleton_families.csv' }
    if (-not $DiffAgainst -and (Test-Path (Join-Path $InputsDir 'truth.txt'))) {
        $DiffAgainst = Join-Path $InputsDir 'truth.txt'
    }
}

# when inputs were extracted earlier, pick up truth.txt for the diff as well
if (-not $DiffAgainst -and (Test-Path (Join-Path $InputsDir 'truth.txt'))) {
    $DiffAgainst = Join-Path $InputsDir 'truth.txt'
}

# ... and fill every input that was not passed explicitly from that directory
if (Test-Path $InputsDir) {
    if (-not $Markers -and (Test-Path (Join-Path $InputsDir 'markers.csv'))) { $Markers = Join-Path $InputsDir 'markers.csv' }
    if (-not $Installed -and (Test-Path (Join-Path $InputsDir 'packages_state.txt'))) { $Installed = Join-Path $InputsDir 'packages_state.txt' }
    if (-not $Claims -and (Test-Path (Join-Path $InputsDir 'claims.csv'))) { $Claims = Join-Path $InputsDir 'claims.csv' }
    if (-not $Flags -and (Test-Path (Join-Path $InputsDir 'flags.csv'))) { $Flags = Join-Path $InputsDir 'flags.csv' }
    if (-not $Buckets -and (Test-Path (Join-Path $InputsDir 'buckets.csv'))) { $Buckets = Join-Path $InputsDir 'buckets.csv' }
    if (-not $Cache -and (Test-Path (Join-Path $InputsDir 'cache.csv'))) { $Cache = Join-Path $InputsDir 'cache.csv' }
    if (-not $Ct -and (Test-Path (Join-Path $InputsDir 'ct.csv'))) { $Ct = Join-Path $InputsDir 'ct.csv' }
    if (-not $VersionFlags -and (Test-Path (Join-Path $InputsDir 'version_flags.csv'))) { $VersionFlags = Join-Path $InputsDir 'version_flags.csv' }
    if (-not $Mcp -and (Test-Path (Join-Path $InputsDir 'mcp.csv'))) { $Mcp = Join-Path $InputsDir 'mcp.csv' }
    if (-not $SkeletonFamilies -and (Test-Path (Join-Path $InputsDir 'skeleton_families.csv'))) { $SkeletonFamilies = Join-Path $InputsDir 'skeleton_families.csv' }
}

if (-not $Sessions) {
    $candidate = Join-Path $root 'Windows\servicing\Sessions'
    if (Test-Path $candidate) { $Sessions = $candidate }
}

$rootInfo = Join-Path $InputsDir 'root_info.json'
if (-not (Test-Path $rootInfo)) { $rootInfo = '' }

# ---------------------------------------------------------------- 2. assemble
function Invoke-Assemble([string] $PassDir, [string] $OverridePath, [switch] $AllowDiff) {
    New-Item -ItemType Directory -Force -Path $PassDir | Out-Null
    # hashtable splat: for scripts, an array splat binds '-Name' strings positionally
    $p = @{
        ImageRoot    = $root + '\'
        StackVersion = $StackVersion
        OutDir       = $PassDir
        HiveName     = $HiveName
        Exe          = $Exe
    }
    if ($Arch) { $p['Arch'] = $Arch }
    if ($Markers) { $p['Markers'] = $Markers }
    if ($Installed) { $p['Installed'] = $Installed }
    if ($Claims) { $p['Claims'] = $Claims }
    if ($Flags) { $p['Flags'] = $Flags }
    if ($Buckets) { $p['Buckets'] = $Buckets }
    if ($Cache) { $p['Cache'] = $Cache }
    if ($Ct) { $p['Ct'] = $Ct }
    if ($VersionFlags) { $p['VersionFlags'] = $VersionFlags }
    if ($Mcp) { $p['Mcp'] = $Mcp }
    if ($SkeletonFamilies) { $p['SkeletonFamilies'] = $SkeletonFamilies }
    if ($Sessions) { $p['Sessions'] = $Sessions }
    if ($rootInfo) { $p['RootInfo'] = $rootInfo }
    if ($OverridePath) { $p['Override'] = $OverridePath }
    if ($DiffAgainst) { $p['DiffAgainst'] = $DiffAgainst }
    if ($Transactional) { $p['Transactional'] = $true }
    if ($SkipFileVerify) { $p['SkipFileVerify'] = $true }
    if ($SkipStoreVerify) { $p['SkipStoreVerify'] = $true }
    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'assemble-cbs-store.ps1') @p
    $code = $LASTEXITCODE
    if ($code -and $code -ne 0) {
        # assemble exits 1 when the requested diff is non-zero; pass 1 of the
        # auto-override flow expects that, everything else is a real failure.
        $dumpOk = Test-Path (Join-Path $PassDir 'hive-dump.txt')
        if (-not $AllowDiff -or -not $dumpOk) { throw "assemble-cbs-store.ps1 failed ($code)" }
        Write-Step "pass finished with exit $code (non-zero diff, as expected)"
    }
}

if ($Override -and $AutoOverrides) { throw "use either -Override or -AutoOverrides, not both" }

if ($AutoOverrides) {
    if (-not $DiffAgainst) { throw "-AutoOverrides needs -DiffAgainst (or -SourceHive with truth.txt)" }
    $pass1 = Join-Path $OutDir 'pass1'
    Write-Step "pass 1 (no overrides) -> $pass1"
    Invoke-Assemble $pass1 '' -AllowDiff
    $dump1 = Join-Path $pass1 'hive-dump.txt'
    $generated = Join-Path $OutDir 'overrides.csv'
    Write-Step "generating override table from $dump1 vs $DiffAgainst"
    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'make-cbs-overrides.ps1') -Dump $dump1 -Truth $DiffAgainst -Out $generated -Exe $Exe
    # $LASTEXITCODE here reflects the child's last native call (cbs-diff returns 1 when it finds a diff), so check the artefact instead
    if (-not (Test-Path $generated)) { throw 'make-cbs-overrides.ps1 produced no override table' }
    $final = Join-Path $OutDir 'final'
    Write-Step "pass 2 (with $generated) -> $final"
    Invoke-Assemble $final $generated
    Write-Step "done: $final"
} else {
    Write-Step "single pass -> $OutDir"
    Invoke-Assemble $OutDir $Override
    Write-Step "done: $OutDir"
}




