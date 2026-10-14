using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>
    /// SxS / CBS 工具页（desktop 线）。
    ///
    /// 【设计原则】本页**不依赖任何外部可执行文件或脚本**。
    /// 原来随应用分发的 `ThirdParty\SxS`（cbsx.exe / sxsfounder.exe / puresxs.py /
    /// havesxs.py / cbss.cmd / prepare.cmd / captureimg.cmd / finishimg.cmd /
    /// imagestate.reg / skuassembly.txt）**已全部删除**，能力逐项由本程序内的 C# 实现承担：
    ///
    ///   cbsx.exe          → <see cref="CbsxService"/>
    ///   sxsfounder.exe    → <see cref="SxSFounderService"/>
    ///   puresxs/havesxs   → <see cref="PureSxsService"/> + <see cref="WinSxSHashCalculator"/>
    ///   cbss/prepare/captureimg/finishimg → <see cref="ExplodeSxSNativeService"/>
    ///   imagestate.reg    → <see cref="ExplodeSxSNativeService.FinishImageStep"/>
    ///   skuassembly.txt   → <see cref="SkuPlanBuilder"/>
    ///   base.manifest     → <see cref="ManifestDecompressor"/>
    ///
    /// 【强制约束】凡会写镜像的操作，目标必须是【挂载的 VHD/VHDX】上的镜像
    /// （<see cref="CbsSafety"/> 门禁），且所有写入操作默认「试运行」。
    ///
    /// 【产品线】本页属 **desktop**。DeviceLayout / FeatureManifest / OEMInput / FFU
    /// 均属 WCOS 侧，不在此页出现。见 PRODUCT_LINES.md。
    /// </summary>
    public partial class SxsViewModel : ObservableObject
    {
        TaskLogSink? _taskSink;
        Views.TaskLogWindow? _taskWin;

        [ObservableProperty] private string _status = "就绪。";

        // ════════════════════════════════════════════════════════════════
        //  一、目标镜像与路由判定
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _targetWinDir = string.Empty;

        [ObservableProperty] private string _routingStrip =
            "填写目标镜像的 Windows 目录后点『判定路由』。";

        [ObservableProperty] private bool _isBusy;

        [RelayCommand]
        private void BrowseTargetWinDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择目标镜像根（其下应有 Windows 目录；必须在挂载的 VHD/VHDX 上）" };
            if (dlg.ShowDialog() != true) return;
            string root = dlg.FolderName;
            TargetWinDir = Directory.Exists(Path.Combine(root, "Windows"))
                ? Path.Combine(root, "Windows")
                : root;
        }

        /// <summary>判定架构、世代与通道；结论一句话呈现。</summary>
        [RelayCommand]
        private async Task CheckRouteAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetWinDir)) { RoutingStrip = "请先填写目标镜像的 Windows 目录。"; return; }
            IsBusy = true;
            try
            {
                var result = await Task.Run(() =>
                {
                    var d = ServicingRouter.Decide(TargetWinDir);
                    var (imgArch, archEv) = ArchRelay.DetectImageArchitecture(TargetWinDir);
                    var me = ArchRelay.CurrentProcessArch();
                    bool relay = imgArch != ArchRelay.Arch.Unknown && imgArch != me;
                    string strip = $"镜像架构 {ArchRelay.ToToken(imgArch)} · 进程 {ArchRelay.ToToken(me)}"
                                 + $" · 世代 {(d.Scheme ?? "未知")} · 通道 【{d.Channel}】"
                                 + (relay ? "（架构不符 → 由 DISM 代劳）" : "");
                    string detail = strip + "\n" + d.Reason + "\n镜像架构判定依据：" + archEv;
                    return (strip, detail);
                });
                RoutingStrip = result.strip;
                Status = "路由判定完成。";
            }
            catch (Exception ex) { RoutingStrip = "判定失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  二、原生流程（铺底 → 装 SKU → DISM → 收尾）
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _nativeArch = "amd64";
        [ObservableProperty] private string _nativeLang = "en-US";
        [ObservableProperty] private string _nativeLanguagePack = string.Empty;
        [ObservableProperty] private string _nativeSkuAssemblyFile = string.Empty;
        [ObservableProperty] private bool _nativeDryRun = true;
        [ObservableProperty] private string _nativeSummary =
            "全流程由本程序完成：WCP 铺底 → CBS 装 SKU → DISM 步骤 → 收尾写 State.ini 与 imagestate。\n"
            + "目标必须是【挂载的 VHD/VHDX】上的镜像；默认『试运行』，不改动任何东西。";

        [RelayCommand]
        private void BrowseNativeLanguagePack()
        {
            var dlg = new OpenFileDialog { Title = "选择语言包 .mum", Filter = "包清单 (*.mum)|*.mum|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) NativeLanguagePack = dlg.FileName;
        }

        [RelayCommand]
        private void BrowseNativeSkuFile()
        {
            var dlg = new OpenFileDialog { Title = "选择 SKU 组装清单（skuassembly.txt）", Filter = "清单 (*.txt)|*.txt|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) NativeSkuAssemblyFile = dlg.FileName;
        }

        [RelayCommand]
        private async Task RunNativePreflightAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetWinDir)) { NativeSummary = "请先填写目标镜像的 Windows 目录。"; return; }
            IsBusy = true;
            try
            {
                var pf = await Task.Run(() => ExplodeSxSNativeService.Preflight(MakeNativeConfig(dryRun: true)));
                var sb = new System.Text.StringBuilder();
                foreach (var c in pf.checks) sb.AppendLine("✓ " + c);
                foreach (var b in pf.blockers) sb.AppendLine("✗ " + b);
                NativeSummary = sb.ToString().TrimEnd();
                Status = pf.ready ? "预检通过，可执行。" : "预检存在阻断项。";
            }
            catch (Exception ex) { NativeSummary = "预检异常：" + ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task RunNativeAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetWinDir)) { Status = "请先填写目标镜像的 Windows 目录。"; return; }
            if (!NativeDryRun &&
                MessageBox.Show("将以真实模式执行，**会写入目标镜像**。\n\n确认目标位于挂载的 VHD/VHDX 上？",
                    "原生流程确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            NativeSummary = NativeDryRun ? "试运行中…" : "执行中…（不要关闭程序）";
            var sink = EnsureTaskLog("SxS / CBS 原生流程");
            try
            {
                var res = await ExplodeSxSNativeService.RunAsync(MakeNativeConfig(NativeDryRun), m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                foreach (var st in res.Steps)
                    sb.AppendLine((st.Ok ? "✓ " : "✗ ") + st.Step + "：" + st.Detail);
                if (res.Error.Length > 0) sb.AppendLine("错误：" + res.Error);
                if (res.RebootRequired) sb.AppendLine("CBS 提示需要重启。");
                NativeSummary = sb.ToString().TrimEnd();
                Status = res.Success ? "原生流程完成。" : "原生流程失败：" + res.Error;
            }
            catch (Exception ex) { NativeSummary = "异常：" + ex.Message; }
            finally { sink.MarkFinished(); IsBusy = false; }
        }

        ExplodeSxSNativeService.Config MakeNativeConfig(bool dryRun) => new()
        {
            WinDir = TargetWinDir.Trim(),
            ImageRoot = string.IsNullOrWhiteSpace(TargetWinDir) ? string.Empty
                      : ExplodeSxSNativeService.DeriveImageRoot(TargetWinDir.Trim()),
            Arch = string.IsNullOrWhiteSpace(NativeArch) ? "amd64" : NativeArch.Trim(),
            Lang = string.IsNullOrWhiteSpace(NativeLang) ? "en-US" : NativeLang.Trim(),
            LanguagePackMum = NativeLanguagePack?.Trim() ?? string.Empty,
            SkuAssemblyFile = NativeSkuAssemblyFile?.Trim() ?? string.Empty,
            DryRun = dryRun
        };

        // ════════════════════════════════════════════════════════════════
        //  三、目标 SKU（edition）—— 只装这一个
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _targetSku = string.Empty;
        [ObservableProperty] private string _skuArchFilter = string.Empty;
        [ObservableProperty] private string _availableSkus = string.Empty;
        [ObservableProperty] private string _skuSummary =
            "SKU 由装哪个 Microsoft-Windows-Editions-* 决定，**只装一个**。"
            + "本页以该 edition 为唯一种子算依赖闭包并生成组装清单。";

        [RelayCommand]
        private async Task DiscoverSkusAsync()
        {
            try
            {
                var dirs = SplitPaths(SkuSourceDirs);
                if (dirs.Count == 0) { AvailableSkus = "请先在『系统组装』区填写 SxS 内容目录。"; return; }
                IsBusy = true;
                var eds = await Task.Run(() => SkuPlanBuilder.DiscoverEditions(dirs,
                    string.IsNullOrWhiteSpace(SkuArchFilter) ? null : SkuArchFilter.Trim()));
                AvailableSkus = SkuPlanBuilder.RenderEditions(eds);
                if (string.IsNullOrWhiteSpace(TargetSku) && eds.Count > 0)
                    TargetSku = (eds.FirstOrDefault(e => e.Name.Equals("professional", StringComparison.OrdinalIgnoreCase))
                                 ?? eds[0]).Name;
                Status = $"发现 {eds.Count} 个可用 SKU";
            }
            catch (Exception ex) { AvailableSkus = "扫描异常：" + ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task BuildSkuPlanAsync()
        {
            var dirs = SplitPaths(SkuSourceDirs);
            if (dirs.Count == 0) { SkuSummary = "请先在『系统组装』区填写 SxS 内容目录。"; return; }
            if (string.IsNullOrWhiteSpace(TargetSku)) { SkuSummary = "请先指定目标 SKU（或点『扫描可用 SKU』）。"; return; }

            IsBusy = true;
            try
            {
                var plan = await Task.Run(() => SkuPlanBuilder.Build(dirs, TargetSku,
                    string.IsNullOrWhiteSpace(SkuArchFilter) ? null : SkuArchFilter.Trim()));
                var sb = new System.Text.StringBuilder();
                if (!plan.Success) { sb.AppendLine("✗ " + plan.Error); SkuSummary = sb.ToString().TrimEnd(); return; }

                string outPath = Path.Combine(Path.GetTempPath(),
                    "ocid_sku_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(outPath, plan.ToSkuAssemblyText());
                NativeSkuAssemblyFile = outPath;      // 直接接到原生流程

                sb.AppendLine(plan.Summary());
                sb.AppendLine($"已写出组装清单：{outPath}");
                sb.AppendLine("（已自动填入『原生流程』的 SKU 清单，可直接执行）");
                sb.AppendLine();
                sb.AppendLine("安装顺序（前 30）：");
                int i = 0;
                foreach (var k in plan.Order)
                {
                    if (++i > 30) { sb.AppendLine($"  …（还有 {plan.Order.Count - 30} 项）"); break; }
                    sb.AppendLine($"  {i,4}. {k}");
                }
                if (plan.Missing.Count > 0) sb.AppendLine().AppendLine("缺失依赖：" + string.Join(", ", plan.Missing.Take(15)));
                if (plan.Cycles.Count > 0) sb.AppendLine().AppendLine("检测到环：" + string.Join(" | ", plan.Cycles.Take(5)));
                SkuSummary = sb.ToString().TrimEnd();
                Status = plan.Summary();
            }
            catch (Exception ex) { SkuSummary = "异常：" + ex.Message; }
            finally { IsBusy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  四、系统组装（依赖图 → 计划 → 执行）
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _skuSourceDirs = string.Empty;
        [ObservableProperty] private bool _assemblyPlanOnly = true;
        [ObservableProperty] private string _assemblySummary =
            "从 SxS 内容出发：算清组件之间的依赖关系，排出安装顺序，再交给 CBS 通道执行。\n"
            + "源目录可填多个，用分号分隔。目标镜像由上方『目标镜像』提供。";

        [RelayCommand]
        private async Task ShowGraphAsync()
        {
            var dirs = SplitPaths(SkuSourceDirs);
            if (dirs.Count == 0) { AssemblySummary = "请填写至少一个 SxS 内容目录。"; return; }
            IsBusy = true;
            try
            {
                var g = await Task.Run(() => SystemAssemblyService.BuildGraph(dirs));
                AssemblySummary = SystemAssemblyService.RenderGraph(g, 60);
                Status = $"依赖图：{g.Nodes.Count} 个节点，缺失依赖 {g.MissingDependencies}";
            }
            catch (Exception ex) { AssemblySummary = "建图异常：" + ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task ShowAssemblyPlanAsync()
        {
            var dirs = SplitPaths(SkuSourceDirs);
            if (dirs.Count == 0) { AssemblySummary = "请填写至少一个 SxS 内容目录。"; return; }
            IsBusy = true;
            try
            {
                var plan = await Task.Run(() =>
                {
                    var graph = SystemAssemblyService.BuildGraph(dirs);
                    var seeds = graph.Nodes.Values
                        .Where(n => n.Kind == SystemAssemblyService.NodeKind.Package)
                        .Select(n => n.Key).ToList();
                    return SystemAssemblyService.PlanFrom(graph, seeds);
                });
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("组装计划：" + plan.Summary()).AppendLine();
                sb.AppendLine("安装顺序（前 40）：");
                int i = 0;
                foreach (var k in plan.Order)
                {
                    if (++i > 40) { sb.AppendLine($"  …（还有 {plan.Order.Count - 40} 项）"); break; }
                    sb.AppendLine($"  {i,4}. {k}");
                }
                if (plan.Missing.Count > 0) sb.AppendLine().AppendLine("缺失依赖：" + string.Join(", ", plan.Missing.Take(20)));
                if (plan.Cycles.Count > 0) sb.AppendLine().AppendLine("检测到环：" + string.Join(" | ", plan.Cycles.Take(5)));
                AssemblySummary = sb.ToString().TrimEnd();
                Status = "组装计划：" + plan.Summary();
            }
            catch (Exception ex) { AssemblySummary = "计划异常：" + ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task RunAssemblyAsync()
        {
            var dirs = SplitPaths(SkuSourceDirs);
            if (dirs.Count == 0) { AssemblySummary = "请填写至少一个 SxS 内容目录。"; return; }
            if (string.IsNullOrWhiteSpace(TargetWinDir)) { AssemblySummary = "请先填写目标镜像的 Windows 目录。"; return; }
            if (!AssemblyPlanOnly &&
                MessageBox.Show("将真实写入目标镜像。\n\n确认目标位于挂载的 VHD/VHDX 上？",
                    "组装确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            var sink = EnsureTaskLog("SxS 系统组装");
            try
            {
                var cfg = new SystemAssemblyService.Config
                {
                    SourceDirs = dirs,
                    WinDir = TargetWinDir.Trim(),
                    SkuAssemblyFile = NativeSkuAssemblyFile?.Trim() ?? string.Empty,
                    PlanOnly = AssemblyPlanOnly
                };
                var res = await SystemAssemblyService.AssembleAsync(cfg, m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                if (res.Plan != null) sb.AppendLine(res.Plan.Summary());
                if (res.Channel != null) sb.AppendLine(res.Channel.Reason);
                if (res.Error.Length > 0) sb.AppendLine("错误：" + res.Error);
                AssemblySummary = sb.ToString().TrimEnd();
                Status = res.Success ? "组装流程完成。" : "组装失败：" + res.Error;
            }
            catch (Exception ex) { AssemblySummary = "异常：" + ex.Message; }
            finally { sink.MarkFinished(); IsBusy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  五、pureSxS 导出（C# 重写，不需要 Python）
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _pureSourceMum = string.Empty;
        [ObservableProperty] private string _pureOutputDir = string.Empty;
        [ObservableProperty] private string _pureSummary = "从一个 .mum 出发，导出它的依赖闭包（包 / 组件 / 程序集）。";

        [RelayCommand]
        private void BrowsePureSource()
        {
            var dlg = new OpenFileDialog { Title = "选择源 .mum", Filter = "包清单 (*.mum)|*.mum|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) PureSourceMum = dlg.FileName;
        }

        [RelayCommand]
        private void BrowsePureOutput()
        {
            var dlg = new OpenFolderDialog { Title = "选择 pureSxS 导出目录" };
            if (dlg.ShowDialog() == true) PureOutputDir = dlg.FolderName;
        }

        [RelayCommand]
        private async Task RunPureExportAsync()
        {
            if (string.IsNullOrWhiteSpace(PureSourceMum) || !File.Exists(PureSourceMum)
                || string.IsNullOrWhiteSpace(PureOutputDir))
            {
                MessageBox.Show("请选择有效的源 .mum 与输出目录。", "pureSxS",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            IsBusy = true;
            var sink = EnsureTaskLog("pureSxS 导出");
            try
            {
                var res = await PureSxsService.ExportAsync(PureSourceMum, PureOutputDir, m => sink.Write(m));
                PureSummary = res.Success
                    ? $"✓ 完成：包 {res.PackagesCopied}、组件 {res.ComponentsCopied}、程序集 {res.AssembliesCopied}、缺失 {res.Missing} → {PureOutputDir}"
                    : "✗ 失败：" + res.Error;
                Status = res.Success ? "pureSxS 导出完成。" : "pureSxS 导出失败。";
            }
            catch (Exception ex) { PureSummary = "异常：" + ex.Message; }
            finally { sink.MarkFinished(); IsBusy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  六、更新包 / Repair Content 解包
        // ════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _updatePackagePaths = string.Empty;
        [ObservableProperty] private string _unpackOutDir = string.Empty;
        [ObservableProperty] private string _unpackBaseDir = string.Empty;
        [ObservableProperty] private string _repairContentDir = string.Empty;
        [ObservableProperty] private bool _unpackListOnly = true;
        [ObservableProperty] private string _unpackSummary =
            "更新包 = 配对 CAB（描述）+ PSF（PA30 载荷）；还原需要 delta 源(base)目录。\n"
            + "Repair Content 有硬性顺序：含 neutral-mergedcomponents 的包必须先单独解包，解完删除其 PSF/CAB，再解其余。";

        [RelayCommand]
        private void PickUpdatePackages()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择更新包（.cab / .psm / .wim，可多选）",
                Filter = "更新包 (*.cab;*.psm;*.wim)|*.cab;*.psm;*.wim|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true) UpdatePackagePaths = string.Join(";", dlg.FileNames);
        }

        [RelayCommand]
        private void PickUnpackOutDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择解包输出目录" };
            if (dlg.ShowDialog() == true) UnpackOutDir = dlg.FolderName;
        }

        [RelayCommand]
        private void PickUnpackBaseDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择 delta 源(base)目录（例：已挂载的同版本镜像的 Windows 目录）" };
            if (dlg.ShowDialog() == true) UnpackBaseDir = dlg.FolderName;
        }

        [RelayCommand]
        private void PickRepairContentDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择 Repair Content 目录（含 cab/psf）" };
            if (dlg.ShowDialog() == true) RepairContentDir = dlg.FolderName;
        }

        [RelayCommand]
        private async Task UnpackUpdatePackagesAsync()
        {
            var paths = SplitPaths(UpdatePackagePaths);
            if (paths.Count == 0) { UnpackSummary = "请先选择要解包的更新包。"; return; }
            if (string.IsNullOrWhiteSpace(UnpackOutDir)) { UnpackSummary = "请先指定输出目录。"; return; }
            IsBusy = true;
            var sink = EnsureTaskLog("更新包解压");
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var p in paths)
                {
                    var cfg = new UpdatePackageService.Config
                    {
                        PackagePath = p,
                        OutputDir = UnpackOutDir,
                        ListOnly = UnpackListOnly
                    };
                    if (!string.IsNullOrWhiteSpace(UnpackBaseDir)) cfg.BaseDirs.Add(UnpackBaseDir.Trim());
                    var r = await Task.Run(() => UpdatePackageService.Extract(cfg, m => sink.Write(m)));
                    sb.AppendLine($"{Path.GetFileName(p)}：布局={r.Detected} 解出={r.FilesExtracted} 跳过={r.FilesSkipped} "
                                + $"缺源={r.FilesMissingSource} 校验失败={r.HashMismatches}");
                }
                if (UnpackListOnly) sb.AppendLine("（当前为『只解析索引』，未写出文件）");
                UnpackSummary = sb.ToString().TrimEnd();
                Status = "更新包处理完成。";
            }
            catch (Exception ex) { UnpackSummary = "异常：" + ex.Message; }
            finally { sink.MarkFinished(); IsBusy = false; }
        }

        [RelayCommand]
        private async Task UnpackRepairContentAsync()
        {
            if (!Directory.Exists(RepairContentDir)) { UnpackSummary = "请先选择有效的 Repair Content 目录。"; return; }
            if (string.IsNullOrWhiteSpace(UnpackOutDir)) { UnpackSummary = "请先指定输出目录。"; return; }
            IsBusy = true;
            var sink = EnsureTaskLog("Repair Content 解包");
            try
            {
                var cfg = new RepairContentService.Config
                {
                    SourceDir = RepairContentDir,
                    OutputDir = UnpackOutDir,
                    ClassifyOnly = UnpackListOnly
                };
                if (!string.IsNullOrWhiteSpace(UnpackBaseDir)) cfg.BaseDirs.Add(UnpackBaseDir.Trim());
                var r = await Task.Run(() => RepairContentService.Extract(cfg, m => sink.Write(m)));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(r.Summary());
                if (r.LanguageCatNotes.Count > 0)
                {
                    sb.AppendLine().AppendLine("语言安全目录检查：");
                    foreach (var n in r.LanguageCatNotes) sb.AppendLine("  " + n);
                }
                if (r.Error.Length > 0) sb.AppendLine("错误：" + r.Error);
                if (!UnpackListOnly && Directory.Exists(UnpackOutDir))
                { sb.AppendLine(); sb.AppendLine(RepairContentService.InspectOutput(UnpackOutDir)); }
                UnpackSummary = sb.ToString().TrimEnd();
                Status = r.Success ? "Repair Content 解包完成。" : "Repair Content 解包失败：" + r.Error;
            }
            catch (Exception ex) { UnpackSummary = "异常：" + ex.Message; }
            finally { sink.MarkFinished(); IsBusy = false; }
        }

        [RelayCommand]
        private void InspectStore()
        {
            string dir = !string.IsNullOrWhiteSpace(UnpackOutDir) ? UnpackOutDir : PureOutputDir;
            UnpackSummary = RepairContentService.InspectOutput(dir);
            Status = "存储检验完成。";
        }

        // ════════════════════════════════════════════════════════════════
        //  七、说明与日志
        // ════════════════════════════════════════════════════════════════

        [RelayCommand]
        private void ShowUsage()
        {
            MessageBox.Show(
                "SxS / CBS 工具（desktop 线）\n\n" +
                "本页全部功能由本程序自带实现完成，**不需要任何外部可执行文件或脚本**。\n" +
                "原来的 cbsx.exe / sxsfounder.exe / puresxs.py / havesxs.py / *.cmd 已全部移除：\n" +
                "  铺底 → SxSFounderService（WCP）\n" +
                "  装包 → CbsxService（原生 CBS 通道）\n" +
                "  跨代/跨架构 → 自动改走 DISM（dism /image 离线调度）\n" +
                "  收尾 → State.ini 与 imagestate 直接写入\n" +
                "  纯 SxS 导出 → PureSxsService\n\n" +
                "── 强制约束 ──\n" +
                "目标镜像必须位于【挂载的 VHD/VHDX】上。CBS 会挂载并改写目标镜像的注册表 hive，\n" +
                "对物理盘或宿主系统盘执行会破坏系统，程序会直接拒绝。\n" +
                "所有写入操作默认『试运行』，确认无误后再关闭。\n\n" +
                "── 建议顺序 ──\n" +
                "① 填目标镜像 → 判定路由（看清走原生还是 DISM）\n" +
                "② 填 SxS 内容目录 → 查看依赖图 / 组装计划\n" +
                "③ 选目标 SKU → 生成组装清单（只装这一个 edition）\n" +
                "④ 预检 → 试运行 → 真实执行",
                "SxS / CBS 工具说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        TaskLogSink EnsureTaskLog(string title)
        {
            if (_taskSink == null || _taskWin is { IsLoaded: false })
            {
                _taskSink = new TaskLogSink(title);
                _taskWin = new Views.TaskLogWindow(_taskSink) { Owner = Application.Current?.MainWindow };
                _taskWin.Show();
            }
            return _taskSink;
        }


        static List<string> SplitPaths(string? text)
            => (text ?? string.Empty)
               .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(x => x.Trim().Trim('"'))
               .Where(x => x.Length > 0)
               .ToList();
    }
}
