using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class DriverExtractorWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private ObservableCollection<DriverItemViewModel> _allDrivers = new();
        private ObservableCollection<DriverItemViewModel> _filteredDrivers = new();

        private bool _hasDrivers;
        public bool HasDrivers
        {
            get => _hasDrivers;
            set { _hasDrivers = value; OnPropertyChanged(nameof(HasDrivers)); }
        }

        private bool _hasSelectedDrivers;
        public bool HasSelectedDrivers
        {
            get => _hasSelectedDrivers;
            set { _hasSelectedDrivers = value; OnPropertyChanged(nameof(HasSelectedDrivers)); }
        }

        private int _totalPackages;
        public int TotalPackages
        {
            get => _totalPackages;
            set { _totalPackages = value; OnPropertyChanged(nameof(TotalPackages)); }
        }

        private int _totalHardwareIds;
        public int TotalHardwareIds
        {
            get => _totalHardwareIds;
            set { _totalHardwareIds = value; OnPropertyChanged(nameof(TotalHardwareIds)); }
        }

        private int _selectedCount;
        public int SelectedCount
        {
            get => _selectedCount;
            set { _selectedCount = value; OnPropertyChanged(nameof(SelectedCount)); UpdateHasSelected(); }
        }

        private string _totalSizeDisplay = "";
        public string TotalSizeDisplay
        {
            get => _totalSizeDisplay;
            set { _totalSizeDisplay = value; OnPropertyChanged(nameof(TotalSizeDisplay)); }
        }

        public DriverExtractorWindow()
        {
            InitializeComponent();
            DataContext = this;
            LstDrivers.ItemsSource = _filteredDrivers;

            // 默认目录相对当前用户的桌面推导，避免把某台机器上的绝对路径写死在 XAML 里。
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            TxtImagePath.Text = Path.Combine(desktop, "MobilePackageGen_Output", "component");
            TxtOutputPath.Text = Path.Combine(desktop, "Drivers_Extracted");
        }

        public DriverExtractorWindow(string imagePath) : this()
        {
            TxtImagePath.Text = imagePath;
        }

        private void BrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择镜像根目录" };
            if (dialog.ShowDialog() == true)
            {
                TxtImagePath.Text = dialog.FolderName;
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择输出目录" };
            if (dialog.ShowDialog() == true)
            {
                TxtOutputPath.Text = dialog.FolderName;
            }
        }

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            string imagePath = TxtImagePath.Text.Trim();
            if (string.IsNullOrEmpty(imagePath) || !Directory.Exists(imagePath))
            {
                MessageBox.Show("请选择有效的镜像目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                TxtStatus.Text = "正在扫描驱动...";
                Cursor = System.Windows.Input.Cursors.Wait;

                var packages = DriverExtractor.ScanDrivers(imagePath);

                _allDrivers.Clear();
                _filteredDrivers.Clear();

                foreach (var package in packages)
                {
                    var item = new DriverItemViewModel(package);
                    _allDrivers.Add(item);
                    _filteredDrivers.Add(item);
                }

                HasDrivers = packages.Count > 0;
                TotalPackages = packages.Count;
                TotalHardwareIds = packages.Sum(p => p.HardwareIds.Count);
                long totalSize = packages.Sum(p => p.TotalSize);
                TotalSizeDisplay = $"{Math.Round(totalSize / 1024.0 / 1024.0, 1)} MB";

                TxtStatus.Text = $"扫描完成，找到 {packages.Count} 个驱动包";
                Cursor = null;

                if (packages.Count == 0)
                {
                    MessageBox.Show("未找到任何驱动包", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"扫描失败: {ex.Message}";
                Cursor = null;
                MessageBox.Show($"扫描失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allDrivers.Where(d => d.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选择要提取的驱动", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExtractDrivers(selected);
        }

        private void ExtractAll_Click(object sender, RoutedEventArgs e)
        {
            if (_allDrivers.Count == 0)
            {
                MessageBox.Show("请先扫描驱动", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExtractDrivers(_allDrivers.ToList());
        }

        private void ExtractDrivers(List<DriverItemViewModel> drivers)
        {
            string outputPath = TxtOutputPath.Text.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("请选择输出目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                TxtStatus.Text = $"正在提取 {drivers.Count} 个驱动...";
                Cursor = System.Windows.Input.Cursors.Wait;

                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                int success = 0;
                foreach (var driver in drivers)
                {
                    try
                    {
                        // 直接复制文件
                        string driverDir = Path.Combine(outputPath,
                            string.IsNullOrEmpty(driver.Package.Class) ? "Unknown" : SanitizeFileName(driver.Package.Class),
                            SanitizeFileName(Path.GetFileNameWithoutExtension(driver.Package.InfFileName)));

                        if (!Directory.Exists(driverDir))
                        {
                            Directory.CreateDirectory(driverDir);
                        }

                        string infDir = Path.GetDirectoryName(driver.Package.InfFilePath) ?? "";

                        // 复制INF
                        File.Copy(driver.Package.InfFilePath, Path.Combine(driverDir, driver.Package.InfFileName), true);

                        // 复制源文件
                        foreach (var file in driver.Package.SourceFiles)
                        {
                            string sourcePath = Path.Combine(infDir, file);
                            if (File.Exists(sourcePath))
                            {
                                string destPath = Path.Combine(driverDir, file);
                                string? destDir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                {
                                    Directory.CreateDirectory(destDir);
                                }
                                File.Copy(sourcePath, destPath, true);
                            }
                        }

                        // 复制CAT
                        foreach (var catFile in driver.Package.CatalogFiles)
                        {
                            string sourcePath = Path.Combine(infDir, catFile);
                            if (File.Exists(sourcePath))
                            {
                                File.Copy(sourcePath, Path.Combine(driverDir, catFile), true);
                            }
                        }

                        success++;
                    }
                    catch { }
                }

                // 生成报告和安装脚本
                var packages = drivers.Select(d => d.Package).ToList();
                GenerateReport(packages, outputPath);
                GenerateInstallScript(packages, outputPath);

                TxtStatus.Text = $"提取完成，成功 {success}/{drivers.Count} 个驱动";
                Cursor = null;

                MessageBox.Show($"提取完成！\n\n成功: {success}/{drivers.Count}\n输出目录: {outputPath}",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"提取失败: {ex.Message}";
                Cursor = null;
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateReport(List<DriverExtractor.DriverPackage> packages, string outputPath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 驱动提取报告 ===");
            sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"驱动包总数: {packages.Count}");
            sb.AppendLine();

            var grouped = packages.GroupBy(p => string.IsNullOrEmpty(p.Class) ? "Unknown" : p.Class);
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                sb.AppendLine($"--- {group.Key} ({group.Count()} 个) ---");
                foreach (var package in group)
                {
                    sb.AppendLine($"  {package.InfFileName} - {package.Description}");
                    sb.AppendLine($"    提供商: {package.Provider}, 版本: {package.DriverVersion}");
                    foreach (var hwId in package.HardwareIds.Take(3))
                    {
                        sb.AppendLine($"    {hwId}");
                    }
                    if (package.HardwareIds.Count > 3)
                    {
                        sb.AppendLine($"    ... 还有 {package.HardwareIds.Count - 3} 个");
                    }
                    sb.AppendLine();
                }
            }

            File.WriteAllText(Path.Combine(outputPath, "driver_report.txt"), sb.ToString(), System.Text.Encoding.UTF8);
        }

        private void GenerateInstallScript(List<DriverExtractor.DriverPackage> packages, string outputPath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("echo === 驱动批量安装 ===");
            sb.AppendLine();

            int index = 1;
            foreach (var package in packages)
            {
                string relativePath = Path.Combine(
                    string.IsNullOrEmpty(package.Class) ? "Unknown" : SanitizeFileName(package.Class),
                    SanitizeFileName(Path.GetFileNameWithoutExtension(package.InfFileName)),
                    package.InfFileName);

                sb.AppendLine($"echo [{index}/{packages.Count}] {package.InfFileName}");
                sb.AppendLine($"pnputil /add-driver \"%~dp0{relativePath}\" /install");
                sb.AppendLine();
                index++;
            }

            sb.AppendLine("echo 完成");
            sb.AppendLine("pause");

            File.WriteAllText(Path.Combine(outputPath, "install_drivers.bat"), sb.ToString(), System.Text.Encoding.GetEncoding("GB2312"));
        }

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = TxtFilter.Text.Trim().ToLower();
            _filteredDrivers.Clear();

            foreach (var driver in _allDrivers)
            {
                if (string.IsNullOrEmpty(filter) ||
                    driver.InfFileName.ToLower().Contains(filter) ||
                    driver.Description.ToLower().Contains(filter) ||
                    driver.Provider.ToLower().Contains(filter) ||
                    driver.Package.HardwareIds.Any(h => h.ToLower().Contains(filter)))
                {
                    _filteredDrivers.Add(driver);
                }
            }
        }

        private void LstDrivers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 可以在这里显示选中驱动的详细信息
        }

        private void DriverCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSelectedCount();
        }

        private void DriverCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            SelectedCount = _allDrivers.Count(d => d.IsSelected);
        }

        private void UpdateHasSelected()
        {
            HasSelectedDrivers = SelectedCount > 0;
        }

        private void ChkSelectAll_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var driver in _filteredDrivers)
            {
                driver.IsSelected = true;
            }
            UpdateSelectedCount();
        }

        private void ChkSelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var driver in _filteredDrivers)
            {
                driver.IsSelected = false;
            }
            UpdateSelectedCount();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 驱动项ViewModel
    /// </summary>
    public class DriverItemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public DriverExtractor.DriverPackage Package { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string Class => string.IsNullOrEmpty(Package.Class) ? "Unknown" : Package.Class;
        public string InfFileName => Package.InfFileName;
        public string Description => Package.Description;
        public string Provider => Package.Provider;
        public string DriverVersion => Package.DriverVersion;
        public int HardwareIdCount => Package.HardwareIds.Count;
        public string SizeDisplay => Package.TotalSize > 0 ? $"{Math.Round(Package.TotalSize / 1024.0, 0)} KB" : "";

        public DriverItemViewModel(DriverExtractor.DriverPackage package)
        {
            Package = package;
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
