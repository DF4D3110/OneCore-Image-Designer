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

        // ── explodeSxS 编排（C# 提权执行，默认不格式化）──
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
                "SxS 功能独立于 UUP。\n\n" +
                "pureSxS：处理组件来源和纯 SxS 目录。\n" +
                "explodeSxS：通过 prepare/captureimg/cbss/finishimg 流程处理镜像中的 CBS/SxS。\n\n" +
                "源码随应用集成；运行 pureSxS 需要 Python。explodeSxS 还依赖 DISM 和已获许可的 CBS 工具。\n\n" +
                "注意：prepare.cmd 会快速格式化 X:。当前应用不会自动运行这些上游脚本；执行前请审阅脚本、确认目标磁盘与备份。",
                "SxS 工具说明", MessageBoxButton.OK, MessageBoxImage.Information);
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
        bool CanPureExport() => !IsPureExporting && !IsExploding;

        [RelayCommand]
        private void BrowseExplodeStack()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择 servicing stack 的 .mum", Filter = "包清单 (*.mum)|*.mum" };
            if (dlg.ShowDialog() == true) ExplodeServicingStack = dlg.FileName;
        }

        [RelayCommand]
        private void BrowseExplodeLangPack()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择语言包 .mum", Filter = "包清单 (*.mum)|*.mum" };
            if (dlg.ShowDialog() == true) ExplodeLanguagePack = dlg.FileName;
        }

        private Services.ExplodeSxSService.Config MakeExplodeConfig() => new()
        {
            WorkDrive = ExplodeWorkDrive,
            Edition = ExplodeEdition,
            Arch = ExplodeArch,
            Lang = ExplodeLang,
            ServicingStackMum = ExplodeServicingStack,
            LanguagePackMum = ExplodeLanguagePack,
            FormatWorkDrive = ExplodeFormatDrive,
            CaptureWim = ExplodeCaptureWim,
        };

        [RelayCommand(CanExecute = nameof(CanExplode))]
        private void RunExplodePreflight()
        {
            var pf = ExplodeSxSService.Preflight(MakeExplodeConfig());
            var sb = new System.Text.StringBuilder();
            foreach (var c in pf.Checks) sb.AppendLine("✓ " + c);
            foreach (var b in pf.Blockers) sb.AppendLine("✗ " + b);
            ExplodeSummary = sb.ToString().TrimEnd();
            Status = pf.Ready ? "explodeSxS 预检通过，可执行（默认不格式化工作盘）" : "explodeSxS 预检存在阻断项";
            Log(Status);
        }

        [RelayCommand(CanExecute = nameof(CanExplode))]
        private async Task RunExplodeAsync()
        {
            var cfg = MakeExplodeConfig();
            var pf = ExplodeSxSService.Preflight(cfg);
            if (!pf.Ready)
            {
                MessageBox.Show("预检未通过：\n" + string.Join("\n", pf.Blockers), "explodeSxS",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string warn = cfg.FormatWorkDrive
                ? $"⚠ 将格式化 {cfg.WorkDrive} 盘！确认该盘无重要数据。\n\n" : "";
            if (MessageBox.Show(warn + "将以管理员权限执行 explodeSxS 完整 staging 流程（DISM/cbsx/sxsfounder/reg），" +
                    "工作盘内容会被覆盖。继续？", "explodeSxS 确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsExploding = true;
            var sink = EnsureTaskLog("explodeSxS staging");
            try
            {
                var code = await ExplodeSxSService.RunAsync(cfg, m => sink.Write(m), CancellationToken.None);
                sink.Write(code == 0 ? "✅ explodeSxS 完成" : $"explodeSxS 结束，退出码 {code?.ToString() ?? "超时/未知"}");
                Status = code == 0 ? "explodeSxS 完成（日志见独立窗口）" : "explodeSxS 未成功，见日志";
            }
            finally
            {
                sink.MarkFinished();
                IsExploding = false;
                RunExplodePreflightCommand.NotifyCanExecuteChanged();
                RunExplodeCommand.NotifyCanExecuteChanged();
                RunPureExportCommand.NotifyCanExecuteChanged();
            }
        }
        bool CanExplode() => !IsExploding && !IsPureExporting;

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
    }
}
