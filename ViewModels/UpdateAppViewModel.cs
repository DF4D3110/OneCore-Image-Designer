using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>
    /// updateapp 调度页：按 build 动态渲染该版本 updateapp.exe 实际支持的命令集
    /// （数据源为该版本 exe 内嵌的官方 usage 文本），支持离线镜像与在线两种执行目标，
    /// 提权执行并实时回传日志。
    /// </summary>
    public partial class UpdateAppViewModel : ObservableObject
    {
        private readonly MainViewModel _mainVM;
        private CancellationTokenSource? _runCts;

        public UpdateAppViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            foreach (var b in UpdateAppUsageService.CatalogBuilds) Builds.Add(b);
            if (Builds.Count > 0) SelectedBuild = Builds.Contains(15063) ? 15063 : Builds[^1];
        }

        // ── 版本与命令 ──
        public ObservableCollection<int> Builds { get; } = new();

        [ObservableProperty] private int _selectedBuild;
        partial void OnSelectedBuildChanged(int value) => RefreshCommands();

        [ObservableProperty] private ObservableCollection<UpdateAppCommandInfo> _commands = new();
        [ObservableProperty] private UpdateAppCommandInfo? _selectedCommand;
        partial void OnSelectedCommandChanged(UpdateAppCommandInfo? value)
        {
            OnPropertyChanged(nameof(CommandArgsHint));
            OnPropertyChanged(nameof(ShowLiveWarning));
            OnPropertyChanged(nameof(RiskText));
        }

        public string CommandArgsHint =>
            SelectedCommand == null
                ? "选择一个命令查看参数模板。"
                : string.IsNullOrWhiteSpace(SelectedCommand.ArgsTemplate)
                    ? $"{SelectedCommand.Name}：无参数。"
                    : $"{SelectedCommand.Name} {SelectedCommand.ArgsTemplate}";

        public string RiskText => SelectedCommand?.Risk switch
        {
            "Destructive" => "该命令会清空/回滚数据，执行前请确认目标。",
            "Mutating" => "该命令会修改镜像或更新状态。",
            _ => "只读或文件级操作。",
        };

        public bool ShowLiveWarning => SelectedCommand?.MayRebootOrMutateLive == true;

        // ── 执行目标 ──
        [ObservableProperty] private bool _isOfflineTarget = true;
        partial void OnIsOfflineTargetChanged(bool value)
        {
            OnPropertyChanged(nameof(IsLiveTarget));
            OnPropertyChanged(nameof(TargetHint));
        }
        public bool IsLiveTarget => !IsOfflineTarget;
        public string TargetHint => IsOfflineTarget
            ? "离线：对选定的 VHD/FFU 镜像执行（mountandinstall / stage+commit 需镜像路径）。"
            : "在线：对当前运行中的系统执行。仅当本程序运行在目标设备上时使用，桌面机上可能误改主机！";

        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _packagePath = string.Empty;
        [ObservableProperty] private string _extraArgs = string.Empty;

        // ── 运行状态 ──
        [ObservableProperty] private string _log = string.Empty;
        [ObservableProperty] private bool _isRunning;
        public bool IsNotRunning => !IsRunning;
        partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsNotRunning));

        private void RefreshCommands()
        {
            var list = UpdateAppUsageService.Parse(SelectedBuild);
            Commands = new ObservableCollection<UpdateAppCommandInfo>(list);
            SelectedCommand = list.FirstOrDefault();
            AppendLog($"[catalog] build {SelectedBuild}：{list.Count} 条命令（官方 usage 解析）。");
        }

        // ── 浏览 ──
        [RelayCommand]
        private void BrowseImage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择离线镜像（VHD / VHDX / FFU）",
                Filter = "镜像文件 (*.vhd;*.vhdx;*.ffu)|*.vhd;*.vhdx;*.ffu|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true) ImagePath = dlg.FileName;
        }

        [RelayCommand]
        private void BrowsePackages()
        {
            var dlg = new OpenFolderDialog { Title = "选择包/动作清单目录" };
            if (dlg.ShowDialog() == true) PackagePath = dlg.FolderName;
        }

        // ── 执行 ──
        [RelayCommand]
        private async Task RunSelectedAsync()
        {
            if (SelectedCommand == null) return;
            var cmd = SelectedCommand.Name;

            string exe = ResolveUpdateAppExe();
            if (string.IsNullOrEmpty(exe))
            {
                AppendLog($"[runner] build {SelectedBuild} 的 updateapp.exe 未找到。请先在「工具 → ADK 工具下载」安装该版本。");
                return;
            }

            var args = BuildArguments(cmd);
            if (args == null) return; // 校验失败已在日志说明

            if (SelectedCommand.Risk == "Destructive")
            {
                var mb = MessageBox.Show(
                    $"「{cmd}」会清空/回滚更新数据，确认继续？",
                    "危险命令确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (mb != MessageBoxResult.Yes) return;
            }

            var logPath = Path.Combine(Path.GetTempPath(),
                $"updateapp_{SelectedBuild}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            IsRunning = true;
            _runCts = new CancellationTokenSource();
            AppendLog($"[runner] 目标：{(IsOfflineTarget ? "离线镜像" : "在线系统")}；exe={exe}");
            AppendLog($"[runner] updateapp.exe {args}");
            try
            {
                var code = await UpdateAppRunnerService.RunAsync(
                    exe, args, logPath,
                    line => Application.Current?.Dispatcher.Invoke(() => AppendLog(line)),
                    _runCts.Token);
                AppendLog(code == null ? "[runner] 执行结束（未取到退出码）。" : $"[runner] 退出码 {code}。");
                AppendLog($"[runner] 完整日志：{logPath}");
            }
            finally
            {
                IsRunning = false;
                _runCts.Dispose();
                _runCts = null;
            }
        }

        [RelayCommand]
        private void CancelRun() => _runCts?.Cancel();

        private string ResolveUpdateAppExe()
        {
            var p = AdkToolService.FindToolPath(SelectedBuild, "updateapp.exe");
            return p ?? string.Empty;
        }

        /// <summary>按官方模板拼装参数；离线模式下自动补镜像/包路径。</summary>
        private string? BuildArguments(string cmd)
        {
            var parts = new System.Collections.Generic.List<string>();

            if (IsOfflineTarget)
            {
                switch (cmd)
                {
                    case "mountandinstall":
                        if (string.IsNullOrWhiteSpace(ImagePath))
                        { AppendLog("[validate] mountandinstall 需要镜像路径，请先选择 VHD/FFU。"); return null; }
                        if (!File.Exists(ImagePath) && !Directory.Exists(ImagePath))
                        { AppendLog("[validate] 镜像路径不存在。"); return null; }
                        parts.Add(Quote(ImagePath));
                        break;
                    case "install":
                    case "installinprocnoreboot":
                    case "installfromsd":
                    case "installfromsdinprocnoreboot":
                    case "stage":
                    case "stageinproc":
                    case "stagetosd":
                    case "stagetosdinproc":
                        if (string.IsNullOrWhiteSpace(PackagePath))
                        { AppendLog($"[validate] {cmd} 需要包/动作清单路径。"); return null; }
                        break;
                }
                if (cmd != "mountandinstall" && !string.IsNullOrWhiteSpace(PackagePath))
                    parts.Add(Quote(PackagePath));
            }
            else
            {
                // 在线：不需要镜像路径；包路径仍按需
                if (!string.IsNullOrWhiteSpace(PackagePath)) parts.Add(Quote(PackagePath));
            }

            if (!string.IsNullOrWhiteSpace(ExtraArgs)) parts.Add(ExtraArgs.Trim());

            return $"{cmd} {string.Join(" ", parts)}".Trim();
        }

        private static string Quote(string p) => $"\"{p}\"";

        private void AppendLog(string line)
        {
            Log += line + Environment.NewLine;
        }
    }
}
