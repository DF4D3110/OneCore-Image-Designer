using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MobilePackageGen;
using OneCore_Image_Designer.Helpers;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Models.FFUBrowser;
using DiscUtils;
using DiscUtils.Streams;
using StorageSpace;
using MobilePackageGen.Adapters;
using Img2Ffu.Reader;

namespace OneCore_Image_Designer.ViewModels.FFUBrowser
{
    /// <summary>
    /// 磁盘布局可视化项
    /// </summary>
    public class DiskLayoutItem
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SizeDisplay { get; set; } = string.Empty;
        public double Percentage { get; set; }
        public double BarWidth { get; set; }
        public System.Windows.Media.Brush Color { get; set; } = System.Windows.Media.Brushes.Gray;
        public bool HasFileSystem { get; set; }
        public bool IsFirmware { get; set; }
    }

    public class FFUBrowserViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _mainVM;
        private readonly string _logFilePath;

        private IEnumerable<IDisk>? _loadedDisks;
        private FFUBrowserNode? _currentFolder;
        private FFUBrowserNode? _currentNode;

        public FFUBrowserNode? CurrentNode
        {
            get => _currentNode;
            set { _currentNode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowPartitionInfo)); }
        }

        public bool ShowPartitionInfo => _currentNode != null &&
            (_currentNode.NodeType == Models.FFUBrowser.FFUNodeType.Partition ||
             _currentNode.NodeType == Models.FFUBrowser.FFUNodeType.Disk);

        public ObservableCollection<FFUBrowserNode> DiskTree { get; } = new();
        public ObservableCollection<FFUBrowserNode> FileList { get; } = new();
        public ObservableCollection<FFUBrowserNode> SelectedFiles { get; } = new();
        public ObservableCollection<string> RecentFiles { get; } = new();
        public bool HasRecentFiles => RecentFiles.Count > 0;

        private Services.FirmwareService.FFUHeaderInfo? _ffuHeader;
        public Services.FirmwareService.FFUHeaderInfo? FfuHeader
        {
            get => _ffuHeader;
            set { _ffuHeader = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFfuHeader)); }
        }
        public bool ShowFfuHeader => _ffuHeader != null;

        private string _imagePath = string.Empty;
        public string ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsImageValid)); }
        }

        public bool IsImageValid => File.Exists(ImagePath);

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotLoading)); }
        }
        public bool IsNotLoading => !IsLoading;

        private string _loadStatus = string.Empty;
        public string LoadStatus
        {
            get => _loadStatus;
            set { _loadStatus = value; OnPropertyChanged(); }
        }

        private string _currentPath = string.Empty;
        public string CurrentPath
        {
            get => _currentPath;
            set { _currentPath = value; OnPropertyChanged(); }
        }

        private bool _showUefiVariables;
        public bool ShowUefiVariables
        {
            get => _showUefiVariables;
            set { _showUefiVariables = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFileList)); OnPropertyChanged(nameof(ShowHexViewer)); }
        }

        private bool _showHexViewer;
        public bool ShowHexViewer
        {
            get => _showHexViewer;
            set { _showHexViewer = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFileList)); OnPropertyChanged(nameof(ShowUefiVariables)); }
        }
        public bool ShowFileList => !ShowUefiVariables && !ShowHexViewer && !ShowSearchResults;

        // 文件搜索
        private string _searchKeyword = string.Empty;
        public string SearchKeyword
        {
            get => _searchKeyword;
            set { _searchKeyword = value; OnPropertyChanged(); }
        }

        private bool _searchAllPartitions = false;
        public bool SearchAllPartitions
        {
            get => _searchAllPartitions;
            set { _searchAllPartitions = value; OnPropertyChanged(); }
        }

        private bool _showSearchResults = false;
        public bool ShowSearchResults
        {
            get => _showSearchResults;
            set { _showSearchResults = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFileList)); OnPropertyChanged(nameof(ShowHexViewer)); OnPropertyChanged(nameof(ShowUefiVariables)); }
        }

        public ObservableCollection<FFUBrowserNode> SearchResults { get; } = new();

        private bool _isSearching = false;
        public bool IsSearching
        {
            get => _isSearching;
            set { _isSearching = value; OnPropertyChanged(); }
        }

        // 文件预览
        private bool _showPreview = false;
        public bool ShowPreview
        {
            get => _showPreview;
            set { _showPreview = value; OnPropertyChanged(); }
        }

        private string _previewText = string.Empty;
        public string PreviewText
        {
            get => _previewText;
            set { _previewText = value; OnPropertyChanged(); }
        }

        private System.Windows.Media.ImageSource? _previewImage;
        public System.Windows.Media.ImageSource? PreviewImage
        {
            get => _previewImage;
            set { _previewImage = value; OnPropertyChanged(); }
        }

        private string _previewTitle = string.Empty;
        public string PreviewTitle
        {
            get => _previewTitle;
            set { _previewTitle = value; OnPropertyChanged(); }
        }

        private bool _previewIsText = true;
        public bool PreviewIsText
        {
            get => _previewIsText;
            set { _previewIsText = value; OnPropertyChanged(); }
        }

        private bool _previewIsImage = false;
        public bool PreviewIsImage
        {
            get => _previewIsImage;
            set { _previewIsImage = value; OnPropertyChanged(); }
        }

        // 支持的文本和图片扩展名
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".xml", ".log", ".ini", ".inf", ".reg", ".json", ".config",
            ".bat", ".cmd", ".ps1", ".cs", ".cpp", ".h", ".hpp", ".js", ".css",
            ".html", ".htm", ".md", ".csv", ".tsv", ".yaml", ".yml", ".toml",
            ".sql", ".sh", ".py", ".java", ".c", ".asm", ".s", ".makefile",
            ".mum", ".manifest", ".cat", ".dsm", ".mof", ".wim", ".manifest"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tiff", ".tif", ".webp"
        };

        // 磁盘布局可视化
        public ObservableCollection<DiskLayoutItem> DiskLayoutItems { get; } = new();

        private bool _showDiskLayout = false;
        public bool ShowDiskLayout
        {
            get => _showDiskLayout;
            set { _showDiskLayout = value; OnPropertyChanged(); }
        }

        // 分区颜色调色板
        private static readonly System.Windows.Media.Brush[] PartitionColors = new[]
        {
            System.Windows.Media.Brushes.SteelBlue,
            System.Windows.Media.Brushes.SeaGreen,
            System.Windows.Media.Brushes.Goldenrod,
            System.Windows.Media.Brushes.IndianRed,
            System.Windows.Media.Brushes.MediumPurple,
            System.Windows.Media.Brushes.LightSeaGreen,
            System.Windows.Media.Brushes.Peru,
            System.Windows.Media.Brushes.RosyBrown,
            System.Windows.Media.Brushes.DarkKhaki,
            System.Windows.Media.Brushes.CadetBlue,
            System.Windows.Media.Brushes.Tomato,
            System.Windows.Media.Brushes.YellowGreen,
            System.Windows.Media.Brushes.SlateBlue,
            System.Windows.Media.Brushes.DarkOrange,
            System.Windows.Media.Brushes.MediumTurquoise,
            System.Windows.Media.Brushes.PaleVioletRed
        };

        // 十六进制查看器数据
        private Stream? _hexStream;
        public Stream? HexStream
        {
            get => _hexStream;
            set { _hexStream = value; OnPropertyChanged(); }
        }

        private long _hexLength;
        public long HexLength
        {
            get => _hexLength;
            set { _hexLength = value; OnPropertyChanged(); }
        }

        private string _hexDisplayName = "";
        public string HexDisplayName
        {
            get => _hexDisplayName;
            set { _hexDisplayName = value; OnPropertyChanged(); }
        }

        private bool _isPartitionSelected;
        public bool IsPartitionSelected
        {
            get => _isPartitionSelected;
            set { _isPartitionSelected = value; OnPropertyChanged(); }
        }

        private bool _isFirmwarePartitionSelected;
        public bool IsFirmwarePartitionSelected
        {
            get => _isFirmwarePartitionSelected;
            set { _isFirmwarePartitionSelected = value; OnPropertyChanged(); }
        }

        public ObservableCollection<Services.FirmwareService.UefiVariable> UefiVariables { get; } = new();

        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand LoadImageCommand { get; }
        public RelayCommand ExtractSelectedCommand { get; }
        public RelayCommand ExtractAllCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand GoBackCommand { get; }
        public RelayCommand NavigateToStartCommand { get; }
        public RelayCommand ExtractPartitionCommand { get; }
        public RelayCommand ExtractAllFirmwareCommand { get; }
        public RelayCommand ExportAsCArrayCommand { get; }
        public RelayCommand ExportAsIntelHexCommand { get; }
        public RelayCommand ShowChecksumCommand { get; }
        public RelayCommand<string> LoadRecentFileCommand { get; }
        public RelayCommand ExportPartitionTableCommand { get; }
        public RelayCommand SearchCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand GenerateReportCommand { get; }

        public FFUBrowserViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;

            // 初始化日志文件
            string logDir = Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"ffubrowser_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            Log("=== FFU浏览器日志已启动 ===");

            BrowseImageCommand = new RelayCommand(BrowseImage);
            LoadImageCommand = new RelayCommand(LoadImage);
            ExtractSelectedCommand = new RelayCommand(ExtractSelected);
            ExtractAllCommand = new RelayCommand(ExtractAll);
            RefreshCommand = new RelayCommand(Refresh);
            GoBackCommand = new RelayCommand(GoBack);
            NavigateToStartCommand = new RelayCommand(() => _mainVM.NavigateToStart());
            ExtractPartitionCommand = new RelayCommand(ExtractCurrentPartition);
            ExtractAllFirmwareCommand = new RelayCommand(ExtractAllFirmware);
            ExportAsCArrayCommand = new RelayCommand(ExportCurrentAsCArray);
            ExportAsIntelHexCommand = new RelayCommand(ExportCurrentAsIntelHex);
            ShowChecksumCommand = new RelayCommand(ShowCurrentChecksum);
            LoadRecentFileCommand = new RelayCommand<string>(LoadRecentFile);
            ExportPartitionTableCommand = new RelayCommand(ExportPartitionTable);
            SearchCommand = new RelayCommand(ExecuteSearch);
            ClearSearchCommand = new RelayCommand(ClearSearch);
            GenerateReportCommand = new RelayCommand(GenerateReport);

            // 加载最近文件列表
            foreach (var f in Services.RecentFilesService.GetRecentFiles())
                RecentFiles.Add(f);
            OnPropertyChanged(nameof(HasRecentFiles));
        }

        /// <summary>
        /// 写日志到文件
        /// </summary>
        private void Log(string message)
        {
            try
            {
                string logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
                System.Diagnostics.Debug.WriteLine(logLine);
            }
            catch { }
        }

        private void BrowseImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择镜像文件",
                Filter = "镜像文件|*.ffu;*.vhdx;*.vhd;*.img;*.bin|FFU文件|*.ffu|VHDX文件|*.vhdx|VHD文件|*.vhd|所有文件|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                ImagePath = dialog.FileName;
                // 选择文件后自动加载
                LoadImage();
            }
        }

        public void LoadImage()
        {
            if (!IsImageValid)
            {
                MessageBox.Show("请选择有效的镜像文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsLoading = true;
            LoadStatus = "加载中...";
            DiskTree.Clear();
            FileList.Clear();

            Log($"开始加载镜像: {ImagePath}");
            Log($"文件大小: {new FileInfo(ImagePath).Length:N0} 字节");

            Task.Run(() =>
            {
                try
                {
                    LoadStatus = "加载镜像中...";
                    
                    // FFU文件特殊处理：解析多个store（UFS LUN）
                    if (Path.GetExtension(ImagePath).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("检测到FFU文件，使用多store解析模式...");
                        _loadedDisks = LoadFFUWithStores(ImagePath);
                    }
                    else
                    {
                        Log("调用 DiskLoader.LoadDisks...");
                        _loadedDisks = DiskLoader.LoadDisks([ImagePath]);
                    }

                    if (_loadedDisks == null || !_loadedDisks.Any())
                    {
                        Log("错误: DiskLoader.LoadDisks 返回空结果");
                        LoadStatus = "加载失败: 无磁盘";
                        IsLoading = false;
                        return;
                    }

                    Log($"DiskLoader.LoadDisks 返回 {_loadedDisks.Count()} 个磁盘");

                    int diskIndex = 0;
                    foreach (var disk in _loadedDisks)
                    {
                        Log($"  磁盘 {diskIndex}: 分区数={disk.Partitions?.Count() ?? 0}");
                        if (disk.Partitions != null)
                        {
                            int partIndex = 0;
                            foreach (var part in disk.Partitions)
                            {
                                Log($"    分区 {partIndex}: Name={part.Name}, Type={part.Type}, Size={part.Size:N0}, FileSystem={(part.FileSystem != null ? "YES" : "NO")}");
                                partIndex++;
                            }
                        }
                        diskIndex++;
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Log("开始构建磁盘树...");
                        BuildDiskTree();
                        Log($"磁盘树构建完成，根节点数={DiskTree.Count}");
                        LoadStatus = $"加载完成: {_loadedDisks.Count()} 个磁盘";

                        // 解析FFU文件头
                        if (Path.GetExtension(ImagePath).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
                        {
                            FfuHeader = Services.FirmwareService.ParseFFUHeader(ImagePath);
                            if (FfuHeader != null)
                                Log($"FFU头解析完成: 签名={FfuHeader.Signature}, 存储数={FfuHeader.StoreCount}");
                        }
                        else
                        {
                            FfuHeader = null;
                        }

                        // 保存到最近文件列表
                        Services.RecentFilesService.AddRecentFile(ImagePath);
                        RecentFiles.Clear();
                        foreach (var f in Services.RecentFilesService.GetRecentFiles())
                            RecentFiles.Add(f);
                        OnPropertyChanged(nameof(HasRecentFiles));
                    });
                }
                catch (Exception ex)
                {
                    Log($"异常: {ex.GetType().Name} - {ex.Message}");
                    Log($"堆栈: {ex.StackTrace}");
                    LoadStatus = $"加载失败: {ex.Message}";
                    MessageBox.Show($"加载镜像失败:\n{ex.Message}\n\n详细日志: {_logFilePath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsLoading = false;
                    Log("加载流程结束");
                }
            });
        }

        /// <summary>
        /// 简单的IDisk实现，用于包装每个store的分区
        /// </summary>
        private class SimpleDisk : IDisk
        {
            public IEnumerable<IPartition> Partitions { get; set; } = [];
        }

        /// <summary>
        /// 加载FFU文件，解析多个store（UFS LUN），每个store作为一个独立磁盘
        /// </summary>
        private IEnumerable<IDisk> LoadFFUWithStores(string ffuPath)
        {
            var disks = new List<IDisk>();

            try
            {
                int storeCount = FullFlashUpdateReaderStream.GetStoreCount(ffuPath);
                Log($"FFU文件包含 {storeCount} 个store");

                for (int i = 0; i < storeCount; i++)
                {
                    try
                    {
                        var store = new FullFlashUpdateReaderStream(ffuPath, (ulong)i);
                        Log($"  Store {i}: Size={store.Length:N0}, SectorSize={store.SectorSize}");

                        long diskCapacity = store.Length;
                        var virtualDisk = new DiscUtils.Raw.Disk(store, Ownership.None, Geometry.FromCapacity(diskCapacity, store.SectorSize));

                        var partitions = DiskCommon.GetPartitions(virtualDisk);
                        var partitionList = new List<IPartition>();

                        foreach (var (partitionInfo, partitionStream) in partitions)
                        {
                            var fsPartition = new FileSystemPartition(
                                partitionStream,
                                partitionInfo.Name,
                                partitionInfo.PartitionTypeGuid,
                                partitionInfo.PartitionGuid);
                            partitionList.Add(fsPartition);
                        }

                        Log($"    Store {i} 解析到 {partitionList.Count} 个分区");
                        disks.Add(new SimpleDisk { Partitions = partitionList });
                    }
                    catch (Exception ex)
                    {
                        Log($"    Store {i} 解析失败: {ex.Message}");
                        disks.Add(new SimpleDisk { Partitions = [] });
                    }
                }

                // 添加UpdateOS磁盘（如果有的话）
                try
                {
                    var updateOSDisks = DiskCommon.GetUpdateOSDisks(disks);
                    if (updateOSDisks != null && updateOSDisks.Any())
                    {
                        Log($"添加 {updateOSDisks.Count()} 个UpdateOS磁盘");
                        disks.AddRange(updateOSDisks);
                    }
                }
                catch (Exception ex)
                {
                    Log($"UpdateOS磁盘解析失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"FFU多store解析失败: {ex.Message}，回退到DiskLoader");
                return DiskLoader.LoadDisks([ffuPath]);
            }

            return disks;
        }

        private void BuildDiskTree()
        {
            if (_loadedDisks == null) return;

            var storagePoolType = new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");

            int diskIndex = 0;
            foreach (var disk in _loadedDisks)
            {
                Log($"  构建磁盘 {diskIndex} 节点...");
                var diskNode = new FFUBrowserNode
                {
                    Name = $"磁盘 {diskIndex}",
                    FullPath = $"磁盘{diskIndex}",
                    NodeType = FFUNodeType.Disk,
                    DiskIndex = diskIndex,
                    Icon = SystemIconHelper.GetDiskIcon(),
                    IsExpanded = true
                };

                // 第一遍：收集存储池内部分区的GUID，避免二次显示
                var storagePoolPartitionGuids = new HashSet<Guid>();
                foreach (var partition in disk.Partitions)
                {
                    if (partition.Type == storagePoolType)
                    {
                        Log($"    收集存储池 {partition.Name} 内部分区GUID...");
                        var guids = GetStoragePoolPartitionGuids(partition);
                        foreach (var guid in guids)
                        {
                            storagePoolPartitionGuids.Add(guid);
                        }
                        Log($"    收集到 {guids.Count} 个存储池内部分区GUID");
                    }
                }

                int partitionCount = 0;
                foreach (var partition in disk.Partitions)
                {
                    // 跳过存储池内部分区（避免二次显示）
                    if (storagePoolPartitionGuids.Contains(partition.ID))
                    {
                        Log($"    跳过存储池内部分区: {partition.Name} (GUID={partition.ID})");
                        continue;
                    }

                    // 存储池分区：特殊处理，显示存储池内部结构
                    if (partition.Type == storagePoolType)
                    {
                        Log($"    发现存储池分区: {partition.Name}, 开始解析存储池...");
                        var storagePoolNode = BuildStoragePoolNode(partition);
                        if (storagePoolNode != null)
                        {
                            diskNode.Children.Add(storagePoolNode);
                            partitionCount++;
                            Log($"    存储池节点添加成功");
                        }
                        else
                        {
                            Log($"    存储池节点构建失败");
                        }
                        continue;
                    }

                    if (partition.FileSystem == null)
                    {
                        var typeInfo = Services.FirmwareService.GetPartitionTypeInfo(partition.Type);
                        Log($"    添加raw分区: {partition.Name}, Size={partition.Size:N0}, Type={typeInfo.Name} ({typeInfo.Description})");
                        var rawPartitionNode = new FFUBrowserNode
                        {
                            Name = string.IsNullOrEmpty(partition.Name) ? "未命名分区" : partition.Name,
                            FullPath = @"\",
                            NodeType = FFUNodeType.Partition,
                            PartitionName = partition.Name ?? "",
                            PartitionTypeGuid = partition.Type,
                            PartitionId = partition.ID,
                            PartitionTypeDescription = typeInfo.Description,
                            PartitionTypeDetail = typeInfo.Detail,
                            IsUefiVariablePartition = partition.Type == new Guid("F0B4F48B-AEBA-4ECF-9142-5DC30CDC3E77"),
                            IsVirtualPartition = partition.Type == Guid.Empty,
                            RawPartition = partition,
                            FileSystem = null,
                            Size = partition.Size,
                            Icon = SystemIconHelper.GetPartitionIcon()
                        };
                        // WCOS分区名称说明覆盖（对BasicData等通用类型，用名称获取更准确的说明）
                        var wcosInfo = Services.FirmwareService.GetWcosPartitionInfo(partition.Name);
                        if (wcosInfo.HasValue)
                        {
                            rawPartitionNode.PartitionTypeDescription = wcosInfo.Value.Description;
                            rawPartitionNode.PartitionTypeDetail = wcosInfo.Value.Detail;
                        }
                        // raw分区不添加占位节点，点击时显示提示
                        diskNode.Children.Add(rawPartitionNode);
                        partitionCount++;
                        continue;
                    }

                    var fsTypeInfo = Services.FirmwareService.GetPartitionTypeInfo(partition.Type);
                    Log($"    添加分区: {partition.Name}, Size={partition.Size:N0}, Type={fsTypeInfo.Name}");
                    var partitionNode = new FFUBrowserNode
                    {
                        Name = string.IsNullOrEmpty(partition.Name) ? "未命名分区" : partition.Name,
                        FullPath = @"\",
                        NodeType = FFUNodeType.Partition,
                        PartitionName = partition.Name ?? "",
                        PartitionTypeGuid = partition.Type,
                        PartitionId = partition.ID,
                        PartitionTypeDescription = fsTypeInfo.Description,
                        PartitionTypeDetail = fsTypeInfo.Detail,
                        IsUefiVariablePartition = partition.Type == new Guid("F0B4F48B-AEBA-4ECF-9142-5DC30CDC3E77"),
                        IsVirtualPartition = partition.Type == Guid.Empty,
                        RawPartition = partition,
                        FileSystem = partition.FileSystem,
                        Size = partition.Size,
                        Icon = SystemIconHelper.GetPartitionIcon()
                    };

                    // WCOS分区名称说明覆盖（对BasicData等通用类型，用名称获取更准确的说明）
                    var wcosInfo2 = Services.FirmwareService.GetWcosPartitionInfo(partition.Name);
                    if (wcosInfo2.HasValue)
                    {
                        partitionNode.PartitionTypeDescription = wcosInfo2.Value.Description;
                        partitionNode.PartitionTypeDetail = wcosInfo2.Value.Detail;
                    }

                    // 添加占位节点
                    partitionNode.Children.Add(new FFUBrowserNode { Name = "加载中..." });
                    diskNode.Children.Add(partitionNode);
                    partitionCount++;
                }

                Log($"  磁盘 {diskIndex} 构建完成: {partitionCount} 个分区/存储池");

                // 计算磁盘总大小
                long totalSize = 0;
                int realPartitionCount = 0;
                if (disk.Partitions != null)
                {
                    foreach (var p in disk.Partitions)
                    {
                        totalSize += p.Size;
                        realPartitionCount++;
                    }
                }
                diskNode.DiskTotalSize = totalSize;
                diskNode.DiskTypeDescription = realPartitionCount > 0 ? "物理磁盘" : "空磁盘";

                if (diskNode.Children.Count > 0)
                {
                    DiskTree.Add(diskNode);
                }
                else
                {
                    Log($"  警告: 磁盘 {diskIndex} 没有可显示的分区，跳过");
                }
                diskIndex++;
            }
        }

        /// <summary>
        /// 获取存储池内所有虚拟磁盘的分区GUID（用于避免二次显示）
        /// </summary>
        private List<Guid> GetStoragePoolPartitionGuids(IPartition partition)
        {
            var guids = new List<Guid>();
            try
            {
                partition.Stream.Seek(0, SeekOrigin.Begin);
                var pool = new Pool(partition.Stream);
                var disks = pool.GetDisks();
                if (disks == null) return guids;

                foreach (var disk in disks)
                {
                    try
                    {
                        var space = pool.OpenDisk(disk.Key);
                        int sectorSize = DetectSectorSize(space);
                        var partitions = ParseVirtualDiskPartitions(space, sectorSize);
                        if (partitions != null)
                        {
                            foreach (var p in partitions)
                            {
                                guids.Add(p.ID);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"    收集存储池分区GUID失败: {ex.Message}");
            }
            return guids;
        }

        /// <summary>
        /// 构建存储池节点，解析存储池中的虚拟磁盘和分区
        /// </summary>
        private FFUBrowserNode? BuildStoragePoolNode(IPartition partition)
        {
            try
            {
                Log($"      存储池: 重置Stream位置到0...");
                partition.Stream.Seek(0, SeekOrigin.Begin);

                Log($"      存储池: 创建Pool对象...");
                var pool = new Pool(partition.Stream);
                var disks = pool.GetDisks();
                Log($"      存储池: GetDisks返回 {(disks != null ? disks.Count : 0)} 个虚拟磁盘");

                var storagePoolNode = new FFUBrowserNode
                {
                    Name = $"💠 存储池: {partition.Name}",
                    FullPath = $"存储池_{partition.Name}",
                    NodeType = FFUNodeType.Partition, // 使用分区类型，但特殊标记
                    PartitionName = partition.Name ?? "",
                    Size = partition.Size,
                    Icon = SystemIconHelper.GetStoragePoolIcon(),
                    IsExpanded = true
                };

                if (disks == null || disks.Count == 0)
                {
                    Log($"      存储池: 无虚拟磁盘");
                    storagePoolNode.Children.Add(new FFUBrowserNode { Name = "存储池为空" });
                    return storagePoolNode;
                }

                foreach (var disk in disks.OrderBy(d => d.Key))
                {
                    try
                    {
                        Log($"      存储池: 打开虚拟磁盘 {disk.Key}: {disk.Value}");
                        var space = pool.OpenDisk(disk.Key);
                        int sectorSize = DetectSectorSize(space);
                        Log($"      存储池: 虚拟磁盘 {disk.Key} 扇区大小={sectorSize}, 容量={space.Length:N0}");

                        var virtualDiskNode = new FFUBrowserNode
                        {
                            Name = $"💾 虚拟磁盘 {disk.Key}: {disk.Value}",
                            FullPath = $"存储池_{partition.Name}_磁盘{disk.Key}",
                            NodeType = FFUNodeType.Disk,
                            Size = space.Length,
                            Icon = SystemIconHelper.GetDiskIcon(),
                            IsExpanded = true
                        };

                        // 解析虚拟磁盘的分区表
                        Log($"      存储池: 解析虚拟磁盘 {disk.Key} 的分区表...");
                        var partitions = ParseVirtualDiskPartitions(space, sectorSize);
                        if (partitions != null)
                        {
                            Log($"      存储池: 虚拟磁盘 {disk.Key} 解析到 {partitions.Count} 个分区");
                            foreach (var p in partitions)
                            {
                                if (p.FileSystem == null)
                                {
                                    Log($"        跳过分区 {p.Name}: 无文件系统");
                                    continue;
                                }

                                Log($"        添加分区: {p.Name}, Size={p.Size:N0}");
                                var partitionNode = new FFUBrowserNode
                                {
                                    Name = string.IsNullOrEmpty(p.Name) ? "未命名分区" : p.Name,
                                    FullPath = @"\",
                                    NodeType = FFUNodeType.Partition,
                                    PartitionName = p.Name ?? "",
                                    FileSystem = p.FileSystem,
                                    Size = p.Size,
                                    Icon = SystemIconHelper.GetPartitionIcon()
                                };
                                partitionNode.Children.Add(new FFUBrowserNode { Name = "加载中..." });
                                virtualDiskNode.Children.Add(partitionNode);
                            }
                        }
                        else
                        {
                            Log($"      存储池: 虚拟磁盘 {disk.Key} 分区表解析失败");
                        }

                        if (virtualDiskNode.Children.Count == 0)
                        {
                            virtualDiskNode.Children.Add(new FFUBrowserNode { Name = "无法识别分区表" });
                        }

                        storagePoolNode.Children.Add(virtualDiskNode);
                    }
                    catch (Exception ex)
                    {
                        Log($"      存储池: 虚拟磁盘 {disk.Key} 解析异常: {ex.Message}");
                        storagePoolNode.Children.Add(new FFUBrowserNode { Name = $"虚拟磁盘 {disk.Key} 解析失败: {ex.Message}" });
                    }
                }

                return storagePoolNode;
            }
            catch (Exception ex)
            {
                Log($"      存储池: 构建异常: {ex.Message}");
                return new FFUBrowserNode
                {
                    Name = $"💠 存储池: {partition.Name} (解析失败: {ex.Message})",
                    Icon = SystemIconHelper.GetStoragePoolIcon()
                };
            }
        }

        /// <summary>
        /// 检测扇区大小
        /// </summary>
        private int DetectSectorSize(Stream stream)
        {
            int sectorSize = 4096;
            try
            {
                if (stream.Length > 4096 * 2)
                {
                    stream.Seek(512, SeekOrigin.Begin);
                    byte[] header1 = new byte[8];
                    stream.Read(header1, 0, 8);

                    stream.Seek(4096, SeekOrigin.Begin);
                    byte[] header2 = new byte[8];
                    stream.Read(header2, 0, 8);

                    string header1str = System.Text.Encoding.ASCII.GetString(header1);
                    string header2str = System.Text.Encoding.ASCII.GetString(header2);

                    if (header1str == "EFI PART")
                    {
                        sectorSize = 512;
                    }
                    else if (header2str == "EFI PART")
                    {
                        sectorSize = 4096;
                    }
                    else if (stream.Length % 512 == 0 && stream.Length % 4096 != 0)
                    {
                        sectorSize = 512;
                    }

                    stream.Seek(0, SeekOrigin.Begin);
                }
            }
            catch { }
            return sectorSize;
        }

        /// <summary>
        /// 解析虚拟磁盘的分区表（GPT + MBR回退）
        /// </summary>
        private List<IPartition>? ParseVirtualDiskPartitions(Stream diskStream, int sectorSize)
        {
            try
            {
                Log($"        解析虚拟磁盘分区表: 容量={diskStream.Length:N0}, 传入扇区大小={sectorSize}");

                // 先尝试用传入的扇区大小
                var partitions = TryParseWithSectorSize(diskStream, sectorSize);
                if (partitions != null && partitions.Count > 0)
                {
                    Log($"        使用扇区大小 {sectorSize} 解析成功，找到 {partitions.Count} 个分区");
                    return partitions;
                }

                // 如果失败，尝试其他扇区大小
                int[] sectorSizesToTry = [512, 4096, 2048, 1024];
                foreach (int size in sectorSizesToTry)
                {
                    if (size == sectorSize) continue;
                    Log($"        尝试扇区大小 {size}...");
                    partitions = TryParseWithSectorSize(diskStream, size);
                    if (partitions != null && partitions.Count > 0)
                    {
                        Log($"        使用扇区大小 {size} 解析成功，找到 {partitions.Count} 个分区");
                        return partitions;
                    }
                }

                Log($"        所有扇区大小都解析失败");
                return null;
            }
            catch (Exception ex)
            {
                Log($"        解析虚拟磁盘分区异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 使用指定扇区大小尝试解析分区表
        /// </summary>
        private List<IPartition>? TryParseWithSectorSize(Stream diskStream, int sectorSize)
        {
            try
            {
                diskStream.Seek(0, SeekOrigin.Begin);
                var geometry = Geometry.FromCapacity(diskStream.Length, sectorSize);
                var virtualDisk = new DiscUtils.Raw.Disk(diskStream, Ownership.None, geometry);

                var rawPartitions = DiskCommon.GetPartitions(virtualDisk);
                if (rawPartitions == null || rawPartitions.Count == 0) return null;

                var result = new List<IPartition>();
                foreach (var (partitionInfo, partitionStream) in rawPartitions)
                {
                    var fsPartition = new FileSystemPartition(
                        partitionStream,
                        partitionInfo.Name,
                        partitionInfo.PartitionTypeGuid,
                        partitionInfo.PartitionGuid);
                    result.Add(fsPartition);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 树节点选中时调用，显示该文件夹的内容
        /// </summary>
        public void OnTreeNodeSelected(FFUBrowserNode? node)
        {
            if (node == null) return;
            if (node.NodeType == FFUNodeType.File) return;

            CurrentNode = node;
            _currentFolder = node;
            IsPartitionSelected = node.NodeType == FFUNodeType.Partition;
            IsFirmwarePartitionSelected = node.NodeType == FFUNodeType.Partition
                && Services.FirmwareService.IsFirmwarePartition(node.PartitionTypeGuid);
            CurrentPath = node.NodeType == FFUNodeType.Partition
                ? $"[{node.Name}]"
                : $"[{node.PartitionName}]\\{node.FullPath}";

            // 更新磁盘布局可视化
            UpdateDiskLayout(node);

            FileList.Clear();
            ShowUefiVariables = false;
            ShowHexViewer = false;

            // UEFI变量分区（UEFI_BS_NV）：解析并显示UEFI变量
            Guid uefiVarType = new("F0B4F48B-AEBA-4ECF-9142-5DC30CDC3E77");
            bool isUefiVarPartition = node.NodeType == FFUNodeType.Partition
                && node.PartitionTypeGuid == uefiVarType;

            Log($"OnTreeNodeSelected: Name={node.Name}, Type={node.NodeType}, PartitionTypeGuid={node.PartitionTypeGuid}, IsUefiVar={isUefiVarPartition}, RawPartition={(node.RawPartition != null ? "YES" : "NO")}");

            if (isUefiVarPartition && node.RawPartition != null)
            {
                Log($"检测到UEFI变量分区: {node.Name}，开始解析UEFI变量...");
                LoadStatus = $"正在解析UEFI变量: {node.Name}...";

                Task.Run(() =>
                {
                    try
                    {
                        var variables = Services.FirmwareService.ParseUefiVariables(node.RawPartition);
                        Log($"UEFI变量解析完成: 共 {variables.Count} 个变量");

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            UefiVariables.Clear();
                            foreach (var v in variables)
                            {
                                UefiVariables.Add(v);
                            }
                            ShowUefiVariables = true;
                            LoadStatus = $"UEFI变量: {variables.Count} 个 (只读)";
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"UEFI变量解析失败: {ex.Message}");
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LoadStatus = $"UEFI变量解析失败: {ex.Message}";
                        });
                    }
                });
                return;
            }

            if (node.FileSystem == null)
            {
                // raw分区
                if (node.RawPartition != null && node.RawPartition.Stream != null)
                {
                    // 懒识别固件格式（如果还没识别过）
                    if (node.FirmwareFormat == null)
                    {
                        try
                        {
                            node.FirmwareFormat = Services.FirmwareService.DetectFirmwareFormat(node.RawPartition);
                            Log($"固件格式识别: {node.Name} -> {node.FirmwareFormat.FormatName} (魔数: {node.FirmwareFormat.MagicHex})");
                        }
                        catch (Exception ex)
                        {
                            Log($"固件格式识别失败: {node.Name} - {ex.Message}");
                        }
                    }

                    // 如果是UEFI FV格式，解析FV并显示FFS模块列表
                    if (node.FirmwareFormat != null &&
                        node.FirmwareFormat.Format == Services.FirmwareService.FirmwareFormat.UefiFirmwareVolume)
                    {
                        try
                        {
                            Log($"检测到UEFI FV格式: {node.Name}，开始解析FV...");
                            LoadStatus = $"正在解析UEFI固件卷: {node.Name}...";

                            node.RawPartition.Stream.Seek(0, SeekOrigin.Begin);
                            var fvInfo = Services.FirmwareParser.Parse(node.RawPartition.Stream, node.Size);
                            Log($"FV解析完成: {fvInfo.Modules.Count} 个模块, 摘要: {fvInfo.Summary}");

                            FileList.Clear();
                            foreach (var module in fvInfo.Modules)
                            {
                                var moduleNode = new FFUBrowserNode
                                {
                                    Name = $"{module.Name} [{module.Type}]",
                                    FullPath = $"\\{module.Name}",
                                    NodeType = FFUNodeType.File,
                                    PartitionName = node.Name,
                                    Size = module.Size,
                                    Icon = SystemIconHelper.GetFileIcon(".efi"),
                                    // 存储模块偏移和大小用于提取
                                    Tag = new FfsModuleInfo { Offset = module.Offset, Size = module.Size, Name = module.Name, TypeGuid = module.TypeGuid }
                                };
                                FileList.Add(moduleNode);
                            }

                            string formatInfo = node.FirmwareFormat != null
                                ? $" | 格式: {node.FirmwareFormat.FormatName}"
                                : "";
                            LoadStatus = $"分区 {node.Name} (raw){formatInfo} | FV模块: {fvInfo.Modules.Count} 个 | 大小: {node.Size:N0} 字节";
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log($"FV解析失败: {node.Name} - {ex.Message}，回退到十六进制视图");
                        }
                    }

                    // 如果是ELF格式，解析ELF并显示段列表
                    if (node.FirmwareFormat != null &&
                        node.FirmwareFormat.Format == Services.FirmwareService.FirmwareFormat.Elf)
                    {
                        try
                        {
                            Log($"检测到ELF格式: {node.Name}，开始解析ELF...");
                            LoadStatus = $"正在解析ELF固件: {node.Name}...";

                            node.RawPartition.Stream.Seek(0, SeekOrigin.Begin);
                            var elfInfo = Services.FirmwareParser.Parse(node.RawPartition.Stream, node.Size);
                            Log($"ELF解析完成: 架构={elfInfo.Architecture}, 入口={elfInfo.EntryPoint}, {elfInfo.Sections.Count} 个段, 摘要: {elfInfo.Summary}");

                            FileList.Clear();

                            // 添加ELF头信息节点
                            var headerNode = new FFUBrowserNode
                            {
                                Name = $"📋 ELF头: {elfInfo.Architecture} | 入口: {elfInfo.EntryPoint} | 代码: {elfInfo.CodeSize:N0}B | 数据: {elfInfo.DataSize:N0}B",
                                FullPath = "\\__elf_header__",
                                NodeType = FFUNodeType.Folder,
                                PartitionName = node.Name,
                                Size = 0,
                                Icon = SystemIconHelper.GetFolderIcon()
                            };
                            FileList.Add(headerNode);

                            // 添加段列表
                            foreach (var section in elfInfo.Sections)
                            {
                                var segNode = new FFUBrowserNode
                                {
                                    Name = $"{section.Name} [{section.Type}] 地址=0x{section.VirtualAddress:X} 标志={section.Flags}",
                                    FullPath = $"\\{section.Name}",
                                    NodeType = FFUNodeType.File,
                                    PartitionName = node.Name,
                                    Size = section.RawSize,
                                    Icon = SystemIconHelper.GetFileIcon(".bin"),
                                    // 存储段偏移和大小用于提取
                                    Tag = new FfsModuleInfo { Offset = section.RawOffset, Size = section.RawSize, Name = section.Name, TypeGuid = Guid.Empty }
                                };
                                FileList.Add(segNode);
                            }

                            string formatInfo = node.FirmwareFormat != null
                                ? $" | 格式: {node.FirmwareFormat.FormatName}"
                                : "";
                            LoadStatus = $"分区 {node.Name} (raw){formatInfo} | {elfInfo.Architecture} | 入口: {elfInfo.EntryPoint} | {elfInfo.Sections.Count} 个段 | 大小: {node.Size:N0} 字节";
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log($"ELF解析失败: {node.Name} - {ex.Message}，回退到十六进制视图");
                        }
                    }

                    // 其他raw格式，显示十六进制查看器
                    HexStream = node.RawPartition.Stream;
                    HexLength = node.Size;
                    HexDisplayName = node.Name;
                    ShowHexViewer = true;

                    string formatInfo2 = node.FirmwareFormat != null
                        ? $" | 格式: {node.FirmwareFormat.FormatName}"
                        : "";
                    LoadStatus = $"分区 {node.Name} (raw){formatInfo2} | 十六进制视图 | 大小: {node.Size:N0} 字节";
                    Log($"切换到十六进制查看器: {node.Name}, 大小={node.Size}");
                }
                else
                {
                    // 无法获取流，显示提示
                    var hintNode = new FFUBrowserNode
                    {
                        Name = "（该分区为 raw 格式，无文件系统）",
                        FullPath = "",
                        NodeType = FFUNodeType.Folder,
                        Size = node.Size,
                        Icon = SystemIconHelper.GetFolderIcon()
                    };
                    FileList.Add(hintNode);
                    LoadStatus = $"分区 {node.Name} 为 raw 格式，无法浏览文件内容";
                }
                return;
            }

            try
            {
                // 如果不是分区根目录，添加"返回上一级"节点
                if (node.NodeType != FFUNodeType.Partition)
                {
                    var parentNode = new FFUBrowserNode
                    {
                        Name = "..",
                        FullPath = GetParentPath(node.FullPath),
                        NodeType = FFUNodeType.Folder,
                        FileSystem = node.FileSystem,
                        PartitionName = node.PartitionName,
                        Icon = SystemIconHelper.GetFolderIcon()
                    };
                    FileList.Add(parentNode);
                }

                // 根目录使用反斜杠，确保DiscUtils正确识别
                string folderPath = node.NodeType == FFUNodeType.Partition ? @"\" : node.FullPath;

                // 先添加文件夹
                var directories = node.FileSystem.GetDirectories(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var dir in directories.OrderBy(d => d))
                {
                    // 规范化路径
                    string normalizedDir = dir.Replace('/', '\\').TrimEnd('\\');
                    var dirName = Path.GetFileName(normalizedDir);
                    if (string.IsNullOrEmpty(dirName)) continue;

                    var fileNode = new FFUBrowserNode
                    {
                        Name = dirName,
                        FullPath = normalizedDir,
                        NodeType = FFUNodeType.Folder,
                        FileSystem = node.FileSystem,
                        PartitionName = node.PartitionName,
                        Icon = SystemIconHelper.GetFolderIcon()
                    };
                    FileList.Add(fileNode);
                }

                // 再添加文件
                var files = node.FileSystem.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files.OrderBy(f => f))
                {
                    // 规范化路径
                    string normalizedFile = file.Replace('/', '\\');
                    var fileName = Path.GetFileName(normalizedFile);
                    long fileSize = 0;
                    DateTime? modified = null;

                    try
                    {
                        var fileInfo = node.FileSystem.GetFileInfo(normalizedFile);
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
                        FileSystem = node.FileSystem,
                        PartitionName = node.PartitionName,
                        Icon = SystemIconHelper.GetFileIcon(fileName)
                    };
                    FileList.Add(fileNode);
                }
            }
            catch (Exception ex)
            {
                LoadStatus = $"读取目录失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 文件列表双击进入文件夹
        /// </summary>
        public void OnFileDoubleClick(FFUBrowserNode? node)
        {
            if (node == null) return;
            if (node.Name == "..")
            {
                GoBack();
                return;
            }
            if (node.NodeType == FFUNodeType.Folder)
            {
                OnTreeNodeSelected(node);
            }
        }

        /// <summary>
        /// 搜索结果双击：导航到文件所在目录
        /// </summary>
        public void OnSearchResultDoubleClick(FFUBrowserNode? node)
        {
            if (node == null) return;
            Log($"搜索结果双击: {node.PartitionName}\\{node.FullPath}");

            // 简单处理：显示文件路径，后续可完善自动导航
            string parentPath = GetParentPath(node.FullPath);
            LoadStatus = $"文件位置: [{node.PartitionName}] {parentPath}（请在左侧树状图中导航）";
            MessageBox.Show($"文件位置:\n\n分区: {node.PartitionName}\n路径: {node.FullPath}\n\n请在左侧树状图中导航到对应分区查看。",
                "搜索结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 获取父路径
        /// </summary>
        private string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == @"\") return @"\";
            path = path.TrimEnd('\\');
            int lastSlash = path.LastIndexOf('\\');
            if (lastSlash <= 0) return @"\";
            return path.Substring(0, lastSlash);
        }

        private void GoBack()
        {
            if (_currentFolder == null) return;

            // 如果当前是分区根目录，不做任何操作
            if (_currentFolder.NodeType == FFUNodeType.Partition) return;

            // 计算父路径
            string parentPath = GetParentPath(_currentFolder.FullPath);

            // 如果父路径是根目录，创建一个分区节点
            if (parentPath == @"\")
            {
                var partitionNode = new FFUBrowserNode
                {
                    Name = _currentFolder.PartitionName,
                    FullPath = @"\",
                    NodeType = FFUNodeType.Partition,
                    PartitionName = _currentFolder.PartitionName,
                    FileSystem = _currentFolder.FileSystem,
                    Icon = SystemIconHelper.GetPartitionIcon()
                };
                OnTreeNodeSelected(partitionNode);
            }
            else
            {
                // 创建父文件夹节点
                var parentNode = new FFUBrowserNode
                {
                    Name = Path.GetFileName(parentPath),
                    FullPath = parentPath,
                    NodeType = FFUNodeType.Folder,
                    FileSystem = _currentFolder.FileSystem,
                    PartitionName = _currentFolder.PartitionName,
                    Icon = SystemIconHelper.GetFolderIcon()
                };
                OnTreeNodeSelected(parentNode);
            }
        }

        private void Refresh()
        {
            if (_currentFolder != null)
            {
                OnTreeNodeSelected(_currentFolder);
            }
        }

        /// <summary>
        /// 提取选中的文件/文件夹
        /// </summary>
        private void ExtractSelected()
        {
            var itemsToExtract = SelectedFiles.Where(f => f.NodeType == FFUNodeType.File).ToList();
            if (itemsToExtract.Count == 0)
            {
                MessageBox.Show("请先选择要提取的文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择提取目标文件夹"
            };
            if (dialog.ShowDialog() != true) return;

            string outputDir = dialog.FolderName;
            ExtractItems(itemsToExtract, outputDir);
        }

        /// <summary>
        /// 提取当前目录的所有文件
        /// </summary>
        private void ExtractAll()
        {
            if (_currentFolder == null || _currentFolder.FileSystem == null)
            {
                MessageBox.Show("请先选择一个分区或文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择提取目标文件夹"
            };
            if (dialog.ShowDialog() != true) return;

            string outputDir = dialog.FolderName;
            string folderPath = _currentFolder.NodeType == FFUNodeType.Partition ? "" : _currentFolder.FullPath;

            try
            {
                var allFiles = _currentFolder.FileSystem.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                int count = 0;
                foreach (var file in allFiles)
                {
                    if (ExtractSingleFile(_currentFolder.FileSystem, file, outputDir, folderPath))
                    {
                        count++;
                    }
                }
                MessageBox.Show($"提取完成: {count} 个文件", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExtractItems(List<FFUBrowserNode> items, string outputDir)
        {
            try
            {
                int count = 0;
                foreach (var item in items)
                {
                    // FFS模块提取（从raw分区流中读取指定偏移和大小）
                    if (item.Tag is Models.FFUBrowser.FfsModuleInfo ffsInfo && _currentFolder?.RawPartition?.Stream != null)
                    {
                        try
                        {
                            string fileName = $"{ffsInfo.Name}.ffs";
                            foreach (char c in Path.GetInvalidFileNameChars())
                                fileName = fileName.Replace(c, '_');
                            string destPath = Path.Combine(outputDir, fileName);

                            var stream = _currentFolder.RawPartition.Stream;
                            stream.Seek(ffsInfo.Offset, SeekOrigin.Begin);
                            byte[] buffer = new byte[ffsInfo.Size];
                            int read = stream.Read(buffer, 0, (int)ffsInfo.Size);
                            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                            {
                                fs.Write(buffer, 0, read);
                            }
                            Log($"FFS模块提取: {ffsInfo.Name} -> {destPath} ({read:N0} 字节)");
                            count++;
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Log($"FFS模块提取失败: {item.Name} - {ex.Message}");
                            continue;
                        }
                    }

                    if (item.FileSystem == null) continue;

                    if (item.NodeType == FFUNodeType.File)
                    {
                        if (ExtractSingleFile(item.FileSystem, item.FullPath, outputDir, ""))
                        {
                            count++;
                        }
                    }
                    else if (item.NodeType == FFUNodeType.Folder)
                    {
                        var allFiles = item.FileSystem.GetFiles(item.FullPath, "*.*", SearchOption.AllDirectories);
                        foreach (var file in allFiles)
                        {
                            if (ExtractSingleFile(item.FileSystem, file, outputDir, item.FullPath))
                            {
                                count++;
                            }
                        }
                    }
                }
                MessageBox.Show($"提取完成: {count} 个文件", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ExtractSingleFile(IFileSystem fs, string sourcePath, string outputDir, string basePath)
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

        /// <summary>
        /// 提取当前选中的分区为bin文件
        /// </summary>
        private void ExtractCurrentPartition()
        {
            if (_currentFolder == null || _currentFolder.RawPartition == null)
            {
                MessageBox.Show("请先在左侧树状图中选择一个分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var partition = _currentFolder.RawPartition;
            string defaultName = string.IsNullOrEmpty(partition.Name) ? "partition" : partition.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                defaultName = defaultName.Replace(c, '_');
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存分区为bin文件",
                FileName = $"{defaultName}.bin",
                Filter = "二进制文件|*.bin|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadStatus = $"正在提取分区 {partition.Name}...";
                bool success = Services.FirmwareService.ExtractPartitionToBin(partition, dialog.FileName);
                if (success)
                {
                    LoadStatus = $"分区 {partition.Name} 已提取到 {dialog.FileName}";
                    MessageBox.Show($"提取完成!\n\n分区: {partition.Name}\n大小: {partition.Size:N0} 字节\n保存到: {dialog.FileName}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("提取失败: 无法读取分区数据", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 导出当前分区为C语言数组
        /// </summary>
        private void ExportCurrentAsCArray()
        {
            if (_currentFolder == null || _currentFolder.RawPartition == null)
            {
                MessageBox.Show("请先在左侧树状图中选择一个分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var partition = _currentFolder.RawPartition;
            string defaultName = string.IsNullOrEmpty(partition.Name) ? "partition" : partition.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                defaultName = defaultName.Replace(c, '_');

            var dialog = new SaveFileDialog
            {
                Title = "导出为C语言数组",
                FileName = $"{defaultName}.h",
                Filter = "C头文件|*.h|文本文件|*.txt|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadStatus = $"正在导出 {partition.Name} 为C数组...";
                bool success = Services.FirmwareService.ExportToCArray(partition, dialog.FileName);
                if (success)
                {
                    LoadStatus = $"已导出 {partition.Name} 为C数组";
                    MessageBox.Show($"导出完成!\n\n分区: {partition.Name}\n大小: {partition.Size:N0} 字节\n保存到: {dialog.FileName}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("导出失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 导出当前分区为Intel HEX格式
        /// </summary>
        private void ExportCurrentAsIntelHex()
        {
            if (_currentFolder == null || _currentFolder.RawPartition == null)
            {
                MessageBox.Show("请先在左侧树状图中选择一个分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var partition = _currentFolder.RawPartition;
            string defaultName = string.IsNullOrEmpty(partition.Name) ? "partition" : partition.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                defaultName = defaultName.Replace(c, '_');

            var dialog = new SaveFileDialog
            {
                Title = "导出为Intel HEX格式",
                FileName = $"{defaultName}.hex",
                Filter = "Intel HEX文件|*.hex|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadStatus = $"正在导出 {partition.Name} 为Intel HEX...";
                bool success = Services.FirmwareService.ExportToIntelHex(partition, dialog.FileName);
                if (success)
                {
                    LoadStatus = $"已导出 {partition.Name} 为Intel HEX";
                    MessageBox.Show($"导出完成!\n\n分区: {partition.Name}\n大小: {partition.Size:N0} 字节\n保存到: {dialog.FileName}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("导出失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 显示当前分区的校验和
        /// </summary>
        private void ShowCurrentChecksum()
        {
            if (_currentFolder == null || _currentFolder.RawPartition == null)
            {
                MessageBox.Show("请先在左侧树状图中选择一个分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var partition = _currentFolder.RawPartition;
            try
            {
                LoadStatus = $"正在计算 {partition.Name} 的校验和...";
                var (md5, sha1, sha256) = Services.FirmwareService.CalculateChecksums(partition);

                string message = $"分区: {partition.Name}\n大小: {partition.Size:N0} 字节\n\n" +
                                  $"MD5:    {md5}\n" +
                                  $"SHA1:   {sha1}\n" +
                                  $"SHA256: {sha256}";

                LoadStatus = $"校验和计算完成: {partition.Name}";
                MessageBox.Show(message, "校验和", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"计算失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 加载最近打开的文件
        /// </summary>
        private void LoadRecentFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("文件不存在或已被移动。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                // 从列表中移除
                RecentFiles.Remove(filePath ?? "");
                return;
            }
            ImagePath = filePath;
            LoadImage();
        }

        /// <summary>外部（起始页最近文件）按路径打开镜像。</summary>
        public void OpenImageFile(string path) => LoadRecentFile(path);

        /// <summary>
        /// 导出分区表
        /// </summary>
        private void ExportPartitionTable()
        {
            if (_loadedDisks == null || !_loadedDisks.Any())
            {
                MessageBox.Show("请先加载镜像文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出分区表",
                FileName = "partition_table.csv",
                Filter = "CSV文件|*.csv|JSON文件|*.json|文本文件|*.txt"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string ext = Path.GetExtension(dialog.FileName).ToLower();
                string content = ext switch
                {
                    ".json" => ExportPartitionTableJson(),
                    ".txt" => ExportPartitionTableText(),
                    _ => ExportPartitionTableCsv()
                };

                File.WriteAllText(dialog.FileName, content, System.Text.Encoding.UTF8);
                MessageBox.Show($"分区表已导出到:\n{dialog.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ExportPartitionTableCsv()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("磁盘索引,分区索引,分区名称,分区类型GUID,类型描述,分区ID,大小(字节),大小(MB),文件系统,是否固件");

            int diskIndex = 0;
            foreach (var disk in _loadedDisks!)
            {
                if (disk.Partitions == null) { diskIndex++; continue; }
                int partIndex = 0;
                foreach (var p in disk.Partitions)
                {
                    var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                    string fs = p.FileSystem != null ? "YES" : "NO";
                    string fw = info.IsFirmware ? "YES" : "NO";
                    double sizeMB = Math.Round(p.Size / 1024.0 / 1024.0, 2);
                    sb.AppendLine($"{diskIndex},{partIndex},{p.Name},{p.Type:B},\"{info.Description}\",{p.ID:B},{p.Size},{sizeMB},{fs},{fw}");
                    partIndex++;
                }
                diskIndex++;
            }
            return sb.ToString();
        }

        private string ExportPartitionTableJson()
        {
            var disks = new List<object>();
            int diskIndex = 0;
            foreach (var disk in _loadedDisks!)
            {
                var parts = new List<object>();
                if (disk.Partitions != null)
                {
                    int partIndex = 0;
                    foreach (var p in disk.Partitions)
                    {
                        var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                        parts.Add(new
                        {
                            index = partIndex,
                            name = p.Name,
                            typeGuid = p.Type.ToString("B"),
                            typeName = info.Name,
                            typeDescription = info.Description,
                            partitionId = p.ID.ToString("B"),
                            size = p.Size,
                            sizeMB = Math.Round(p.Size / 1024.0 / 1024.0, 2),
                            hasFileSystem = p.FileSystem != null,
                            isFirmware = info.IsFirmware
                        });
                        partIndex++;
                    }
                }
                disks.Add(new { diskIndex, partitions = parts });
                diskIndex++;
            }
            return System.Text.Json.JsonSerializer.Serialize(new { disks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private string ExportPartitionTableText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 分区表导出 ===");
            sb.AppendLine($"镜像: {ImagePath}");
            sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            int diskIndex = 0;
            foreach (var disk in _loadedDisks!)
            {
                sb.AppendLine($"--- 磁盘 {diskIndex} ---");
                if (disk.Partitions != null)
                {
                    int partIndex = 0;
                    foreach (var p in disk.Partitions)
                    {
                        var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                        string fs = p.FileSystem != null ? "已识别" : "raw(无)";
                        sb.AppendLine($"  分区 {partIndex}: {p.Name}");
                        sb.AppendLine($"    类型: {info.Name} - {info.Description}");
                        sb.AppendLine($"    类型GUID: {p.Type:B}");
                        sb.AppendLine($"    分区ID: {p.ID:B}");
                        sb.AppendLine($"    大小: {p.Size:N0} 字节 ({Math.Round(p.Size / 1024.0 / 1024.0, 2)} MB)");
                        sb.AppendLine($"    文件系统: {fs}");
                        sb.AppendLine($"    固件分区: {(info.IsFirmware ? "是" : "否")}");
                        sb.AppendLine();
                        partIndex++;
                    }
                }
                else
                {
                    sb.AppendLine("  (无分区)");
                    sb.AppendLine();
                }
                diskIndex++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 批量提取所有固件分区为bin文件
        /// </summary>
        private void ExtractAllFirmware()
        {
            if (_loadedDisks == null || !_loadedDisks.Any())
            {
                MessageBox.Show("请先加载镜像文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择固件分区保存目录"
            };
            if (dialog.ShowDialog() != true) return;

            string outputDir = Path.Combine(dialog.FolderName, "Firmware");
            try
            {
                LoadStatus = "正在提取所有固件分区...";
                int count = Services.FirmwareService.ExtractAllFirmwarePartitions(_loadedDisks, outputDir);
                LoadStatus = $"已提取 {count} 个固件分区到 {outputDir}";
                MessageBox.Show($"批量提取完成!\n\n共提取 {count} 个固件分区\n保存到: {outputDir}",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 执行文件搜索
        /// </summary>
        private async void ExecuteSearch()
        {
            string keyword = SearchKeyword.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                MessageBox.Show("请输入搜索关键词。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_loadedDisks == null || !_loadedDisks.Any())
            {
                MessageBox.Show("请先加载镜像文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsSearching = true;
            ShowSearchResults = true;
            SearchResults.Clear();
            LoadStatus = $"正在搜索: {keyword}...";
            Log($"开始搜索: keyword={keyword}, allPartitions={SearchAllPartitions}");

            try
            {
                var results = await Task.Run(() =>
                {
                    var found = new List<FFUBrowserNode>();
                    var searchPattern = ConvertWildcardToRegex(keyword);

                    // 确定要搜索的分区列表
                    var partitionsToSearch = new List<(string PartitionName, IFileSystem? FileSystem)>();

                    if (SearchAllPartitions)
                    {
                        // 搜索所有分区
                        foreach (var disk in _loadedDisks!)
                        {
                            if (disk.Partitions == null) continue;
                            foreach (var p in disk.Partitions)
                            {
                                if (p.FileSystem != null)
                                    partitionsToSearch.Add((p.Name ?? "未命名", p.FileSystem));
                            }
                        }
                    }
                    else
                    {
                        // 只搜索当前分区
                        if (_currentFolder?.FileSystem != null)
                        {
                            partitionsToSearch.Add((_currentFolder.PartitionName, _currentFolder.FileSystem));
                        }
                        else
                        {
                            return found;
                        }
                    }

                    // 遍历每个分区搜索
                    foreach (var (partName, fs) in partitionsToSearch)
                    {
                        try
                        {
                            SearchInFileSystem(fs, @"\", searchPattern, partName, found, maxResults: 500);
                        }
                        catch (Exception ex)
                        {
                            Log($"搜索分区 {partName} 时出错: {ex.Message}");
                        }
                    }

                    return found;
                });

                foreach (var node in results)
                {
                    SearchResults.Add(node);
                }

                LoadStatus = $"搜索完成: 找到 {SearchResults.Count} 个文件 (关键词: {keyword})";
                Log($"搜索完成: 找到 {SearchResults.Count} 个文件");
            }
            catch (Exception ex)
            {
                LoadStatus = $"搜索失败: {ex.Message}";
                Log($"搜索失败: {ex.Message}");
                MessageBox.Show($"搜索失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>
        /// 在文件系统中递归搜索
        /// </summary>
        private void SearchInFileSystem(IFileSystem fs, string path, System.Text.RegularExpressions.Regex pattern, string partitionName, List<FFUBrowserNode> results, int maxResults)
        {
            if (results.Count >= maxResults) return;

            try
            {
                // 搜索当前目录的文件
                var files = fs.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    if (results.Count >= maxResults) return;

                    string normalized = file.Replace('/', '\\');
                    string fileName = Path.GetFileName(normalized);

                    if (pattern.IsMatch(fileName))
                    {
                        long fileSize = 0;
                        try
                        {
                            var fi = fs.GetFileInfo(normalized);
                            fileSize = fi.Length;
                        }
                        catch { }

                        results.Add(new FFUBrowserNode
                        {
                            Name = fileName,
                            FullPath = normalized,
                            NodeType = FFUNodeType.File,
                            Size = fileSize,
                            FileSystem = fs,
                            PartitionName = partitionName,
                            Icon = SystemIconHelper.GetFileIcon(Path.GetExtension(fileName))
                        });
                    }
                }

                // 递归搜索子目录
                var dirs = fs.GetDirectories(path, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    if (results.Count >= maxResults) return;
                    string normalizedDir = dir.Replace('/', '\\').TrimEnd('\\');
                    SearchInFileSystem(fs, normalizedDir, pattern, partitionName, results, maxResults);
                }
            }
            catch (Exception ex)
            {
                Log($"搜索目录 {path} 时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 将通配符转换为正则表达式
        /// </summary>
        private static System.Text.RegularExpressions.Regex ConvertWildcardToRegex(string wildcard)
        {
            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(wildcard)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 清除搜索结果
        /// </summary>
        private void ClearSearch()
        {
            SearchKeyword = string.Empty;
            SearchResults.Clear();
            ShowSearchResults = false;
            LoadStatus = "搜索已清除";
        }

        /// <summary>
        /// 加载文件预览（文本或图片）
        /// </summary>
        public async void LoadPreview(FFUBrowserNode? node)
        {
            // 清除之前的预览
            PreviewText = string.Empty;
            PreviewImage = null;
            ShowPreview = false;

            if (node == null || node.NodeType != FFUNodeType.File || node.FileSystem == null)
                return;

            string ext = Path.GetExtension(node.Name);
            bool isText = TextExtensions.Contains(ext);
            bool isImage = ImageExtensions.Contains(ext);

            if (!isText && !isImage)
                return;

            // 限制文件大小
            const long maxTextSize = 2 * 1024 * 1024; // 2MB
            const long maxImageSize = 20 * 1024 * 1024; // 20MB

            if (isText && node.Size > maxTextSize)
            {
                PreviewTitle = $"{node.Name} - 文件过大（{node.SizeDisplay}），超过文本预览限制（2MB）";
                PreviewText = "文件过大，无法预览。\n\n请使用提取功能保存到本地后查看。";
                PreviewIsText = true;
                PreviewIsImage = false;
                ShowPreview = true;
                return;
            }

            if (isImage && node.Size > maxImageSize)
            {
                PreviewTitle = $"{node.Name} - 文件过大（{node.SizeDisplay}），超过图片预览限制（20MB）";
                PreviewText = "图片文件过大，无法预览。\n\n请使用提取功能保存到本地后查看。";
                PreviewIsText = true;
                PreviewIsImage = false;
                ShowPreview = true;
                return;
            }

            PreviewTitle = $"{node.Name} ({node.SizeDisplay})";
            LoadStatus = $"正在预览: {node.Name}...";

            try
            {
                if (isText)
                {
                    string text = await Task.Run(() =>
                    {
                        try
                        {
                            using var stream = node.FileSystem.OpenFile(node.FullPath, FileMode.Open, FileAccess.Read);
                            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                            return reader.ReadToEnd();
                        }
                        catch
                        {
                            // 尝试用默认编码读取
                            try
                            {
                                using var stream = node.FileSystem.OpenFile(node.FullPath, FileMode.Open, FileAccess.Read);
                                using var reader = new StreamReader(stream, System.Text.Encoding.Default, true);
                                return reader.ReadToEnd();
                            }
                            catch (Exception ex2)
                            {
                                return $"读取失败: {ex2.Message}";
                            }
                        }
                    });

                    PreviewText = text;
                    PreviewIsText = true;
                    PreviewIsImage = false;
                    ShowPreview = true;
                    LoadStatus = $"预览完成: {node.Name}";
                }
                else if (isImage)
                {
                    var image = await Task.Run(() =>
                    {
                        try
                        {
                            using var stream = node.FileSystem.OpenFile(node.FullPath, FileMode.Open, FileAccess.Read);
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            return bitmap;
                        }
                        catch (Exception ex)
                        {
                            Log($"图片预览失败: {ex.Message}");
                            return null;
                        }
                    });

                    if (image != null)
                    {
                        PreviewImage = image;
                        PreviewIsText = false;
                        PreviewIsImage = true;
                        ShowPreview = true;
                        LoadStatus = $"预览完成: {node.Name} ({image.PixelWidth}x{image.PixelHeight})";
                    }
                    else
                    {
                        PreviewText = "图片加载失败，可能是不支持的格式或文件已损坏。";
                        PreviewIsText = true;
                        PreviewIsImage = false;
                        ShowPreview = true;
                        LoadStatus = $"图片预览失败: {node.Name}";
                    }
                }
            }
            catch (Exception ex)
            {
                PreviewText = $"预览失败: {ex.Message}";
                PreviewIsText = true;
                PreviewIsImage = false;
                ShowPreview = true;
                LoadStatus = $"预览失败: {node.Name}";
                Log($"预览失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭预览
        /// </summary>
        public void ClosePreview()
        {
            ShowPreview = false;
            PreviewText = string.Empty;
            PreviewImage = null;
        }

        /// <summary>
        /// 更新磁盘布局可视化
        /// </summary>
        private void UpdateDiskLayout(FFUBrowserNode node)
        {
            DiskLayoutItems.Clear();

            if (node.NodeType != FFUNodeType.Disk || node.Children == null || node.Children.Count == 0)
            {
                ShowDiskLayout = false;
                return;
            }

            long totalSize = node.Children.Sum(c => c.Size);
            if (totalSize == 0)
            {
                ShowDiskLayout = false;
                return;
            }

            int colorIndex = 0;
            foreach (var child in node.Children)
            {
                var info = Services.FirmwareService.GetPartitionTypeInfo(child.PartitionTypeGuid);
                double percentage = Math.Round((double)child.Size / totalSize * 100, 2);

                DiskLayoutItems.Add(new DiskLayoutItem
                {
                    Name = child.Name ?? "未命名",
                    Size = child.Size,
                    SizeDisplay = child.SizeDisplay,
                    Percentage = percentage,
                    BarWidth = Math.Max(2, percentage * 5), // 总宽度约500，最小2像素
                    Color = PartitionColors[colorIndex % PartitionColors.Length],
                    HasFileSystem = child.FileSystem != null,
                    IsFirmware = info.IsFirmware
                });

                colorIndex++;
            }

            ShowDiskLayout = true;
            Log($"磁盘布局可视化更新: {node.Name}, {DiskLayoutItems.Count} 个分区, 总大小 {totalSize:N0} 字节");
        }

        /// <summary>
        /// 生成镜像信息报告
        /// </summary>
        private void GenerateReport()
        {
            if (_loadedDisks == null || !_loadedDisks.Any())
            {
                MessageBox.Show("请先加载镜像文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "生成镜像信息报告",
                FileName = $"FFU_Report_{DateTime.Now:yyyyMMdd_HHmmss}.html",
                Filter = "HTML报告|*.html|文本报告|*.txt"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadStatus = "正在生成镜像信息报告...";
                string ext = Path.GetExtension(dialog.FileName).ToLower();
                string content = ext == ".txt" ? GenerateTextReport() : GenerateHtmlReport();

                File.WriteAllText(dialog.FileName, content, System.Text.Encoding.UTF8);
                LoadStatus = $"报告已生成: {dialog.FileName}";
                Log($"镜像信息报告已生成: {dialog.FileName}");

                MessageBox.Show($"报告已生成!\n\n保存到: {dialog.FileName}",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadStatus = $"报告生成失败: {ex.Message}";
                Log($"报告生成失败: {ex.Message}");
                MessageBox.Show($"报告生成失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 生成HTML格式报告
        /// </summary>
        private string GenerateHtmlReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='zh-CN'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<title>FFU镜像信息报告</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 20px; background: #f5f5f5; }");
            sb.AppendLine("h1 { color: #2c5aa0; border-bottom: 2px solid #2c5aa0; padding-bottom: 8px; }");
            sb.AppendLine("h2 { color: #2c5aa0; margin-top: 24px; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 12px 0; background: white; }");
            sb.AppendLine("th, td { border: 1px solid #ddd; padding: 8px 12px; text-align: left; font-size: 13px; }");
            sb.AppendLine("th { background: #2c5aa0; color: white; }");
            sb.AppendLine("tr:nth-child(even) { background: #f9f9f9; }");
            sb.AppendLine(".info-box { background: #e8f4fd; border-left: 4px solid #2c5aa0; padding: 12px; margin: 12px 0; }");
            sb.AppendLine(".firmware { background: #fff3cd; }");
            sb.AppendLine(".nofs { color: #999; }");
            sb.AppendLine(".footer { margin-top: 30px; color: #888; font-size: 12px; text-align: center; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<h1>📦 FFU镜像信息报告</h1>");

            // 基本信息
            sb.AppendLine("<div class='info-box'>");
            sb.AppendLine($"<p><strong>镜像文件:</strong> {ImagePath}</p>");
            sb.AppendLine($"<p><strong>文件大小:</strong> {new FileInfo(ImagePath).Length:N0} 字节</p>");
            sb.AppendLine($"<p><strong>生成时间:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
            if (_ffuHeader != null)
            {
                sb.AppendLine($"<p><strong>FFU签名:</strong> {_ffuHeader.Signature}</p>");
                sb.AppendLine($"<p><strong>存储数量:</strong> {_ffuHeader.StoreCount}</p>");
                sb.AppendLine($"<p><strong>块大小:</strong> {_ffuHeader.ChunkSize} KB</p>");
            }
            sb.AppendLine("</div>");

            // 统计
            int totalDisks = _loadedDisks!.Count();
            int totalPartitions = _loadedDisks.Sum(d => d.Partitions?.Count() ?? 0);
            int firmwarePartitions = 0;
            int fsPartitions = 0;
            long totalSize = 0;

            foreach (var disk in _loadedDisks)
            {
                if (disk.Partitions == null) continue;
                foreach (var p in disk.Partitions)
                {
                    totalSize += p.Size;
                    var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                    if (info.IsFirmware) firmwarePartitions++;
                    if (p.FileSystem != null) fsPartitions++;
                }
            }

            sb.AppendLine("<h2>📊 统计概览</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>项目</th><th>数量</th></tr>");
            sb.AppendLine($"<tr><td>磁盘数量</td><td>{totalDisks}</td></tr>");
            sb.AppendLine($"<tr><td>分区总数</td><td>{totalPartitions}</td></tr>");
            sb.AppendLine($"<tr><td>有文件系统的分区</td><td>{fsPartitions}</td></tr>");
            sb.AppendLine($"<tr><td>固件分区</td><td>{firmwarePartitions}</td></tr>");
            sb.AppendLine($"<tr><td>总大小</td><td>{totalSize:N0} 字节 ({Math.Round(totalSize / 1024.0 / 1024.0 / 1024.0, 2)} GB)</td></tr>");
            sb.AppendLine("</table>");

            // 每个磁盘的分区列表
            int diskIndex = 0;
            foreach (var disk in _loadedDisks)
            {
                sb.AppendLine($"<h2>💾 磁盘 {diskIndex}</h2>");
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th>索引</th><th>分区名</th><th>类型</th><th>大小</th><th>文件系统</th><th>固件</th></tr>");

                if (disk.Partitions != null)
                {
                    int partIndex = 0;
                    foreach (var p in disk.Partitions)
                    {
                        var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                        string rowClass = info.IsFirmware ? "firmware" : "";
                        string fsStatus = p.FileSystem != null ? "✓ 是" : "<span class='nofs'>✗ 否</span>";
                        string fwStatus = info.IsFirmware ? "✓ 是" : "否";

                        sb.AppendLine($"<tr class='{rowClass}'>");
                        sb.AppendLine($"<td>{partIndex}</td>");
                        sb.AppendLine($"<td><strong>{p.Name}</strong></td>");
                        sb.AppendLine($"<td>{info.Name}<br><small>{p.Type:B}</small></td>");
                        sb.AppendLine($"<td>{p.Size:N0} 字节</td>");
                        sb.AppendLine($"<td>{fsStatus}</td>");
                        sb.AppendLine($"<td>{fwStatus}</td>");
                        sb.AppendLine("</tr>");
                        partIndex++;
                    }
                }

                sb.AppendLine("</table>");
                diskIndex++;
            }

            sb.AppendLine("<div class='footer'>");
            sb.AppendLine("由 OneCore Image Designer 生成 | FFU Browser Report");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        /// <summary>
        /// 生成文本格式报告
        /// </summary>
        private string GenerateTextReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== FFU镜像信息报告 ===");
            sb.AppendLine($"镜像文件: {ImagePath}");
            sb.AppendLine($"文件大小: {new FileInfo(ImagePath).Length:N0} 字节");
            sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (_ffuHeader != null)
            {
                sb.AppendLine("--- FFU头信息 ---");
                sb.AppendLine($"签名: {_ffuHeader.Signature}");
                sb.AppendLine($"存储数量: {_ffuHeader.StoreCount}");
                sb.AppendLine($"块大小: {_ffuHeader.ChunkSize} KB");
                sb.AppendLine();
            }

            int totalPartitions = _loadedDisks!.Sum(d => d.Partitions?.Count() ?? 0);
            int firmwarePartitions = 0;
            int fsPartitions = 0;
            long totalSize = 0;

            foreach (var disk in _loadedDisks)
            {
                if (disk.Partitions == null) continue;
                foreach (var p in disk.Partitions)
                {
                    totalSize += p.Size;
                    var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                    if (info.IsFirmware) firmwarePartitions++;
                    if (p.FileSystem != null) fsPartitions++;
                }
            }

            sb.AppendLine("--- 统计概览 ---");
            sb.AppendLine($"磁盘数量: {_loadedDisks.Count()}");
            sb.AppendLine($"分区总数: {totalPartitions}");
            sb.AppendLine($"有文件系统: {fsPartitions}");
            sb.AppendLine($"固件分区: {firmwarePartitions}");
            sb.AppendLine($"总大小: {totalSize:N0} 字节");
            sb.AppendLine();

            int diskIndex = 0;
            foreach (var disk in _loadedDisks)
            {
                sb.AppendLine($"--- 磁盘 {diskIndex} ---");
                if (disk.Partitions != null)
                {
                    int partIndex = 0;
                    foreach (var p in disk.Partitions)
                    {
                        var info = Services.FirmwareService.GetPartitionTypeInfo(p.Type);
                        string fw = info.IsFirmware ? " [固件]" : "";
                        string fs = p.FileSystem != null ? "有FS" : "无FS";

                        sb.AppendLine($"  [{partIndex}] {p.Name}{fw}");
                        sb.AppendLine($"       类型: {info.Name} ({p.Type:B})");
                        sb.AppendLine($"       大小: {p.Size:N0} 字节 | {fs}");
                        partIndex++;
                    }
                }
                sb.AppendLine();
                diskIndex++;
            }

            sb.AppendLine("=== 报告结束 ===");
            return sb.ToString();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
