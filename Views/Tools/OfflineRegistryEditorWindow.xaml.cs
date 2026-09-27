using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class OfflineRegistryEditorWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private OfflineRegistryService? _registryService;
        private ObservableCollection<RegistryTreeNode> _rootNodes = new();
        private ObservableCollection<RegistryValueItem> _values = new();

        private bool _isLoaded;
        public bool IsLoaded
        {
            get => _isLoaded;
            set { _isLoaded = value; OnPropertyChanged(nameof(IsLoaded)); }
        }

        private bool _showSearchResults;
        public bool ShowSearchResults
        {
            get => _showSearchResults;
            set { _showSearchResults = value; OnPropertyChanged(nameof(ShowSearchResults)); }
        }

        public OfflineRegistryEditorWindow()
        {
            InitializeComponent();
            DataContext = this;
            TreeKeys.ItemsSource = _rootNodes;
            LstValues.ItemsSource = _values;
        }

        public OfflineRegistryEditorWindow(string hivePath, bool writable = false) : this()
        {
            LoadHive(hivePath, writable);
        }

        private void LoadHive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择注册表 Hive 文件",
                Filter = "所有文件|*.*|Hive 文件|SYSTEM;SOFTWARE;SAM;SECURITY;DEFAULT;COMPONENTS;BCD|Hive 文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadHive(dialog.FileName, ChkWritable.IsChecked == true);
            }
        }

        public void LoadHive(string hivePath, bool writable = false)
        {
            try
            {
                TxtStatus.Text = $"正在加载: {Path.GetFileName(hivePath)}...";

                _registryService?.Dispose();
                _registryService = new OfflineRegistryService();

                if (_registryService.LoadHive(hivePath, writable))
                {
                    IsLoaded = true;
                    ChkWritable.IsChecked = writable;

                    string mode = writable ? "可写" : "只读";
                    TxtHiveInfo.Text = $"{hivePath} ({mode})";
                    TxtStatus.Text = "加载成功";

                    // 加载根节点
                    _rootNodes.Clear();
                    var rootNode = new RegistryTreeNode
                    {
                        Name = Path.GetFileName(hivePath),
                        Path = "",
                        Service = _registryService
                    };
                    rootNode.LoadChildren();
                    _rootNodes.Add(rootNode);

                    // 选中根节点
                    rootNode.IsSelected = true;
                }
            }
            catch (Exception ex)
            {
                IsLoaded = false;
                TxtStatus.Text = $"加载失败: {ex.Message}";
                MessageBox.Show($"加载 Hive 失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TreeKeys_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            System.Diagnostics.Debug.WriteLine($"[TreeView] SelectedItemChanged: old={(e.OldValue as RegistryTreeNode)?.Name ?? "null"}, new={(e.NewValue as RegistryTreeNode)?.Name ?? "null"}");

            if (e.NewValue is RegistryTreeNode node && _registryService != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[TreeView] 选中节点: '{node.Name}', path='{node.Path}'");
                    TxtCurrentPath.Text = $"路径: \\{node.Path.TrimStart('\\')}";

                    // 加载子节点（懒加载）
                    System.Diagnostics.Debug.WriteLine($"[TreeView] 调用 node.LoadChildren()");
                    node.LoadChildren();
                    System.Diagnostics.Debug.WriteLine($"[TreeView] LoadChildren 完成，Children.Count={node.Children.Count}");

                    // 获取键信息
                    var (subKeyCount, valueCount) = _registryService.GetKeyInfo(node.Path);
                    System.Diagnostics.Debug.WriteLine($"[TreeView] GetKeyInfo: subKeys={subKeyCount}, values={valueCount}");
                    TxtKeyInfo.Text = $"子键: {subKeyCount}  值: {valueCount}";

                    // 加载值列表
                    _values.Clear();
                    var valueNames = _registryService.GetValueNames(node.Path);
                    System.Diagnostics.Debug.WriteLine($"[TreeView] GetValueNames: {valueNames.Length} 个值");
                    foreach (var valueName in valueNames)
                    {
                        try
                        {
                            var kind = _registryService.GetValueKind(node.Path, valueName);
                            var data = _registryService.GetValue(node.Path, valueName);
                            _values.Add(new RegistryValueItem
                            {
                                Name = string.IsNullOrEmpty(valueName) ? "(默认)" : valueName,
                                Kind = kind.ToString(),
                                Data = OfflineRegistryService.FormatValue(data, kind)
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TreeView] 加载值失败: '{valueName}': {ex.Message}");
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[TreeView] 值列表加载完成，共 {_values.Count} 项");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TreeView] 异常: {ex.Message}");
                    TxtStatus.Text = $"错误: {ex.Message}";
                }
            }
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_registryService == null || !_registryService.IsLoaded) return;

            // 简单的输入对话框
            var dialog = new SearchDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    TxtStatus.Text = "正在搜索...";
                    var results = _registryService.Search(
                        dialog.SearchTerm,
                        dialog.SearchInKeys,
                        dialog.SearchInValues,
                        dialog.SearchInData,
                        500);

                    LstSearchResults.ItemsSource = results;
                    TxtSearchCount.Text = $"找到 {results.Count} 项";
                    ShowSearchResults = true;
                    TxtStatus.Text = $"搜索完成，找到 {results.Count} 项";
                }
                catch (Exception ex)
                {
                    TxtStatus.Text = $"搜索失败: {ex.Message}";
                    MessageBox.Show($"搜索失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CloseSearchResults_Click(object sender, RoutedEventArgs e)
        {
            ShowSearchResults = false;
        }

        private void LstValues_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 只读模式下双击无效
            if (_registryService == null || !_registryService.IsWritable)
            {
                System.Diagnostics.Debug.WriteLine("[Edit] 只读模式，双击无效");
                return;
            }

            if (LstValues.SelectedItem is RegistryValueItem item)
            {
                System.Diagnostics.Debug.WriteLine($"[Edit] 双击编辑: '{item.Name}', 类型={item.Kind}");

                // 获取当前键路径
                string? keyPath = null;
                if (TreeKeys.SelectedItem is RegistryTreeNode node)
                    keyPath = node.Path;

                // 获取原始值和类型
                var originalValue = _registryService.GetValue(keyPath ?? "", item.Name == "(默认)" ? "" : item.Name);
                var originalKind = _registryService.GetValueKind(keyPath ?? "", item.Name == "(默认)" ? "" : item.Name);

                // 弹出编辑对话框
                var dialog = new RegistryValueEditDialog(item.Name, originalValue, originalKind)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    string valueName = item.Name == "(默认)" ? "" : item.Name;
                    bool success = _registryService.SetValue(keyPath ?? "", valueName, dialog.ValueData, dialog.ValueKind);

                    if (success)
                    {
                        TxtStatus.Text = $"已修改: {item.Name}";
                        BtnSave.IsEnabled = true;
                        System.Diagnostics.Debug.WriteLine($"[Edit] 修改成功: '{item.Name}'");

                        // 刷新值列表
                        RefreshValueList(keyPath);
                    }
                    else
                    {
                        TxtStatus.Text = $"修改失败: {item.Name}";
                        MessageBox.Show($"修改失败，请检查权限和值格式", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void RefreshValueList(string? keyPath)
        {
            _values.Clear();
            var valueNames = _registryService?.GetValueNames(keyPath) ?? Array.Empty<string>();
            foreach (var valueName in valueNames)
            {
                try
                {
                    var kind = _registryService!.GetValueKind(keyPath ?? "", valueName);
                    var data = _registryService.GetValue(keyPath ?? "", valueName);
                    _values.Add(new RegistryValueItem
                    {
                        Name = string.IsNullOrEmpty(valueName) ? "(默认)" : valueName,
                        Kind = kind.ToString(),
                        Data = OfflineRegistryService.FormatValue(data, kind)
                    });
                }
                catch { }
            }
        }

        private void LstSearchResults_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LstSearchResults.SelectedItem is RegistrySearchResult result)
            {
                // 定位到路径
                NavigateToPath(result.Path);
            }
        }

        private void NavigateToPath(string path)
        {
            if (string.IsNullOrEmpty(path) || _rootNodes.Count == 0) return;

            var parts = path.Trim('\\').Split('\\');
            RegistryTreeNode? current = _rootNodes.FirstOrDefault();

            if (current == null) return;

            foreach (var part in parts)
            {
                current.LoadChildren();
                var child = current.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child == null) break;
                current = child;
            }

            if (current != null)
            {
                current.IsSelected = true;
                current.IsExpanded = true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_registryService == null || !_registryService.IsLoaded) return;

            if (!_registryService.HasChanges)
            {
                MessageBox.Show("没有未保存的修改", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要保存修改到原文件吗？\n\n原文件: {_registryService.HivePath}\n\n将自动创建 .bak 备份。",
                "保存修改",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                TxtStatus.Text = "正在保存...";
                bool success = _registryService.SaveToOriginal();

                if (success)
                {
                    BtnSave.IsEnabled = false;
                    TxtStatus.Text = "已保存到原文件";
                    MessageBox.Show("保存成功！\n\n已创建备份: " + _registryService.HivePath + ".bak", "完成", MessageBoxButton.OK, MessageBoxImage.Information);

                    // 重新加载树
                    _rootNodes.Clear();
                    var rootNode = new RegistryTreeNode
                    {
                        Name = Path.GetFileName(_registryService.HivePath),
                        Path = "",
                        Service = _registryService
                    };
                    rootNode.LoadChildren();
                    _rootNodes.Add(rootNode);
                }
                else
                {
                    TxtStatus.Text = "保存失败";
                    MessageBox.Show("保存失败，请查看日志了解详情。\n\n可能原因：原文件被其他程序占用。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_registryService == null || !_registryService.IsLoaded) return;

            if (TreeKeys.SelectedItem is RegistryTreeNode node)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出注册表",
                    FileName = $"{node.Name}.reg",
                    Filter = "注册表文件|*.reg"
                };

                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        _registryService.ExportKey(node.Path, dialog.FileName);
                        TxtStatus.Text = $"已导出到: {dialog.FileName}";
                        MessageBox.Show($"导出成功:\n{dialog.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        TxtStatus.Text = $"导出失败: {ex.Message}";
                        MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_registryService != null && _registryService.HasChanges)
            {
                var result = MessageBox.Show(
                    "有未保存的修改，是否保存？\n\n是：保存并关闭\n否：不保存直接关闭\n取消：继续编辑",
                    "未保存的修改",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                else if (result == MessageBoxResult.Yes)
                {
                    bool success = _registryService.SaveToOriginal();
                    if (!success)
                    {
                        var retry = MessageBox.Show("保存失败，是否仍要关闭？", "保存失败", MessageBoxButton.YesNo, MessageBoxImage.Error);
                        if (retry == MessageBoxResult.No)
                        {
                            e.Cancel = true;
                            return;
                        }
                    }
                }
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _registryService?.Dispose();
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// 注册表树节点（懒加载）
    /// </summary>
    public class RegistryTreeNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public OfflineRegistryService? Service { get; set; }
        public ObservableCollection<RegistryTreeNode> Children { get; } = new();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                System.Diagnostics.Debug.WriteLine($"[TreeNode] IsExpanded changed: '{Name}' -> {value} (old: {_isExpanded})");
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                // 展开时懒加载子节点
                if (value)
                {
                    System.Diagnostics.Debug.WriteLine($"[TreeNode] 触发 LoadChildren for '{Name}'");
                    LoadChildren();
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        private bool _childrenLoaded;
        private int _subKeyCount;
        public int SubKeyCount => _subKeyCount;

        public void LoadChildren()
        {
            System.Diagnostics.Debug.WriteLine($"[TreeNode] LoadChildren called: '{Name}', path='{Path}', _childrenLoaded={_childrenLoaded}, Service={(Service != null ? "not null" : "null")}");

            if (_childrenLoaded)
            {
                System.Diagnostics.Debug.WriteLine($"[TreeNode] 已加载过，跳过: '{Name}'");
                return;
            }
            if (Service == null)
            {
                System.Diagnostics.Debug.WriteLine($"[TreeNode] Service 为 null，跳过: '{Name}'");
                return;
            }

            _childrenLoaded = true;
            System.Diagnostics.Debug.WriteLine($"[TreeNode] 开始加载子节点: '{Name}', path='{Path}'");

            try
            {
                var subKeys = Service.GetSubKeyNames(Path);
                _subKeyCount = subKeys.Length;
                System.Diagnostics.Debug.WriteLine($"[TreeNode] 获取到 {subKeys.Length} 个子键: {string.Join(", ", subKeys.Take(10))}");

                foreach (var subKey in subKeys)
                {
                    string subPath = string.IsNullOrEmpty(Path) ? subKey : $"{Path}\\{subKey}";
                    var childNode = new RegistryTreeNode
                    {
                        Name = subKey,
                        Path = subPath,
                        Service = Service
                    };
                    Children.Add(childNode);
                    System.Diagnostics.Debug.WriteLine($"[TreeNode] 添加子节点: '{subKey}' -> path='{subPath}'");
                }

                System.Diagnostics.Debug.WriteLine($"[TreeNode] 加载完成: '{Name}', 共 {Children.Count} 个子节点");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TreeNode] LoadChildren 异常: '{Name}': {ex.Message}");
            }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 注册表值项
    /// </summary>
    public class RegistryValueItem
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Data { get; set; } = "";
    }

    /// <summary>
    /// 搜索对话框
    /// </summary>
    public partial class SearchDialog : Window
    {
        public string SearchTerm { get; private set; } = "";
        public bool SearchInKeys { get; private set; } = true;
        public bool SearchInValues { get; private set; } = true;
        public bool SearchInData { get; private set; } = false;

        private TextBox _txtSearch = null!;
        private CheckBox _chkKeys = null!;
        private CheckBox _chkValues = null!;
        private CheckBox _chkData = null!;

        public SearchDialog()
        {
            Title = "搜索注册表";
            Width = 400;
            Height = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = System.Windows.Media.Brushes.WhiteSmoke;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 搜索词
            var lblSearch = new TextBlock { Text = "搜索关键词:", Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(lblSearch, 0);
            grid.Children.Add(lblSearch);

            _txtSearch = new TextBox { Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetRow(_txtSearch, 1);
            grid.Children.Add(_txtSearch);

            // 选项
            var optionsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _chkKeys = new CheckBox { Content = "搜索键名", IsChecked = true, Margin = new Thickness(0, 0, 16, 4) };
            _chkValues = new CheckBox { Content = "搜索值名", IsChecked = true, Margin = new Thickness(0, 0, 16, 4) };
            _chkData = new CheckBox { Content = "搜索值数据（较慢）", IsChecked = false };
            optionsPanel.Children.Add(_chkKeys);
            optionsPanel.Children.Add(_chkValues);
            optionsPanel.Children.Add(_chkData);
            Grid.SetRow(optionsPanel, 2);
            grid.Children.Add(optionsPanel);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOk = new Button { Content = "搜索", Width = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 80, Height = 28 };
            btnOk.Click += (s, e) =>
            {
                SearchTerm = _txtSearch.Text.Trim();
                SearchInKeys = _chkKeys.IsChecked == true;
                SearchInValues = _chkValues.IsChecked == true;
                SearchInData = _chkData.IsChecked == true;
                if (string.IsNullOrEmpty(SearchTerm))
                {
                    MessageBox.Show("请输入搜索关键词", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                DialogResult = true;
                Close();
            };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            Grid.SetRow(btnPanel, 4);
            grid.Children.Add(btnPanel);

            Content = grid;
            _txtSearch.Focus();
        }
    }
}
