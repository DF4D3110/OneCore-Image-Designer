using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    public partial class UupViewModel : ObservableObject
    {
        private readonly UupIntegrationService _service = new();
        private readonly UupBridgeService _bridge = new();
        private readonly IntegrationArtifactRegistry _artifacts = IntegrationArtifactRegistry.Shared;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _conversionCts;

        [ObservableProperty] private string _snapshotPath = string.Empty;
        [ObservableProperty] private string _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UUP Downloads");
        [ObservableProperty] private string _status = "选择产品线和更新通道后开始扫描";
        [ObservableProperty] private UupScanSnapshot? _snapshot;
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _converterExecutablePath = string.Empty;
        [ObservableProperty] private string _conversionInputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UUP Downloads");
        [ObservableProperty] private string _conversionIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Windows.iso");
        [ObservableProperty] private string _conversionTemporaryDirectory = string.Empty;
        [ObservableProperty] private string _conversionLanguageCode = "en-US";
        [ObservableProperty] private string _conversionEdition = string.Empty;
        [ObservableProperty] private string _conversionPreflightSummary = "选择 UUP 输入目录、转换器和空的临时目录后运行预检。";
        [ObservableProperty] private string _conversionStatus = "转换器不会随应用打包；需提供兼容的 UUPMediaConverter.exe。";
        [ObservableProperty] private bool _conversionPreflightReady;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        private bool _isCheckingConversionPreflight;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        private bool _isConverting;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        private UupScanEntry? _selectedEntry;

        public UupSkuOption[] ReportingSkuChoices { get; } =
        [
            new("Professional", "Professional — Windows 客户端"),
            new("ProfessionalN", "ProfessionalN — Windows 客户端 N"),
            new("Enterprise", "Enterprise — Windows 客户端"),
            new("EnterpriseN", "EnterpriseN — Windows 客户端 N"),
            new("Education", "Education — Windows 客户端"),
            new("Core", "Core — Windows Home"),
            new("CoreN", "CoreN — Windows Home N"),
            new("ProWorkstation", "ProWorkstation — Windows 工作站"),
            new("StandardServer", "ServerStandard / StandardServer — Windows Server Standard"),
            new("DatacenterServer", "ServerDatacenter / DatacenterServer — Windows Server Datacenter"),
            new("StandardServerCore", "StandardServerCore — Windows Server Standard Core"),
            new("DatacenterServerCore", "DatacenterServerCore — Windows Server Datacenter Core"),
            new("EnterpriseServer", "EnterpriseServer — Windows Server Enterprise"),
            new("EnterpriseServerCore", "EnterpriseServerCore — Windows Server Enterprise Core"),
            new("StandardEvaluationServer", "StandardEvaluationServer — Windows Server Standard Evaluation"),
            new("DatacenterEvaluationServer", "DatacenterEvaluationServer — Windows Server Datacenter Evaluation"),
            new("AzureStackHCIServerCore", "AzureStackHCIServerCore — Azure Stack HCI"),
            new("IoTUAP", "IoTUAP — Windows IoT Core"),
            new("IoTEnterprise", "IoTEnterprise — Windows IoT Enterprise"),
            new("IoTEnterpriseS", "IoTEnterpriseS — Windows IoT Enterprise LTSC"),
            new("IoTOS", "IoTOS — Windows IoT OS"),
            new("IoTEdgeOS", "IoTEdgeOS — Windows IoT Edge OS"),
            new("Holographic", "Holographic — HoloLens"),
            new("MobileCore", "MobileCore — Windows Mobile"),
            new("PPIPro", "PPIPro — Surface Hub"),
            new("WNC", "WNC — Windows 365 / Cloud PC")
        ];
        public string[] ArchitectureChoices { get; } = ["amd64", "arm64", "x86", "arm"];
        public string[] FlightRingChoices { get; } = ["Retail", "External", "WIS", "WIF", "RP"];
        public string[] FlightingBranchChoices { get; } =
            ["", "ReleasePreview", "Beta", "Dev", "CanaryChannel"];
        [ObservableProperty] private string _selectedSku = "Professional";
        [ObservableProperty] private string _selectedArchitecture = "amd64";
        [ObservableProperty] private string _selectedFlightRing = "Retail";
        [ObservableProperty] private string _selectedFlightingBranch = string.Empty;
        [ObservableProperty] private string _reportingVersion = "10.0.26100.1";
        [ObservableProperty] private string _currentBranch = "ge_release";
        [ObservableProperty] private bool _syncCurrentVersionOnly;
        [ObservableProperty] private bool _includeProductRelease = true;
        [ObservableProperty] private bool _includeOsFlight = true;

        public ObservableCollection<string> ConversionChecks { get; } = new();
        public ObservableCollection<UupScanEntry> Entries { get; } = new();
        public bool HasEntries => Entries.Count > 0;

        [RelayCommand]
        private async Task Scan()
        {
            string version;
            string[] filters;
            try
            {
                version = UupQueryParser.ParseVersion(ReportingVersion);
                UupQueryParser.ParseArchitecture(SelectedArchitecture);
                filters = UupQueryParser.ParseFilters(new[]
                {
                    IncludeProductRelease ? "ProductRelease" : null,
                    IncludeOsFlight ? "OSFlight" : null
                }.Where(filter => filter != null).Cast<string>());
            }
            catch (ArgumentException ex)
            {
                Fail("扫描设置无效", ex);
                return;
            }

            await RunAsync(async token =>
            {
                TaskHeader("UUP 扫描");
                Status = "正在查询 Microsoft UUP…";
                Log(Status);
                var request = new UupScanRequest
                {
                    ReportingSku = SelectedSku,
                    ReportingVersion = version,
                    Architecture = UupQueryParser.ParseArchitecture(SelectedArchitecture),
                    FlightRing = SelectedFlightRing,
                    FlightingBranchName = SelectedFlightingBranch,
                    CurrentBranch = CurrentBranch.Trim(),
                    SyncCurrentVersionOnly = SyncCurrentVersionOnly,
                    Filters = filters
                };
                var snapshot = await _bridge.ScanAsync(request, new Progress<string>(Log), token);
                Snapshot = snapshot;
                SnapshotPath = snapshot.SnapshotPath;
                RegisterSnapshotArtifact(snapshot.SnapshotPath);
                Entries.Clear();
                foreach (var entry in snapshot.Entries) Entries.Add(entry);
                SelectedEntry = Entries.FirstOrDefault();
                OnPropertyChanged(nameof(HasEntries));
                Status = $"扫描完成：找到 {Entries.Count} 个可选更新，快照已保存";
                Log(Status);
            });
        }

        [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
        private async Task DownloadSelected()
        {
            if (Snapshot == null || SelectedEntry == null) return;
            var confirmation = MessageBox.Show(
                $"将下载所选更新：\n\n{SelectedEntry.Title}\n\n输出目录：\n{OutputDirectory}",
                "确认下载 UUP 更新", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.OK) return;
            TaskHeader("UUP 下载");
            try
            {
                await RunAsync(async token =>
                {
                    Directory.CreateDirectory(OutputDirectory);
                    Status = $"开始下载：{SelectedEntry.Title}";
                    await _bridge.DownloadAsync(Snapshot, SelectedEntry, OutputDirectory,
                        new Progress<string>(Log), token);
                    var artifact = _artifacts.Register(new IntegrationArtifact(
                        IntegrationArtifactKind.UupPackage, OutputDirectory, $"Downloaded UUP files: {SelectedEntry.Title}",
                        SelectedEntry.Build, Snapshot.Architecture));
                    var preflight = _artifacts.Preflight(artifact);
                    Log(preflight.IsReady
                        ? $"已注册 UUP 下载产物：{artifact.Path}"
                        : $"UUP 下载产物预检警告：{string.Join("; ", preflight.Errors)}");
                    Status = $"下载完成：{SelectedEntry.Title}";
                });
            }
            finally { _taskSink?.Write("── 下载任务结束 ──"); }
        }

        // ── 任务日志窗口：UUP 所有日志（扫描/下载/转换）只进独立窗口，页内不显示 ──
        private Services.TaskLogSink? _taskSink;
        private Views.TaskLogWindow? _taskWin;

        private Services.TaskLogSink EnsureTaskLog()
        {
            if (_taskSink == null || _taskWin is { IsLoaded: false })
            {
                _taskSink = new Services.TaskLogSink("UUP 操作日志");
                _taskWin = new Views.TaskLogWindow(_taskSink) { Owner = Application.Current?.MainWindow };
                _taskWin.Show();
            }
            return _taskSink;
        }

        private void TaskHeader(string title)
        {
            EnsureTaskLog().Write($"=== {title} ===");
        }

        private bool CanDownloadSelected() => !IsBusy && Snapshot != null && SelectedEntry != null;

        private UupMediaConversionRequest GetConversionRequest() => new(
            ConverterExecutablePath,
            ConversionInputDirectory,
            ConversionIsoPath,
            ConversionTemporaryDirectory,
            ConversionLanguageCode,
            ConversionEdition);

        [RelayCommand]
        private void BrowseConverter()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 UUPMediaConverter.exe",
                Filter = "UUPMediaConverter (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) ConverterExecutablePath = dialog.FileName;
        }

        [RelayCommand]
        private void BrowseConversionInput()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择已下载的 UUP 文件目录" };
            if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
            if (dialog.ShowDialog() == true) ConversionInputDirectory = dialog.FolderName;
        }

        [RelayCommand]
        private void BrowseConversionOutput()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择新 ISO 输出路径",
                Filter = "ISO image (*.iso)|*.iso",
                FileName = "Windows.iso",
                AddExtension = true,
                DefaultExt = ".iso",
                OverwritePrompt = false
            };
            if (dialog.ShowDialog() == true) ConversionIsoPath = dialog.FileName;
        }

        [RelayCommand]
        private void BrowseConversionTemp()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择已创建的空临时工作目录",
                InitialDirectory = Directory.Exists(ConversionTemporaryDirectory)
                    ? ConversionTemporaryDirectory : Path.GetTempPath()
            };
            if (dialog.ShowDialog() == true) ConversionTemporaryDirectory = dialog.FolderName;
        }

        [RelayCommand]
        private async Task CheckConversionPreflight()
        {
            if (IsCheckingConversionPreflight) return;
            IsCheckingConversionPreflight = true;
            ConversionStatus = "正在检查 UUP 文件、运行时和磁盘空间…";
            try
            {
                var request = GetConversionRequest();
                var report = await Task.Run(() => UupMediaConversionService.Preflight(request));
                ConversionChecks.Clear();
                foreach (var error in report.Errors) ConversionChecks.Add("阻断: " + error);
                foreach (var warning in report.Warnings) ConversionChecks.Add("提示: " + warning);
                ConversionPreflightReady = report.IsReady;
                var perDestinationGiB = report.EstimatedRequiredBytes / (1024d * 1024 * 1024);
                ConversionPreflightSummary = $"{(report.IsReady ? "预检通过" : "预检未通过")}；每个目标盘估算约 {perDestinationGiB:F1} GiB；若 ISO 与临时目录同盘，该盘合计约 {perDestinationGiB * 2:F1} GiB。";
                ConversionStatus = report.IsReady
                    ? "预检通过；尚未执行转换。"
                    : "转换未启动；请先解决所有阻断项。";
            }
            catch (Exception ex)
            {
                ConversionPreflightReady = false;
                ConversionStatus = "预检失败：" + ex.Message;
                ConversionChecks.Clear();
                ConversionChecks.Add(ConversionStatus);
            }
            finally { IsCheckingConversionPreflight = false; }
        }

        [RelayCommand(CanExecute = nameof(CanStartConversion))]
        private async Task StartConversionAsync()
        {
            await CheckConversionPreflight();
            if (!ConversionPreflightReady) return;
            var confirmation = MessageBox.Show(
                $"UUPMediaConverter 将读取：\n{ConversionInputDirectory}\n\n并新建：\n{ConversionIsoPath}\n\n临时文件目录：\n{ConversionTemporaryDirectory}\n\n继续启动外部转换器？",
                "确认 UUP 媒体转换", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK) return;

            _conversionCts = new CancellationTokenSource();
            IsConverting = true;
            TaskHeader("UUP 媒体转换");
            ConversionStatus = "正在转换…可使用取消按钮终止外部进程。";
            try
            {
                var progress = new Progress<string>(line => Log($"[Media] {line}"));
                var exitCode = await UupMediaConversionService.RunAsync(
                    GetConversionRequest(), progress, _conversionCts.Token);
                if (exitCode != 0)
                    throw new InvalidOperationException($"UUPMediaConverter exited with code {exitCode}.");

                if (!File.Exists(ConversionIsoPath))
                    throw new InvalidDataException("The converter exited successfully but the requested ISO was not created.");
                var artifact = _artifacts.Register(new IntegrationArtifact(
                    IntegrationArtifactKind.MediaImage, ConversionIsoPath, "Converted desktop UUP ISO"));
                var preflight = _artifacts.Preflight(artifact);
                ConversionStatus = preflight.IsReady
                    ? $"转换完成并登记产物：{artifact.Path}"
                    : "转换完成；产物预检失败：" + string.Join("; ", preflight.Errors);
                Log(ConversionStatus);
            }
            catch (OperationCanceledException)
            {
                ConversionStatus = "转换已取消。";
                Log(ConversionStatus);
            }
            catch (Exception ex)
            {
                ConversionStatus = "转换失败：" + ex.Message;
                Log(ConversionStatus);
                MessageBox.Show(ex.Message, "UUP 媒体转换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _conversionCts.Dispose();
                _conversionCts = null;
                IsConverting = false;
            }
        }

        private bool CanStartConversion() => !IsConverting && !IsBusy && !IsCheckingConversionPreflight;

        [RelayCommand]
        private void CancelConversion() => _conversionCts?.Cancel();

        [RelayCommand]
        private void OpenSnapshot()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "UUP 快照 (*.json)|*.json|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                SnapshotPath = dialog.FileName;
                Snapshot = _service.LoadSnapshot(dialog.FileName);
                Snapshot.SnapshotPath = dialog.FileName;
                RegisterSnapshotArtifact(dialog.FileName);
                Entries.Clear();
                foreach (var entry in Snapshot.Entries) Entries.Add(entry);
                SelectedEntry = Entries.FirstOrDefault();
                OnPropertyChanged(nameof(HasEntries));
                Status = $"已载入 {Snapshot.Entries.Count} 个更新条目";
                Log(Status);
            }
            catch (Exception ex) { Fail("载入快照失败", ex); }
        }

        [RelayCommand]
        private void BrowseOutputDirectory()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 UUP 更新输出目录",
                InitialDirectory = Directory.Exists(OutputDirectory)
                    ? OutputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
        }

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        private async Task RunAsync(Func<CancellationToken, Task> action)
        {
            if (IsBusy) return;
            IsBusy = true;
            DownloadSelectedCommand.NotifyCanExecuteChanged();
            _cts = new CancellationTokenSource();
            try { await action(_cts.Token); }
            catch (OperationCanceledException) { Status = "操作已取消"; Log(Status); }
            catch (Exception ex) { Fail("UUP 操作失败", ex); }
            finally
            {
                _cts.Dispose();
                _cts = null;
                IsBusy = false;
                DownloadSelectedCommand.NotifyCanExecuteChanged();
            }
        }

        private void Log(string text) => EnsureTaskLog().Write(text);

        private void RegisterSnapshotArtifact(string path)
        {
            var artifact = _artifacts.Register(new IntegrationArtifact(
                IntegrationArtifactKind.UupSnapshot, path, "UUP query snapshot",
                Architecture: Snapshot?.Architecture));
            var preflight = _artifacts.Preflight(artifact);
            Log(preflight.IsReady
                ? $"已注册 UUP 快照产物：{artifact.Path}"
                : $"UUP 快照预检失败：{string.Join("; ", preflight.Errors)}");
        }

        private void Fail(string title, Exception ex)
        {
            Status = ex.Message;
            Log($"{title}: {ex.Message}");
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
