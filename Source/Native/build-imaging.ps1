# build-imaging.ps1 —— 构建 OcidImaging 工程（ocid_imagegen.exe + *.wcos.dll）
#
# 为什么要独立脚本（不复用 build-servicing.ps1）：
#   build-servicing.ps1 以 OcidServicing 为根，输出目录/obj 命名都按那边的约定，
#   而 OcidImaging 是独立工程（DLL 名统一以 .wcos.dll 结尾，用户要求"按 DLL 分，
#   出错时一眼看出是哪个阶段"）。两边的对象/库互不干扰，各建各的。
#
# 用法：
#   pwsh -File build-imaging.ps1            # 增量构建
#   pwsh -File build-imaging.ps1 -Clean     # 先清空输出
#
# 约定：本脚本必须是纯 ASCII（PowerShell 5.1 无 BOM 时按 GBK 读，中文会坏）。
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'OcidImaging'
$inc = Join-Path $proj 'include'
$out = Join-Path $root 'Output\Imaging'
$obj = Join-Path $root 'Output\build_imaging'

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
    foreach ($dir in @($out, $obj)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
}
foreach ($dir in @($out, $obj)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

$cflags = @('/nologo', '/c', '/EHsc', '/std:c++17', '/W4', '/O2', '/MD', '/utf-8', '/DUNICODE', '/D_UNICODE', "/I`"$inc`"")

function Invoke-Batch {
    param([string]$name, [string[]]$lines)
    $cmdPath = Join-Path $obj "_build_$name.cmd"
    $text = "@echo off`r`ncall `"$vcvars`" >nul`r`n"
    foreach ($line in $lines) { $text += "$line || exit /b 1`r`n" }
    [System.IO.File]::WriteAllText($cmdPath, $text, [System.Text.Encoding]::ASCII)
    # 注意：一定要用 Out-Host 把 cl/link 的输出吃掉，否则它们会混进本函数的返回流，
    # 让 Build-Object 的返回值变成"cl 输出 + obj 路径"的字符串数组，最后 link 拿到一堆源码文件名
    # （曾因此报 LNK1181 无法打开输入文件 "Common.cpp"）。
    & cmd /c "`"$cmdPath`"" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build step failed: $name (exit $LASTEXITCODE)" }
}

function Build-Object {
    param([string]$name, [string]$source, [string[]]$extra = @())
    $leaf = [System.IO.Path]::GetFileNameWithoutExtension($source)
    $objFile = Join-Path $obj "$leaf.$name.obj"
    $src = Join-Path $proj $source
    $args = @("cl $($cflags -join ' ') $($extra -join ' ') /Fo`"$objFile`" `"$src`"")
    Invoke-Batch $name $args
    return $objFile
}

function Build-Dll {
    param([string]$name, [string]$leafName, [string[]]$objects, [string[]]$extraLibs = @())
    $dll = Join-Path $out "$leafName.wcos.dll"
    $lib = Join-Path $out "$leafName.wcos.lib"
    $objs = ($objects | ForEach-Object { "`"$_`"" }) -join ' '
    $libs = ($extraLibs -join ' ')
    $lines = @("link /nologo /DLL /OUT:`"$dll`" /IMPLIB:`"$lib`" $objs $libs")
    Invoke-Batch $name $lines
    Write-Host "[build] OK $leafName.wcos.dll"
}

# ---- 1) common（控制台/日志/文件/XML + XSD 校验）----
$commonObjs = @()
$commonObjs += Build-Object 'common' 'src\Common.cpp' @('/DOCID_COMMON_BUILD')
$commonObjs += Build-Object 'common' 'src\Validate.cpp' @('/DOCID_COMMON_BUILD')
Build-Dll 'common' 'ocid_common' $commonObjs @('ole32.lib', 'oleaut32.lib')

# ---- 2) plan（选包）----
$planObjs = @()
$planObjs += Build-Object 'plan' 'src\Plan.cpp' @('/DOCID_PLAN_BUILD')
$commonLib = Join-Path $out 'ocid_common.wcos.lib'
Build-Dll 'plan' 'ocid_plan' $planObjs @("`"$commonLib`"")

# ---- 3) cab（.cab 读取：CFHEADER/CFFOLDER/CFFILE/CFDATA + NONE/MSZIP）----
$cabObjs = @()
$cabObjs += Build-Object 'cab' 'src\Cab.cpp' @('/DOCID_CAB_BUILD')
$cabLib = Join-Path $out 'ocid_cab.wcos.lib'
Build-Dll 'cab' 'ocid_cab' $cabObjs @("`"$commonLib`"")

# ---- 4) exe ----
$exeObjs = @()
$exeObjs += Build-Object 'imagegen' 'tools\imagegen.cpp'
$exe = Join-Path $out 'ocid_imagegen.exe'
$planLib = Join-Path $out 'ocid_plan.wcos.lib'
$exeObjArgs = ($exeObjs | ForEach-Object { '"' + $_ + '"' }) -join ' '
$linkLines = @("link /nologo /OUT:`"$exe`" $exeObjArgs `"$planLib`" `"$cabLib`" `"$commonLib`"")
$null = Invoke-Batch 'imagegen' $linkLines
Write-Host "[build] OK ocid_imagegen.exe"

# ---- 5) Schemas（复用现成 XSD，不重写校验规则）----
$schemaSrc = Join-Path (Split-Path -Parent $root) 'Schemas'
$schemaDst = Join-Path $out 'schemas'
if (Test-Path $schemaSrc) {
    if (-not (Test-Path $schemaDst)) { New-Item -ItemType Directory -Path $schemaDst | Out-Null }
    Copy-Item (Join-Path $schemaSrc '*.xsd') $schemaDst -Force
    Write-Host "[build] OK schemas ($((Get-ChildItem $schemaDst -Filter *.xsd).Count) xsd)"
}

Write-Host '[build] done'
