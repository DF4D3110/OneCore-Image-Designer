# Build-Servicing.ps1 -- build the OCID native servicing library, tools and tests
#
# Why this exists: the servicing code (OcidServicing) had no build script, so every
# rebuild was an ad-hoc cl.exe invocation. That made "did I actually rebuild?"
# impossible to answer, and a stale .obj once hid a real compile error.
#
# Targets:
#   Output\x86\OcidServicing.dll       32-bit rule library (what a 32-bit host P/Invokes)
#   Output\x64\OcidServicing.dll       64-bit build of the SAME sources
#   Output\x86\ocid_identity_tests.exe
#   Output\x86\ocid_manifest_tests.exe
#   Output\x86\ocid_wcm_tests.exe
#   Output\x86\ocid_value_tests.exe
#   Output\x86\ocid_cmi_tests.exe
#   Output\x86\ocid_manifest.exe       identity + manifest + wcm + registryvalue + cmi
#
# Why both DLL bitnesses: the C# app (OneCore Image Designer) is built with
# <Platforms>x64;x86;ARM64</Platforms>, so the process may be 64-bit while the
# 8061 servicing stack it is replacing is 32-bit. A 32-bit DLL cannot be loaded by a
# 64-bit process -- and the failure is only "BadImageFormatException", which reads
# like a broken DLL rather than an architecture mismatch. Building both removes the
# question. The C# side picks by Environment.Is64BitProcess.
#
# NOTE: keep this file pure ASCII. A non-ASCII .ps1 without a BOM is read as GBK by
# Windows PowerShell 5.1 and lines get swallowed.

param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'OcidServicing'

$vcRoot = 'D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build'

function Get-Toolchain {
    param([string]$Arch)
    if ($Arch -eq 'x64') {
        return @{ Vcvars = Join-Path $vcRoot 'vcvars64.bat'; Out = Join-Path $root 'Output\x64'; Obj = Join-Path $root 'Output\build_servicing_x64' }
    }
    return @{ Vcvars = Join-Path $vcRoot 'vcvars32.bat'; Out = Join-Path $root 'Output\x86'; Obj = Join-Path $root 'Output\build_servicing_x86' }
}

# /EHsc   standard C++ exceptions
# /std:c++17
# /W4     warnings matter here - this code encodes on-disk formats
# /utf-8  MANDATORY. The sources are UTF-8 WITHOUT a BOM and contain Chinese comments.
#         Without /utf-8, cl.exe reads them as codepage 936 (GBK), mangles the text and
#         then reports nonsense like "error C2447: missing function header" at the
#         first function after a Chinese comment. This is the C++ twin of the
#         "PowerShell 5.1 needs ASCII or a BOM" rule that already burned us 5+ times.
function Get-CFlags {
    param([string]$Inc)
    return "/nologo /c /EHsc /std:c++17 /W4 /O2 /MD /utf-8 /DUNICODE /D_UNICODE /I`"$Inc`""
}

function New-Dirs {
    param([string]$Out, [string]$Obj)
    New-Item -ItemType Directory -Force -Path $Out, $Obj | Out-Null
    if ($Clean) {
        Get-ChildItem $Obj -File -ErrorAction SilentlyContinue | Remove-Item -Force
        Write-Host "[build] cleaned $Obj"
    }
}

function Invoke-Batch {
    param([string]$Vcvars, [string]$Obj, [string]$Name, [string[]]$Lines)
    if (-not (Test-Path $Vcvars)) { throw "vcvars not found: $Vcvars" }
    $bat = Join-Path $Obj "_build_$Name.cmd"
    $body = "@echo off`r`ncall `"$Vcvars`" >nul`r`n" + ($Lines -join "`r`n") + "`r`n"
    Set-Content -Path $bat -Value $body -Encoding ASCII
    & cmd.exe /c "`"$bat`""
    if ($LASTEXITCODE -ne 0) { throw "build failed for $Name (exit $LASTEXITCODE)" }
    Write-Host "[build] OK $Name"
}

$inc = Join-Path $src 'include'

function Build-Target {
    param([string]$Name, [string[]]$Sources)
    $tc   = Get-Toolchain 'x86'
    $cf   = Get-CFlags $inc
    $objs = @()
    $lines = @()
    foreach ($s in $Sources) {
        $srcPath = Join-Path $src $s
        if (-not (Test-Path $srcPath)) { throw "source not found: $srcPath" }
        $obj = Join-Path $tc.Obj ((Split-Path $s -Leaf) -replace '\.cpp$', ".$Name.obj")
        $objs += $obj
        $lines += "cl $cf `"$srcPath`" /Fo`"$obj`""
    }
    $exe = Join-Path $tc.Out $Name
    $lines += "link /nologo /OUT:`"$exe`" " + (($objs | ForEach-Object { "`"$_`"" }) -join ' ')
    Invoke-Batch -Vcvars $tc.Vcvars -Obj $tc.Obj -Name $Name -Lines $lines
}

