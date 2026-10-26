using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;
using System.Windows;

namespace OneCore_Image_Designer.ViewModels;

public partial class CatalogDownloadViewModel : ObservableObject
{
    private readonly MicrosoftCatalogService _catalog = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _fileCts;
    private UpdateDownloader _downloader = new();
    private TaskLogSink? _taskSink;
    private Views.TaskLogWindow? _taskWindow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _search = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _kbNumber = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _architecture = "All";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _operatingSystem = "全部产品线";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _productFamily = "全部产品";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _version = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _language = "全部语言";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _updateType = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private bool _includePreview;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _status = "输入关键词后查询 Microsoft Update Catalog";
    [ObservableProperty] private string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Windows Updates");
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _downloadAllFiles = true;
    [ObservableProperty] private MicrosoftCatalogUpdate? _selectedUpdate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _log = string.Empty;

    public ObservableCollection<MicrosoftCatalogUpdate> Updates { get; } = new();
    public ObservableCollection<MicrosoftCatalogFile> Files { get; } = new();
    public string[] ArchitectureChoices { get; } = ["All", "x86", "x64", "AMD64", "IA64", "ARM", "ARM64"];
    public string[] ProductFamilyChoices { get; } =
        ["全部产品", "Windows", "Windows Phone / Mobile", "Windows CE / Embedded",
         "Windows IoT", "HoloLens", "OneCore / UUP", "Office", "SQL Server",
         "Exchange", "Visual Studio", ".NET", "Microsoft Edge", "Defender",
         "Drivers", "Other Microsoft Software"];
    public ObservableCollection<string> OperatingSystemChoices { get; } =
        new(["全部产品线", "Windows 2000", "Windows XP / XP x64", "Windows Server 2003",
             "Windows Vista", "Windows Server 2008", "Windows 7", "Windows Server 2008 R2",
             "Windows 8", "Windows Server 2012", "Windows 8.1", "Windows Server 2012 R2",
             "Windows 10 1507/1511", "Windows 10 1607", "Windows 10 1703/1709",
             "Windows 10 1803/1809", "Windows 10 1903/1909", "Windows 10 2004/20H2",
             "Windows 10 21H1/21H2", "Windows 10 22H2", "Windows 11 21H2/22H2",
             "Windows 11 23H2", "Windows 11 24H2/25H2", "Windows Server 2016",
             "Windows Server 2019", "Windows Server 2022", "Windows Server 2025"]);
    public string[] UpdateTypeChoices { get; } =
        ["", "Security Updates", "Updates", "Critical Updates", "Feature Packs",
         "Service Packs", "Tools", "Update Rollups", "Cumulative Updates",
         "Security Quality Updates", "Driver Updates"];
    public string[] LanguageChoices { get; } =
        ["全部语言", "English", "Chinese (Simplified)", "Chinese (Traditional)", "Japanese",
         "Korean", "German", "French", "Spanish", "Russian", "Arabic"];
    public string EngineName => _downloader.EngineName;
    public string ResultCountText => $"{Updates.Count} 条更新";
    public bool HasUpdates => Updates.Count > 0;

    partial void OnProductFamilyChanged(string value)
    {
        var choices = GetProductLineChoices(value);
        OperatingSystemChoices.Clear();
        foreach (var choice in choices) OperatingSystemChoices.Add(choice);
        OperatingSystem = choices[0];
    }

