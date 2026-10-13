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

        private void Log(string text) => EnsureTaskLog("SxS 操作日志").Write(text);
    }
}