# The product side (C#, Services/OcidSmiService.cs, Services/OcidServicingNative.cs)
# must call THESE rules, not its own copy of them. Until this DLL existed, C# had a
# parallel implementation (ToRegistryKind / ConvertData) that could drift from the
# C++ one and silently write wrong bytes.
function Build-Dll {
    param([string]$Arch, [string[]]$Sources, [string]$DefFile)
    $tc = Get-Toolchain $Arch
    $cf = Get-CFlags $inc
    $objs = @()
    $lines = @()
    foreach ($s in $Sources) {
        $srcPath = Join-Path $src $s
        if (-not (Test-Path $srcPath)) { throw "source not found: $srcPath" }
        $obj = Join-Path $tc.Obj ((Split-Path $s -Leaf) -replace '\.cpp$', ".dll.obj")
        $objs += $obj
        $lines += "cl $cf `"$srcPath`" /Fo`"$obj`""
    }
    if (-not (Test-Path $DefFile)) { throw "def file not found: $DefFile" }
    $dll = Join-Path $tc.Out 'OcidServicing.dll'
    $lines += "link /nologo /DLL /OUT:`"$dll`" /DEF:`"$DefFile`" " + (($objs | ForEach-Object { "`"$_`"" }) -join ' ')
    Invoke-Batch -Vcvars $tc.Vcvars -Obj $tc.Obj -Name "OcidServicing_$Arch" -Lines $lines
}

Get-Toolchain 'x86' | ForEach-Object { New-Dirs -Out $_.Out -Obj $_.Obj }
Get-Toolchain 'x64' | ForEach-Object { New-Dirs -Out $_.Out -Obj $_.Obj }

$core = @('src\Identity.cpp', 'src\Manifest.cpp', 'src\WcmSchema.cpp', 'src\RegistryValue.cpp', 'src\CmiInstaller.cpp')
$api  = $core + @('src\OcidServicingApi.cpp')
$def  = Join-Path $src 'src\OcidServicing.def'

Build-Dll 'x86' $api $def
Build-Dll 'x64' $api $def

Build-Target 'ocid_identity_tests.exe' @('src\Identity.cpp', 'tests\identity_tests.cpp')
Build-Target 'ocid_manifest_tests.exe' @('src\Identity.cpp', 'src\Manifest.cpp', 'tests\manifest_tests.cpp')
Build-Target 'ocid_wcm_tests.exe'      @('src\Identity.cpp', 'src\Manifest.cpp', 'src\WcmSchema.cpp', 'tests\wcm_tests.cpp')
Build-Target 'ocid_value_tests.exe'    @('src\RegistryValue.cpp', 'tests\registryvalue_tests.cpp')
Build-Target 'ocid_cmi_tests.exe'      @('src\RegistryValue.cpp', 'src\CmiInstaller.cpp', 'tests\cmiinstaller_tests.cpp')
Build-Target 'ocid_manifest.exe'       ($core + @('tools\ocid_manifest.cpp'))

Write-Host "[build] done -> $(Join-Path $root 'Output')"