    partial void OnSelectedUpdateChanged(MicrosoftCatalogUpdate? value)
    {
        _fileCts?.Cancel();
        _fileCts?.Dispose();
        _fileCts = value is null ? null : new CancellationTokenSource();
        Files.Clear();
        DownloadCommand.NotifyCanExecuteChanged();
        if (value is null) return;
        _ = LoadFilesAsync(value, _fileCts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchCatalog()
    {
        await RunAsync(async token =>
        {
            Updates.Clear();
            Files.Clear();
            var result = await _catalog.SearchAsync(new MicrosoftCatalogSearchOptions(
                Search.Trim(), Architecture, OperatingSystem, Version, UpdateType,
                IncludePreview: IncludePreview, ProductFamily: ProductFamily,
                KbNumber: KbNumber, Language: Language == "全部语言" ? null : Language),
                CreateLogProgress(), token);
            foreach (var update in result) Updates.Add(update);
            Status = $"查询完成：{Updates.Count} 条更新";
            OnPropertyChanged(nameof(ResultCountText));
            OnPropertyChanged(nameof(HasUpdates));
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择更新下载目录", InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        var files = DownloadAllFiles ? Files.ToArray() : Files.Take(1).ToArray();
        if (files.Length == 0) { AppendLog("当前更新没有可下载文件。"); return; }
        await RunAsync(async token =>
        {
            var progress = new Progress<double>(value => Progress = value);
            for (var index = 0; index < files.Length; index++)
            {
                Status = $"下载 {index + 1}/{files.Length}：{files[index].Name}";
                AppendLog(Status);
                var result = await _downloader.DownloadAsync(files[index], OutputDirectory, Overwrite, progress,
                    CreateLogProgress(), token);
                AppendLog($"{result.Status}：{result.Destination}");
            }
            Status = $"下载完成：{files.Length} 个文件";
        });
    }

    private async Task LoadFilesAsync(MicrosoftCatalogUpdate update, CancellationToken token)
    {
        try
        {
            Status = "正在读取文件明细…";
            var loaded = await _catalog.LoadFilesAsync(update, token);
            if (!ReferenceEquals(SelectedUpdate, update)) return;
            foreach (var file in loaded.Files) Files.Add(file);
            Status = $"已读取 {Files.Count} 个文件";
            DownloadCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog($"读取文件明细失败：{ex.Message}");
            Status = "文件明细读取失败";
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0; _cts = new CancellationTokenSource();
        EnsureTaskLog();
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { Status = "操作已取消"; AppendLog(Status); }
        catch (Exception ex) { Status = "操作失败"; AppendLog($"错误：{ex.Message}"); }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            _taskSink?.MarkFinished();
        }
    }

    private bool CanSearch() => !IsBusy &&
        !string.IsNullOrWhiteSpace(MicrosoftCatalogService.BuildQuery(new MicrosoftCatalogSearchOptions(
            Search: Search,
            Architecture: Architecture,
            OperatingSystem: OperatingSystem,
            Version: Version,
            UpdateType: UpdateType,
            IncludePreview: IncludePreview,
            ProductFamily: ProductFamily,
            KbNumber: KbNumber,
            Language: Language == "全部语言" ? null : Language)));
    private bool CanCancel() => IsBusy;
    private bool CanDownload() => !IsBusy && Files.Count > 0;

    private static IReadOnlyList<string> GetProductLineChoices(string family) =>
        family switch
        {
            "Windows Phone / Mobile" => ["全部产品线", "Windows Phone 7", "Windows Phone 7.5/7.8",
                "Windows Phone 8", "Windows Phone 8.1", "Windows 10 Mobile"],
            "Windows CE / Embedded" => ["全部产品线", "Windows CE 5.0", "Windows CE 6.0",
                "Windows Embedded Compact 7", "Windows Embedded Standard 7", "Windows Embedded 8/8.1",
                "Windows Embedded POSReady", "Windows Embedded Handheld"],
            "Windows IoT" => ["全部产品线", "Windows 10 IoT Core", "Windows 10 IoT Enterprise",
                "Windows 11 IoT Enterprise", "Azure Sphere"],
            "HoloLens" => ["全部产品线", "HoloLens", "HoloLens 2", "HoloLens 2 Industrial Edition"],
            "Office" => ["全部产品线", "Office 2000", "Office XP/2003", "Office 2007",
                "Office 2010", "Office 2013", "Office 2016", "Office 2019", "Office 2021",
                "Microsoft 365 Apps"],
            "SQL Server" => ["全部产品线", "SQL Server 2000", "SQL Server 2005", "SQL Server 2008/R2",
                "SQL Server 2012", "SQL Server 2014", "SQL Server 2016", "SQL Server 2017",
                "SQL Server 2019", "SQL Server 2022"],
            "Exchange" => ["全部产品线", "Exchange 2007", "Exchange 2010", "Exchange 2013",
                "Exchange 2016", "Exchange 2019"],
            "Visual Studio" => ["全部产品线", "Visual Studio 2005", "Visual Studio 2008",
                "Visual Studio 2010", "Visual Studio 2012", "Visual Studio 2013",
                "Visual Studio 2015", "Visual Studio 2017", "Visual Studio 2019", "Visual Studio 2022"],
            ".NET" => ["全部产品线", ".NET Framework 2.0/3.5", ".NET Framework 4.x",
                ".NET Core", ".NET 5", ".NET 6", ".NET 7", ".NET 8", ".NET 9"],
            _ => ["全部产品线", "Windows 2000", "Windows XP / XP x64", "Windows Server 2003",
                "Windows Vista", "Windows Server 2008", "Windows 7", "Windows Server 2008 R2",
                "Windows 8", "Windows Server 2012", "Windows 8.1", "Windows Server 2012 R2",
                "Windows 10 1507/1511", "Windows 10 1607", "Windows 10 1703/1709",
                "Windows 10 1803/1809", "Windows 10 1903/1909", "Windows 10 2004/20H2",
                "Windows 10 21H1/21H2", "Windows 10 22H2", "Windows 11 21H2/22H2",
                "Windows 11 23H2", "Windows 11 24H2/25H2", "Windows Server 2016",
                "Windows Server 2019", "Windows Server 2022", "Windows Server 2025"]
        };
    [RelayCommand]
    private void ShowLog()
    {
        EnsureTaskLog();
        _taskWindow?.Activate();
    }

    private TaskLogSink EnsureTaskLog()
    {
        if (_taskSink != null && _taskWindow != null) return _taskSink;
        _taskSink = new TaskLogSink("Microsoft 更新目录下载");
        _taskWindow = new Views.TaskLogWindow(_taskSink) { Owner = Application.Current?.MainWindow };
        _taskWindow.Closed += (_, _) => _taskWindow = null;
        _taskWindow.Show();
        return _taskSink;
    }

    private IProgress<string> CreateLogProgress() =>
        new Progress<string>(AppendLog);

    private void AppendLog(string text)
    {
        Log = string.IsNullOrEmpty(Log) ? text : Log + Environment.NewLine + text;
        EnsureTaskLog().Write(text);
    }
}
