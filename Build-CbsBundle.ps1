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
    [string]$NSudoBinaries = 'D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release',
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

# ── 4. 结果 ──
$files = Get-ChildItem $cbsDir -Recurse -File
$sizeMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "CBS 文件夹就绪：$cbsDir"
Write-Host "  $($files.Count) 个文件 / $sizeMb MB"
Write-Host "  CbsxCli.exe        : $(Test-Path (Join-Path $cbsDir 'CbsxCli.exe'))"
Write-Host "  NSudoAPI.dll       : $($placed -join ', ')"
Write-Host "  自包含（免装 .NET）: $(Test-Path (Join-Path $cbsDir 'coreclr.dll'))"
