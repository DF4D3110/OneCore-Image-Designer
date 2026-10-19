# Extract-CbsInputs.ps1 -- derive every "install history" input CSV for the CBS
# writer from a *reference* image's own COMPONENTS hive (+ its SOFTWARE hive).
#
# Why: the CBS writer reproduces a COMPONENTS store byte-for-byte from manifests,
# packages and a handful of values that record what the machine actually installed
# (deployment markers, claim marks, closure flags, cache identifiers, ordering of
# f256 segments, ...).  Those values cannot be derived from the manifests, so they
# are inputs.  This script extracts them so that `assemble-cbs-store.ps1` can be
# driven on any image (8061 x86/ARM, 8419, 16299, ...) without hand-written probes.
#
# Usage:
#   .\Extract-CbsInputs.ps1 -Hive 'HKLM\OCIDCOMP' -OutDir E:\ocid_verify\inputs8061 `
#       -SoftwareHivePath 'Q:\Windows\System32\config\SOFTWARE' -ImageRoot 'Q:\'
#
# Outputs (all in -OutDir):
#   truth.txt            cbs-dump of the reference hive (also the diff baseline)
#   markers.csv          deploymentKey|appid|thumb|marks        (--markers)
#   claims.csv           componentKey|deploymentKey            (--claims)
#   flags.csv            componentKey|CF                       (--flags)
#   buckets.csv          familyKey|bucketText                  (--buckets, non-default buckets)
#   cache.csv            bucket|id8|-1 / bucket|id8|usedBytes  (--cache)
#   ct.csv               familyKey|version|64hex               (--ct)
#   version_flags.csv    relativeVersionPath|dword             (--version-flags)
#   mcp.csv              relativeVersionPath|id8|offset|length  (--mcp)
#   skeleton_families.csv familyKey                            (--skeleton-families)
#   packages_state.txt   packageKey|CurrentState               (--installed)
#   root_info.json       root values + stack entries + counts
#
# Notes:
#  * The hive must already be loaded (e.g. `reg load HKLM\OCIDCOMP <path>`); this
#    script does not mount COMPONENTS itself.
#  * SOFTWARE hive is mounted read-only under -MountName and unloaded at the end;
#    the mount name must not end in "SOFTWARE" (UCPD.sys denylists such names).
#  * Types: 1 = REG_SZ (UTF-16LE text), 3 = REG_BINARY, 4 = REG_DWORD.
#    cbs-dump prints raw value bytes as hex, so text is decoded here.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Hive,
    [Parameter(Mandatory = $true)][string] $OutDir,
    [string] $SoftwareHivePath,
    [string] $InstalledHive,
    [string] $MountName = 'OCIDIN',
    [string] $DefaultBucket,
    [string] $ImageRoot,
    [string] $Exe
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $PSCommandPath

function Resolve-Exe {
    param([string] $Path)
    if ($Path -and (Test-Path $Path)) { return (Resolve-Path $Path).Path }
    $here = $scriptDir
    foreach ($candidate in @(
            (Join-Path $here 'Output\x86\ocid_manifest.exe'),
            (Join-Path $here '..\Output\x86\ocid_manifest.exe'))) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    throw "cannot find ocid_manifest.exe; pass -Exe"
}

function ConvertFrom-Hex([string] $Hex) {
    if (-not $Hex) { return [byte[]]@() }
    $bytes = New-Object 'byte[]' ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Get-Ascii([string] $Hex) {
    $b = ConvertFrom-Hex $Hex
    if ($b.Length -eq 0) { return '' }
    return [Text.Encoding]::ASCII.GetString($b)
}

function Get-Utf16([string] $Hex) {
    $b = ConvertFrom-Hex $Hex
    if ($b.Length -eq 0) { return '' }
    return [Text.Encoding]::Unicode.GetString($b)
}

function Get-Dword([string] $Hex) {
    $b = ConvertFrom-Hex $Hex
    if ($b.Length -lt 4) { return 0 }
    return [BitConverter]::ToUInt32($b, 0)
}

function Get-Qword([string] $Hex) {
    $b = ConvertFrom-Hex $Hex
    if ($b.Length -lt 8) { return 0 }
    return [BitConverter]::ToUInt64($b, 0)
}

# Hex text -> ASCII hex (e.g. CT stores a 64-char hex string as UTF-16LE).
function Get-HexText([string] $Hex) {
    $text = Get-Utf16 $Hex
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $text.ToCharArray()) {
        [void]$sb.Append(('{0:x2}' -f [int][char]$ch))
    }
    return $sb.ToString()
}

$exePath = Resolve-Exe $Exe
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$truth = Join-Path $OutDir 'truth.txt'
& $exePath cbs-dump $Hive $truth | Out-Null
if (-not (Test-Path $truth)) { throw "cbs-dump failed: $Hive" }

