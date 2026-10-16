$ErrorActionPreference='Continue'
# Decompile the 8061 servicing stack WITH SYMBOLS -- this is the reference OCID is rewriting.
#
# The earlier symbol-less run is worthless for this purpose: function names came out as sub_XXXX
# and, as the user warned, understanding a binary only in fragments leads to a rewrite that is
# subtly and unrecognisably wrong. With the 8061 PDBs loaded, CbsCore.dll alone drops from
# mostly-sub_XXXX to 7 percent unnamed and exposes real names such as CCbsDriverDeployment,
# CCbsComponentDeployment, CCbsExecutionDriver and CCbsClassFactory.
#
# The local symbol tree is already in symbol-server layout
# (<name>.pdb\<GUID><age>\<name>.pdb), so it can be handed to IDA directly through
# _NT_SYMBOL_PATH without any copying or indexing.
#
# ASCII only: a UTF-8 .ps1 without BOM gets mangled by powershell 5.1 and whole steps vanish.
$ida   = 'D:\Program Files\IDA Professional 9.4\idat.exe'
$script= 'D:\GitHub_Projects\OneCore-Image-Designer\Reference\ocid_decompile.py'
$work  = 'E:\ocid_decomp'
$sym   = 'E:\windesktop\6.2.8061.0.fbl_core1_soc.110803-1830\sym'

$env:OCID_DECOMP_OUT = $work
$env:_NT_SYMBOL_PATH = "$sym\fre_x86;srv*E:\symbols*https://msdl.microsoft.com/download/symbols"
New-Item -ItemType Directory -Path "$work\idb" -Force | Out-Null
New-Item -ItemType Directory -Path "$work\src" -Force | Out-Null

$dir = "$work\stack8061_x86"
$files = Get-ChildItem $dir -File -Filter '*.dll' | Sort-Object Length -Descending
foreach ($f in $files) {
  $tag = "sym8061x86_$($f.BaseName)"
  $outC = Join-Path $work ($tag + '.c')
  if (Test-Path $outC) { Write-Output ("  [have] {0}" -f $tag); continue }

  $staged = Join-Path "$work\src" $tag
  Copy-Item $f.FullName $staged -Force
  Push-Location "$work\idb"
  $sw=[Diagnostics.Stopwatch]::StartNew()
  & $ida -A "-S`"$script`"" -B $staged 2>&1 | Out-Null
  $sw.Stop()
  Pop-Location

  $named = Join-Path $work ($tag + '.c')
  $kb = if (Test-Path $named) { [math]::Round((Get-Item $named).Length/1KB,0) } else { 0 }
  # count how much stayed unnamed -- that is the quickest signal of whether symbols loaded
  $subs = 0
  if (Test-Path $named) { $subs = (Select-String -Path $named -Pattern '^// sub_[0-9A-F]+' -EA SilentlyContinue | Measure-Object).Count }
  Write-Output ("  {0,-40} {1,8:N0} KB  sub_={2,-5} {3,4}s" -f $tag, $kb, $subs, [int]$sw.Elapsed.TotalSeconds)
}
Write-Output "SYM_DECOMP_DONE"
