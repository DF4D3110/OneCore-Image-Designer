using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels;

public partial class CatalogDownloadViewModel : ObservableObject
{
    private readonly MicrosoftCatalogService _catalog = new();
    private CancellationTokenSource? _cts;
    private UpdateDownloader _downloader = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCatalogCommand))]
    private string _search = string.Empty;
    [ObservableProperty] private string _architecture = "All";
    [ObservableProperty] private string _operatingSystem = "Windows 11";
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _updateType = string.Empty;
    [ObservableProperty] private bool _includePreview;
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
    public string[] ArchitectureChoices { get; } = ["All", "x64", "x86", "arm64"];
    public string[] OperatingSystemChoices { get; } = ["Windows 11", "Windows 10", "Windows Server"];
    public string[] UpdateTypeChoices { get; } = ["", "Security Updates", "Cumulative Updates", "Driver Updates", "Servicing Stack"];
    public string EngineName => _downloader.EngineName;
    public string ResultCountText => $"{Updates.Count} 条更新";

    partial void OnSelectedUpdateChanged(MicrosoftCatalogUpdate? value)
    {
        Files.Clear();
        if (value is null) return;
        _ = LoadFilesAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchCatalog()
    {
        await RunAsync(async token =>
        {
            Updates.Clear();
            Files.Clear();
            var result = await _catalog.SearchAsync(new MicrosoftCatalogSearchOptions(
                Search.Trim(), Architecture, OperatingSystem, Version, UpdateType, IncludePreview: IncludePreview),
                new Progress<string>(AppendLog), token);
            foreach (var update in result) Updates.Add(update);
            Status = $"查询完成：{Updates.Count} 条更新";
            OnPropertyChanged(nameof(ResultCountText));
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
                    new Progress<string>(AppendLog), token);
                AppendLog($"{result.Status}：{result.Destination}");
            }
            Status = $"下载完成：{files.Length} 个文件";
        });
    }

    private async Task LoadFilesAsync(MicrosoftCatalogUpdate update)
    {
        try
        {
            Status = "正在读取文件明细…";
            var loaded = await _catalog.LoadFilesAsync(update);
            if (!ReferenceEquals(SelectedUpdate, update)) return;
            foreach (var file in loaded.Files) Files.Add(file);
            Status = $"已读取 {Files.Count} 个文件";
            DownloadCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) { AppendLog($"读取文件明细失败：{ex.Message}"); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0; _cts = new CancellationTokenSource();
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { Status = "操作已取消"; AppendLog(Status); }
        catch (Exception ex) { Status = "操作失败"; AppendLog($"错误：{ex.Message}"); }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(Search);
    private bool CanCancel() => IsBusy;
    private bool CanDownload() => !IsBusy && Files.Count > 0;
    private void AppendLog(string text) => Log = string.IsNullOrEmpty(Log) ? text : Log + Environment.NewLine + text;
}
