<#
.SYNOPSIS
  产品线边界检查：防止 desktop 与 WCOS/Phone 两边的代码互相引用。

.DESCRIPTION
  本仓库同时承载两条产品线：

    Desktop（CBS）      普通 Windows（Client/Server SKU）的 CBS 离线服务
    WCOS / Phone        WCOS 与 Windows Phone 类系统的镜像构建（MobilePackageGen 一族）

  两者的词汇与机制完全不同，混用会写出语义错误的代码：
    · desktop 是 ICbsSession / DISM / WinSxS / servicing\Packages / Microsoft-Windows-Editions-*
    · WCOS 是 imageapp / Img2Ffu / FFU / FeatureManifest(OEMInput) / DeviceLayout / 分区映像

  本脚本按文件集合做**引用方向**检查，违规即非零退出，便于接进 CI 或提交前钩子。

  说明：CbsBuildService 刻意**不归属任何一边** —— WCOS 的 CAB 包展开后与普通包高度相似，
  难以干净区分，故两条线共用它。脚本因此不检查它。

.NOTES
  纯静态文本检查，不做语义分析；宁可少报也不要误报。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

# 注意：param() 的默认值里取不到 $PSScriptRoot / $MyInvocation（PS 5.1 行为），
# 所以统一在函数体里计算。
if ([string]::IsNullOrEmpty($RepoRoot)) { $RepoRoot = $PSScriptRoot }
if ([string]::IsNullOrEmpty($RepoRoot)) { $RepoRoot = (Get-Location).Path }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

# ── desktop 独有的服务（WCOS 侧不应引用） ──
$DesktopOnly = @(
    'CbsxService', 'DismServicingService', 'ServicingRouter', 'SystemAssemblyService',
    'SxSFounderService', 'ExplodeSxSNativeService', 'FounderStackInstaller',
    'UpdatePackageService', 'RepairContentService', 'PrivilegeHelper', 'ArchRelay',
    'PureSxsService', 'CbsToRegService'
)

# ── 两条线**共用**的组件（不参与任何一侧的引用禁止） ──
#   判定标准：纯算法/格式知识，与"系统怎么被服务"无关。两侧镜像都具备同样的形态。
#     WinSxSHashCalculator  SxS 组件哈希与文件夹命名算法 —— 两侧镜像都有 WinSxS
#     ManifestDecompressor  DCM 清单解压（算法）
$Shared = @(
    'WinSxSHashCalculator', 'ManifestDecompressor'
)

# ── WCOS 独有的词汇（desktop 侧不应引用） ──
$WcosOnly = @(
    'DeviceLayout', 'OemInput', 'OEMInput', 'FeatureManifest', 'FMService',
    'Img2Ffu', 'FullFlashUpdate', 'FFUFactory'
)

# ── 两条线各自的文件集合（目录前缀） ──
$WcosDirs = @(
    'ViewModels\SystemReconstruct', 'ViewModels\OEMInput', 'ViewModels\DeviceLayout',
    'ViewModels\FFUBrowser',
    'Views\SystemReconstruct', 'Views\OEMInput', 'Views\DeviceLayout', 'Views\FFUBrowser',
    'Services\DeviceLayoutBuilderService.cs', 'Services\DeviceLayoutRuleChecker.cs',
    'Services\DeviceLayoutService.cs', 'Services\DeviceLayoutTemplates.cs',
    'Services\OEMInputService.cs', 'Services\OemInputEraRulesService.cs',
    'Services\OemInputSchemaFamilyService.cs', 'Services\OemInputSerializer.cs',
    'Services\FMService.cs', 'Services\FirmwareService.cs'
)

# desktop 侧的页面（只列明确属于 desktop 的）
$DesktopDirs = @(
    'ViewModels\Cbs\CbsBuildViewModel.cs',
    'ViewModels\SxsViewModel.cs',
    'Views\Cbs', 'Views\SxsView.xaml'
)

function Get-SourceFiles([string]$rel) {
    $full = Join-Path $RepoRoot $rel
    if (Test-Path -LiteralPath $full -PathType Container) {
        Get-ChildItem -LiteralPath $full -Recurse -File -Include '*.cs', '*.xaml' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }
    } elseif (Test-Path -LiteralPath $full -PathType Leaf) {
        Get-Item -LiteralPath $full
    }
}

$violations = New-Object System.Collections.Generic.List[string]

# ① WCOS 文件不得引用 desktop 独有服务
foreach ($d in $WcosDirs) {
    foreach ($f in Get-SourceFiles $d) {
        $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }
        foreach ($svc in $DesktopOnly) {
            if ($text -match [regex]::Escape($svc)) {
                $rel = $f.FullName.Substring($RepoRoot.Length).TrimStart('\')
                $violations.Add("WCOS 侧引用了 desktop 服务 [$svc] -> $rel")
            }
        }
    }
}

# ② desktop 页面不得引用 WCOS 独有词汇
foreach ($d in $DesktopDirs) {
    foreach ($f in Get-SourceFiles $d) {
        $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }
        foreach ($w in $WcosOnly) {
            # 允许出现在"说明为什么没有它"的注释里：要求该行同时含"WCOS"或"不在此"或"属 WCOS"
            foreach ($line in ($text -split "`r?`n")) {
                if ($line -match [regex]::Escape($w)) {
                    if ($line -match 'WCOS|不在此|已撤|不在本页|停用|注释') { continue }
                    $rel = $f.FullName.Substring($RepoRoot.Length).TrimStart('\')
                    $violations.Add("desktop 侧引用了 WCOS 词汇 [$w] -> ${rel}: $($line.Trim())")
                }
            }
        }
    }
}

Write-Host "产品线边界检查（$RepoRoot）" -ForegroundColor Cyan
Write-Host ("  WCOS 文件集合 : {0} 个文件/前缀" -f $WcosDirs.Count)
Write-Host ("  desktop 页面  : {0} 个文件/前缀" -f $DesktopDirs.Count)
Write-Host ("  共用组件      : {0} 个（{1}）" -f $Shared.Count, ($Shared -join ", "))
Write-Host ""

if ($violations.Count -eq 0) {
    Write-Host "  ✅ 未发现跨产品线引用" -ForegroundColor Green
    exit 0
}

Write-Host ("  ❌ 发现 {0} 处跨产品线引用：" -f $violations.Count) -ForegroundColor Red
foreach ($v in $violations) { Write-Host "     $v" -ForegroundColor Red }
Write-Host ""
Write-Host "  处理方式：把该能力移到它所属那一侧的页面/服务；" -ForegroundColor Yellow
Write-Host "  若确属共用（如 CbsBuildService），请把它从本脚本的检查集合中排除并在此注明理由。" -ForegroundColor Yellow
exit 1
