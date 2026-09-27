using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class ComponentStoreVerifierWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private ComponentStoreVerifier.VerifyResult? _result;
        private ObservableCollection<ComponentStoreVerifier.VerifyItem> _filteredItems = new();

        public bool HasResult => _result != null;
        public int TotalComponents => _result?.TotalComponents ?? 0;
        public int NormalCount => _result?.Items.Count(i => i.Exists && !i.IsEmpty) ?? 0;
        public int MissingCount => _result?.MissingCount ?? 0;
        public int EmptyCount => _result?.EmptyCount ?? 0;

        /// <summary>因属 .inf 驱动类而跳过的组件数（其哈希无法复现，不计入缺失）。</summary>
        public int InfSkippedCount => _result?.InfSkippedCount ?? 0;

        /// <summary>解析失败的 manifest 数。非 0 时说明有 manifest 没能读出 assemblyIdentity。</summary>
        public int ParseFailedCount => _result?.ParseFailedCount ?? 0;

        public string TotalSizeDisplay => _result != null ? $"{Math.Round(_result.TotalSize / 1024.0 / 1024.0, 1)} MB" : "";

        public ComponentStoreVerifierWindow()
        {
            InitializeComponent();
            DataContext = this;
            LstResults.ItemsSource = _filteredItems;
        }

        private void BrowseManifest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择Manifest目录" };
            if (dialog.ShowDialog() == true)
            {
                TxtManifestDir.Text = dialog.FolderName;
            }
        }

        private void BrowseWinSxS_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择WinSxS目录" };
            if (dialog.ShowDialog() == true)
            {
                TxtWinSxSDir.Text = dialog.FolderName;
            }
        }

        private async void Verify_Click(object sender, RoutedEventArgs e)
        {
            string manifestDir = TxtManifestDir.Text.Trim();
            string winsxsDir = TxtWinSxSDir.Text.Trim();

            if (string.IsNullOrEmpty(manifestDir) || !Directory.Exists(manifestDir))
            {
                MessageBox.Show("请选择有效的Manifest目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // WinSxS 的 manifest 是 DCM 压缩存储的，需要逐个调用 wcpex 解压。
            // 真实 C:\Windows\WinSxS\Manifests 有 2 万多个文件，放在 UI 线程上
            // 会让界面冻结数分钟，因此必须放到后台线程执行。
            int fileCount = 0;
            try
            {
                fileCount = Directory.GetFiles(manifestDir, "*.manifest", SearchOption.AllDirectories).Length
                          + Directory.GetFiles(manifestDir, "*.mum", SearchOption.AllDirectories).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 统计失败不阻断校验
            }

            BtnVerify.IsEnabled = false;
            string oldText = BtnVerify.Content?.ToString() ?? "";
            BtnVerify.Content = "校验中…";

            try
            {
                if (fileCount > 500)
                {
                    BtnVerify.Content = $"校验中… (共 {fileCount} 个文件，需逐个解压)";
                }

                // 后台执行，避免阻塞 UI 线程
                _result = await System.Threading.Tasks.Task.Run(
                    () => ComponentStoreVerifier.Verify(manifestDir, winsxsDir));

                UpdateFilteredList();

                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(TotalComponents));
                OnPropertyChanged(nameof(NormalCount));
                OnPropertyChanged(nameof(MissingCount));
                OnPropertyChanged(nameof(EmptyCount));
                OnPropertyChanged(nameof(InfSkippedCount));
                OnPropertyChanged(nameof(ParseFailedCount));
                OnPropertyChanged(nameof(TotalSizeDisplay));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("校验完成！").AppendLine();
                sb.AppendLine($"组件总数: {TotalComponents}");
                sb.AppendLine($"正常:     {NormalCount}");
                sb.AppendLine($"缺失:     {MissingCount}");
                sb.AppendLine($"空文件夹: {EmptyCount}");
                if (InfSkippedCount > 0)
                    sb.AppendLine($"已跳过(.inf 驱动类): {InfSkippedCount}");
                if (ParseFailedCount > 0)
                    sb.AppendLine($"解析失败: {ParseFailedCount}  ← 详情见报告");

                if (ParseFailedCount > 0)
                {
                    sb.AppendLine().AppendLine("解析失败原因：");
                    foreach (var kv in _result!.ParseFailures.OrderByDescending(k => k.Value).Take(5))
                        sb.AppendLine($"  {kv.Value,6}  {kv.Key}");
                }

                MessageBox.Show(sb.ToString(), "校验完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"校验失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnVerify.IsEnabled = true;
                BtnVerify.Content = oldText;
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null)
            {
                MessageBox.Show("请先进行校验。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出校验报告",
                FileName = $"component_store_verify_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filter = "文本文件|*.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string report = ComponentStoreVerifier.GenerateReport(_result);
                    File.WriteAllText(dialog.FileName, report, System.Text.Encoding.UTF8);
                    MessageBox.Show($"报告已导出到:\n{dialog.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFilteredList();
        }

        private void UpdateFilteredList()
        {
            if (_result == null) return;

            _filteredItems.Clear();
            string filter = (CmbFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";

            IEnumerable<ComponentStoreVerifier.VerifyItem> query = _result.Items;

            switch (filter)
            {
                case "仅缺失":
                    // .inf 类是被主动跳过的（哈希不可复现），不算"缺失"，需排除
                    query = query.Where(i => !i.Exists && !i.IsInfSkipped);
                    break;
                case "仅空文件夹":
                    query = query.Where(i => i.Exists && i.IsEmpty);
                    break;
                case "仅正常":
                    query = query.Where(i => i.Exists && !i.IsEmpty);
                    break;
                case "仅已跳过(.inf)":
                    query = query.Where(i => i.IsInfSkipped);
                    break;
            }

            foreach (var item in query.OrderBy(i => i.Name))
            {
                _filteredItems.Add(item);
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
