using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneCore_Image_Designer.Models.FFUBrowser;
using OneCore_Image_Designer.ViewModels.FFUBrowser;

namespace OneCore_Image_Designer.Views.FFUBrowser
{
    public partial class FFUBrowserView : UserControl
    {
        private Point _dragStartPoint;
        private bool _isDragStarting;
        private FFUBrowserViewModel? _viewModel;

        public FFUBrowserView()
        {
            InitializeComponent();
            DataContextChanged += FFUBrowserView_DataContextChanged;
        }

        private void RecentFilesButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentFilesMenu != null && _viewModel != null && _viewModel.RecentFiles.Count > 0)
            {
                RecentFilesMenu.PlacementTarget = RecentFilesButton;
                RecentFilesMenu.IsOpen = true;
            }
            else
            {
                MessageBox.Show("暂无最近打开的文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void FFUBrowserView_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string ext = Path.GetExtension(files[0]).ToLower();
                    string[] supported = { ".ffu", ".vhdx", ".vhd", ".img", ".bin" };
                    if (supported.Contains(ext))
                    {
                        e.Effects = DragDropEffects.Copy;
                        e.Handled = true;
                        return;
                    }
                }
            }
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void FFUBrowserView_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 && _viewModel != null)
                {
                    string ext = Path.GetExtension(files[0]).ToLower();
                    string[] supported = { ".ffu", ".vhdx", ".vhd", ".img", ".bin" };
                    if (supported.Contains(ext))
                    {
                        _viewModel.ImagePath = files[0];
                        _viewModel.LoadImage();
                        e.Handled = true;
                    }
                    else
                    {
                        MessageBox.Show($"不支持的文件格式: {ext}\n支持: FFU, VHDX, VHD, IMG, BIN", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void FFUBrowserView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _viewModel = DataContext as FFUBrowserViewModel;

            if (_viewModel != null)
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FFUBrowserViewModel.ShowHexViewer))
            {
                if (_viewModel != null && _viewModel.ShowHexViewer && _viewModel.HexStream != null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        HexViewer.LoadStream(_viewModel.HexStream, _viewModel.HexLength, _viewModel.HexDisplayName);
                    });
                }
                else if (_viewModel != null && !_viewModel.ShowHexViewer)
                {
                    HexViewer.Clear();
                }
            }
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is FFUBrowserViewModel vm && e.NewValue is FFUBrowserNode node)
            {
                vm.OnTreeNodeSelected(node);
            }
        }

        private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // 根据选中的节点类型控制菜单项可见性
            if (FolderTree.ContextMenu == null) return;

            bool isFirmwarePartition = false;
            bool isPartition = false;
            if (FolderTree.SelectedItem is FFUBrowserNode node)
            {
                isPartition = node.NodeType == FFUNodeType.Partition;
                isFirmwarePartition = isPartition
                    && OneCore_Image_Designer.Services.FirmwareService.IsFirmwarePartition(node.PartitionTypeGuid);
            }

            // 遍历菜单项，设置可见性
            foreach (var item in FolderTree.ContextMenu.Items)
            {
                if (item is MenuItem menuItem && menuItem.Header != null)
                {
                    string header = menuItem.Header.ToString();
                    if (header.Contains("提取此分区为bin文件"))
                    {
                        menuItem.Visibility = isFirmwarePartition ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else if (header.Contains("解析固件结构"))
                    {
                        menuItem.Visibility = isPartition ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }

        private void ParseFirmware_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTree.SelectedItem is not FFUBrowserNode node) return;
            if (node.NodeType != FFUNodeType.Partition || node.RawPartition == null)
            {
                MessageBox.Show("请先选择一个分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // 提取分区数据到临时文件
                string tempDir = Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Firmware");
                Directory.CreateDirectory(tempDir);

                string partitionName = string.IsNullOrEmpty(node.Name) ? "partition" : node.Name;
                foreach (char c in Path.GetInvalidFileNameChars())
                    partitionName = partitionName.Replace(c, '_');

                string tempFile = Path.Combine(tempDir, $"{partitionName}_{DateTime.Now:yyyyMMdd_HHmmss}.bin");

                bool success = OneCore_Image_Designer.Services.FirmwareService.ExtractPartitionToBin(node.RawPartition, tempFile);
                if (!success)
                {
                    MessageBox.Show("提取分区数据失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 打开固件解析器窗口
                var parserWindow = new OneCore_Image_Designer.Views.Tools.FirmwareParserWindow(tempFile);
                parserWindow.Owner = Window.GetWindow(this);
                parserWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析固件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FFUBrowserViewModel vm && FileListView.SelectedItem is FFUBrowserNode node)
            {
                vm.OnFileDoubleClick(node);
            }
        }

        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is FFUBrowserViewModel vm)
            {
                // 只在单选时预览
                if (FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FFUBrowserNode node)
                {
                    vm.LoadPreview(node);
                }
                else if (FileListView.SelectedItems.Count == 0)
                {
                    vm.ClosePreview();
                }
            }
        }

        private void ClosePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FFUBrowserViewModel vm)
            {
                vm.ClosePreview();
            }
        }

        private void SearchResultListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FFUBrowserViewModel vm && SearchResultListView.SelectedItem is FFUBrowserNode node)
            {
                vm.OnSearchResultDoubleClick(node);
            }
        }

        private void FileListView_MouseMove(object sender, MouseEventArgs e)
        {
            // 检测拖拽开始
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragStarting)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPos;

                // 鼠标移动超过系统拖拽阈值才开始拖拽
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    StartDrag();
                }
            }
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            _dragStartPoint = e.GetPosition(null);
            _isDragStarting = false;
        }

        private void StartDrag()
        {
            if (DataContext is not FFUBrowserViewModel vm) return;

            var selectedFiles = FileListView.SelectedItems
                .Cast<FFUBrowserNode>()
                .Where(n => n.NodeType == FFUNodeType.File || n.NodeType == FFUNodeType.Folder)
                .ToList();

            if (selectedFiles.Count == 0) return;

            _isDragStarting = true;

            try
            {
                // 创建临时目录
                string tempDir = Path.Combine(Path.GetTempPath(), $"FFU_DragDrop_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                int extractedCount = 0;

                // 提取选中的文件/文件夹到临时目录
                foreach (var item in selectedFiles)
                {
                    if (item.FileSystem == null) continue;

                    if (item.NodeType == FFUNodeType.File)
                    {
                        if (ExtractSingleFile(item.FileSystem, item.FullPath, tempDir, ""))
                        {
                            extractedCount++;
                        }
                    }
                    else if (item.NodeType == FFUNodeType.Folder)
                    {
                        try
                        {
                            var allFiles = item.FileSystem.GetFiles(item.FullPath, "*.*", SearchOption.AllDirectories);
                            foreach (var file in allFiles)
                            {
                                if (ExtractSingleFile(item.FileSystem, file, tempDir, item.FullPath))
                                {
                                    extractedCount++;
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (extractedCount == 0)
                {
                    Directory.Delete(tempDir, true);
                    return;
                }

                // 获取临时目录中的所有文件路径
                var filePaths = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories).ToList();

                // 创建DataObject，使用FileDrop格式
                var dataObject = new DataObject(DataFormats.FileDrop, filePaths.ToArray());

                // 也添加首选效果（复制）
                DragDrop.DoDragDrop(FileListView, dataObject, DragDropEffects.Copy);

                // 拖拽完成后，临时文件保留在临时目录中（用户可能已经复制走了）
                // 可以在这里添加清理逻辑，但通常保留更安全
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拖拽提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isDragStarting = false;
            }
        }

        private bool ExtractSingleFile(DiscUtils.IFileSystem fs, string sourcePath, string outputDir, string basePath)
        {
            try
            {
                // 计算相对路径
                string relativePath = sourcePath;
                if (!string.IsNullOrEmpty(basePath) && sourcePath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = sourcePath.Substring(basePath.Length).TrimStart('\\', '/');
                }

                // 目标路径
                string destPath = Path.Combine(outputDir, relativePath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // 读取并写入文件
                using var sourceStream = fs.OpenFile(sourcePath, FileMode.Open, FileAccess.Read);
                using var destStream = File.Create(destPath);
                sourceStream.CopyTo(destStream);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

