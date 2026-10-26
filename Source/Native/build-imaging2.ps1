# build-imaging2.ps1 -- build OcidImaging (multi-stage: fe / rs5)
#
# Why this script exists (do not merge with build-servicing.ps1):
#   OcidImaging is a standalone project. Every module is a separate DLL so that a
#   failure points at exactly one stage ("which DLL was loaded last / called last").
#
# Stage separation (user requirement, 2026-10):
#   FE  (20348/20279 branch) -> modules named ocid_*.<fe>.wcos.dll  + ocid_imagegen.exe
#   RS5 (17763 branch)       -> modules named ocid_*.<rs5>.wcos.dll + ocid_imagegen.rs5.exe
#   Both never share a process and never share an ADK root (each exe writes its own
#   OCID_ADK_ROOT at startup; Stage.cpp refuses to run if the env points elsewhere).
#   A dispatcher ocid_imagegen.all.exe sits on top and forwards to the right exe.
#
# Usage:
#   pwsh -File build-imaging2.ps1                 # build both stages + dispatcher
#   pwsh -File build-imaging2.ps1 -Stage fe       # only FE
#   pwsh -File build-imaging2.ps1 -Stage rs5
#   pwsh -File build-imaging2.ps1 -Clean
#
# ASCII only on purpose: PowerShell 5.1 reads BOM-less files as ANSI, CJK breaks.
param(
    [switch]$Clean,
    [string]$OutName = 'Imaging2',
    [string]$Stage = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'OcidImaging'
$inc = Join-Path $proj 'include'
$out = Join-Path $root ('Output\' + $OutName)
$objRoot = Join-Path $root ('Output\build_' + $OutName.ToLower())

function Find-VcRoot {
    $candidates = @(
        'D:\Program Files\Microsoft Visual Studio\2022\Community',
        'D:\Program Files\Microsoft Visual Studio\2022\Professional',
        'D:\Program Files\Microsoft Visual Studio\2022\Enterprise',
        'C:\Program Files\Microsoft Visual Studio\2022\Community',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate 'VC\Auxiliary\Build\vcvars32.bat')) { return $candidate }
    }
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($found -and (Test-Path (Join-Path $found 'VC\Auxiliary\Build\vcvars32.bat'))) { return $found }
    }
    throw 'vcvars32.bat not found (Visual Studio C++ toolchain missing)'
}

$vcRoot = Find-VcRoot
$vcvars = Join-Path $vcRoot 'VC\Auxiliary\Build\vcvars32.bat'

if ($Clean) {
    foreach ($dir in @($out, $objRoot)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
}

$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'

# Stage table: name, exe name, stage macro, ADK i386 dir, layout note.
$stages = @(
    @{ Name = 'fe';  Exe = 'ocid_imagegen.exe';     Macro = '/DOCID_STAGE_FE'; Helper = 'OcidAppX.cs';
       Adk = 'D:\ADK2022Wrokspace\Windows Kits\10\tools\bin\i386' },
    @{ Name = 'rs5'; Exe = 'ocid_imagegen.rs5.exe'; Macro = '/DOCID_STAGE_RS5'; Helper = 'OcidAppX.Rs5.cs';
       Adk = 'D:\ADK2022Wrokspace\RS5\bin\i386' }
)

function Invoke-Batch {
    param([string]$name, [string]$objDir, [string[]]$lines)
    if (-not (Test-Path $objDir)) { New-Item -ItemType Directory -Path $objDir | Out-Null }
    $cmdPath = Join-Path $objDir "_build_$name.cmd"
    $text = "@echo off`r`ncall `"$vcvars`" >nul`r`n"
    foreach ($line in $lines) { $text += "$line || exit /b 1`r`n" }
    [System.IO.File]::WriteAllText($cmdPath, $text, [System.Text.Encoding]::ASCII)
    & cmd /c "`"$cmdPath`"" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build step failed: $name (exit $LASTEXITCODE)" }
}

