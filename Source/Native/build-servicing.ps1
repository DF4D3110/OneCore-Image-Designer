# Build-Servicing.ps1 -- build the OCID native servicing library, tools and tests (x86)
#
# Why this exists: the servicing code (OcidServicing) had no build script, so every
# rebuild was an ad-hoc cl.exe invocation. That made "did I actually rebuild?"
# impossible to answer, and a stale .obj once hid a real compile error.
#
# Targets (all go to Source\Native\Output\x86):
#   ocid_identity_tests.exe   Identity.cpp
#   ocid_manifest_tests.exe   Identity.cpp + Manifest.cpp
#   ocid_wcm_tests.exe        Identity.cpp + Manifest.cpp + WcmSchema.cpp
#   ocid_value_tests.exe      RegistryValue.cpp
#   ocid_manifest.exe         Identity.cpp + Manifest.cpp + WcmSchema.cpp + RegistryValue.cpp
#
# NOTE: keep this file pure ASCII. A non-ASCII .ps1 without a BOM is read as GBK by
# Windows PowerShell 5.1 and lines get swallowed.

param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$src    = Join-Path $root 'OcidServicing'
$out    = Join-Path $root 'Output\x86'
$objdir = Join-Path $root 'Output\build_servicing'

$vcvars = 'D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars32.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars32.bat not found: $vcvars" }

New-Item -ItemType Directory -Force -Path $out, $objdir | Out-Null

if ($Clean) {
    Get-ChildItem $objdir -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host "[build] cleaned $objdir"
}

$inc = Join-Path $src 'include'

# /EHsc   standard C++ exceptions
# /std:c++17
# /W4     warnings matter here - this code encodes on-disk formats
# /utf-8  MANDATORY. The sources are UTF-8 WITHOUT a BOM and contain Chinese comments.
#         Without /utf-8, cl.exe reads them as codepage 936 (GBK), mangles the text and
#         then reports nonsense like "error C2447: missing function header" at the
#         first function after a Chinese comment. This is the C++ twin of the
#         "PowerShell 5.1 needs ASCII or a BOM" rule that already burned us 5+ times.
$cflags = "/nologo /c /EHsc /std:c++17 /W4 /O2 /MD /utf-8 /DUNICODE /D_UNICODE /I`"$inc`""

function Build-Target {
    param(
        [string]$Name,
        [string[]]$Sources
    )
    $objs = @()
    $lines = @()
    foreach ($s in $Sources) {
        $srcPath = Join-Path $src $s
        if (-not (Test-Path $srcPath)) { throw "source not found: $srcPath" }
        $obj = Join-Path $objdir ((Split-Path $s -Leaf) -replace '\.cpp$', ".$Name.obj")
        $objs += $obj
        $lines += "cl $cflags `"$srcPath`" /Fo`"$obj`""
    }
    $exe = Join-Path $out $Name
    $lines += "link /nologo /OUT:`"$exe`" " + (($objs | ForEach-Object { "`"$_`"" }) -join ' ')

    $bat = Join-Path $objdir "_build_$Name.cmd"
    $body = "@echo off`r`ncall `"$vcvars`" >nul`r`n" + ($lines -join "`r`n") + "`r`n"
    Set-Content -Path $bat -Value $body -Encoding ASCII
    & cmd.exe /c "`"$bat`""
    if ($LASTEXITCODE -ne 0) { throw "build failed for $Name (exit $LASTEXITCODE)" }
    Write-Host "[build] OK $Name"
}

$core = @('src\Identity.cpp', 'src\Manifest.cpp', 'src\WcmSchema.cpp', 'src\RegistryValue.cpp')

Build-Target 'ocid_identity_tests.exe' @('src\Identity.cpp', 'tests\identity_tests.cpp')
Build-Target 'ocid_manifest_tests.exe' @('src\Identity.cpp', 'src\Manifest.cpp', 'tests\manifest_tests.cpp')
Build-Target 'ocid_wcm_tests.exe'      @('src\Identity.cpp', 'src\Manifest.cpp', 'src\WcmSchema.cpp', 'tests\wcm_tests.cpp')
Build-Target 'ocid_value_tests.exe'    @('src\RegistryValue.cpp', 'tests\registryvalue_tests.cpp')
Build-Target 'ocid_manifest.exe'       ($core + @('tools\ocid_manifest.cpp'))

Write-Host "[build] done -> $out"
