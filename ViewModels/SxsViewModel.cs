using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    public partial class SxsViewModel : ObservableObject
    {
        private readonly IntegrationToolService _service = new();
        private readonly SxsWorkflowPreflightService _preflight = new();
        private static readonly string SxsRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ThirdParty", "SxS");

        [ObservableProperty] private string _status = "正在检查随应用集成的 SxS 源码";
        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _outputDirectory = string.Empty;
        [ObservableProperty] private string _previewSummary = "选择输入镜像和输出目录后运行安全预检。";
        [ObservableProperty] private bool _previewReady;

        public ObservableCollection<ToolProbeResult> Tools { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<string> PreviewInputs { get; } = new();
        public ObservableCollection<string> PreviewOutputs { get; } = new();
        public ObservableCollection<string> PreflightChecks { get; } = new();
        public string BundledSourceDirectory => SxsRoot;

        // ── pureSxS 导出（C# 重写，无需 Python）──
        [ObservableProperty] private string _pureSourceMum = string.Empty;
        [ObservableProperty] private string _pureOutputDir = string.Empty;
        [ObservableProperty] private bool _isPureExporting;

        // ── 供原生流程复用的输入（架构 / 语言 / 语言包）；旧 explodeSxS 命令已移除，字段保留 ──
        [ObservableProperty] private string _explodeWorkDrive = "X:\\";
        [ObservableProperty] private string _explodeEdition = "Core";
        [ObservableProperty] private string _explodeArch = "amd64";
        [ObservableProperty] private string _explodeLang = "en-US";
        [ObservableProperty] private string _explodeServicingStack = string.Empty;
        [ObservableProperty] private string _explodeLanguagePack = string.Empty;
        [ObservableProperty] private bool _explodeFormatDrive;
        [ObservableProperty] private bool _explodeCaptureWim;
        [ObservableProperty] private bool _isExploding;
        [ObservableProperty] private string _explodeSummary = "预检后显示就绪状态。CBS 工具（cbsx/sxsfounder）自动从仓库 bin 或 D:\\sxs 发现。";

        private Services.TaskLogSink? _taskSink;
        private Views.TaskLogWindow? _taskWin;

        private Services.TaskLogSink EnsureTaskLog(string title)
        {
            if (_taskSink == null || _taskWin is { IsLoaded: false })
            {
                _taskSink = new Services.TaskLogSink(title);
                _taskWin = new Views.TaskLogWindow(_taskSink) { Owner = Application.Current?.MainWindow };
                _taskWin.Show();
            }
            return _taskSink;
        }

        public SxsViewModel() => DiscoverTools();

        [RelayCommand]
        private void DiscoverTools()
        {
            Tools.Clear();
            foreach (var tool in _service.DiscoverSxs(SxsRoot)) Tools.Add(tool);
            Status = $"已发现 {Tools.Count(t => t.IsAvailable)} / {Tools.Count} 个 SxS 工具";
            Log(Status);
        }

        [RelayCommand]
        private void OpenSourceDirectory()
        {
            try
            {
                if (!Directory.Exists(SxsRoot))
                    throw new DirectoryNotFoundException($"随应用集成的 SxS 源码目录不存在：{SxsRoot}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = SxsRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DirectoryNotFoundException)
            {
                MessageBox.Show(ex.Message, "无法打开 SxS 源码目录",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        private void ShowUsage()
        {
            MessageBox.Show(
                "SxS / CBS 功能独立于 UUP。\n\n" +
                "【pureSxS】处理组件来源与纯 SxS 目录；已用 C# 重写，不需要 Python。\n" +
                "【explodeSxS】把镜像的 CBS/SxS 存储展开或组装；已用 C# 重写，不需要外部脚本。\n\n" +
                "── 现在全部由本程序自己完成，不再依赖任何外部工具 ──\n" +
                "  1) 铺底          ：WCP 铺底（等价原 sxsfounder.exe）\n" +
                "  2) 装 SKU/组件   ：CBS 组件安装（等价原 cbsx.exe /b:）\n" +
                "     镜像属 6.x 体系 → 走原生 CBS 通道；10.0 及以后 → 自动改走 DISM\n" +
                "  3) 语言包/无人值守/语言配置/组件清理：DISM\n" +
                "  4) 收尾          ：写 State.ini 与 imagestate\n\n" +
                "── 强制约束 ──\n" +
                "  目标镜像必须位于【挂载的 VHD/VHDX】上。CBS 会挂载并改写目标镜像的注册表 hive，\n" +
                "  对物理盘或宿主系统盘执行会破坏系统，因此程序会直接拒绝。\n" +
                "  建议先用『原生流程预检』与『试运行』确认，再执行真实操作。\n\n" +
                "── 系统组装（新增）──\n" +
                "  从 SxS 内容出发，先算清组件之间的依赖关系，排出安装顺序，再交给 CBS 通道执行。\n\n" +
                "── 命令行等价物 ──\n" +
                "  CbsxCli.exe（原 cbsx 替代）· SxSFounderCli.exe（原 sxsfounder 替代）",
                "SxS / CBS 工具说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private void BrowseImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择待处理的 Windows 镜像",
                Filter = "Windows images (*.wim;*.esd;*.ffu)|*.wim;*.esd;*.ffu|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) ImagePath = dialog.FileName;
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择预览的输出目录" };
            if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
        }

        [RelayCommand]
        private void RunPreflight()
        {
            var preview = _preflight.Preview(ImagePath, OutputDirectory, SxsRoot);
            PreviewInputs.Clear();
            foreach (var input in preview.Inputs) PreviewInputs.Add(input);
            PreviewOutputs.Clear();
            foreach (var output in preview.PlannedOutputs) PreviewOutputs.Add(output);
            PreflightChecks.Clear();
            foreach (var check in preview.Checks) PreflightChecks.Add(check);
            PreviewSummary = preview.Summary;
            PreviewReady = preview.Ready;
            Status = preview.Ready ? "预检通过；此页面仍不会自动运行 SxS 操作。" : "安全预览完成；存在阻断项，未执行任何镜像操作。";
            Log(Status);
        }

        [RelayCommand]
        private void BrowsePureSource()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            { Title = "选择 CBS 包 .mum（WinSxS\\Manifests 或已 stage 包目录）", Filter = "包清单 (*.mum)|*.mum|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) PureSourceMum = dlg.FileName;
        }

        [RelayCommand]
        private void BrowsePureOutput()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 pureSxS 输出目录" };
            if (dlg.ShowDialog() == true) PureOutputDir = dlg.FolderName;
        }

        [RelayCommand(CanExecute = nameof(CanPureExport))]
        private async Task RunPureExportAsync()
        {
            if (string.IsNullOrWhiteSpace(PureSourceMum) || !File.Exists(PureSourceMum)
                || string.IsNullOrWhiteSpace(PureOutputDir))
            {
                MessageBox.Show("请选择有效的源 .mum 与输出目录。", "pureSxS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            IsPureExporting = true;
            var sink = EnsureTaskLog("pureSxS 导出");
            try
            {
                var res = await PureSxsService.ExportAsync(PureSourceMum, PureOutputDir, m => sink.Write(m));
                sink.Write(res.Success
                    ? $"✅ pureSxS 完成：包 {res.PackagesCopied}、组件 {res.ComponentsCopied}、程序集 {res.AssembliesCopied}、缺失 {res.Missing} → {PureOutputDir}"
                    : "❌ pureSxS 失败：" + res.Error);
                Status = res.Success ? "pureSxS 导出完成（日志见独立窗口）" : "pureSxS 导出失败";
            }
            finally
            {
                sink.MarkFinished();
                IsPureExporting = false;
            }
        }
        bool CanPureExport() => !IsPureExporting;

        // ─── 旧 explodeSxS 路线已整体移除 ───
        // 原实现拼 PowerShell 序列、把 cbsx.exe / sxsfounder.exe 拷到工作盘再运行（外部 exe 调度）。
        // 现已由 ExplodeSxSNativeService（全 C#）取代，见下方"原生流程"。
        // · servicing stack 改为自动解析，不再需要手选 .mum（故移除 BrowseExplodeStack）
        // · 语言包仍需指定，保留 BrowseExplodeLangPack
        // · _explodeArch / _explodeLang / _explodeLanguagePack 被原生流程复用，字段保留

        [RelayCommand]
        private void BrowseExplodeLangPack()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择语言包 .mum", Filter = "包清单 (*.mum)|*.mum" };
            if (dlg.ShowDialog() == true) ExplodeLanguagePack = dlg.FileName;
        }

        // ─────────────── 原生流程（纯 C#，不使用 cbsx.exe / sxsfounder.exe）───────────────
        // 纯增量：不改动上面任何现有字段与命令，避免影响既有 UI 行为。
        // 对应 ExplodeSxSNativeService：WCP 铺底 → CBS 装 SKU → DISM 收尾 → 可选捕获。

        [ObservableProperty] private string _nativeWinDir = string.Empty;
        [ObservableProperty] private string _nativeSkuAssemblyFile = string.Empty;
        [ObservableProperty] private bool _nativeDryRun = true;
        [ObservableProperty] private bool _nativeCaptureWim;
        [ObservableProperty] private bool _isNativeRunning;
        [ObservableProperty] private string _nativeSummary =
            "原生流程：全部由 C# 完成（WCP 铺底 + CBS 装 SKU + DISM 收尾），不需要 cbsx.exe / sxsfounder.exe。\n"
            + "目标必须是【挂载的 VHD/VHDX】上的镜像（例如 G:\\Windows）——这是强制约束，用于保护宿主系统。";

        [RelayCommand]
        private void RunNativePreflight()
        {
            try
            {
                var cfg = MakeNativeConfig(dryRun: true);
                var (ready, checks, blockers) = ExplodeSxSNativeService.Preflight(cfg);
                var sb = new System.Text.StringBuilder();
                foreach (var c in checks) sb.AppendLine("✓ " + c);
                foreach (var b in blockers) sb.AppendLine("✗ " + b);
                NativeSummary = sb.ToString().TrimEnd();
                Status = ready ? "原生流程预检通过，可执行。" : "原生流程预检存在阻断项。";
                Log(Status);
            }
            catch (Exception ex)
            {
                NativeSummary = "预检异常：" + ex.Message;
                Log(NativeSummary);
            }
        }

        [RelayCommand(CanExecute = nameof(CanRunNative))]
        private async Task RunNativeAsync()
        {
            if (string.IsNullOrWhiteSpace(NativeWinDir))
            {
                MessageBox.Show("请填写镜像的 Windows 目录（例如 G:\\Windows）。", "原生流程",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var cfg = MakeNativeConfig(dryRun: NativeDryRun);
            NativeSummary = NativeDryRun ? "试运行中…" : "执行中…（不要关闭程序）";
            IsNativeRunning = true;
            RunNativeCommand.NotifyCanExecuteChanged();
            RunNativePreflightCommand.NotifyCanExecuteChanged();
            var sink = EnsureTaskLog("原生流程");
            try
            {
                var res = await ExplodeSxSNativeService.RunAsync(cfg, m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                foreach (var (step, ok, detail) in res.Steps)
                    sb.AppendLine((ok ? "✓ " : "✗ ") + step + "：" + detail);
                if (res.Error.Length > 0) sb.AppendLine("错误：" + res.Error);
                if (res.RebootRequired) sb.AppendLine("CBS 提示需要重启。");
                NativeSummary = sb.ToString().TrimEnd();
                Status = res.Success ? "原生流程完成。" : "原生流程失败：" + res.Error;
                Log(Status);
            }
            catch (Exception ex)
            {
                NativeSummary = "异常：" + ex.GetType().Name + ": " + ex.Message;
                Log(NativeSummary);
            }
            finally
            {
                sink.MarkFinished();
                IsNativeRunning = false;
                RunNativeCommand.NotifyCanExecuteChanged();
                RunNativePreflightCommand.NotifyCanExecuteChanged();
            }
        }

        bool CanRunNative() => !IsNativeRunning && !IsExploding && !IsPureExporting;

        ExplodeSxSNativeService.Config MakeNativeConfig(bool dryRun) => new()
        {
            WinDir = NativeWinDir?.Trim() ?? string.Empty,
            ImageRoot = string.IsNullOrWhiteSpace(NativeWinDir)
                ? string.Empty
                : ExplodeSxSNativeService.DeriveImageRoot(NativeWinDir.Trim()),
            Arch = string.IsNullOrWhiteSpace(ExplodeArch) ? "amd64" : ExplodeArch.Trim(),
            SkuAssemblyFile = NativeSkuAssemblyFile?.Trim() ?? string.Empty,
            LanguagePackMum = ExplodeLanguagePack?.Trim() ?? string.Empty,
            Lang = string.IsNullOrWhiteSpace(ExplodeLang) ? "en-US" : ExplodeLang.Trim(),
            CaptureWim = NativeCaptureWim,
            WimPath = string.IsNullOrWhiteSpace(OutputDirectory)
                ? string.Empty
                : System.IO.Path.Combine(OutputDirectory, "install.wim"),
            WimName = "Windows",
            DryRun = dryRun
        };

        private void Log(string text) => EnsureTaskLog("SxS 操作日志").Write(text);

        // ─────────────── 系统组装（组件之间的联系 → 组装顺序 → 交给 CBS）───────────────
        // 纯增量：不动上面任何现有成员。
        // 对应 SystemAssemblyService：建依赖图 → 算闭包与拓扑序 → 按世代选通道执行。

        [ObservableProperty] private string _assemblySourceDirs = string.Empty;
        [ObservableProperty] private string _assemblyWinDir = string.Empty;
        [ObservableProperty] private string _assemblySeeds = string.Empty;
        [ObservableProperty] private bool _assemblyPlanOnly = true;
        [ObservableProperty] private bool _isAssemblyRunning;
        [ObservableProperty] private string _assemblySummary =
            "系统组装：从 SxS 内容出发，先算清组件之间的依赖关系，排出安装顺序，再交给 CBS 通道执行。\n"
            + "源目录可填多个，用分号分隔（例如包目录;Manifest 目录）。目标必须是【挂载的 VHD/VHDX】上的镜像。";

        [RelayCommand]
        private void RunAssemblyGraph()
        {
            try
            {
                var dirs = SplitDirs(AssemblySourceDirs);
                if (dirs.Count == 0)
                {
                    MessageBox.Show("请填写至少一个 SxS 内容目录（多个用分号分隔）。", "依赖图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var sink = EnsureTaskLog("系统组装");
                var g = SystemAssemblyService.BuildGraph(dirs, m => sink.Write(m));
                string graph = SystemAssemblyService.RenderGraph(g, 40);
                AssemblySummary = graph;
                sink.Write(graph);
                Status = $"依赖图：{g.Nodes.Count} 个节点，缺失依赖 {g.MissingDependencies}";
                Log(Status);
            }
            catch (Exception ex)
            {
                AssemblySummary = "建图异常：" + ex.GetType().Name + ": " + ex.Message;
                Log(AssemblySummary);
            }
        }

        [RelayCommand(CanExecute = nameof(CanRunAssembly))]
        private async Task RunAssemblyAsync()
        {
            var dirs = SplitDirs(AssemblySourceDirs);
            if (dirs.Count == 0)
            {
                MessageBox.Show("请填写至少一个 SxS 内容目录（多个用分号分隔）。", "系统组装",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var cfg = new SystemAssemblyService.Config
            {
                SourceDirs = dirs,
                WinDir = AssemblyWinDir?.Trim() ?? string.Empty,
                SkuAssemblyFile = NativeSkuAssemblyFile?.Trim() ?? string.Empty,
                UnattendXml = string.Empty,
                PlanOnly = AssemblyPlanOnly,
                Seeds = SplitDirs(AssemblySeeds)
            };

            IsAssemblyRunning = true;
            RunAssemblyCommand.NotifyCanExecuteChanged();
            RunAssemblyGraphCommand.NotifyCanExecuteChanged();
            AssemblySummary = AssemblyPlanOnly ? "计算组装计划中…" : "执行组装中…（不要关闭程序）";
            var sink = EnsureTaskLog("系统组装");
            try
            {
                var res = await SystemAssemblyService.AssembleAsync(cfg, m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                if (res.Plan != null)
                {
                    sb.AppendLine(res.Plan.Summary());
                    sb.AppendLine();
                    sb.AppendLine("安装顺序（前 20）：");
                    foreach (var k in res.Plan.Order.Take(20)) sb.AppendLine("  " + k);
                    if (res.Plan.Missing.Count > 0)
                        sb.AppendLine("缺失依赖：" + string.Join(", ", res.Plan.Missing.Take(10)));
                }
                if (res.Channel != null) sb.AppendLine().AppendLine(res.Channel.Reason);
                if (res.Error.Length > 0) sb.AppendLine("错误：" + res.Error);
                AssemblySummary = sb.ToString().TrimEnd();
                Status = res.Success ? "组装流程完成。" : "组装失败：" + res.Error;
                Log(Status);
            }
            catch (Exception ex)
            {
                AssemblySummary = "异常：" + ex.GetType().Name + ": " + ex.Message;
                Log(AssemblySummary);
            }
            finally
            {
                sink.MarkFinished();
                IsAssemblyRunning = false;
                RunAssemblyCommand.NotifyCanExecuteChanged();
                RunAssemblyGraphCommand.NotifyCanExecuteChanged();
            }
        }

        bool CanRunAssembly() => !IsAssemblyRunning && !IsExploding && !IsPureExporting && !IsNativeRunning;

        static List<string> SplitDirs(string? text)
            => (text ?? string.Empty)
               .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(s => s.Trim().Trim('"'))
               .Where(s => s.Length > 0)
               .ToList();
    }
}
