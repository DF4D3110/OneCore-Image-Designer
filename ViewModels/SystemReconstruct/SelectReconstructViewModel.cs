using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;
using Microsoft.Win32;
using MobilePackageGen;
using MobilePackageGen.Adapters;
using MobilePackageGen.UpdateHistory;
using OneCore_Image_Designer.Helpers;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Models.MobilePackageGen;
using XmlMum = MobilePackageGen.XmlMum;

namespace OneCore_Image_Designer.ViewModels.SystemReconstruct
{
    /// <summary>
    /// 选择构建视图模型 - 浏览镜像中所有可提取的包，按需提取
    /// </summary>
    public class SelectReconstructViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _mainVM;
        private System.Collections.Generic.IEnumerable<IDisk>? _loadedDisks;
        private UpdateHistory? _updateHistory;

        public SelectReconstructViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            GoBackCommand = new RelayCommand(GoBack);
            BrowseImageCommand = new RelayCommand(BrowseImage);
            BrowseOutputCommand = new RelayCommand(BrowseOutput);
            LoadImageCommand = new RelayCommand(LoadImage);
            SelectAllCommand = new RelayCommand(SelectAll);
            DeselectAllCommand = new RelayCommand(DeselectAll);
            StartBuildCommand = new RelayCommand(StartBuild);
            SaveOEMInputCommand = new RelayCommand(SaveOEMInput);
            ExpandAllCommand = new RelayCommand(() => FileTreeBuilder.SetAllExpanded(PackageFileTree, true));
            CollapseAllCommand = new RelayCommand(() => FileTreeBuilder.SetAllExpanded(PackageFileTree, false));
            ExtractComponentsCommand = new RelayCommand(ExtractComponents);
            ExtractDriversFromFolderCommand = new RelayCommand(ExtractDriversFromFolder);
            ScanHiveCommand = new RelayCommand(ScanHive);
            PreviewItemCommand = new RelayCommand<object>(PreviewItem);
            PreviewTreeNodeCommand = new RelayCommand<Models.FileTreeNode>(PreviewTreeNode);
        }

        // ═════════ 双击预览（XML/文本） ═════════

        /// <summary>列表条目双击：FM 文件从镜像读；OEMInput 用已缓存内容。</summary>
        public RelayCommand<object> PreviewItemCommand { get; }

        /// <summary>文件树节点双击：解析「磁盘N\分区\路径」从镜像读出。</summary>
        public RelayCommand<Models.FileTreeNode> PreviewTreeNodeCommand { get; }

        private void PreviewItem(object? item)
        {
            if (item is FMFileInfo fm)
            {
                if (_loadedDisks == null) { MessageBox.Show("镜像未加载，无法读取 FM 内容。", "预览"); return; }
                var (dIdx, pIdx, fsPath) = ResolvePartition(fm.PartitionName);
                if (dIdx < 0) { MessageBox.Show($"找不到分区 {fm.PartitionName}", "预览"); return; }
                var res = Services.ImageFileReader.Read(_loadedDisks.ElementAt(dIdx), pIdx, fm.SourcePath);
                ShowPreview(fm.Name, res);
            }
            else if (item is PackageInfo pkg && pkg.IsOEMInput)
            {
                ShowPreview("OEMInput.xml", new Services.ImageFileReader.ReadResult
                { Ok = true, Text = OEMInputContent, IsXml = true, Note = "来源分区: " + OEMInputPartition });
            }
        }

        private void PreviewTreeNode(Models.FileTreeNode? node)
        {
            if (node == null || node.IsFolder) return;
            if (_loadedDisks == null) { MessageBox.Show("镜像未加载。", "预览"); return; }
            if (!Services.ImageFileReader.TryParseTreePath(node.FullPath, _loadedDisks,
                    out int dIdx, out int pIdx, out string fsPath))
            { MessageBox.Show("无法解析节点路径: " + node.FullPath, "预览"); return; }
            var res = Services.ImageFileReader.Read(_loadedDisks.ElementAt(dIdx), pIdx, fsPath);
            ShowPreview(node.FullPath, res);
        }

        private (int, int, string) ResolvePartition(string partitionName)
        {
            if (_loadedDisks == null) return (-1, -1, "");
            int di = 0;
            foreach (var disk in _loadedDisks)
            {
                int pi = 0;
                foreach (var part in disk.Partitions)
                {
                    if (string.Equals(part.Name, partitionName, StringComparison.OrdinalIgnoreCase))
                        return (di, pi, "");
                    pi++;
                }
                di++;
            }
            return (-1, -1, "");
        }

        private void ShowPreview(string title, Services.ImageFileReader.ReadResult res)
        {
            if (!res.Ok) { MessageBox.Show(res.Note, "预览"); return; }
            var win = new Views.XmlPreviewWindow(title, res.Text, res.Note, res.IsXml)
            { Owner = Application.Current?.MainWindow };
            win.Show();
        }

        // === 镜像文件路径 ===
        private string _imagePath = string.Empty;
        public string ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsImageValid)); }
        }

        public bool IsImageValid => File.Exists(ImagePath);

        // === 加载状态 ===
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotLoading)); }
        }

        public bool IsNotLoading => !IsLoading;

        private string _loadStatus = "未加载";
        public string LoadStatus
        {
            get => _loadStatus;
            set { _loadStatus = value; OnPropertyChanged(); }
        }

        // === 包列表 ===
        public ObservableCollection<PackageInfo> AllPackages { get; } = new();
        public ObservableCollection<FMFileInfo> AllFMFiles { get; } = new();

        // === OEMInput ===
        private string _oemInputContent = string.Empty;
        public string OEMInputContent
        {
            get => _oemInputContent;
            set { _oemInputContent = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOEMInput)); }
        }

        public bool HasOEMInput => !string.IsNullOrEmpty(OEMInputContent);

        private string _oemInputPartition = string.Empty;
        public string OEMInputPartition
        {
            get => _oemInputPartition;
            set { _oemInputPartition = value; OnPropertyChanged(); }
        }

        // === 过滤 ===
        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set { _filterText = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredPackages)); }
        }

        private string _selectedFilter = "全部";
        public string SelectedFilter
        {
            get => _selectedFilter;
            set { _selectedFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredPackages)); }
        }

        public ObservableCollection<string> FilterTypes { get; } = new() { "全部", "CBS", "SPKG", "Driver", "第三方驱动", "FM" };

        // === 驱动扫描通道开关（WCOS 镜像可能没有 *.inf 可扫）===
        private bool _scanManifestChannel = true;
        public bool ScanManifestChannel
        {
            get => _scanManifestChannel;
            set { _scanManifestChannel = value; OnPropertyChanged(); }
        }

        private bool _scanOfflineHiveChannel = true;
        public bool ScanOfflineHiveChannel
        {
            get => _scanOfflineHiveChannel;
            set { _scanOfflineHiveChannel = value; OnPropertyChanged(); }
        }

        public ObservableCollection<object> FilteredPackages
        {
            get
            {
                var result = new ObservableCollection<object>();
                var filter = FilterText.ToLowerInvariant();

                if (SelectedFilter == "全部" || SelectedFilter == "CBS")
                {
                    foreach (var p in AllPackages.Where(p => p.Type == PackageType.CBS && !p.IsOEMInput))
                    {
                        if (string.IsNullOrEmpty(filter) || p.Name.ToLowerInvariant().Contains(filter))
                            result.Add(p);
                    }
                }
                if (SelectedFilter == "全部" || SelectedFilter == "SPKG")
                {
                    foreach (var p in AllPackages.Where(p => p.Type == PackageType.SPKG))
                    {
                        if (string.IsNullOrEmpty(filter) || p.Name.ToLowerInvariant().Contains(filter))
                            result.Add(p);
                    }
                }
                if (SelectedFilter == "全部" || SelectedFilter == "Driver")
                {
                    // 组内连续性：官方驱动在前、第三方在后，分组视图各自成段
                    foreach (var p in AllPackages.Where(p => p.Type == PackageType.Driver)
                                 .OrderBy(p => p.IsThirdPartyDriver))
                    {
                        if (string.IsNullOrEmpty(filter) || p.Name.ToLowerInvariant().Contains(filter))
                            result.Add(p);
                    }
                }
                if (SelectedFilter == "第三方驱动")
                {
                    foreach (var p in AllPackages.Where(p => p.Type == PackageType.Driver && p.IsThirdPartyDriver))
                    {
                        if (string.IsNullOrEmpty(filter) || p.Name.ToLowerInvariant().Contains(filter))
                            result.Add(p);
                    }
                }
                if (SelectedFilter == "全部")
                {
                    foreach (var p in AllPackages.Where(p => p.IsOEMInput))
                    {
                        if (string.IsNullOrEmpty(filter) || p.Name.ToLowerInvariant().Contains(filter))
                            result.Add(p);
                    }
                }
                if (SelectedFilter == "全部" || SelectedFilter == "FM")
                {
                    foreach (var f in AllFMFiles.OrderBy(f => f.Vendor))
                    {
                        if (string.IsNullOrEmpty(filter) || f.Name.ToLowerInvariant().Contains(filter))
                            result.Add(f);
                    }
                }
                return result;
            }
        }

        // === 选中的包 ===
        public System.Collections.Generic.List<object> SelectedPackages =>
            AllPackages.Where(p => p.Selected).Cast<object>()
            .Concat(AllFMFiles.Where(f => f.Selected).Cast<object>())
            .ToList();

        // === 操作日志（输出到文件） ===
        private string _operationLogFile = string.Empty;
        private string _buildLogFile = string.Empty;

        private void EnsureLogFiles()
        {
            if (string.IsNullOrEmpty(_operationLogFile))
            {
                var logDir = Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _operationLogFile = Path.Combine(logDir, $"operation_{timestamp}.log");
                _buildLogFile = Path.Combine(logDir, $"build_{timestamp}.log");
            }
        }

        private void Log(string message)
        {
            EnsureLogFiles();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMsg = $"[{timestamp}] {message}{Environment.NewLine}";
            try
            {
                File.AppendAllText(_operationLogFile, logMsg);
            }
            catch { }
        }

        private void LogBuild(string message)
        {
            EnsureLogFiles();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMsg = $"[{timestamp}] {message}{Environment.NewLine}";
            try
            {
                File.AppendAllText(_buildLogFile, logMsg);
            }
            catch { }
        }

        // === 当前选中包的详情 ===
        private object? _selectedItem;
        public object? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _selectedItem = value;
                OnPropertyChanged();

                // 如果镜像未加载但已选择镜像文件，自动加载
                if (value != null && _loadedDisks == null && IsImageValid && !IsLoading)
                {
                    Log("选中包，自动加载镜像...");
                    Task.Run(() => LoadImage());
                }
                else
                {
                    LoadPackageDetails(value);
                }
            }
        }

        private string _selectedPackageName = string.Empty;
        public string SelectedPackageName
        {
            get => _selectedPackageName;
            set { _selectedPackageName = value; OnPropertyChanged(); }
        }

        private string _selectedPackageInfo = string.Empty;
        public string SelectedPackageInfo
        {
            get => _selectedPackageInfo;
            set { _selectedPackageInfo = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> PackageFiles { get; } = new();
        public ObservableCollection<FileTreeNode> PackageFileTree { get; } = new();

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

        // === 输出文件夹 ===
        private string _outputPath = string.Empty;
        public string OutputPath
        {
            get => _outputPath;
            set { _outputPath = value; OnPropertyChanged(); }
        }

        // === 组件提取选项 ===
        private bool _extractGroupByComponent = false;
        public bool ExtractGroupByComponent
        {
            get => _extractGroupByComponent;
            set { _extractGroupByComponent = value; OnPropertyChanged(); }
        }

        private bool _extractIncludeRegistry = true;
        public bool ExtractIncludeRegistry
        {
            get => _extractIncludeRegistry;
            set { _extractIncludeRegistry = value; OnPropertyChanged(); }
        }

        private bool _isExtracting;
        public bool IsExtracting
        {
            get => _isExtracting;
            set { _isExtracting = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotExtracting)); }
        }

        public bool IsNotExtracting => !IsExtracting;

        private string _extractStatus = "就绪";
        public string ExtractStatus
        {
            get => _extractStatus;
            set { _extractStatus = value; OnPropertyChanged(); }
        }

        // === 命令 ===
        public RelayCommand GoBackCommand { get; }
        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand BrowseOutputCommand { get; }
        public RelayCommand LoadImageCommand { get; }
        public RelayCommand SelectAllCommand { get; }
        public RelayCommand DeselectAllCommand { get; }
        public RelayCommand StartBuildCommand { get; }
        public RelayCommand SaveOEMInputCommand { get; }
        public RelayCommand ExpandAllCommand { get; }
        public RelayCommand CollapseAllCommand { get; }
        public RelayCommand ExtractComponentsCommand { get; }
        public RelayCommand ExtractDriversFromFolderCommand { get; }
        public RelayCommand ScanHiveCommand { get; }

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

        public void LoadImage()
        {
            if (!IsImageValid)
            {
                MessageBox.Show("请选择有效的镜像文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsLoading = true;
            LoadStatus = "加载中...";
            AllPackages.Clear();
            AllFMFiles.Clear();
            PackageFiles.Clear();
            PackageFileTree.Clear();
            Log($"开始加载镜像: {ImagePath}");

            try
            {
                LoadStatus = "加载镜像中...";
                Log("正在解析镜像格式...");
                _loadedDisks = DiskLoader.LoadDisks([ImagePath]);
                if (_loadedDisks == null || !_loadedDisks.Any())
                {
                    LoadStatus = "加载失败: 无磁盘";
                    Log("错误: 未找到任何磁盘");
                    IsLoading = false;
                    return;
                }
                Log($"找到 {_loadedDisks.Count()} 个磁盘");

                LoadStatus = "获取更新历史...";
                Log("正在获取更新历史...");
                _updateHistory = BuildMetadataHandler.GetUpdateHistory(_loadedDisks);

                LoadStatus = "枚举包...";
                Log("正在枚举CBS/SPKG/Driver包...");
                var tempOutput = Path.Combine(Path.GetTempPath(), "MobilePackageGen_Temp");
                var packages = PackageEnumerator.EnumerateAll(_loadedDisks, tempOutput).ToList();
                Log($"枚举到 {packages.Count} 个包");

                // 注册表通道：包 manifest 的 registry 节点识别驱动包（WCOS 无 inf 可扫时补位）
                if (ScanManifestChannel)
                {
                    Log("正在用包内注册表节点补扫驱动包...");
                    var existing = new HashSet<string>(packages.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                    int added = 0;
                    foreach (var p in Services.RegistryDriverScanner.ScanManifestRegistry(_loadedDisks, tempOutput, Log))
                    {
                        if (existing.Add(p.Name)) { packages.Add(p); added++; }
                    }
                    Log($"注册表补扫新增 {added} 个驱动包");
                }

                foreach (var p in packages)
                {
                    p.Selected = false;
                    AllPackages.Add(p);
                }

                // 设置每个包的磁盘索引和存储池信息
                AssignDiskInfoToPackages();

                // 枚举FM文件
                LoadStatus = "枚举FM文件...";
                Log("正在枚举FeatureManifest文件...");
                var fmDirs = new[] { "Microsoft", "OEM" };
                var basePath = @"Windows\ImageUpdate\FeatureManifest";
                var fmList = new System.Collections.Generic.List<FMFileInfo>();

                foreach (var disk in _loadedDisks)
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
                                    var fm = new FMFileInfo
                                    {
                                        Name = name,
                                        SourcePath = file,
                                        Vendor = vendor,
                                        PartitionName = partition.Name ?? "Unknown",
                                        DestinationPath = Path.Combine("FMFiles", vendor, name)
                                    };
                                    try { fm.Size = fs.GetFileInfo(file).Length; } catch { }
                                    fmList.Add(fm);
                                }
                            }
                            catch { }
                        }
                    }
                }

                foreach (var f in fmList)
                {
                    f.Selected = false;
                    AllFMFiles.Add(f);
                }
                Log($"枚举到 {fmList.Count} 个FM文件");

                // 提取OEMInput.xml
                LoadStatus = "提取OEMInput.xml...";
                Log("正在查找OEMInput.xml...");
                OEMInputContent = string.Empty;
                OEMInputPartition = string.Empty;
                var oemInputPath = @"Windows\ImageUpdate\OEMInput.xml";
                foreach (var disk in _loadedDisks)
                {
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;
                        try
                        {
                            if (fs.FileExists(oemInputPath))
                            {
                                using var stream = fs.OpenFile(oemInputPath, FileMode.Open, FileAccess.Read);
                                using var reader = new StreamReader(stream);
                                OEMInputContent = reader.ReadToEnd();
                                OEMInputPartition = partition.Name ?? "Unknown";
                                LoadStatus = $"找到OEMInput.xml (分区: {OEMInputPartition})";
                                Log($"找到OEMInput.xml，来源分区: {OEMInputPartition}");
                                break;
                            }
                        }
                        catch { }
                    }
                    if (HasOEMInput) break;
                }

                LoadStatus = $"加载完成: {AllPackages.Count} 个包, {AllFMFiles.Count} 个FM文件{(HasOEMInput ? ", OEMInput已提取" : "")}";
                Log($"镜像加载完成: {AllPackages.Count} 个包, {AllFMFiles.Count} 个FM文件");

                // 如果找到OEMInput，添加到包列表
                if (HasOEMInput)
                {
                    var oemInputItem = new PackageInfo
                    {
                        Type = PackageType.CBS,
                        Name = "OEMInput.xml",
                        PartitionName = OEMInputPartition,
                        IsOEMInput = true,
                        Selected = false
                    };
                    AllPackages.Insert(0, oemInputItem);
                    Log("已将OEMInput.xml添加到包列表");
                }

                OnPropertyChanged(nameof(FilteredPackages));

                // 自动加载选中包的详情
                if (_selectedItem != null)
                {
                    Log("自动加载选中包详情...");
                    LoadPackageDetails(_selectedItem);
                }
            }
            catch (Exception ex)
            {
                Log($"加载失败: {ex.Message}");
                var detail = ex.Message;
                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    detail += $"\n\n[Inner {depth + 1}] {inner.GetType().Name}: {inner.Message}";
                    inner = inner.InnerException;
                    depth++;
                }
                detail += $"\n\nStackTrace:\n{ex.StackTrace}";

                LoadStatus = $"加载失败: {ex.Message}";
                MessageBox.Show($"加载镜像失败:\n{detail}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 为每个包分配磁盘索引和存储池信息
        /// </summary>
        private void AssignDiskInfoToPackages()
        {
            if (_loadedDisks == null) return;

            var storagePoolType = new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");

            foreach (var pkg in AllPackages)
            {
                if (string.IsNullOrEmpty(pkg.SourceManifest)) continue;

                int diskIndex = 0;
                foreach (var disk in _loadedDisks)
                {
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;
                        try
                        {
                            if (fs.FileExists(pkg.SourceManifest))
                            {
                                pkg.DiskIndex = diskIndex;
                                pkg.IsFromStoragePool = partition.Type == storagePoolType;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (pkg.DiskIndex >= 0) break;
                    diskIndex++;
                }
            }

            Log($"已为 {AllPackages.Count(p => p.DiskIndex >= 0)} 个包分配磁盘信息");
        }

        private void LoadPackageDetails(object? item)
        {
            PackageFiles.Clear();
            PackageFileTree.Clear();
            SelectedPackageName = string.Empty;
            SelectedPackageInfo = string.Empty;

            if (item == null) return;

            // OEMInput特殊项
            if (item is PackageInfo pkgOEM && pkgOEM.IsOEMInput)
            {
                SelectedPackageName = "OEMInput.xml";
                SelectedPackageInfo = $"类型: 系统配置\n" +
                    $"来源分区: {OEMInputPartition}\n" +
                    $"大小: {OEMInputContent.Length} 字符";
                // OEMInput没有文件列表，清空
                goto BuildTree;
            }

            if (item is PackageInfo pkg)
            {
                SelectedPackageName = pkg.Name;
                SelectedPackageInfo = $"类型: {pkg.TypeDisplay}\n" +
                    $"位置: {pkg.FullLocationDisplay}\n" +
                    $"架构: {pkg.Architecture}\n" +
                    $"版本: {pkg.Version}\n" +
                    $"语言: {pkg.Language}\n" +
                    $"Manifest: {pkg.SourceManifest}";

                // 解析manifest获取文件列表
                if (_loadedDisks != null && !string.IsNullOrEmpty(pkg.SourceManifest))
                {
                    try
                    {
                        int diskIndex = 0;
                        foreach (var disk in _loadedDisks)
                        {
                            int partitionIndex = 0;
                            foreach (var partition in disk.Partitions)
                            {
                                var fs = partition.FileSystem;
                                if (fs == null) { partitionIndex++; continue; }
                                try
                                {
                                    if (fs.FileExists(pkg.SourceManifest))
                                    {
                                        // 判断是否为存储池分区
                                        bool isStoragePool = partition.Type == new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
                                        string poolTag = isStoragePool ? "[存储池]" : "";

                                        // 构建包含磁盘和分区信息的路径前缀
                                        string pathPrefix = $"磁盘{diskIndex}{poolTag}\\{partition.Name}";

                                        // 根据包类型处理文件列表
                                        string ext = Path.GetExtension(pkg.SourceManifest).ToLowerInvariant();
                                        if (ext == ".inf")
                                        {
                                            // 驱动包：列出驱动包所在文件夹中的所有文件
                                            string? driverFolder = Path.GetDirectoryName(pkg.SourceManifest);
                                            if (!string.IsNullOrEmpty(driverFolder) && fs.DirectoryExists(driverFolder))
                                            {
                                                try
                                                {
                                                    var driverFiles = fs.GetFiles(driverFolder, "*.*", SearchOption.AllDirectories);
                                                    foreach (var driverFile in driverFiles)
                                                    {
                                                        var fullPath = $"{pathPrefix}\\{driverFile}";
                                                        PackageFiles.Add(fullPath);
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        else
                                        {
                                            // CBS/SPKG包：解析manifest获取文件列表
                                            using var stream = fs.OpenFile(pkg.SourceManifest, FileMode.Open, FileAccess.Read);
                                            var serializer = new XmlSerializer(typeof(XmlMum.Assembly));
                                            var cbs = (XmlMum.Assembly)serializer.Deserialize(stream)!;
                                            if (cbs?.Package?.CustomInformation?.File != null)
                                            {
                                                foreach (var file in cbs.Package.CustomInformation.File)
                                                {
                                                    // 包含磁盘、分区信息的完整路径
                                                    var fullPath = $"{pathPrefix}\\{file.Name}";
                                                    PackageFiles.Add(fullPath);
                                                }
                                            }
                                        }
                                        goto BuildTree;
                                    }
                                }
                                catch { }
                                partitionIndex++;
                            }
                            diskIndex++;
                        }
                    }
                    catch { }
                }
            }
            else if (item is FMFileInfo fm)
            {
                SelectedPackageName = fm.Name;
                SelectedPackageInfo = $"类型: FM\n" +
                    $"供应商: {fm.Vendor}\n" +
                    $"分区: {fm.PartitionName}\n" +
                    $"大小: {FormatSize(fm.Size)}\n" +
                    $"源路径: {fm.SourcePath}";
                PackageFiles.Add(fm.SourcePath);
            }

            BuildTree:
            // 构建文件树
            if (PackageFiles.Count > 0)
            {
                var tree = FileTreeBuilder.BuildTree(PackageFiles);
                foreach (var node in tree)
                {
                    PackageFileTree.Add(node);
                }
            }
        }

        public void SelectAll()
        {
            foreach (var p in AllPackages) p.Selected = true;
            foreach (var f in AllFMFiles) f.Selected = true;
            OnPropertyChanged(nameof(FilteredPackages));
        }

        public void DeselectAll()
        {
            foreach (var p in AllPackages) p.Selected = false;
            foreach (var f in AllFMFiles) f.Selected = false;
            OnPropertyChanged(nameof(FilteredPackages));
        }

        public async void StartBuild()
        {
            var selected = AllPackages.Where(p => p.Selected).ToList();
            var selectedFM = AllFMFiles.Where(f => f.Selected).ToList();

            if (selected.Count == 0 && selectedFM.Count == 0)
            {
                MessageBox.Show("请至少选择一个包或FM文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(OutputPath))
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择输出文件夹",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (dialog.ShowDialog() == true)
                {
                    OutputPath = dialog.FolderName;
                }
                else
                {
                    return;
                }
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
                        if (_loadedDisks == null) return;

                        var cbsNames = new System.Collections.Generic.HashSet<string>(selected.Where(p => p.Type == PackageType.CBS).Select(p => Path.GetFileName(p.CabFileName)));
                        var spkgNames = new System.Collections.Generic.HashSet<string>(selected.Where(p => p.Type == PackageType.SPKG).Select(p => Path.GetFileName(p.CabFileName)));
                        var driverNames = new System.Collections.Generic.HashSet<string>(selected.Where(p => p.Type == PackageType.Driver).Select(p => Path.GetFileName(p.CabFileName)));

                        if (cbsNames.Count > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建CBS包..."; Progress = 20; });
                            CBSBuilder.BuildCBS(_loadedDisks, OutputPath, _updateHistory, cbsNames);
                        }

                        if (spkgNames.Count > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建SPKG包..."; Progress = 45; });
                            SPKGBuilder.BuildSPKG(_loadedDisks, OutputPath, _updateHistory, spkgNames);
                        }

                        if (driverNames.Count > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() => { BuildStatus = "构建Driver包..."; Progress = 70; });
                            DriverBuilder.BuildDrivers(_loadedDisks, OutputPath, _updateHistory, driverNames);
                        }

                        // 提取FM文件
                        if (selectedFM.Count > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() => { BuildStatus = "提取FM文件..."; Progress = 90; });
                            foreach (var fm in selectedFM)
                            {
                                foreach (var disk in _loadedDisks)
                                {
                                    foreach (var partition in disk.Partitions)
                                    {
                                        var fs = partition.FileSystem;
                                        if (fs == null) continue;
                                        try
                                        {
                                            if (fs.FileExists(fm.SourcePath))
                                            {
                                                var destPath = Path.Combine(OutputPath, fm.DestinationPath);
                                                var destDir = Path.GetDirectoryName(destPath);
                                                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                                    Directory.CreateDirectory(destDir);
                                                using var srcStream = fs.OpenFile(fm.SourcePath, FileMode.Open, FileAccess.Read);
                                                using var destStream = File.Create(destPath);
                                                srcStream.CopyTo(destStream);
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }

                        // 固件分区不再单独导出 bin：所选包/cab 提取已覆盖固件内容，独立 bin 无意义
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Progress = 100;
                            BuildStatus = "构建完成";
                            MessageBox.Show($"选择构建完成！\n\n提取了 {selected.Count} 个包、{selectedFM.Count} 个FM文件\n输出目录: {OutputPath}", "构建完成", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void GoBack()
        {
            _mainVM.NavigateToSystemReconstruct();
        }

        private static string FormatSize(long size)
        {
            return size switch
            {
                > 1073741824 => $"{size / 1073741824.0:F2} GB",
                > 1048576 => $"{size / 1048576.0:F2} MB",
                > 1024 => $"{size / 1024.0:F2} KB",
                _ => $"{size} B"
            };
        }

        public void SaveOEMInput()
        {
            if (!HasOEMInput)
            {
                MessageBox.Show("未找到OEMInput.xml", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存OEMInput.xml",
                Filter = "XML文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
                FileName = "OEMInput.xml",
                InitialDirectory = !string.IsNullOrEmpty(OutputPath) && Directory.Exists(OutputPath) ? OutputPath : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dialog.FileName, OEMInputContent);
                    MessageBox.Show($"已保存到: {dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 组件提取：按系统原有结构导出选中的组件
        /// </summary>
        private async void ExtractComponents()
        {
            var selected = AllPackages.Where(p => p.Selected).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("请至少选择一个组件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(OutputPath))
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择输出文件夹",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (dialog.ShowDialog() == true)
                {
                    OutputPath = dialog.FolderName;
                }
                else
                {
                    return;
                }
            }

            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            // 提取预览：先扫描，让用户确认
            var progress = Services.GlobalProgressService.Instance;

            // 任务日志窗口：后台线程写 sink，窗口 400ms 批量刷新（不再逐行 Invoke，防卡死）
            var sink = new Services.TaskLogSink("组件提取");
            Services.ComponentExtractor.LiveSink = m => sink.Write(m);
            var logWin = new Views.TaskLogWindow(sink) { Owner = Application.Current?.MainWindow };
            logWin.Show();

            progress.StartOperation($"正在扫描 {selected.Count} 个组件...", canCancel: false, isIndeterminate: true);
            sink.Write($"开始扫描 {selected.Count} 个组件（镜像: {ImagePath}）");

            Services.ComponentExtractor.ExtractPreviewResult? preview = null;
            try
            {
                preview = await Task.Run(() =>
                {
                    if (_loadedDisks == null) return null;
                    return Services.ComponentExtractor.PreviewExtract(
                        selected,
                        _loadedDisks,
                        ExtractIncludeRegistry,
                        ImagePath);
                });
            }
            finally
            {
                progress.Reset();
            }

            if (preview != null)
            {
                string sizeMB = Math.Round(preview.TotalFileSize / 1024.0 / 1024.0, 2).ToString("F2");
                string previewMsg = $"即将提取以下内容：\n\n" +
                                    $"组件数量: {preview.TotalComponents} 个\n" +
                                    $"文件数量: {preview.TotalFiles} 个\n" +
                                    $"文件总大小: {sizeMB} MB\n" +
                                    $"注册表项: {preview.TotalRegistryKeys} 个\n" +
                                    $"依赖数量: {preview.TotalDependencies} 个\n" +
                                    $"错误: {preview.Errors.Count} 个\n\n" +
                                    $"输出目录: {OutputPath}\n\n" +
                                    $"注意：输出目录将被强制清空覆盖！\n\n" +
                                    $"是否继续提取？";

                var result = MessageBox.Show(previewMsg, "提取预览确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    ExtractStatus = "已取消提取";
                    Services.ComponentExtractor.LiveSink = null;
                    sink.MarkFinished();
                    logWin.Close();
                    return;
                }
            }

            IsExtracting = true;
            ExtractStatus = "提取中...";
            sink.Write($"开始提取 → {OutputPath}（模式: {(ExtractGroupByComponent ? "按组件分组" : "按磁盘平铺")}，注册表: {(ExtractIncludeRegistry ? "含" : "不含")}）");

            // 启动全局进度面板
            progress.StartOperation($"正在提取 {selected.Count} 个组件...", canCancel: false, isIndeterminate: true);

            try
            {
                var result = await Task.Run(() =>
                {
                    if (_loadedDisks == null) return null;

                    var mode = ExtractGroupByComponent
                        ? Services.ComponentExtractor.ExtractMode.GroupByComponent
                        : Services.ComponentExtractor.ExtractMode.FlatByDisk;

                    return Services.ComponentExtractor.ExtractComponents(
                        selected,
                        _loadedDisks,
                        OutputPath,
                        mode,
                        ExtractIncludeRegistry,
                        ImagePath);
                });

                if (result != null)
                {
                    progress.CompleteOperation();
                    string msg = $"组件提取完成！\n\n" +
                                 $"提取组件: {result.TotalComponents} 个\n" +
                                 $"提取文件: {result.TotalFiles} 个\n" +
                                 $"注册表项: {result.TotalRegistryKeys} 个\n" +
                                 $"错误: {result.Errors.Count} 个\n\n" +
                                 $"--- 校验结果 ---\n" +
                                 $"校验通过: {result.VerifiedFiles} 个文件\n" +
                                 $"校验失败: {result.VerifyFailed} 个文件\n" +
                                 $"校验报告: {result.VerifyReportPath}\n\n" +
                                 $"输出目录: {OutputPath}";

                    if (result.Errors.Count > 0)
                    {
                        msg += "\n\n错误详情:\n" + string.Join("\n", result.Errors.Take(10));
                        if (result.Errors.Count > 10)
                            msg += $"\n... 还有 {result.Errors.Count - 10} 个错误";
                    }

                    MessageBox.Show(msg, "提取完成", MessageBoxButton.OK,
                        result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                    ExtractStatus = $"提取完成 - {result.TotalComponents} 个组件, {result.TotalFiles} 个文件";
                }
                else
                {
                    progress.Reset();
                    ExtractStatus = "提取失败";
                    MessageBox.Show("提取失败：镜像未加载。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                progress.Reset();
                ExtractStatus = "提取失败";
                MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsExtracting = false;
                Services.ComponentExtractor.LiveSink = null;
                sink.Write("任务结束（窗口保留，可复制/打开日志文件回看）");
                sink.MarkFinished();
            }
        }

        /// <summary>从外部目录（含 INF 的子目录）打包第三方驱动：生成 wm.xml 源包，可选调 PkgGen 出 cab。</summary>
        private async void ExtractDriversFromFolder()
        {
            var srcDlg = new OpenFolderDialog { Title = "选择驱动源目录（递归扫描 *.inf，每个含 INF 的子目录成一个包）" };
            if (srcDlg.ShowDialog() != true) return;
            string scanRoot = srcDlg.FolderName;

            string outRoot = OutputPath;
            if (string.IsNullOrEmpty(outRoot))
            {
                var outDlg = new OpenFolderDialog { Title = "选择驱动包输出目录" };
                if (outDlg.ShowDialog() == true) { outRoot = outDlg.FolderName; OutputPath = outRoot; }
                else return;
            }

            var found = Services.DriverPackageBuilder.Scan(scanRoot);
            if (found.Count == 0)
            {
                MessageBox.Show("所选目录下未找到任何 *.inf。", "驱动打包");
                return;
            }
            var ask = MessageBox.Show(
                $"扫描到 {found.Count} 个含 INF 的驱动目录。\n" +
                $"1) 生成源包 + 调 PkgGen 出 cab\n" +
                $"2) 仅生成 wm.xml 源包（交 addonkit 构建）\n\n" +
                $"选「是」= 出 cab；「否」= 仅源包；「取消」= 放弃。",
                "驱动打包方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Cancel) return;
            bool runPkgGen = ask == MessageBoxResult.Yes;

            // 架构/版本/owner：优先从已加载镜像推断，否则默认 arm64 / 10.0.22621.1 / OEM
            var anyPkg = AllPackages.FirstOrDefault(p => !string.IsNullOrEmpty(p.Architecture));
            string arch = anyPkg?.Architecture ?? "arm64";
            string version = anyPkg?.Version ?? "10.0.22621.1";
            string owner = (anyPkg?.Name ?? "OEM").Split('.', '-').FirstOrDefault() ?? "OEM";

            var sink = new Services.TaskLogSink("驱动打包");
            var logWin = new Views.TaskLogWindow(sink) { Owner = Application.Current?.MainWindow };
            logWin.Show();
            sink.Write($"源目录: {scanRoot} → 输出: {outRoot}（{(runPkgGen ? "出 cab" : "仅源包")}，arch={arch} ver={version}）");

            try
            {
                await Task.Run(() =>
                {
                    string pkgRoot = Path.Combine(outRoot, "Packages");
                    string cabDir = Path.Combine(outRoot, "Drivers");
                    string? pkggen = null;
                    if (runPkgGen)
                    {
                        int b = Services.BspComponentBuilderService.BuildOfVersion(version);
                        pkggen = (b > 0 ? Services.AdkToolService.FindToolPath(b, "PkgGen.exe") : null)
                                 ?? Services.DeviceLayoutBuilderService.FindTool(null, "PkgGen.exe");
                        if (pkggen == null)
                            sink.Write("⚠ 未找到 PkgGen.exe（请安装对应 ADK），降级为仅源包");
                        else sink.Write("PkgGen: " + pkggen);
                    }
                    foreach (var p in found)
                    {
                        try
                        {
                            Services.DriverPackageBuilder.BuildSourcePackage(p, pkgRoot, "MainOS");
                            sink.Write($"源包: {p.OutputName}（{p.InfFiles.Count} INF，FeatureID {p.FeatureId}）");
                            if (pkggen != null)
                            {
                                var cabs = Services.DriverPackageBuilder.RunPkgGen(pkggen, p,
                                    owner, version, arch, "fre", pkgRoot, cabDir,
                                    new System.Collections.Generic.List<string>());
                                foreach (var c in cabs) sink.Write("  cab: " + Path.GetFileName(c));
                                if (cabs.Count == 0) sink.Write("  ⚠ 未出 cab（签名/依赖问题，源包仍可用 addonkit 构建）");
                            }
                        }
                        catch (Exception ex) { sink.Write($"❌ {p.OutputName}: {ex.Message}"); }
                    }
                });
            }
            finally
            {
                sink.Write("任务结束（窗口保留，可复制/回看）");
                sink.MarkFinished();
            }
        }

        /// <summary>离线 SYSTEM hive 驱动扫描：结果进独立日志窗口并落盘 CSV。</summary>
        private async void ScanHive()
        {
            if (_loadedDisks == null)
            {
                MessageBox.Show("请先加载镜像。", "离线注册表扫描", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sink = new Services.TaskLogSink("离线注册表驱动扫描");
            var logWin = new Views.TaskLogWindow(sink) { Owner = Application.Current?.MainWindow };
            logWin.Show();
            sink.Write($"镜像: {ImagePath}");

            List<Services.DriverRegistryEntry> entries = new();
            try
            {
                entries = await Task.Run(() => Services.RegistryDriverScanner.ScanOfflineHive(_loadedDisks!, m => sink.Write(m)));
            }
            catch (Exception ex) { sink.Write("❌ 扫描失败: " + ex.Message); }
            finally
            {
                int third = entries.Count(e => e.IsThirdParty);
                sink.Write($"共 {entries.Count} 条驱动注册项（第三方 {third}）");
                if (entries.Count > 0)
                {
                    try
                    {
                        string outDir = !string.IsNullOrEmpty(OutputPath) && Directory.Exists(OutputPath)
                            ? OutputPath : Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Logs");
                        string csv = Path.Combine(outDir, "DriverRegistryScan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("Name,Kind,SourceKey,ImagePath,InfPath,ClassGuid,ThirdParty");
                        foreach (var e in entries.OrderBy(e => e.Name))
                            sb.AppendLine(string.Join(',',
                                Csv(e.Name), Csv(e.SourceKey.Contains("Services") ? "Service" : "ClassInf"),
                                Csv(e.SourceKey), Csv(e.ImagePath), Csv(e.InfPath), Csv(e.ClassGuid),
                                e.IsThirdParty ? "1" : "0"));
                        File.WriteAllText(csv, sb.ToString(), System.Text.Encoding.UTF8);
                        sink.Write("CSV 已保存: " + csv);
                    }
                    catch (Exception ex) { sink.Write("⚠ CSV 保存失败: " + ex.Message); }
                }
                sink.Write("任务结束（窗口保留，可复制/回看）");
                sink.MarkFinished();
            }
        }

        private static string Csv(string s) =>
            string.IsNullOrEmpty(s) ? "\"\"" : "\"" + s.Replace("\"", "\"\"") + "\"";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ════════════════════════════════════════════════════════════════════
        //        三选一模型（选择重构）—— 纯增量新增
        // ════════════════════════════════════════════════════════════════════
        // 页面上原本散落着许多开关。真正需要用户决定的其实只有三件事：
        //   ① 输入形态  ② 目标动作  ③ 执行方式
        // 其余选项要么能由这三项推导出来，要么属于"高级参数"。
        // 本组属性只做**显式化与说明**，不删除任何既有能力。
        //
        // 注意：这里用字符串而不是枚举，是为了让 ComboBox 直接绑定、避免
        // enum 与 ItemSource 的类型转换问题。取值同时用于 CapabilitySummary 的推导。

        public ObservableCollection<string> SourceShapes { get; } = new()
        { "包 CAB 集", "更新包（PSF+CAB）", "Repair Content", "完整镜像" };

        public ObservableCollection<string> TargetActions { get; } = new()
        { "仅展开（不写镜像）", "组装（写入镜像）", "服务既有镜像" };

        public ObservableCollection<string> ExecutionModes { get; } = new()
        { "试运行（不改动任何东西）", "真实执行" };

        private string _sourceShape = "包 CAB 集";
        /// <summary>① 输入形态：决定用哪个解包/展开入口。</summary>
        public string SourceShape
        {
            get => _sourceShape;
            set { _sourceShape = value; OnPropertyChanged(); OnPropertyChanged(nameof(CapabilitySummary)); }
        }

        private string _targetAction = "仅展开（不写镜像）";
        /// <summary>② 目标动作：决定要不要写镜像。</summary>
        public string TargetAction
        {
            get => _targetAction;
            set { _targetAction = value; OnPropertyChanged(); OnPropertyChanged(nameof(CapabilitySummary)); }
        }

        private string _executionMode = "试运行（不改动任何东西）";
        /// <summary>③ 执行方式：真实执行需二次确认。</summary>
        public string ExecutionMode
        {
            get => _executionMode;
            set { _executionMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(CapabilitySummary)); }
        }

        /// <summary>
        /// 由三个选择推导出的"将要执行什么" —— 让用户在下手前就知道会跑哪些服务。
        /// </summary>
        public string CapabilitySummary
        {
            get
            {
                string input = SourceShape switch
                {
                    "包 CAB 集" => "CbsBuildService（按 update.mum 展开为目录树）",
                    "更新包（PSF+CAB）" => "UpdatePackageService（解出 cix.xml 索引，按 PA30 delta 还原；需要 base 源目录）",
                    "Repair Content" => "RepairContentService（硬性顺序：neutral-mergedcomponents 最先，解完删除其 PSF/CAB，再解其余）",
                    "完整镜像" => "直接对镜像操作，无需解包",
                    _ => "(未知输入形态)"
                };
                string action = TargetAction switch
                {
                    "仅展开（不写镜像）" => "只产出目录树，不接触任何镜像 —— 最安全",
                    "组装（写入镜像）" => "SystemAssemblyService：算依赖图 → 排拓扑序 → 交给 CBS 通道执行",
                    "服务既有镜像" => "ServicingRouter 判世代 → 6.x 走原生 CBS（CbsxService），10.0+ 自动走 DISM",
                    _ => "(未知目标动作)"
                };
                string exec = ExecutionMode.StartsWith("试运行")
                    ? "试运行：只做预检与计划（PlanOnly / DryRun 默认开启），不改动任何东西"
                    : "真实执行：会写入目标镜像 —— 目标必须位于挂载的 VHD/VHDX 上，否则程序直接拒绝";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("① 输入形态 → " + input);
                sb.AppendLine("② 目标动作 → " + action);
                sb.AppendLine("③ 执行方式 → " + exec);
                if (TargetAction != "仅展开（不写镜像）" && ExecutionMode.StartsWith("真实执行"))
                    sb.AppendLine();
                if (TargetAction != "仅展开（不写镜像）" && ExecutionMode.StartsWith("真实执行"))
                    sb.AppendLine("⚠ 当前组合会真正写入镜像。建议先把执行方式切回『试运行』确认一遍。");
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>存在真实写入风险时置位，供界面显示警示。</summary>
        public bool WillWriteImage =>
            TargetAction != "仅展开（不写镜像）" && ExecutionMode.StartsWith("真实执行");

        // ════════════════════════════════════════════════════════════════════
        //        三选一 → 执行分发（纯增量新增）
        // ════════════════════════════════════════════════════════════════════
        // 按 SourceShape / TargetAction / ExecutionMode 分发到对应服务。
        // 惰性创建命令，避免改动构造函数。

        private RelayCommand? _dispatchCommand;
        /// <summary>统一分发：按三选一决定跑哪个服务。</summary>
        public RelayCommand DispatchCommand => _dispatchCommand ??= new RelayCommand(Dispatch);

        private string _dispatchResult = "";
        public string DispatchResult
        {
            get => _dispatchResult;
            set { _dispatchResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDispatchResult)); }
        }

        public bool HasDispatchResult => !string.IsNullOrEmpty(_dispatchResult);

        async void Dispatch()
        {
            var sb = new System.Text.StringBuilder();
            void L(string t) { sb.AppendLine(t); BuildStatus = t; }

            bool real = ExecutionMode.StartsWith("真实执行");
            try
            {
                // ── 目标动作：仅展开 ──
                if (TargetAction == "仅展开（不写镜像）")
                {
                    switch (SourceShape)
                    {
                        case "包 CAB 集":
                            L("仅展开 · 包 CAB 集 → 走页面上原有的『构建』按钮（CbsBuildService）");
                            L("  该路径纯展开目录树，不接触镜像。请用上面的包列表与构建按钮。");
                            break;

                        case "更新包（PSF+CAB）":
                            if (string.IsNullOrWhiteSpace(OutputPath)) { L("✗ 请先指定输出目录"); break; }
                            var ucfg = new Services.UpdatePackageService.Config
                            {
                                PackagePath = ImagePath,
                                OutputDir = OutputPath,
                                ListOnly = !real
                            };
                            L($"更新包解包：{System.IO.Path.GetFileName(ImagePath)}（{(real ? "真实" : "只解析索引")}）");
                            var ur = await Task.Run(() => Services.UpdatePackageService.Extract(ucfg, m => sb.AppendLine("  " + m)));
                            L($"  → 布局={ur.Detected} 解出={ur.FilesExtracted} 跳过={ur.FilesSkipped} 缺源={ur.FilesMissingSource} 校验失败={ur.HashMismatches}");
                            if (ur.Error.Length > 0) L("  ✗ " + ur.Error);
                            break;

                        case "Repair Content":
                            if (string.IsNullOrWhiteSpace(OutputPath)) { L("✗ 请先指定输出目录"); break; }
                            var rcfg = new Services.RepairContentService.Config
                            {
                                SourceDir = ImagePath,
                                OutputDir = OutputPath,
                                ClassifyOnly = !real
                            };
                            L($"Repair Content：{ImagePath}（{(real ? "真实两阶段解包" : "只分类")}）");
                            var rr = await Task.Run(() => Services.RepairContentService.Extract(rcfg, m => sb.AppendLine("  " + m)));
                            L("  " + rr.Summary());
                            if (rr.Error.Length > 0) L("  ✗ " + rr.Error);
                            break;

                        case "完整镜像":
                            L("仅展开 · 完整镜像：无意义组合 —— 镜像本身不需要展开。");
                            L("  若想查看镜像内容，请用『服务既有镜像』。");
                            break;
                    }
                }
                // ── 目标动作：组装 / 服务既有镜像 ──
                else
                {
                    if (string.IsNullOrWhiteSpace(ImagePath)) { L("✗ 请先指定目标镜像（必须是挂载的 VHD/VHDX 上的 Windows 目录）"); }
                    else
                    {
                        // 先做门禁与世代判定，绝不盲目动手
                        var decision = Services.ServicingRouter.Decide(ImagePath);
                        L($"世代判定：【{decision.Channel}】{decision.Reason}");
                        if (real) L("⚠ 真实执行模式：会写入目标镜像。");

                        var cfg = new Services.ExplodeSxSNativeService.Config
                        {
                            WinDir = ImagePath,
                            ImageRoot = Services.ExplodeSxSNativeService.DeriveImageRoot(ImagePath),
                            DryRun = !real
                        };
                        L($"开始执行原生 staging（{(real ? "真实" : "试运行")}）…");
                        var res = await Services.ExplodeSxSNativeService.RunAsync(cfg, m => sb.AppendLine("  " + m));
                        foreach (var (step, ok, detail) in res.Steps)
                            L($"  {(ok ? "OK  " : "FAIL")} {step}：{detail}");
                        if (res.Error.Length > 0) L("  ✗ " + res.Error);
                        if (res.RebootRequired) L("  CBS 提示需要重启。");
                        L(res.Success ? "== 完成 ==" : "== 未成功 ==");
                    }
                }
            }
            catch (Exception ex) { L("异常：" + ex.GetType().Name + ": " + ex.Message); }
            finally { DispatchResult = sb.ToString().TrimEnd(); }
        }
    }
}