$markers = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$claims = New-Object 'System.Collections.Generic.List[string]'
$flags = New-Object 'System.Collections.Generic.List[string]'
$buckets = New-Object 'System.Collections.Generic.List[string]'
$cache = New-Object 'System.Collections.Generic.List[string]'
$ct = New-Object 'System.Collections.Generic.List[string]'
$vflags = New-Object 'System.Collections.Generic.List[string]'
$mcp = New-Object 'System.Collections.Generic.List[string]'
$skeleton = New-Object 'System.Collections.Generic.HashSet[string]'
$content = New-Object 'System.Collections.Generic.HashSet[string]'
$root = @{ values = @{}; stacks = @{} }
$hasCcp = $false
$ccpKeyName = 'ccpinterface'
$hasConfiguration = $false
$hasNonCanonicalData = $false
$hasInstallers = $false
$hasTransformSettings = $false
$hasInstallersShortcut = $false

$componentRoot = 'DerivedData\Components'
$deployRoot = 'CanonicalData\Deployments'
$vindexRoot = 'DerivedData\VersionedIndex'

foreach ($line in [IO.File]::ReadLines($truth)) {
    if (-not $line) { continue }
    $f = $line.Split("`t")
    if ($f[0] -eq 'K') {
        # A family that lives in an older build bucket can have no values at the
        # family key itself (everything sits under v!<version>), so the bucket
        # association must be taken from the key list, not from values.
        # 16299 用 `CCPInterface`，8061/8419 用 `ccpinterface`；大小写要照抄真机，
        # 否则 cbs-diff 会多出一个键（PowerShell 的 -eq 不区分大小写，这里只记原文）。
        if ($f.Count -ge 2 -and $f[1] -eq 'ccpinterface') { $hasCcp = $true; $ccpKeyName = $f[1] }
        # 现代镜像（16299+）的根键集合与 8061/8419 不同：没有 Configuration，
        # 多了 NonCanonicalData / DerivedData\TransformSettingsVersion。
        if ($f.Count -ge 2) {
            switch ($f[1]) {
                'Configuration' { $hasConfiguration = $true }
                'NonCanonicalData' { $hasNonCanonicalData = $true }
                'Installers' { $hasInstallers = $true }
                'DerivedData\TransformSettingsVersion' { $hasTransformSettings = $true }
            }
        }
        if ($f.Count -ge 2 -and $f[1] -eq 'Installers\Shortcut') { $hasInstallersShortcut = $true }
        if ($f.Count -ge 2 -and $f[1].StartsWith($vindexRoot + '\')) {
            $relPath = $f[1].Substring($vindexRoot.Length + 1)
            $partsKey = $relPath.Split('\')
            if ($partsKey.Length -eq 3 -and $partsKey[1] -eq 'ComponentFamilies' -and $partsKey[2] -ne '') {
                if (-not $DefaultBucket -or $partsKey[0] -ne $DefaultBucket) {
                    $buckets.Add(($partsKey[2] + '|' + $partsKey[0]))
                }
            }
        }
        continue
    }
    if ($f[0] -eq 'V') {
        if ($f.Count -lt 5) { continue }
        $path = $f[1]; $name = $f[2]; $type = [int]$f[3]; $hex = $f[4]
        # ---- root values -------------------------------------------------
        if ($path -eq '') {
            $root.values[$name] = @{ type = $type; hex = $hex }
            continue
        }
        if ($path -eq 'ServicingStackVersions' -or $path.StartsWith('ServicingStackVersions\')) {
            $root.stacks[$name] = @{ type = $type; hex = $hex }
            continue
        }
        # ---- deployments -------------------------------------------------
        if ($path.StartsWith($deployRoot + '\')) {
            $dkey = $path.Substring($deployRoot.Length + 1)
            if ($dkey.Contains('\')) { continue }
            if (-not $markers.ContainsKey($dkey)) {
                $markers[$dkey] = @{ appid = ''; thumb = ''; marks = (New-Object 'System.Collections.Generic.SortedSet[string]') }
            }
            $rec = $markers[$dkey]
            if ($name -eq 'appid') { $rec.appid = Get-Ascii $hex }
            elseif ($name -eq 'CatalogThumbprint') { $rec.thumb = Get-Utf16 $hex }
            elseif ($name.Length -ge 2 -and $name[1] -eq '!') { [void]$rec.marks.Add([string]$name[0]) }
            continue
        }
        # ---- versioned index ---------------------------------------------
        if ($path.StartsWith($vindexRoot + '\')) {
            $rel = $path.Substring($vindexRoot.Length + 1)
            $parts = $rel.Split('\')
            # bucket-level values: <bucket>\ManifestCacheIdentifier / UtilizedSpace_MCP_<id>
            if (-not $rel.Contains('\')) {
                if ($name -eq 'ManifestCacheIdentifier') {
                    $cache.Add(('{0}|{1:x8}|-1' -f $rel, (Get-Dword $hex)))
                    continue
                }
                if ($name -like 'UtilizedSpace_MCP_*') {
                    $id = [Convert]::ToUInt32($name.Substring($name.LastIndexOf('_') + 1), 16)
                    $cache.Add(('{0}|{1:x8}|{2}' -f $rel, $id, (Get-Qword $hex)))
                    continue
                }
                continue
            }
            $famIdx = [Array]::IndexOf($parts, 'ComponentFamilies')
            if ($famIdx -ge 0 -and $famIdx + 1 -lt $parts.Length) {
                $bucketText = $parts[0]
                $fam = $parts[$famIdx + 1]
                $isVersionNode = ($parts.Length -gt $famIdx + 2)
                if (-not $isVersionNode) {
                    if ($name.StartsWith('f256!')) { [void]$content.Add($fam); continue }
                    if ($name -eq 'SomeUnparsedVersionsExist') {
                        [void]$skeleton.Add($fam)
                        continue
                    }
                    if (-not $DefaultBucket -or $bucketText -ne $DefaultBucket) {
                        $buckets.Add(($fam + '|' + $bucketText))
                    }
                    continue
                }
                if ($name -eq '') {
                    $vflags.Add(($path + '|' + (Get-Dword $hex)))
                    continue
                }
                if ($name -like 'MCP_*') {
                    $b = ConvertFrom-Hex $hex
                    if ($b.Length -ge 12) {
                        $off = [BitConverter]::ToUInt32($b, 4)
                        $len = [BitConverter]::ToUInt32($b, 8)
                        $mcp.Add(('{0}|{1}|{2}|{3}' -f $path, $name.Substring(4), $off, $len))
                        [void]$content.Add($fam)
                    }
                    continue
                }
                if ($name -eq 'CT') {
                    $ct.Add(($fam + '|' + $parts[$famIdx + 2].Substring(2) + '|' + ((Get-Utf16 $hex).Trim())))
                    [void]$content.Add($fam)
                    continue
                }
                continue
            }
        }
        # ---- components ---------------------------------------------------
        if ($path.StartsWith($componentRoot + '\')) {
            $ckey = $path.Substring($componentRoot.Length + 1)
            if ($ckey.Contains('\')) { continue }
            if ($name -eq 'CF') {
                $flags.Add(($ckey + '|' + (Get-Dword $hex)))
                continue
            }
            if ($name.StartsWith('c!')) {
                $claims.Add(($ckey + '|' + $name.Substring(2)))
                continue
            }
        }
    }
}

function Write-Lines([string] $Path, $Lines) {
    if ($null -eq $Lines) { [IO.File]::WriteAllText($Path, '', (New-Object Text.UTF8Encoding($false))); return }
    [IO.File]::WriteAllLines($Path, [string[]]$Lines, (New-Object Text.UTF8Encoding($false)))
}

$markerLines = New-Object 'System.Collections.Generic.List[string]'
foreach ($k in ($markers.Keys | Sort-Object)) {
    $rec = $markers[$k]
    $marks = -join ($rec.marks | Sort-Object)
    $markerLines.Add(('{0}|{1}|{2}|{3}' -f $k, $rec.appid, $rec.thumb, $marks))
}
Write-Lines (Join-Path $OutDir 'markers.csv') $markerLines
Write-Lines (Join-Path $OutDir 'claims.csv') ($claims | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'flags.csv') ($flags | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'buckets.csv') ($buckets | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'cache.csv') ($cache | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'ct.csv') ($ct | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'version_flags.csv') ($vflags | Sort-Object -Unique)
Write-Lines (Join-Path $OutDir 'mcp.csv') ($mcp | Sort-Object -Unique)
# a family that also carries real content (f256/MCP/CT/version flags) is not skeleton-only
$skeleton.ExceptWith($content)
Write-Lines (Join-Path $OutDir 'skeleton_families.csv') ($skeleton | Sort-Object)

# ---- packages state (SOFTWARE hive) ----------------------------------------
$packages = 0
if ($InstalledHive) {
    $pkgRoot = "$InstalledHive\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages"
    $items = Get-ChildItem -Path ("Registry::" + $pkgRoot) -ErrorAction SilentlyContinue
    $linesPkg = New-Object 'System.Collections.Generic.List[string]'
    foreach ($it in $items) {
        $ps = Get-ItemProperty -Path $it.PSPath -ErrorAction SilentlyContinue
        $state = 0
        if ($null -ne $ps.CurrentState) { $state = [int]$ps.CurrentState }
        $linesPkg.Add(($it.PSChildName + '|' + $state))
    }
    Write-Lines (Join-Path $OutDir 'packages_state.txt') $linesPkg
    $packages = $linesPkg.Count
}
elseif ($SoftwareHivePath) {
    $full = (Resolve-Path $SoftwareHivePath).Path
    $key = "HKLM\$MountName"
    # `reg load` 对"已经挂着同名键"会失败但不报错：如果上一次抽取没卸干净（或上次是
    # 别的镜像），这里会静默读到**上一个镜像**的包状态（实测把 8419 ARM 的状态喂给了
    # 9600，导致所有部署的载荷值都没写）。所以先确保这个挂载点干净，再核对确实挂上了。
    if (Test-Path ("Registry::" + $key)) {
        & reg unload $key | Out-Null
        Start-Sleep -Milliseconds 300
        if (Test-Path ("Registry::" + $key)) {
            throw "挂载点 $key 仍被占用（另一个 reg load 没卸），先 reg unload 再抽取"
        }
    }
    & reg load $key $full | Out-Null
    if (-not (Test-Path ("Registry::" + $key))) {
        throw "reg load $key $full 失败（包状态读不到）"
    }
    try {
        $pkgRoot = "${key}\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages"
        $items = Get-ChildItem -Path ("Registry::" + $pkgRoot) -ErrorAction SilentlyContinue
        $lines = New-Object 'System.Collections.Generic.List[string]'
        foreach ($it in $items) {
            $ps = Get-ItemProperty -Path $it.PSPath -ErrorAction SilentlyContinue
            $state = 0
            if ($null -ne $ps.CurrentState) { $state = [int]$ps.CurrentState }
            $lines.Add(($it.PSChildName + '|' + $state))
        }
        Write-Lines (Join-Path $OutDir 'packages_state.txt') $lines
        $packages = $lines.Count
    }
    finally {
        # 注册表句柄释放有延迟：立刻 unload 常常 Access is denied。重试几次，
        # 别把挂载点留给下一次（那会让下一次抽取静默读到这个镜像的状态）。
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        $unloaded = $false
        for ($try = 0; $try -lt 5 -and -not $unloaded; $try++) {
            & reg unload $key 2>&1 | Out-Null
            Start-Sleep -Milliseconds 300
            $unloaded = -not (Test-Path ("Registry::" + $key))
        }
        if (-not $unloaded) { Write-Warning "无法卸载 $key（下一次抽取前请手工 reg unload）" }
    }
}

# ---- root info -------------------------------------------------------------
function Get-RootText([string] $Name) {
    if (-not $root.values.ContainsKey($Name)) { return $null }
    return (Get-Utf16 $root.values[$Name].hex)
}
$rootInfo = [ordered]@{
    storeFormatVersion = Get-RootText 'StoreFormatVersion'
    storeArchitecture  = if ($root.values.ContainsKey('StoreArchitecture')) { (Get-Dword $root.values['StoreArchitecture'].hex) } else { $null }
    executionState     = if ($root.values.ContainsKey('ExecutionState')) { (Get-Dword $root.values['ExecutionState'].hex) } else { $null }
    pendingXmlIdentifier = Get-RootText 'PendingXmlIdentifier'
    stackEntries       = @()
    lastWcp            = $null
    counts             = [ordered]@{
        deployments   = $markers.Count
        claims        = $claims.Count
        flags         = $flags.Count
        buckets       = ($buckets | Sort-Object -Unique).Count
        ct            = $ct.Count
        versionFlags  = $vflags.Count
        mcp           = $mcp.Count
        skeleton      = $skeleton.Count
        packages      = $packages
    }
    imageRoot          = $ImageRoot
    ccpinterface       = $hasCcp
    ccpKeyName         = $ccpKeyName
    configuration      = $hasConfiguration
    nonCanonicalData   = $hasNonCanonicalData
    installers         = $hasInstallers
    transformSettings  = $hasTransformSettings
    installersShortcut = $hasInstallersShortcut
}
foreach ($name in ($root.stacks.Keys | Sort-Object)) {
    $text = (Get-Utf16 $root.stacks[$name].hex).TrimEnd([char]0)
    if ($name -eq 'LastWCPVersionToAccessStore') { $rootInfo.lastWcp = $text; continue }
    $rootInfo.stackEntries += [ordered]@{ name = $name; text = $text }
}
$json = $rootInfo | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $OutDir 'root_info.json'), $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ("[inputs] {0}: deployments={1} claims={2} flags={3} buckets={4} ct={5} vflags={6} mcp={7} skeleton={8} packages={9}" -f `
        $Hive, $markers.Count, $claims.Count, $flags.Count, ($buckets | Sort-Object -Unique).Count, $ct.Count, $vflags.Count, $mcp.Count, $skeleton.Count, $packages)

