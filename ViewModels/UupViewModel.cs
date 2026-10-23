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
        private CancellationTokenSource? _languageCts;

        [ObservableProperty] private string _snapshotPath = string.Empty;
        [ObservableProperty] private string _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UUP Downloads");
        [ObservableProperty] private string _status = "选择 SKU 和架构后开始扫描";
        [ObservableProperty] private UupScanSnapshot? _snapshot;
        [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _conversionInputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UUP Downloads");
        [ObservableProperty] private string _conversionIsoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Windows.iso");
        [ObservableProperty] private string _conversionTemporaryDirectory = string.Empty;
        [ObservableProperty] private string _conversionLanguageCode = "en-US";
        [ObservableProperty] private string _conversionEdition = string.Empty;
        [ObservableProperty] private string _conversionPreflightSummary = "选择 UUP 输入目录和空的临时目录后运行预检。";
        [ObservableProperty] private string _conversionStatus = "内置 UUP 转换器就绪；请先运行预检。";
        [ObservableProperty] private bool _conversionPreflightReady;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        private bool _isCheckingConversionPreflight;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartConversionCommand))]
        private bool _isConverting;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        private UupScanEntry? _selectedEntry;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        private bool _isLoadingLanguages;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
        private string _selectedDownloadLanguage = string.Empty;
        [ObservableProperty] private string _downloadLanguageStatus = "选择更新以读取可用语言";

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
        public string[] ArchitectureChoices { get; } = ["arm64", "amd64", "x86", "arm"];
        public UupQueryOption[] FlightRingChoices { get; } =
        [
            new(string.Empty, "自动（正式版 / Insider）"),
            new("Retail", "Retail"),
            new("External", "External"),
            new("WIS", "WIS"),
            new("WIF", "WIF"),
            new("RP", "RP")
        ];
        [ObservableProperty] private string _selectedSku = "Professional";
        [ObservableProperty] private string _selectedArchitecture = "amd64";
        [ObservableProperty] private string _selectedFlightRing = string.Empty;
        [ObservableProperty] private string _reportingVersion = string.Empty;
        [ObservableProperty] private string _currentBranch = string.Empty;
        [ObservableProperty] private string _selectedFlightingBranch = string.Empty;
        [ObservableProperty] private bool _syncCurrentVersionOnly;
        [ObservableProperty] private bool _includeProductRelease = true;
        [ObservableProperty] private bool _includeOsFlight = true;
        [ObservableProperty] private string _targetBuildFilter = string.Empty;
        [ObservableProperty] private string _titleKeywordFilter = string.Empty;

        public ObservableCollection<string> ConversionChecks { get; } = new();
        public ObservableCollection<UupScanEntry> Entries { get; } = new();
        public ObservableCollection<UupScanEntry> FilteredEntries { get; } = new();
        public ObservableCollection<UupLanguageOption> AvailableDownloadLanguages { get; } =
            new() { new UupLanguageOption(string.Empty, "全部语言") };
        public bool HasEntries => Entries.Count > 0;
        public bool HasVisibleEntries => FilteredEntries.Count > 0;
        public string ResultCountText => Entries.Count == FilteredEntries.Count
            ? $"{Entries.Count} 项"
            : $"显示 {FilteredEntries.Count} / {Entries.Count} 项";
        public string EmptyResultsMessage => HasEntries
            ? "没有符合当前 Build / 标题条件的更新"
            : "还没有查询结果";
        public string QuerySummaryText
        {
            get
            {
                var profiles = Snapshot?.QueryProfiles;
                if (Snapshot == null || profiles == null || profiles.Count == 0) return string.Empty;
                var successful = profiles.Where(profile => string.IsNullOrEmpty(profile.Error)).ToArray();
                var rawResults = successful.Sum(profile => profile.ResultCount);
                var skipped = successful.Sum(profile => profile.SkippedCount);
                var repeated = Math.Max(0, rawResults - Entries.Count - skipped);
                return $"查询组合 {successful.Length}/{profiles.Count} 成功；服务端返回 {rawResults} 条，合并为 {Entries.Count} 个更新身份（ID + 修订号），重复 {repeated} 条，缺少身份跳过 {skipped} 条。";
            }
        }

        partial void OnTargetBuildFilterChanged(string value) => RefreshFilteredEntries();
        partial void OnTitleKeywordFilterChanged(string value) => RefreshFilteredEntries();
        partial void OnSelectedEntryChanged(UupScanEntry? value) => _ = LoadAvailableLanguagesAsync(value);

        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task Scan()
        {
            string version;
            string[] filters;
            try
            {
                version = string.IsNullOrWhiteSpace(ReportingVersion)
                    ? string.Empty
                    : UupQueryParser.ParseVersion(ReportingVersion);
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
                    FlightingBranchName = SelectedFlightingBranch.Trim(),
                    CurrentBranch = CurrentBranch.Trim(),
                    SyncCurrentVersionOnly = SyncCurrentVersionOnly,
                    Filters = filters
                };
                var snapshot = await _bridge.ScanAsync(request, CreateLogProgress(), token);
                Snapshot = snapshot;
                OnPropertyChanged(nameof(QuerySummaryText));
                SnapshotPath = snapshot.SnapshotPath;
                RegisterSnapshotArtifact(snapshot.SnapshotPath);
                SetEntries(snapshot.Entries);
                Status = snapshot.FailedRequests == 0
                    ? $"扫描完成：找到 {Entries.Count} 个可选更新，快照已保存"
                    : $"扫描部分完成：{Entries.Count} 个更新；{snapshot.FailedRequests}/{snapshot.TotalRequests} 个查询组合失败";
                Log(Status);
            });
        }

        [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
        private async Task DownloadSelected()
        {
            var snapshot = Snapshot;
            var selectedEntry = SelectedEntry;
            var outputDirectory = OutputDirectory.Trim();
            var downloadLanguage = SelectedDownloadLanguage;
            if (snapshot == null || selectedEntry == null) return;
            if (outputDirectory.Length == 0)
            {
                Fail("下载目录无效", new ArgumentException("请选择 UUP 下载输出目录。"));
                return;
            }
            var confirmation = MessageBox.Show(
                $"将下载所选更新：\n\n{selectedEntry.Title}\n\n下载语言：{(string.IsNullOrEmpty(downloadLanguage) ? "全部语言" : downloadLanguage)}\n\n输出目录：\n{outputDirectory}",
                "确认下载 UUP 更新", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.OK) return;
            TaskHeader("UUP 下载");
            try
            {
                await RunAsync(async token =>
                {
                    Status = $"开始下载：{selectedEntry.Title}";
                    await _bridge.DownloadAsync(snapshot, selectedEntry, outputDirectory,
                        downloadLanguage, CreateLogProgress(), token);
                    var artifact = _artifacts.Register(new IntegrationArtifact(
                        IntegrationArtifactKind.UupPackage, outputDirectory, $"Downloaded UUP files: {selectedEntry.Title}",
                        selectedEntry.Build, snapshot.Architecture));
                    var preflight = _artifacts.Preflight(artifact);
                    Log(preflight.IsReady
                        ? $"已注册 UUP 下载产物：{artifact.Path}"
                        : $"UUP 下载产物预检警告：{string.Join("; ", preflight.Errors)}");
                    Status = $"下载完成：{selectedEntry.Title}";
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

        [RelayCommand]
        private void ShowLog()
        {
            EnsureTaskLog();
            _taskWin?.Activate();
        }

        private void TaskHeader(string title)
        {
            EnsureTaskLog().Write($"=== {title} ===");
        }

        private bool CanScan() => !IsBusy && !IsConverting && !IsCheckingConversionPreflight;

        private bool CanDownloadSelected() =>
            !IsBusy && !IsConverting && !IsCheckingConversionPreflight &&
            !IsLoadingLanguages && Snapshot != null && SelectedEntry != null && FilteredEntries.Contains(SelectedEntry);

        private async Task LoadAvailableLanguagesAsync(UupScanEntry? entry)
        {
            _languageCts?.Cancel();
            _languageCts?.Dispose();
            _languageCts = null;
            IsLoadingLanguages = false;
            AvailableDownloadLanguages.Clear();
            AvailableDownloadLanguages.Add(new UupLanguageOption(string.Empty, "全部语言"));
            SelectedDownloadLanguage = string.Empty;

            var snapshot = Snapshot;
            if (entry == null || snapshot == null) {
                DownloadLanguageStatus = "选择更新以读取可用语言";
                return;
            }

            var cancellation = new CancellationTokenSource();
            _languageCts = cancellation;
            IsLoadingLanguages = true;
            DownloadLanguageStatus = "正在读取该更新的语言选项…";
            try
            {
                var languages = await _bridge.GetAvailableLanguagesAsync(
                    snapshot, entry, CreateLogProgress(), cancellation.Token);
                if (!ReferenceEquals(_languageCts, cancellation)) return;

                foreach (var language in languages)
                {
                    var displayName = GetLanguageDisplayName(language);
                    AvailableDownloadLanguages.Add(new UupLanguageOption(language, $"{displayName} ({language})"));
                }
                if (languages.Length > 0)
                    SelectedDownloadLanguage = languages[0];
                DownloadLanguageStatus = languages.Length == 0
                    ? "该更新未返回可用语言；请检查日志后重试或选择“全部语言”"
                    : $"发现 {languages.Length} 个语言选项";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_languageCts, cancellation)) return;
                DownloadLanguageStatus = "语言列表读取失败；下载时将保留全部语言：" + ex.Message;
                Log(DownloadLanguageStatus);
            }
            finally
            {
                if (ReferenceEquals(_languageCts, cancellation))
                {
                    _languageCts = null;
                    cancellation.Dispose();
                    IsLoadingLanguages = false;
                }
            }
        }

        [RelayCommand]
        private Task ReloadAvailableLanguages() => LoadAvailableLanguagesAsync(SelectedEntry);

        private static string GetLanguageDisplayName(string language)
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(language).DisplayName; }
            catch (System.Globalization.CultureNotFoundException) { return language; }
        }

        private void SetEntries(System.Collections.Generic.IEnumerable<UupScanEntry> entries)
        {
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            SelectedEntry = null;
            RefreshFilteredEntries();
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(QuerySummaryText));
        }

        private void RefreshFilteredEntries()
        {
            var previousSelection = SelectedEntry;
            FilteredEntries.Clear();
            foreach (var entry in UupResultFilter.Apply(Entries, TargetBuildFilter, TitleKeywordFilter))
                FilteredEntries.Add(entry);

            SelectedEntry = previousSelection != null && FilteredEntries.Contains(previousSelection)
                ? previousSelection
                : FilteredEntries.FirstOrDefault();
            OnPropertyChanged(nameof(HasVisibleEntries));
            OnPropertyChanged(nameof(ResultCountText));
            OnPropertyChanged(nameof(EmptyResultsMessage));
            DownloadSelectedCommand.NotifyCanExecuteChanged();
        }

        private UupMediaConversionRequest GetConversionRequest() => new(
            ConversionInputDirectory,
            ConversionIsoPath,
            ConversionTemporaryDirectory,
            ConversionLanguageCode,
            ConversionEdition);

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
            ConversionStatus = "正在检查内置转换器、UUP 文件和磁盘空间…";
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
                $"内置 UUP 转换功能将读取：\n{ConversionInputDirectory}\n\n并新建：\n{ConversionIsoPath}\n\n临时文件目录：\n{ConversionTemporaryDirectory}\n\n继续转换？",
                "确认 UUP 媒体转换", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK) return;

            _conversionCts = new CancellationTokenSource();
            IsConverting = true;
            TaskHeader("UUP 媒体转换");
            ConversionStatus = "正在转换…可使用取消按钮终止转换进程。";
            try
            {
                var logSink = EnsureTaskLog();
                var progress = new InlineProgress<string>(line => logSink.Write($"[Media] {line}"));
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
                OnPropertyChanged(nameof(QuerySummaryText));
                RegisterSnapshotArtifact(dialog.FileName);
                SetEntries(Snapshot.Entries);
                Status = Snapshot.FailedRequests == 0
                    ? $"已载入 {Snapshot.Entries.Count} 个更新条目"
                    : $"已载入 {Snapshot.Entries.Count} 个更新条目；原扫描有 {Snapshot.FailedRequests}/{Snapshot.TotalRequests} 个查询组合失败";
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

        private IProgress<string> CreateLogProgress()
        {
            var logSink = EnsureTaskLog();
            return new InlineProgress<string>(logSink.Write);
        }

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

        private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
    }
}
