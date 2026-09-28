using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels.ImageBuild
{
    /// <summary>
    /// 镜像构建视图模型
    /// 参考 DevImgGen BuildPage 实现
    /// 支持 imggen.cmd 和 imageapp.exe 两种构建方式
    /// </summary>
    public class ImageBuildViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _mainVM;

        public ImageBuildViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            Architectures = new ObservableCollection<string> { "x86", "AMD64", "ARM", "ARM64" };
            OutputTypes = new ObservableCollection<string> { "ffu", "vhdx" };
            BuildTools = new ObservableCollection<string> { "imageapp.exe", "imggen.cmd" };
            SelectedArchitecture = "ARM64";
            SelectedOutputType = "ffu";
            SelectedBuildTool = "imageapp.exe";

            BrowseOSPackageDirCommand = new RelayCommand(BrowseOSPackageDir);
            BrowseOEMInputCommand = new RelayCommand(BrowseOEMInput);
            BrowseBuildToolCommand = new RelayCommand(BrowseBuildTool);
            BrowseOutputCommand = new RelayCommand(BrowseOutput);
            StartBuildCommand = new RelayCommand(StartBuild, () => !IsBuilding);
            GoBackCommand = new RelayCommand(GoBack);
        }

        // === OS包目录 ===
        private string _osPackageDir = string.Empty;
        public string OSPackageDir
        {
            get => _osPackageDir;
            set { _osPackageDir = value; OnPropertyChanged(); OnPropertyChanged(nameof(RetailArchFrePath)); OnPropertyChanged(nameof(IsRetailPathValid)); TryAutoDiscoverBuildTool(); }
        }

        public string RetailArchFrePath => Path.Combine(OSPackageDir, "Retail", SelectedArchitecture, "fre");
        public bool IsRetailPathValid => Directory.Exists(RetailArchFrePath);

        // === OEMInput文件 ===
        private string _oemInputPath = string.Empty;
        public string OEMInputPath
        {
            get => _oemInputPath;
            set { _oemInputPath = value; OnPropertyChanged(); }
        }

        // === 构建工具路径 ===
        private string _buildToolPath = string.Empty;
        public string BuildToolPath
        {
            get => _buildToolPath;
            set { _buildToolPath = value; OnPropertyChanged(); }
        }

        // === 构建工具类型 ===
        public ObservableCollection<string> BuildTools { get; }

        private string _selectedBuildTool = "imageapp.exe";
        public string SelectedBuildTool
        {
            get => _selectedBuildTool;
            set { _selectedBuildTool = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsImageApp)); OnPropertyChanged(nameof(IsImggenCmd)); OnPropertyChanged(nameof(BuildToolLabel)); TryAutoDiscoverBuildTool(); }
        }

        public bool IsImageApp => SelectedBuildTool == "imageapp.exe";
        public bool IsImggenCmd => SelectedBuildTool == "imggen.cmd";
        public string BuildToolLabel => IsImageApp ? "imageapp.exe" : "imggen.cmd";

        /// <summary>
        /// 按 OS 包路径中出现的 build 号，自动在已内置安装的 ADK（addonkit\adk\&lt;build&gt;\）中定位工具；
        /// 命中即填入路径（用户仍可手动浏览覆盖）。未命中则不动现有值。
        /// </summary>
        private void TryAutoDiscoverBuildTool()
        {
            if (string.IsNullOrWhiteSpace(OSPackageDir)) return;
            foreach (var build in AdkToolService.GetInstalledBuilds())
            {
                if (OSPackageDir.IndexOf(build.ToString(), StringComparison.Ordinal) < 0) continue;
                var found = AdkToolService.FindToolPath(build, BuildToolLabel);
                if (found != null)
                {
                    BuildToolPath = found;
                    BuildStatus = $"已自动发现 ADK {build} 的 {BuildToolLabel}";
                    return;
                }
            }
        }

        // === 架构选择 ===
        public ObservableCollection<string> Architectures { get; }

        private string _selectedArchitecture = "ARM64";
        public string SelectedArchitecture
        {
            get => _selectedArchitecture;
            set { _selectedArchitecture = value; OnPropertyChanged(); OnPropertyChanged(nameof(RetailArchFrePath)); OnPropertyChanged(nameof(IsRetailPathValid)); }
        }

        // === 输出文件类型 ===
        public ObservableCollection<string> OutputTypes { get; }

        private string _selectedOutputType = "ffu";
        public string SelectedOutputType
        {
            get => _selectedOutputType;
            set { _selectedOutputType = value; OnPropertyChanged(); UpdateOutputExtension(); }
        }

        // === 输出文件路径 ===
        private string _outputPath = string.Empty;
        public string OutputPath
        {
            get => _outputPath;
            set { _outputPath = value; OnPropertyChanged(); }
        }

        private void UpdateOutputExtension()
        {
            if (string.IsNullOrEmpty(OutputPath))
                return;
            var dir = Path.GetDirectoryName(OutputPath);
            var name = Path.GetFileNameWithoutExtension(OutputPath);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
                OutputPath = Path.Combine(dir, $"{name}.{SelectedOutputType}");
        }

        // === 构建状态 ===
        private bool _isBuilding;
        public bool IsBuilding
        {
            get => _isBuilding;
            set { _isBuilding = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBuilding)); }
        }

        public bool IsNotBuilding => !IsBuilding;

        private string _buildStatus = "就绪";
        public string BuildStatus
        {
            get => _buildStatus;
            set { _buildStatus = value; OnPropertyChanged(); }
        }

        // === 命令 ===
        public RelayCommand BrowseOSPackageDirCommand { get; }
        public RelayCommand BrowseOEMInputCommand { get; }
        public RelayCommand BrowseBuildToolCommand { get; }
        public RelayCommand BrowseOutputCommand { get; }
        public RelayCommand StartBuildCommand { get; }
        public RelayCommand GoBackCommand { get; }

        // === 浏览OS包目录 ===
        public void BrowseOSPackageDir()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 OS 包根目录 (包含 Retail\\<架构>\\fre)",
                InitialDirectory = Directory.Exists(OSPackageDir) ? OSPackageDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog() == true)
            {
                OSPackageDir = dialog.FolderName;
            }
        }

        // === 浏览OEMInput ===
        public void BrowseOEMInput()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 OEMInput.xml",
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                InitialDirectory = Directory.Exists(OSPackageDir) ? OSPackageDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (!string.IsNullOrEmpty(OEMInputPath) && File.Exists(OEMInputPath))
                dialog.FileName = OEMInputPath;
            if (dialog.ShowDialog() == true)
            {
                OEMInputPath = dialog.FileName;
            }
        }

        // === 浏览构建工具 ===
        public void BrowseBuildTool()
        {
            var dialog = new OpenFileDialog
            {
                Title = IsImageApp ? "选择 imageapp.exe" : "选择 imggen.cmd",
                Filter = IsImageApp
                    ? "Executable (*.exe)|*.exe|All Files (*.*)|*.*"
                    : "Command Script (*.cmd;*.bat)|*.cmd;*.bat|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (!string.IsNullOrEmpty(BuildToolPath) && File.Exists(BuildToolPath))
                dialog.FileName = BuildToolPath;
            if (dialog.ShowDialog() == true)
            {
                BuildToolPath = dialog.FileName;
            }
        }

        // === 浏览输出文件 ===
        public void BrowseOutput()
        {
            var dialog = new SaveFileDialog
            {
                Title = "选择输出镜像文件",
                Filter = $"{SelectedOutputType.ToUpper()} Files (*.{SelectedOutputType})|*.{SelectedOutputType}|All Files (*.*)|*.*",
                FileName = $"flash.{SelectedOutputType}",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog() == true)
            {
                OutputPath = dialog.FileName;
            }
        }

        // === 开始构建 ===
        public void StartBuild()
        {
            // 验证
            if (!Directory.Exists(OSPackageDir))
            {
                MessageBox.Show("OS 包目录不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!IsRetailPathValid)
            {
                MessageBox.Show($"在 OS 包目录中未找到必需路径:\n{RetailArchFrePath}\n\n请检查架构选择是否正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(OEMInputPath))
            {
                MessageBox.Show("OEMInput XML 文件不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(BuildToolPath))
            {
                MessageBox.Show($"{BuildToolLabel} 路径不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                MessageBox.Show("输出目录不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                Process process;

                if (IsImageApp)
                {
                    // imageapp.exe: "<output>" "<OEMInput>" "<pkgDir>" +StrictSettingPolicies /CPUType:<架构>
                    var arguments = $"\"{OutputPath}\" \"{OEMInputPath}\" \"{OSPackageDir}\" +StrictSettingPolicies /CPUType:{SelectedArchitecture}";
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = BuildToolPath,
                            Arguments = arguments,
                            WorkingDirectory = Path.GetDirectoryName(BuildToolPath) ?? string.Empty,
                            UseShellExecute = true,
                            Verb = "runas"
                        }
                    };
                }
                else
                {
                    // imggen.cmd: 通过 cmd.exe /k 启动，架构用小写
                    // cmd.exe /k <drive>: && cd "<imggenDir>" && "<imggen.cmd>" "<output>" "<OEMInput>" "<pkgDir>" <小写架构>
                    var imggenDir = Path.GetDirectoryName(Path.GetFullPath(BuildToolPath)) ?? string.Empty;
                    var imggenFileName = Path.GetFileName(BuildToolPath);
                    var drive = Path.GetPathRoot(imggenDir)?.TrimEnd('\\') ?? "C:";
                    var archLower = SelectedArchitecture.ToLowerInvariant();
                    var arguments = $"/k {drive} && cd \"{imggenDir}\" && \"{imggenFileName}\" \"{OutputPath}\" \"{OEMInputPath}\" \"{OSPackageDir}\" {archLower}";

                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = arguments,
                            UseShellExecute = true,
                            Verb = "runas"
                        }
                    };
                }

                process.Start();
                IsBuilding = true;
                BuildStatus = "构建中... (在新窗口中运行)";
                _mainVM.SetStatus("镜像构建中...");

                // 异步等待进程结束
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsBuilding = false;
                        var exitCode = process.ExitCode;
                        BuildStatus = exitCode == 0 ? "构建完成" : $"构建失败 (退出码: {exitCode})";
                        _mainVM.SetStatus(BuildStatus);
                        if (exitCode == 0 && File.Exists(OutputPath))
                        {
                            var fi = new FileInfo(OutputPath);
                            MessageBox.Show($"构建完成!\n\n输出文件: {OutputPath}\n大小: {fi.Length / 1024.0 / 1024.0:F1} MB", "构建完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else if (exitCode != 0)
                        {
                            MessageBox.Show($"构建失败，退出码: {exitCode}\n\n请查看构建窗口中的错误信息。", "构建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    });
                };
            }
            catch (Exception ex)
            {
                IsBuilding = false;
                BuildStatus = "启动失败";
                MessageBox.Show($"启动构建失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void GoBack()
        {
            _mainVM.NavigateToStart();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 简单命令实现
    /// </summary>
    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged;
    }
}
