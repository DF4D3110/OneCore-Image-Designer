using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>
    /// addonkit 工作区页：承载内置 Microsoft iot-adk-addonkit 的状态检测、示例工作区、
    /// 产品/BSP 发现，以及常用命令（建包 / 建 FFU / 建 Recovery / 导入示例包）的提权执行。
    /// </summary>
    public partial class AddonKitViewModel : ObservableObject
    {
        private readonly MainViewModel _mainVM;
        private readonly AdkToolService _adk = new();
        private readonly AddonKitCommandService _cmd = new();
        private CancellationTokenSource? _cts;

        public AddonKitViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            RefreshState();
        }

        // === 状态 ===
        [ObservableProperty] private string _status = "";
        [ObservableProperty] private bool _isBusy;
        public bool IsNotBusy => !IsBusy;

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));
        [ObservableProperty] private string _moduleStatus = "";
        [ObservableProperty] private string _workspacePath = AddonKitShellService.SampleWorkspaceXml;
        [ObservableProperty] private bool _useSampleWorkspace = true;
        [ObservableProperty] private string _preflightSummary = "尚未运行检查";
        [ObservableProperty] private bool _preflightHasErrors;
        [ObservableProperty] private bool _allowRunDespiteErrors;

        // === 命令输入 ===
        public ObservableCollection<string> ProductNames { get; } = new();
        public ObservableCollection<string> BspNames { get; } = new();
        [ObservableProperty] private string _selectedProduct = "";
        [ObservableProperty] private string _configuration = "Test";
        [ObservableProperty] private string _buildNumber = "17763";

        public ObservableCollection<string> ConfigurationChoices { get; } = new() { "Test", "Retail" };

        public ObservableCollection<string> LogLines { get; } = new();

        public bool IsWorkspaceValid => File.Exists(WorkspacePath);

        [RelayCommand]
        public void OpenAdkDownload() => _mainVM.OpenAdkTool();

        [RelayCommand]
        public void LaunchShell()
        {
            var log = new Progress<string>(AppendLog);
            if (!AddonKitShellService.LaunchShell(SafeWorkspace(), log))
                AppendLog("无法启动 shell，请检查模块与工作区路径。");
        }

        [RelayCommand]
        public void BrowseWorkspace()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 IoTWorkspace.xml",
                Filter = "IoT Workspace (*.xml)|*.xml|All Files (*.*)|*.*"
            };
            if (File.Exists(WorkspacePath)) dlg.InitialDirectory = Path.GetDirectoryName(WorkspacePath);
            if (dlg.ShowDialog() == true)
            {
                WorkspacePath = dlg.FileName;
                UseSampleWorkspace = false;
                RefreshState();
            }
        }

        [RelayCommand]
        public void RefreshState()
        {
            ModuleStatus = AddonKitShellService.IsModuleAvailable
                ? "✓ 内置 IoTCoreImaging 模块就绪"
                : "✗ 模块缺失：" + AddonKitShellService.ModuleManifest;

            var installed = AdkToolService.GetInstalledBuilds();
            Status = installed.Count > 0
                ? $"已安装 ADK：{string.Join(", ", installed)}"
                : "尚未安装任何 ADK 工具包，请先在『ADK 工具下载』中下载对应 build。";

            OnPropertyChanged(nameof(IsWorkspaceValid));
        }

        [RelayCommand]
        public async Task DiscoverProductsAsync()
        {
            var ws = SafeWorkspace();
            if (!Directory.Exists(ws))
            {
                AppendLog("工作区目录不存在：" + Path.GetDirectoryName(ws));
                return;
            }
            ProductNames.Clear();
            BspNames.Clear();
            try
            {
                foreach (var p in await _adk.GetProductsAsync(ws)) ProductNames.Add(p);
                foreach (var b in await _adk.GetBSPsAsync(ws)) BspNames.Add(b);
                SelectedProduct = ProductNames.Count > 0 ? ProductNames[0] : "";
                AppendLog($"发现 {ProductNames.Count} 个产品、{BspNames.Count} 个 BSP。");
            }
            catch (Exception ex) { AppendLog("枚举失败：" + ex.Message); }
        }

        [RelayCommand]
        public void RunPreflight()
        {
            var report = AddonKitPreflightService.Check(SafeWorkspace(), BuildNumber, Configuration);
            PreflightSummary = report.Summarize();
            PreflightHasErrors = report.HasErrors;
            if (!report.HasErrors) AllowRunDespiteErrors = false;
            AppendLog(report.HasErrors
                ? "[preflight] 存在阻塞问题，详见状态区。"
                : "[preflight] 检查通过。");
        }

        [RelayCommand]
        public Task ImportSamplePackagesAsync() =>
            RunCommand("Import-IoTOEMPackage *", "导入示例包");

        [RelayCommand]
        public Task BuildPackagesAsync() =>
            RunCommand("New-IoTCabPackage All", "构建全部包");

        [RelayCommand]
        public Task BuildFfuAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedProduct))
            {
                AppendLog("请先选择产品（或点『发现产品/BSP』）。");
                return Task.CompletedTask;
            }
            return RunCommand($"New-IoTFFUImage '{Q(SelectedProduct)}' '{Q(Configuration)}'", "构建 FFU");
        }

        [RelayCommand]
        public Task BuildRecoveryAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedProduct))
            {
                AppendLog("请先选择产品。");
                return Task.CompletedTask;
            }
            return RunCommand($"New-IoTRecoveryImage '{Q(SelectedProduct)}' '{Q(Configuration)}'", "构建 Recovery");
        }

        [RelayCommand]
        public void CancelOperation() => _cts?.Cancel();

        private string SafeWorkspace() =>
            UseSampleWorkspace ? AddonKitShellService.SampleWorkspaceXml : WorkspacePath;

        private async Task RunCommand(string command, string label)
        {
            if (IsBusy) return;
            var ws = SafeWorkspace();
            if (!File.Exists(ws))
            {
                AppendLog($"工作区 XML 不存在：{ws}");
                return;
            }

            // preflight 拦截：有阻塞项且未勾选“忽略检查”时不提交提权命令
            var report = AddonKitPreflightService.Check(ws, BuildNumber, Configuration);
            PreflightSummary = report.Summarize();
            PreflightHasErrors = report.HasErrors;
            if (report.HasErrors && !AllowRunDespiteErrors)
            {
                AppendLog($"[preflight] {label} 已拦截：{Environment.NewLine}{report.Summarize()}");
                AppendLog("确认可接受风险后勾选“忽略检查仍然运行”，再点一次。");
                return;
            }
            if (report.HasErrors)
                AppendLog($"[preflight] {label}：用户选择忽略检查，继续执行。");

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var log = new Progress<string>(AppendLog);
            try
            {
                AppendLog($"—— {label} ——");
                var code = await _cmd.RunWorkspaceCommandAsync(ws, command, log, _cts.Token);
                Status = code == 0 ? $"{label}完成" : $"{label}失败（退出码 {code}）";
            }
            catch (OperationCanceledException)
            {
                AppendLog("操作已取消。");
            }
            catch (Exception ex)
            {
                AppendLog($"{label}异常：{ex.Message}");
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        private void AppendLog(string text)
        {
            void add() => LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (Application.Current?.Dispatcher.CheckAccess() == true) add();
            else Application.Current?.Dispatcher.Invoke(add);
        }

        /// <summary>PowerShell 单引号字符串内转义（此处输入均为受控短串，防注入）。</summary>
        private static string Q(string s) => (s ?? "").Replace("'", "''");
    }
}