function Build-OneStage {
    param([hashtable]$StageInfo)

    $stageName = $StageInfo.Name
    $suffix = $stageName
    $outDir = Join-Path $out $stageName
    $objDir = Join-Path $objRoot $stageName
    foreach ($dir in @($outDir, $objDir)) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    }

    $cflags = @('/nologo', '/c', '/EHsc', '/std:c++17', '/W4', '/O2', '/MD', '/utf-8', '/DUNICODE', '/D_UNICODE',
                "/I`"$inc`"", $StageInfo.Macro)

    function Build-Object {
        param([string]$name, [string]$source, [string[]]$extra = @())
        $leaf = [System.IO.Path]::GetFileNameWithoutExtension($source)
        $objFile = Join-Path $objDir ("$leaf.$name.obj")
        $src = Join-Path $proj $source
        $line = "cl $($cflags -join ' ') $($extra -join ' ') /Fo`"$objFile`" `"$src`""
        Invoke-Batch "$stageName.$name" $objDir @($line)
        return $objFile
    }

    function Build-Dll {
        param([string]$name, [string]$leafName, [string[]]$objects, [string[]]$extraLibs = @())
        $dll = Join-Path $outDir "$leafName.$suffix.wcos.dll"
        $lib = Join-Path $outDir "$leafName.$suffix.wcos.lib"
        $objs = ($objects | ForEach-Object { "`"$_`"" }) -join ' '
        $libs = ($extraLibs -join ' ')
        $line = "link /nologo /DLL /OUT:`"$dll`" /IMPLIB:`"$lib`" $objs $libs"
        Invoke-Batch "$stageName.$name" $objDir @($line)
        Write-Host "[build:$stageName] OK $leafName.$suffix.wcos.dll"
    }

    # ---- stage (must be first: everything else may ask for its info) ----
    $stageObjs = @()
    $stageObjs += Build-Object 'stage' 'src\Stage.cpp' @('/DOCID_STAGE_BUILD')
    Build-Dll 'stage' 'ocid_stage' $stageObjs @('ole32.lib')
    $stageLib = Join-Path $outDir "ocid_stage.$suffix.wcos.lib"

    # ---- common ----
    $commonObjs = @()
    $commonObjs += Build-Object 'common' 'src\Common.cpp' @('/DOCID_COMMON_BUILD')
    $commonObjs += Build-Object 'common' 'src\Validate.cpp' @('/DOCID_COMMON_BUILD')
    Build-Dll 'common' 'ocid_common' $commonObjs @('ole32.lib', 'oleaut32.lib')
    $commonLib = Join-Path $outDir "ocid_common.$suffix.wcos.lib"

    # ---- plan ----
    $planObjs = @()
    $planObjs += Build-Object 'plan' 'src\Plan.cpp' @('/DOCID_PLAN_BUILD')
    $planLib = Join-Path $outDir "ocid_plan.$suffix.wcos.lib"
    Build-Dll 'plan' 'ocid_plan' $planObjs @("`"$commonLib`"")

    # ---- cab ----
    $cabObjs = @()
    $cabObjs += Build-Object 'cab' 'src\Cab.cpp' @('/DOCID_CAB_BUILD')
    $cabLib = Join-Path $outDir "ocid_cab.$suffix.wcos.lib"
    Build-Dll 'cab' 'ocid_cab' $cabObjs @("`"$commonLib`"", 'cabinet.lib')

    # ---- vhd ----
    $vhdObjs = @()
    $vhdObjs += Build-Object 'vhd' 'src\Vhd.cpp' @('/DOCID_VHD_BUILD')
    $vhdLib = Join-Path $outDir "ocid_vhd.$suffix.wcos.lib"
    Build-Dll 'vhd' 'ocid_vhd' $vhdObjs @("`"$commonLib`"", 'virtdisk.lib', 'ole32.lib', 'uuid.lib', 'rstrtmgr.lib')

    # ---- pool (ImageStorageService wrapper; per-stage ABI) ----
    # FE(20348) 与 RS5(17763) 的 imagestorageservice.dll 不是同一套导出：FE 是 cdecl 名字 +
    # AttachStorageSpace/OpenStoragePool；RS5 是 stdcall 装饰名 + CreateStorageSpace/GetStorageSpace。
    # 因此两阶段用不同源文件——不在同一份实现里靠 ifdef 混写，出错时一眼能看出是哪套 ABI。
    $poolObjs = @()
    if ($suffix -eq 'rs5') {
        $poolObjs += Build-Object 'pool' 'src\PoolRs5.cpp' @('/DOCID_POOL_BUILD')
    } else {
        $poolObjs += Build-Object 'pool' 'src\Pool.cpp' @('/DOCID_POOL_BUILD')
        $poolObjs += Build-Object 'pool' 'src\PoolMount.cpp' @('/DOCID_POOL_BUILD')
    }
    $poolLib = Join-Path $outDir "ocid_pool.$suffix.wcos.lib"
    Build-Dll 'pool' 'ocid_pool' $poolObjs @("`"$commonLib`"", 'advapi32.lib', "`"$vhdLib`"", 'user32.lib')

    # ---- update (UpdateDLL wrapper) ----
    $updateObjs = @()
    $updateObjs += Build-Object 'update' 'src\Update.cpp' @('/DOCID_UPDATE_BUILD')
    $updateLib = Join-Path $outDir "ocid_update.$suffix.wcos.lib"
    Build-Dll 'update' 'ocid_update' $updateObjs @("`"$commonLib`"", "`"$poolLib`"", 'advapi32.lib', 'user32.lib')

    # ---- ffu ----
    $ffuObjs = @()
    $ffuObjs += Build-Object 'ffu' 'src\Ffu.cpp' @('/DOCID_FFU_BUILD')
    $ffuLib = Join-Path $outDir "ocid_ffu.$suffix.wcos.lib"
    Build-Dll 'ffu' 'ocid_ffu' $ffuObjs @("`"$commonLib`"", "`"$vhdLib`"", 'virtdisk.lib', 'bcrypt.lib')

    # ---- appx (FM <AppX><PackageFile> list + 32-bit OcidAppX.exe helper) ----
    $appxObjs = @()
    $appxObjs += Build-Object 'appx' 'src\Appx.cpp' @('/DOCID_APPX_BUILD')
    $appxLib = Join-Path $outDir "ocid_appx.$suffix.wcos.lib"
    Build-Dll 'appx' 'ocid_appx' $appxObjs @("`"$commonLib`"", "`"$planLib`"")

    # ---- exe ----
    $exeObjs = @()
    $exeObjs += Build-Object 'imagegen' 'tools\imagegen.cpp'
    $exe = Join-Path $outDir $StageInfo.Exe
    $exeObjArgs = ($exeObjs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $linkLine = "link /nologo /OUT:`"$exe`" $exeObjArgs `"$stageLib`" `"$planLib`" `"$cabLib`" `"$vhdLib`" `"$poolLib`" `"$updateLib`" `"$ffuLib`" `"$appxLib`" `"$commonLib`" virtdisk.lib ole32.lib advapi32.lib"
    Invoke-Batch "$stageName.imagegen" $objDir @($linkLine)
    Write-Host "[build:$stageName] OK $($StageInfo.Exe)"

    # ---- schemas ----
    $repoRoot = Split-Path -Parent (Split-Path -Parent $root)
    $schemaSrc = Join-Path $repoRoot 'Schemas'
    $schemaDst = Join-Path $outDir 'schemas'
    if (Test-Path $schemaSrc) {
        if (-not (Test-Path $schemaDst)) { New-Item -ItemType Directory -Path $schemaDst | Out-Null }
        Copy-Item (Join-Path $schemaSrc '*.xsd') $schemaDst -Force
        $adkSchemaSrc = Join-Path $schemaSrc 'adk'
        if (Test-Path $adkSchemaSrc) {
            $adkSchemaDst = Join-Path $schemaDst 'adk'
            if (-not (Test-Path $adkSchemaDst)) { New-Item -ItemType Directory -Path $adkSchemaDst | Out-Null }
            Copy-Item (Join-Path $adkSchemaSrc '*.xsd') $adkSchemaDst -Force
        }
        Write-Host "[build:$stageName] OK schemas"
    } else {
        Write-Host "[build:$stageName] WARN schemas not found: $schemaSrc"
    }

    # ---- appx helper toolset: copy THIS stage's ADK i386 next to the helper exe ----
    $appxDst = Join-Path $outDir 'appx'
    if (Test-Path $StageInfo.Adk) {
        if (-not (Test-Path $appxDst)) { New-Item -ItemType Directory -Path $appxDst | Out-Null }
        Copy-Item (Join-Path $StageInfo.Adk '*') $appxDst -Recurse -Force
        $helperSrc = Join-Path $proj ("appx\" + $StageInfo.Helper)
        $helperOut = Join-Path $appxDst 'OcidAppX.exe'
        $refs = @('appximaging.dll', 'featureapi.dll', 'toolscommon.dll', 'pkgtoolbox.dll', 'imagecommon.dll', 'pkgcommonmanaged.dll')
        $refArgs = ($refs | Where-Object { Test-Path (Join-Path $appxDst $_) } | ForEach-Object { "/r:`"$(Join-Path $appxDst $_)`"" }) -join ' '
        $helperLine = "`"$csc`" /nologo /platform:x86 /target:exe /optimize+ /out:`"$helperOut`" $refArgs `"$helperSrc`""
        try {
            Invoke-Batch "$stageName.appxhelper" $objDir @($helperLine)
            Write-Host "[build:$stageName] OK appx\OcidAppX.exe (against $($StageInfo.Adk))"
        } catch {
            Write-Host "[build:$stageName] WARN appx helper failed (stage ADK API differs?): $($_.Exception.Message)"
        }
    } else {
        Write-Host "[build:$stageName] WARN ADK i386 not found: $($StageInfo.Adk)"
    }
}

$selected = @()
if ($Stage -eq 'all') { $selected = $stages }
else {
    $selected = $stages | Where-Object { $_.Name -eq $Stage }
    if (-not $selected) { throw "unknown -Stage '$Stage' (fe / rs5 / all)" }
}

foreach ($item in $selected) { Build-OneStage -StageInfo $item }

# ---- dispatcher (ocid_imagegen.all.exe): forwards to the right stage exe ----
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }
$dispObjDir = Join-Path $objRoot 'dispatch'
$dispObj = Join-Path $dispObjDir 'dispatch.obj'
Invoke-Batch 'dispatch' $dispObjDir @("cl /nologo /c /EHsc /std:c++17 /O2 /MD /utf-8 /DUNICODE /D_UNICODE /Fo`"$dispObj`" `"$(Join-Path $proj 'tools\dispatch.cpp')`"")
$dispExe = Join-Path $out 'ocid_imagegen.all.exe'
Invoke-Batch 'dispatch-link' $dispObjDir @("link /nologo /OUT:`"$dispExe`" `"$dispObj`" advapi32.lib")
Write-Host '[build] OK ocid_imagegen.all.exe'

Write-Host '[build] done'
