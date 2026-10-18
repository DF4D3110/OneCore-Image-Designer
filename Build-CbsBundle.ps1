# Build-CbsBundle.ps1 —— 生成与 OCID 同级的 `CBS` 文件夹（控制台程序 + 依赖）
#
# 为什么要这一步：`bin\Release\` 是构建产物，`dotnet clean` / 重新构建会把它清掉。
# 这个脚本把「CBS 控制台 + 提权 DLL」重新组装回去，所以随时可重建。
#
# 布局：
#   <OCID 输出>\OneCore Image Designer.exe     ← OCID（不动）
#   <OCID 输出>\CBS\CbsxCli.exe                ← 控制台（自包含，免装 .NET）
#   <OCID 输出>\CBS\x64\NSudoAPI.dll
#   <OCID 输出>\CBS\Win32\NSudoAPI.dll
#
# 用法：  powershell -ExecutionPolicy Bypass -File Build-CbsBundle.ps1
#         powershell -ExecutionPolicy Bypass -File Build-CbsBundle.ps1 -Configuration Debug -NoBuild

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # NSudo 构建产物根目录（其下应有 x64\ / Win32\ / ARM64\ 三个子目录，各含 NSudoAPI.dll）。
    # 默认读环境变量 OCID_NSUDO_BINARIES，未设置则用脚本同级的 NSudo 目录；
    # 都不存在时下面只会告警并跳过放置（提权退化为不做提权），不写死任何开发机路径。
    [string]$NSudoBinaries = $(if ($env:OCID_NSUDO_BINARIES) { $env:OCID_NSUDO_BINARIES } else { Join-Path $PSScriptRoot 'NSudo' }),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

$cliProj = Join-Path $repo 'MobilePackageGen\CbsxCli\CbsxCli.csproj'
$ocidDir = Join-Path $repo "bin\$Configuration\net8.0-windows"
$cliDir  = Join-Path $repo "MobilePackageGen\CbsxCli\bin\$Configuration\net8.0\win-x86"
$cbsDir  = Join-Path $ocidDir 'CBS'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ── 1. 构建（CbsxCli 必须是 win-x86：arm 镜像里带的是 x86 服务栈，x86 进程才能加载它） ──
if (-not $NoBuild) {
    Step "构建 CbsxCli (win-x86, 自包含)"
    & dotnet publish $cliProj -c $Configuration -r win-x86 --self-contained true --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "CbsxCli 构建失败" }
}

if (-not (Test-Path $cliDir)) { throw "找不到 CbsxCli 输出：$cliDir（先构建，或去掉 -NoBuild）" }
if (-not (Test-Path $ocidDir)) { throw "找不到 OCID 输出：$ocidDir（先构建 OCID）" }

# ── 2. 组装 CBS 文件夹（与 OCID 同级） ──
Step "组装 $cbsDir"
if (Test-Path $cbsDir) { Remove-Item $cbsDir -Recurse -Force }
New-Item -ItemType Directory -Path $cbsDir -Force | Out-Null
Copy-Item (Join-Path $cliDir '*') $cbsDir -Recurse -Force

# ── 3. 提权 DLL：ElevationHelper 在 <BaseDirectory>\<x64|Win32|ARM64>\ 里找 ──
Step "放置 NSudoAPI.dll"
$placed = @()
foreach ($arch in @('x64', 'Win32')) {
    $src = Join-Path $NSudoBinaries "$arch\NSudoAPI.dll"
    if (-not (Test-Path $src)) { Write-Warning "  跳过 $arch：找不到 $src"; continue }
    $dstDir = Join-Path $cbsDir $arch
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    Copy-Item $src (Join-Path $dstDir 'NSudoAPI.dll') -Force
    $placed += "$arch ($([int]((Get-Item $src).Length / 1KB)) KB)"
}
if ($placed.Count -eq 0) { Write-Warning '  一个 NSudoAPI.dll 都没放进去 —— 提权会退化为不做提权' }

