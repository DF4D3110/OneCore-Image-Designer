using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OneCore_Image_Designer.Models
{
    /// <summary>
    /// 节点类型
    /// </summary>
    public enum FileNodeType
    {
        Folder,
        File,
        Disk,
        StoragePool,
        Partition
    }

    /// <summary>
    /// 文件树节点 - 支持文件夹和文件
    /// </summary>
    public class FileTreeNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public FileNodeType NodeType { get; set; } = FileNodeType.Folder;
        public ObservableCollection<FileTreeNode> Children { get; } = new();

        public ImageSource? Icon { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string SizeDisplay => Size switch
        {
            > 1073741824 => $"{Size / 1073741824.0:F2} GB",
            > 1048576 => $"{Size / 1048576.0:F2} MB",
            > 1024 => $"{Size / 1024.0:F2} KB",
            _ => $"{Size} B"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 系统图标辅助类 - 获取文件夹和文件的系统图标
    /// </summary>
    public static class SystemIconHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly Dictionary<string, ImageSource?> _iconCache = new();

        /// <summary>
        /// 获取文件夹图标
        /// </summary>
        public static ImageSource? GetFolderIcon()
        {
            const string key = "folder";
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            var icon = GetIcon("folder", FILE_ATTRIBUTE_DIRECTORY);
            _iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        /// 获取磁盘图标
        /// </summary>
        public static ImageSource? GetDiskIcon()
        {
            const string key = "disk";
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            // 使用固定驱动器的图标
            var icon = GetIcon("C:\\", 0);
            if (icon == null) icon = GetIcon("drive", FILE_ATTRIBUTE_DIRECTORY);
            _iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        /// 获取分区/硬盘图标
        /// </summary>
        public static ImageSource? GetPartitionIcon()
        {
            const string key = "partition";
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            // 使用可移动磁盘图标作为分区图标
            var icon = GetIcon("A:\\", 0);
            if (icon == null) icon = GetFolderIcon();
            _iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        /// 获取存储池图标
        /// </summary>
        public static ImageSource? GetStoragePoolIcon()
        {
            const string key = "storagepool";
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            // 使用CD驱动器图标作为存储池图标（或者用网络驱动器）
            var icon = GetIcon("Z:\\", 0);
            if (icon == null) icon = GetFolderIcon();
            _iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        /// 根据文件扩展名获取文件图标
        /// </summary>
        public static ImageSource? GetFileIcon(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".unknown";

            if (_iconCache.TryGetValue(ext, out var cached))
                return cached;

            var icon = GetIcon(ext, FILE_ATTRIBUTE_NORMAL);
            _iconCache[ext] = icon;
            return icon;
        }

        private static ImageSource? GetIcon(string path, uint attributes)
        {
            try
            {
                SHFILEINFO shfi = new();
                IntPtr hImg = SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (shfi.hIcon != IntPtr.Zero)
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    DestroyIcon(shfi.hIcon);
                    return bitmap;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// 文件树构建器 - 把文件路径列表转换成树状结构
    /// </summary>
    public static class FileTreeBuilder
    {
        /// <summary>
        /// 构建文件树（大小写不敏感合并文件夹）
        /// </summary>
        public static ObservableCollection<FileTreeNode> BuildTree(IEnumerable<string> filePaths)
        {
            var root = new FileTreeNode { Name = "根目录", IsFolder = true, IsExpanded = true };
            // 使用大小写不敏感的比较器，合并 Windows/windows 等文件夹
            var nodeMap = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
            // 记录每个路径第一次出现时的原始名称（用于显示）
            var displayNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var normalizedPath = filePath.Replace('\\', '/').TrimStart('/');
                var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                string currentPath = "";
                FileTreeNode parent = root;

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    string fullPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";
                    bool isFolder = i < parts.Length - 1;

                    // 记录第一次出现的原始名称
                    if (!displayNameMap.ContainsKey(fullPath))
                    {
                        displayNameMap[fullPath] = part;
                    }

                    if (!nodeMap.TryGetValue(fullPath, out var node))
                    {
                        // 判断节点类型
                        FileNodeType nodeType = isFolder ? FileNodeType.Folder : FileNodeType.File;
                        ImageSource? icon = null;

                        if (i == 0 && part.StartsWith("磁盘", StringComparison.OrdinalIgnoreCase))
                        {
                            // 磁盘节点
                            nodeType = FileNodeType.Disk;
                            icon = SystemIconHelper.GetDiskIcon();
                        }
                        else if (i == 1)
                        {
                            // 分区节点（磁盘的直接子节点）
                            if (part.Contains("[存储池]", StringComparison.OrdinalIgnoreCase))
                            {
                                nodeType = FileNodeType.StoragePool;
                                icon = SystemIconHelper.GetStoragePoolIcon();
                            }
                            else
                            {
                                nodeType = FileNodeType.Partition;
                                icon = SystemIconHelper.GetPartitionIcon();
                            }
                        }
                        else
                        {
                            // 普通文件夹或文件
                            icon = isFolder ? SystemIconHelper.GetFolderIcon() : SystemIconHelper.GetFileIcon(part);
                        }

                        node = new FileTreeNode
                        {
                            Name = displayNameMap[fullPath], // 使用第一次出现的原始名称
                            FullPath = fullPath,
                            IsFolder = isFolder,
                            NodeType = nodeType,
                            IsExpanded = true, // 默认展开
                            Icon = icon
                        };
                        parent.Children.Add(node);
                        nodeMap[fullPath] = node;
                    }

                    parent = node;
                    currentPath = fullPath;
                }
            }

            // 对每个节点的子节点按名称排序（文件夹在前，文件在后）
            SortNodes(root);

            return root.Children;
        }

        private static void SortNodes(FileTreeNode node)
        {
            var sorted = node.Children
                .OrderByDescending(n => n.IsFolder)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            node.Children.Clear();
            foreach (var child in sorted)
            {
                node.Children.Add(child);
                if (child.IsFolder) SortNodes(child);
            }
        }

        /// <summary>
        /// 展开/收缩所有节点
        /// </summary>
        public static void SetAllExpanded(IEnumerable<FileTreeNode> nodes, bool expanded)
        {
            foreach (var node in nodes)
            {
                node.IsExpanded = expanded;
                if (node.Children.Count > 0)
                {
                    SetAllExpanded(node.Children, expanded);
                }
            }
        }
    }
}
