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
        private CancellationTokenSource? _cts;

        [ObservableProperty] private string _snapshotPath = string.Empty;
        [ObservableProperty] private string _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UUP Downloads");
        [ObservableProperty] private string _status = "选择产品线和更新通道后开始扫描";
        [ObservableProperty] private UupScanSnapshot? _snapshot;
        [ObservableProperty] private bool _isBusy;
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

        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<UupScanEntry> Entries { get; } = new();
        public bool HasEntries => Entries.Count > 0;

        [RelayCommand]
        private async Task Scan()
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                ReportingVersion.Trim(), @"^\d+\.\d+\.\d+\.\d+$"))
            {
                Fail("扫描设置无效", new ArgumentException(
                    "上报版本需要四段式版本号（例如 10.0.26100.1）；它代表客户端基线，不是目标下载版本。"));
                return;
            }
            if (!IncludeProductRelease && !IncludeOsFlight)
            {
                Fail("扫描设置无效", new InvalidOperationException("至少选择一个更新源。"));
                return;
            }

            await RunAsync(async token =>
            {
                Status = "正在查询 Microsoft UUP…";
                Log(Status);
                var request = new UupScanRequest
                {
                    ReportingSku = SelectedSku,
                    ReportingVersion = ReportingVersion.Trim(),
                    Architecture = SelectedArchitecture,
                    FlightRing = SelectedFlightRing,
                    FlightingBranchName = SelectedFlightingBranch,
                    CurrentBranch = CurrentBranch.Trim(),
                    SyncCurrentVersionOnly = SyncCurrentVersionOnly,
                    Filters = new[]
                    {
                        IncludeProductRelease ? "ProductRelease" : null,
                        IncludeOsFlight ? "OSFlight" : null
                    }.Where(f => f != null).Cast<string>().ToArray()
                };
                var snapshot = await _bridge.ScanAsync(request, new Progress<string>(Log), token);
                Snapshot = snapshot;
                SnapshotPath = snapshot.SnapshotPath;
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
            await RunAsync(async token =>
            {
                Directory.CreateDirectory(OutputDirectory);
                Status = $"开始下载：{SelectedEntry.Title}";
                await _bridge.DownloadAsync(Snapshot, SelectedEntry, OutputDirectory,
                    new Progress<string>(Log), token);
                Status = $"下载完成：{SelectedEntry.Title}";
            });
        }

        private bool CanDownloadSelected() => !IsBusy && Snapshot != null && SelectedEntry != null;

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

        private void Log(string text) =>
            Application.Current.Dispatcher.Invoke(() => LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}"));

        private void Fail(string title, Exception ex)
        {
            Status = ex.Message;
            Log($"{title}: {ex.Message}");
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