# ── 3.5. 注册表截获钩子 OcidRegHook.dll（原生 x86）──
#
# 它是给 wcp.dll / CbsCore.dll 等的导入表打补丁用的原生 DLL，
# CbsxService.TryInstallRegHook 会在 <BaseDirectory>\ 和 <BaseDirectory>\<x64|Win32>\ 里找它。
#
# 【为什么要放进正式脚本】此前它是我手工 cl 编出来的 —— 换台机器、或清一次生成就没了，
# 于是"程序里明明有这段逻辑、运行时却悄悄跳过"。写进脚本才可复现。
#
# 【两个必须记住的编译要点】
#   · .cpp 带 UTF-8 BOM 或加 /utf-8 —— 否则 cl 按代码页 936 读中文注释，乱码破坏语法
#   · 需要 .def 文件 —— __stdcall + __declspec(dllexport) 导出的是修饰名
#     (_OcidRegHook_Install@12)，而调用方按未修饰名查，不用 .def 就找不到
Step "构建 OcidRegHook.dll (原生 x86)"
$hookSrcDir = Join-Path $repo 'Source\Native\OcidRegHook'
$hookOutDir = Join-Path $repo 'Source\Native\Output\x86'
$hookDll    = Join-Path $hookOutDir 'OcidRegHook.dll'
$hookBuilt  = $false
if (Test-Path (Join-Path $hookSrcDir 'OcidRegHook.cpp')) {
    New-Item -ItemType Directory -Path $hookOutDir -Force | Out-Null
    $vcvars = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'D:\Program Files\Microsoft Visual Studio' `
                          -Recurse -File -Filter 'vcvars32.bat' -ErrorAction SilentlyContinue -Depth 6 |
              Select-Object -First 1 -ExpandProperty FullName
    if ($vcvars) {
        $def = Join-Path $hookSrcDir 'OcidRegHook.def'
        $cl  = "call `"$vcvars`" >nul 2>&1 && cd /d `"$hookSrcDir`" && " +
               "cl /nologo /LD /O2 /W3 /EHsc /utf-8 /DUNICODE /D_UNICODE OcidRegHook.cpp " +
               "/Fe:`"$hookDll`" /Fo:`"$hookOutDir\OcidRegHook.obj`" " +
               "/link /MACHINE:X86 /DEF:`"$def`" ntdll.lib kernel32.lib shlwapi.lib"
        & cmd /c $cl 2>&1 | Select-String -Pattern 'error|错误' | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
        $hookBuilt = Test-Path $hookDll
        if (-not $hookBuilt) { Write-Warning '  cl 编译失败（见上面的 error）' }
    } else { Write-Warning '  找不到 vcvars32.bat —— 未安装 MSVC 的 x86 工具链？' }
} else { Write-Warning "  找不到源文件：$hookSrcDir\OcidRegHook.cpp" }

if ($hookBuilt) {
    Copy-Item $hookDll (Join-Path $cbsDir 'OcidRegHook.dll') -Force
    $w32 = Join-Path $cbsDir 'Win32'
    if (Test-Path $w32) { Copy-Item $hookDll (Join-Path $w32 'OcidRegHook.dll') -Force }
} else {
    Write-Warning '  OcidRegHook.dll 未生成 —— 注册表截获不可用（其余功能不受影响）'
}

# ── 4. 结果 ──
$files = Get-ChildItem $cbsDir -Recurse -File
$sizeMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "CBS 文件夹就绪：$cbsDir"
Write-Host "  $($files.Count) 个文件 / $sizeMb MB"
Write-Host "  CbsxCli.exe        : $(Test-Path (Join-Path $cbsDir 'CbsxCli.exe'))"
Write-Host "  NSudoAPI.dll       : $($placed -join ', ')"
Write-Host "  OcidRegHook.dll    : $(Test-Path (Join-Path $cbsDir 'OcidRegHook.dll'))"
Write-Host "  自包含（免装 .NET）: $(Test-Path (Join-Path $cbsDir 'coreclr.dll'))"
