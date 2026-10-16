$ErrorActionPreference='Continue'
# Decompile every DISM component of interest, for both ADK versions.
#
# Each binary takes roughly 10-60 seconds, so this runs as one batch. Output lands in
# E:\ocid_decomp\<name>.c (pseudocode) and .asm (listing).
#
# ASCII only: a UTF-8 .ps1 without BOM gets mangled by powershell 5.1 and whole steps silently
# vanish. That has bitten this project more than five times.
$ida   = 'D:\Program Files\IDA Professional 9.4\idat.exe'
$script= 'D:\GitHub_Projects\OneCore-Image-Designer\Reference\ocid_decompile.py'
$work  = 'E:\ocid_decomp'
$env:OCID_DECOMP_OUT = $work
New-Item -ItemType Directory -Path "$work\idb" -Force | Out-Null
New-Item -ItemType Directory -Path "$work\src"  -Force | Out-Null

$roots = @{
  '19041' = 'E:\Assessment and Deployment Kit\dism\19041\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\x86\DISM'
  '10527' = 'E:\Assessment and Deployment Kit\dism\10527\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\x86\DISM'
}

$names = @(
  'dism.exe','dismapi.dll','dismcore.dll','dismprov.dll',
  'wimprovider.dll','folderprovider.dll','vhdprovider.dll','ffuprovider.dll',
  'imagingprovider.dll','logprovider.dll','siloedpackageprovider.dll',
  'wimgapi.dll','ssshim.dll'
)

foreach ($ver in @('19041','10527')) {
  $srcDir = $roots[$ver]
  foreach ($n in $names) {
    $p = Join-Path $srcDir $n
    if (-not (Test-Path $p)) { Write-Output "  [skip] $ver/$n (not present)"; continue }
    $tag = "${ver}_$n"
    $dst = Join-Path "$work\src" $tag
    Copy-Item $p $dst -Force
    if (Test-Path "$work\$tag.c") { Remove-Item "$work\$tag.c" -Force -EA SilentlyContinue }
    # rename so the decompiled file is self-describing
    $staged = Join-Path "$work\src" $tag
    Push-Location "$work\idb"
    $sw=[Diagnostics.Stopwatch]::StartNew()
    & $ida -A "-S`"$script`"" -B $staged 2>&1 | Out-Null
    $sw.Stop()
    Pop-Location
    $produced = Join-Path $work ($tag + '.c')
    $kb = if (Test-Path $produced) { [math]::Round((Get-Item $produced).Length/1KB,1) } else { 0 }
    Write-Output ("  {0,-40} {1,8} KB  {2,4}s" -f $tag, $kb, [int]$sw.Elapsed.TotalSeconds)
  }
}
Write-Output "DECOMP_DONE"
