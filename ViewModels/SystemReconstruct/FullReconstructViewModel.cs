using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using MobilePackageGen;
using MobilePackageGen.Adapters;
using OneCore_Image_Designer.Helpers;

namespace OneCore_Image_Designer.ViewModels.SystemReconstruct
{
    /// <summary>
    /// 完全构建视图模型 - 提取镜像中的全部内容
    /// </summary>
    public class FullReconstructViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _mainVM;

        public FullReconstructViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            GoBackCommand = new RelayCommand(GoBack);
            BrowseImageCommand = new RelayCommand(BrowseImage);
            BrowseOutputCommand = new RelayCommand(BrowseOutput);
            StartBuildCommand = new RelayCommand(StartBuild, () => !IsBuilding);
        }

        // === 镜像文件路径 ===
        private string _imagePath = string.Empty;
        public string ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsImageValid)); }
        }

        public bool IsImageValid => File.Exists(ImagePath);

        // === 输出文件夹 ===
        private string _outputPath = string.Empty;
        public string OutputPath
        {
            get => _outputPath;
            set { _outputPath = value; OnPropertyChanged(); }
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

        private int _progress;
        public int Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        // === 命令 ===
        public RelayCommand GoBackCommand { get; }
        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand BrowseOutputCommand { get; }
        public RelayCommand StartBuildCommand { get; }

        public void BrowseImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择镜像文件 (FFU/VHDX/WIM)",
                Filter = "镜像文件 (*.ffu;*.vhdx;*.vhd;*.wim)|*.ffu;*.vhdx;*.vhd;*.wim|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
                dialog.FileName = ImagePath;
            if (dialog.ShowDialog() == true)
            {
                ImagePath = dialog.FileName;
            }
        }

        public void BrowseOutput()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择输出文件夹",
                InitialDirectory = Directory.Exists(OutputPath) ? OutputPath : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog() == true)
            {
                OutputPath = dialog.FolderName;
            }
        }

        public async void StartBuild()
        {
            if (!IsImageValid)
            {
                MessageBox.Show("请选择有效的镜像文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(OutputPath))
            {
                MessageBox.Show("请选择输出文件夹。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            IsBuilding = true;
            BuildStatus = "构建中...";
            Progress = 0;

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "加载镜像中..."; Progress = 10; });
                        var disks = DiskLoader.LoadDisks([ImagePath]);
                        if (disks == null || !disks.Any())
                        {
                            Application.Current.Dispatcher.Invoke(() => BuildStatus = "加载失败: 无磁盘");
                            return;
                        }

                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "获取更新历史..."; Progress = 20; });
                        var updateHistory = BuildMetadataHandler.GetUpdateHistory(disks);

                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建CBS包..."; Progress = 35; });
                        CBSBuilder.BuildCBS(disks, OutputPath, updateHistory);

                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建SPKG包..."; Progress = 55; });
                        SPKGBuilder.BuildSPKG(disks, OutputPath, updateHistory);

                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建Driver包..."; Progress = 75; });
                        DriverBuilder.BuildDrivers(disks, OutputPath, updateHistory);

                        Application.Current.Dispatcher.Invoke(() => { BuildStatus = "提取FM文件..."; Progress = 90; });
                        ExtractFMFiles(disks, OutputPath);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Progress = 100;
                            BuildStatus = "构建完成";
                            MessageBox.Show($"完全构建完成！\n\n输出目录: {OutputPath}", "构建完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            BuildStatus = "构建失败";
                            MessageBox.Show($"构建失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
            finally
            {
                IsBuilding = false;
            }
        }

        private void ExtractFMFiles(System.Collections.Generic.IEnumerable<IDisk> disks, string outputPath)
        {
            var fmDirs = new[] { "Microsoft", "OEM" };
            var basePath = @"Windows\ImageUpdate\FeatureManifest";

            foreach (var disk in disks)
            {
                foreach (var partition in disk.Partitions)
                {
                    var fs = partition.FileSystem;
                    if (fs == null) continue;

                    foreach (var vendor in fmDirs)
                    {
                        var dirPath = Path.Combine(basePath, vendor);
                        try
                        {
                            if (!fs.DirectoryExists(dirPath)) continue;
                            var files = fs.GetFiles(dirPath, "*.xml", SearchOption.TopDirectoryOnly);
                            foreach (var file in files)
                            {
                                var name = Path.GetFileName(file);
                                var destPath = Path.Combine(outputPath, "FMFiles", vendor, name);
                                var destDir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                    Directory.CreateDirectory(destDir);

                                using var srcStream = fs.OpenFile(file, FileMode.Open, FileAccess.Read);
                                using var destStream = File.Create(destPath);
                                srcStream.CopyTo(destStream);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void GoBack()
        {
            _mainVM.NavigateToSystemReconstruct();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
