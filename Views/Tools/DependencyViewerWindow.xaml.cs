using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class DependencyViewerWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private DependencyAnalyzer.AnalysisResult? _result;
        private ObservableCollection<DependencyAnalyzer.DependencyNode> _filteredPackages = new();

        public bool HasResult => _result != null;
        public int TotalPackages => _result?.TotalPackages ?? 0;
        public int TotalDependencies => _result?.TotalDependencies ?? 0;
        public int MissingCount => _result?.MissingDependencies.Count ?? 0;
        public int CircularCount => _result?.CircularDependencies.Count ?? 0;

        private DependencyAnalyzer.DependencyNode? _selectedPackage;
        public DependencyAnalyzer.DependencyNode? SelectedPackage
        {
            get => _selectedPackage;
            set
            {
                _selectedPackage = value;
                OnPropertyChanged(nameof(SelectedPackage));
            }
        }

        public DependencyViewerWindow()
        {
            InitializeComponent();
            DataContext = this;
            LstPackages.ItemsSource = _filteredPackages;

            // 默认目录相对当前用户的桌面推导，避免把某台机器上的绝对路径写死在 XAML 里。
            TxtDirectory.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "MobilePackageGen_Output", "component", "_temp_manifests");
        }

        private void Analyze_Click(object sender, RoutedEventArgs e)
        {
            string directory = TxtDirectory.Text.Trim();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择Manifest目录"
                };
                if (dialog.ShowDialog() == true)
                {
                    directory = dialog.FolderName;
                    TxtDirectory.Text = directory;
                }
                else
                {
                    return;
                }
            }

            try
            {
                _result = DependencyAnalyzer.AnalyzeFromDirectory(directory);
                _filteredPackages.Clear();

                foreach (var node in _result.AllNodes.Where(n => !n.IsMissing).OrderBy(n => n.Name))
                {
                    _filteredPackages.Add(node);
                }

                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(TotalPackages));
                OnPropertyChanged(nameof(TotalDependencies));
                OnPropertyChanged(nameof(MissingCount));
                OnPropertyChanged(nameof(CircularCount));

                MessageBox.Show($"分析完成！\n\n包总数: {TotalPackages}\n依赖关系: {TotalDependencies}\n缺失依赖: {MissingCount}\n循环依赖: {CircularCount}",
                    "分析完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null)
            {
                MessageBox.Show("请先进行分析。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出依赖关系报告",
                FileName = $"dependency_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filter = "文本文件|*.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string report = DependencyAnalyzer.GenerateReport(_result);
                    File.WriteAllText(dialog.FileName, report, System.Text.Encoding.UTF8);
                    MessageBox.Show($"报告已导出到:\n{dialog.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LstPackages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedPackage = LstPackages.SelectedItem as DependencyAnalyzer.DependencyNode;
        }

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_result == null) return;

            string filter = TxtFilter.Text.Trim().ToLower();
            _filteredPackages.Clear();

            var query = _result.AllNodes.Where(n => !n.IsMissing);
            if (!string.IsNullOrEmpty(filter))
            {
                query = query.Where(n => n.Name.ToLower().Contains(filter));
            }

            foreach (var node in query.OrderBy(n => n.Name))
            {
                _filteredPackages.Add(node);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
