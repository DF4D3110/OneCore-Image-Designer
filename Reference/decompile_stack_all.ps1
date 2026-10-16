$ErrorActionPreference='Continue'
# Decompile the 8061 x86 servicing stack -- the binaries OCID must replace.
#
# These are the real implementations: CbsCore (CBS), cmiv2 (CMI), smiengine (SMI),
# wcp (the SIL registry/file providers), drvstore/DrUpdate (DMI), and the advanced installers.
# DISM itself is only a client of these, which is why both are being decompiled.
#
# ASCII only: a UTF-8 .ps1 without BOM gets mangled by powershell 5.1 and whole steps vanish.
$ida   = 'D:\Program Files\IDA Professional 9.4\idat.exe'
$script= 'D:\GitHub_Projects\OneCore-Image-Designer\Reference\ocid_decompile.py'
$work  = 'E:\ocid_decomp'
$env:OCID_DECOMP_OUT = $work
New-Item -ItemType Directory -Path "$work\idb" -Force | Out-Null

$dirs = @(
  @{ tag='8061x86'; path="$work\stack8061_x86" },
  
)

# Biggest first is a bad idea when the small ones finish fast; order by name for readability of the
# log instead.
foreach ($d in $dirs) {
  if (-not (Test-Path $d.path)) { Write-Output "  [skip] $($d.tag) (no folder)"; continue }
  $files = Get-ChildItem $d.path -File -Filter '*.dll' | Sort-Object Length -Descending
  foreach ($f in $files) {
    $tag = "$($d.tag)_$($f.BaseName)"
    $outC = Join-Path $work ($tag + '.c')
    if (Test-Path $outC) { Write-Output ("  [have] {0}" -f $tag); continue }

    $staged = Join-Path "$work\src" $tag
    Copy-Item $f.FullName $staged -Force
    Push-Location "$work\idb"
    $sw=[Diagnostics.Stopwatch]::StartNew()
    & $ida -A "-S`"$script`"" -B $staged 2>&1 | Out-Null
    $sw.Stop()
    Pop-Location

    # the script names output after the staged file's base name
    $named = Join-Path $work ($tag + '.c')
    $kb = if (Test-Path $named) { [math]::Round((Get-Item $named).Length/1KB,0) } else { 0 }
    Write-Output ("  {0,-42} {1,8:N0} KB  {2,4}s" -f $tag, $kb, [int]$sw.Elapsed.TotalSeconds)
  }
}
Write-Output "STACK_DECOMP_DONE"
