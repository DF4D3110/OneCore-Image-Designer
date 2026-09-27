using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DiscUtils;

namespace OneCore_Image_Designer.Models.FFUBrowser
{
    /// <summary>
    /// 节点类型
    /// </summary>
    public enum FFUNodeType
    {
        Disk,
        Partition,
        Folder,
        File
    }

    /// <summary>
    /// FFS模块信息（用于UEFI FV解析后的模块提取）
    /// </summary>
    public class FfsModuleInfo
    {
        public long Offset { get; set; }
        public long Size { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid TypeGuid { get; set; }
    }

    /// <summary>
    /// FFU浏览器树节点
    /// </summary>
    public class FFUBrowserNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _childrenLoaded;
        private long _size;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public FFUNodeType NodeType { get; set; }
        public long Size
        {
            get => _size;
            set { _size = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); }
        }
        public DateTime? ModifiedTime { get; set; }

        /// <summary>
        /// 关联的文件系统（分区节点才有）
        /// </summary>
        public IFileSystem? FileSystem { get; set; }

        /// <summary>
        /// 是否有文件系统
        /// </summary>
        public bool HasFileSystem => FileSystem != null;

        /// <summary>
        /// 是否为虚拟分区（如UpdateOS WIM内容，非真实物理分区）
        /// </summary>
        public bool IsVirtualPartition { get; set; } = false;

        /// <summary>
        /// 磁盘总大小（磁盘节点才有）
        /// </summary>
        public long DiskTotalSize { get; set; } = 0;

        /// <summary>
        /// 磁盘类型描述（磁盘节点才有）
        /// </summary>
        public string DiskTypeDescription { get; set; } = "";

        /// <summary>
        /// 是否为磁盘节点
        /// </summary>
        public bool IsDisk => NodeType == FFUNodeType.Disk;

        /// <summary>
        /// 是否为分区节点
        /// </summary>
        public bool IsPartition => NodeType == FFUNodeType.Partition;

        /// <summary>
        /// 磁盘索引（磁盘节点才有）
        /// </summary>
        public int DiskIndex { get; set; } = -1;

        /// <summary>
        /// 分区名称（分区节点才有）
        /// </summary>
        public string PartitionName { get; set; } = string.Empty;

        /// <summary>
        /// 分区类型GUID（分区节点才有）
        /// </summary>
        public Guid PartitionTypeGuid { get; set; } = Guid.Empty;

        /// <summary>
        /// 分区唯一ID（分区节点才有）
        /// </summary>
        public Guid PartitionId { get; set; } = Guid.Empty;

        /// <summary>
        /// 分区类型描述（分区节点才有）
        /// </summary>
        public string PartitionTypeDescription { get; set; } = string.Empty;

        /// <summary>
        /// 分区类型详细说明（分区节点才有）
        /// </summary>
        public string PartitionTypeDetail { get; set; } = string.Empty;

        /// <summary>
        /// 自定义标签（用于存储FFS模块信息等额外数据）
        /// </summary>
        public object? Tag { get; set; }

        /// <summary>
        /// 是否为UEFI变量分区（UEFI_BS_NV）
        /// </summary>
        public bool IsUefiVariablePartition { get; set; }

        /// <summary>
        /// 关联的IPartition对象（用于提取raw分区）
        /// </summary>
        public global::MobilePackageGen.IPartition? RawPartition { get; set; }

        /// <summary>
        /// 固件格式信息（仅raw分区有）
        /// </summary>
        public Services.FirmwareService.FirmwareFormatInfo? FirmwareFormat { get; set; }

        /// <summary>
        /// 固件格式显示名称
        /// </summary>
        public string FirmwareFormatDisplay => FirmwareFormat?.FormatName ?? string.Empty;

        /// <summary>
        /// 是否有固件格式信息
        /// </summary>
        public bool HasFirmwareFormat => FirmwareFormat != null && FirmwareFormat.Format != Services.FirmwareService.FirmwareFormat.Unknown;

        public ObservableCollection<FFUBrowserNode> Children { get; } = new();

        public ImageSource? Icon { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged();
                // 展开时懒加载子节点
                if (value && !_childrenLoaded && NodeType != FFUNodeType.File)
                {
                    LoadChildren();
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string SizeDisplay
        {
            get
            {
                if (NodeType == FFUNodeType.Folder) return string.Empty;
                return Size switch
                {
                    > 1073741824 => $"{Size / 1073741824.0:F2} GB",
                    > 1048576 => $"{Size / 1048576.0:F2} MB",
                    > 1024 => $"{Size / 1024.0:F2} KB",
                    _ => $"{Size} B"
                };
            }
        }

        public string TypeDisplay => NodeType switch
        {
            FFUNodeType.Disk => "磁盘",
            FFUNodeType.Partition => "分区",
            FFUNodeType.Folder => "文件夹",
            FFUNodeType.File => "文件",
            _ => "未知"
        };

        /// <summary>
        /// 懒加载子节点
        /// </summary>
        private void LoadChildren()
        {
            _childrenLoaded = true;

            if (FileSystem == null) return;
            if (NodeType == FFUNodeType.File) return;

            try
            {
                // 清空占位节点
                Children.Clear();

                // 根目录使用反斜杠，确保DiscUtils正确识别
                string folderPath = NodeType == FFUNodeType.Partition ? @"\" : FullPath;

                // 获取子文件夹
                var directories = FileSystem.GetDirectories(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var dir in directories)
                {
                    // 规范化路径，确保使用反斜杠
                    string normalizedDir = dir.Replace('/', '\\').TrimEnd('\\');
                    var dirName = Path.GetFileName(normalizedDir);
                    if (string.IsNullOrEmpty(dirName)) continue;

                    var childNode = new FFUBrowserNode
                    {
                        Name = dirName,
                        FullPath = normalizedDir,
                        NodeType = FFUNodeType.Folder,
                        FileSystem = FileSystem,
                        PartitionName = PartitionName, // 传递分区名称
                        Icon = SystemIconHelper.GetFolderIcon()
                    };

                    // 添加占位节点，让TreeView显示展开箭头
                    childNode.Children.Add(new FFUBrowserNode { Name = "加载中..." });
                    Children.Add(childNode);
                }

                // 获取文件
                var files = FileSystem.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    // 规范化路径
                    string normalizedFile = file.Replace('/', '\\');
                    var fileName = Path.GetFileName(normalizedFile);
                    long fileSize = 0;
                    DateTime? modified = null;

                    try
                    {
                        var fileInfo = FileSystem.GetFileInfo(normalizedFile);
                        fileSize = fileInfo.Length;
                        modified = fileInfo.LastWriteTime;
                    }
                    catch { }

                    var fileNode = new FFUBrowserNode
                    {
                        Name = fileName,
                        FullPath = normalizedFile,
                        NodeType = FFUNodeType.File,
                        Size = fileSize,
                        ModifiedTime = modified,
                        FileSystem = FileSystem,
                        PartitionName = PartitionName, // 传递分区名称
                        Icon = SystemIconHelper.GetFileIcon(fileName)
                    };
                    Children.Add(fileNode);
                }
            }
            catch (Exception ex)
            {
                // 加载失败时显示错误节点
                Children.Clear();
                Children.Add(new FFUBrowserNode { Name = $"加载失败: {ex.Message}", Icon = null });
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
