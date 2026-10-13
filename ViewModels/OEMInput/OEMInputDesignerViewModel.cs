using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Models.FM;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels.OEMInput
{
    /// <summary>
    /// OEMInput 设计器视图模型 - 管理步骤导航和整体状态
    /// </summary>
    public class OEMInputDesignerViewModel : ObservableObject
    {
        private readonly MainViewModel _mainVM;

        // 步骤定义（扫描→确认→配置）
        public enum Step
        {
            Detect = 0,
            BasicInfo = 1,
            Languages = 2,
            Features = 3,
            Preview = 4
        }

        private int _currentStepIndex;
        public int CurrentStepIndex
        {
            get => _currentStepIndex;
            set
            {
                if (SetProperty(ref _currentStepIndex, value))
                {
                    CurrentStepName = StepNames[value];
                    UpdateStepState();
                    CurrentStepViewModel = value switch
                    {
                        (int)Step.Detect => _detectStepVM,
                        (int)Step.BasicInfo => _basicInfoStepVM,
                        (int)Step.Languages => _languagesStepVM,
                        (int)Step.Features => _featuresStepVM,
                        (int)Step.Preview => _previewStepVM,
                        _ => _detectStepVM
                    };
                    if (value == (int)Step.Preview)
                        GeneratePreview();
                }
            }
        }

        private string _currentStepName = "自动识别";
        public string CurrentStepName { get => _currentStepName; set => SetProperty(ref _currentStepName, value); }

        private bool _canGoBack;
        public bool CanGoBack { get => _canGoBack; set => SetProperty(ref _canGoBack, value); }

        private bool _canGoNext = true;
        public bool CanGoNext { get => _canGoNext; set => SetProperty(ref _canGoNext, value); }

        private bool _isLastStep;
        public bool IsLastStep { get => _isLastStep; set => SetProperty(ref _isLastStep, value); }

        private ObservableObject? _currentStepViewModel;
        public ObservableObject? CurrentStepViewModel { get => _currentStepViewModel; set => SetProperty(ref _currentStepViewModel, value); }

        // 步骤ViewModels
        private readonly DetectStepViewModel? _detectStepVM;
        private readonly BasicInfoStepViewModel? _basicInfoStepVM;
        private readonly LanguagesStepViewModel? _languagesStepVM;
        private readonly FeaturesStepViewModel? _featuresStepVM;
        private readonly PreviewStepViewModel? _previewStepVM;

        // OEMInput 数据
        private OEMInputDoc _oemInputData = new();
        public OEMInputDoc OEMInputData
        {
            get => _oemInputData;
            set
            {
                if (SetProperty(ref _oemInputData, value))
                    HookDirtyTracking();
            }
        }

        /// <summary>
        /// schema 版本代理（供第 4 步的下拉框绑定，读写落到 OEMInputData.SchemaVersion）。
        /// 用索引代理避免 ComboBox 的 Tag(字符串) 与枚举值不匹配导致初始不回显。
        /// </summary>
        public int SchemaVersionIndex
        {
            get => (int)_oemInputData.SchemaVersion;
            set
            {
                var v = (OemInputSchemaVersion)value;
                if (_oemInputData.SchemaVersion != v)
                {
                    _oemInputData.SchemaVersion = v;   // 模型内部通知 IsSchemaV1/V2/Label
                    OnPropertyChanged();
                    IsDirty = true;
                }
            }
        }
        /// <summary>模型 SchemaVersion 变化时（含探测）同步通知，保持下拉框回显。</summary>
        private void NotifySchemaVersionChanged()
        {
            OnPropertyChanged(nameof(SchemaVersionIndex));
            OnPropertyChanged(nameof(SchemaVersionHint));
        }
        /// <summary>当前版本的简短提示，供语言页等联动文案。</summary>
        public string SchemaVersionHint => _oemInputData.IsSchemaV2
            ? "V2：语言用扁平 Languages + 唯一 Default" : "V1：语言用 SupportedLanguages 三分组 + BootUILanguage";

        /// <summary>目标 build 代理（读写落到 OEMInputData.TargetBuild；0=按 20348 基线校验）。</summary>
        public int TargetBuild
        {
            get => _oemInputData.TargetBuild;
            set
            {
                if (_oemInputData.TargetBuild != value)
                {
                    _oemInputData.TargetBuild = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SchemaFamilyHint));
                    IsDirty = true;
                }
            }
        }

        /// <summary>当前 build+代际实际选中的 schema 家族（校验用哪份官方 XSD）。</summary>
        public string SchemaFamilyHint => TargetBuild > 0
            ? $"XSD 家族：{Services.OemInputSchemaFamilyService.FamilyLabel(TargetBuild, _oemInputData.SchemaVersion)}"
            : "XSD 家族：20348 基线（未指定目标 build）";

        /// <summary>从包根路径自动探测 5 位 build 号（1xxxx/2xxxx），命中则填入 TargetBuild。</summary>
        private void TryDetectTargetBuildFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var m = System.Text.RegularExpressions.Regex.Match(path, @"(?<!\d)1[0-9]{4}(?!\d)|(?<!\d)2[0-9]{4}(?!\d)");
            if (m.Success && int.TryParse(m.Value, out int b) && b != _oemInputData.TargetBuild)
            {
                TargetBuild = b;
                ScanSummary += $"\n自动探测目标 build：{b}（XSD 校验按该时代家族）";
            }
        }

        // ── 脏标记（ICD 式：离开/关闭前提示保存）──
        private bool _isDirty;
        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (SetProperty(ref _isDirty, value) && value)
                    _mainVM.SetStatus("有未保存的更改");
            }
        }

        /// <summary>供视图/命令显式标脏（集合类变更不会触发模型 PropertyChanged）。</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>保存成功后清除脏标记。</summary>
        public void ClearDirty() => IsDirty = false;

        /// <summary>由 MainViewModel/MainWindow 在离开前调用；返回 false 表示用户取消。</summary>
        public bool ConfirmDiscardIfDirty()
        {
            if (!IsDirty) return true;
            var r = MessageBox.Show(
                "当前 OEMInput 有未保存的更改。\n\n是：保存后继续\n否：放弃更改继续\n取消：停留在此",
                "未保存的更改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            switch (r)
            {
                case MessageBoxResult.Yes:
                    SaveOEMInput();
                    return !IsDirty;      // 保存成功会清脏；取消保存则仍脏 → 阻止离开
                case MessageBoxResult.No:
                    IsDirty = false;
                    return true;
                default:
                    return false;         // 取消
            }
        }

        /// <summary>订阅模型标量属性变更以自动标脏（构造函数与替换模型时调用）。</summary>
        private void HookDirtyTracking()
        {
            _oemInputData.PropertyChanged -= OnOemDataChangedForDirty;
            _oemInputData.PropertyChanged += OnOemDataChangedForDirty;
        }

        private void OnOemDataChangedForDirty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 模型 SchemaVersion 变化（如加载文件自动探测）时同步下拉框
            if (e.PropertyName == nameof(OEMInputDoc.SchemaVersion))
            {
                OnPropertyChanged(nameof(SchemaVersionIndex));
                OnPropertyChanged(nameof(SchemaVersionHint));
            }
            IsDirty = true;
        }

        // MSPackageRoot
        private string _msPackageRoot = string.Empty;
        public string MSPackageRoot { get => _msPackageRoot; set => SetProperty(ref _msPackageRoot, value); }

        private string _msPackageRootStatus = "未设置";
        public string MSPackageRootStatus { get => _msPackageRootStatus; set => SetProperty(ref _msPackageRootStatus, value); }

        // FM 列表
        public ObservableCollection<string> AdditionalFMFiles { get; } = new();

        // ── 自动发现（包根扫描）──
        /// <summary>扫描到的候选 FM（含已勾选状态），供步骤2确认页展示。</summary>
        public ObservableCollection<DiscoveredFmItem> DiscoveredFMs { get; } = new();

        private bool _isScanning;
        public bool IsScanning { get => _isScanning; set => SetProperty(ref _isScanning, value); }

        private string _scanSummary = "尚未扫描：选择或粘贴包根路径后按回车，或点“浏览…”";
        public string ScanSummary { get => _scanSummary; set => SetProperty(ref _scanSummary, value); }

        /// <summary>CAB 抽出 XML 的会话级临时目录（生命周期随设计器 VM）。</summary>
        private readonly string _extractTempDir =
            Path.Combine(Path.GetTempPath(), "icd_pkgscan_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        // 解析后的FM
        public List<FeatureManifest> ParsedFMs { get; private set; } = new();

        // Features
        public ObservableCollection<FeatureInfo> AvailableFeatures { get; } = new();
        public List<FeatureGroupInfo> FeatureGroups { get; private set; } = new();

        /// <summary>
        /// 按Category分组的Features（用于树视图）
        /// </summary>
        public ObservableCollection<FeatureCategoryGroup> FeatureCategories { get; } = new();

        private string _featureViewMode = "Tree";
        public string FeatureViewMode { get => _featureViewMode; set => SetProperty(ref _featureViewMode, value); }

        private string _featureSearchText = string.Empty;
        public string FeatureSearchText
        {
            get => _featureSearchText;
            set
            {
                if (SetProperty(ref _featureSearchText, value))
                    RebuildFeatureColumns();
            }
        }

        /// <summary>双列视图：过滤后的 Microsoft 组 Features。</summary>
        public ObservableCollection<FeatureInfo> MicrosoftFeatureColumn { get; } = new();

        /// <summary>双列视图：过滤后的 OEM 组 Features。</summary>
        public ObservableCollection<FeatureInfo> OemFeatureColumn { get; } = new();

        /// <summary>已选 Feature 数（底部统计）。</summary>
        public int SelectedFeatureCount => AvailableFeatures.Count(f => f.IsSelected);

        /// <summary>按当前搜索词重建双列内容。</summary>
        public void RebuildFeatureColumns()
        {
            string q = _featureSearchText?.Trim() ?? string.Empty;
            IEnumerable<FeatureInfo> Query(string category) =>
                AvailableFeatures.Where(f =>
                    string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase) &&
                    (q.Length == 0 || f.ID.Contains(q, StringComparison.OrdinalIgnoreCase)));

            Reset(MicrosoftFeatureColumn, Query("Microsoft"));
            Reset(OemFeatureColumn, Query("OEM"));
            OnPropertyChanged(nameof(SelectedFeatureCount));
        }

        private static void Reset(ObservableCollection<FeatureInfo> target, IEnumerable<FeatureInfo> items)
        {
            target.Clear();
            foreach (var item in items)
                target.Add(item);
        }

        // 设备布局
        private string _deviceLayoutCabPath = string.Empty;
        public string DeviceLayoutCabPath { get => _deviceLayoutCabPath; set => SetProperty(ref _deviceLayoutCabPath, value); }

        private string _deviceLayoutStatus = "未识别";
        public string DeviceLayoutStatus { get => _deviceLayoutStatus; set => SetProperty(ref _deviceLayoutStatus, value); }

        private bool _deviceLayoutAutoDetected;
        public bool DeviceLayoutAutoDetected { get => _deviceLayoutAutoDetected; set => SetProperty(ref _deviceLayoutAutoDetected, value); }

        /// <summary>
        /// 从FM中识别到的所有可用设备布局（供用户选择）
        /// </summary>
        public ObservableCollection<DeviceLayoutPackageInfo> AvailableDeviceLayouts { get; } = new();

        private DeviceLayoutPackageInfo? _selectedDeviceLayout;
        public DeviceLayoutPackageInfo? SelectedDeviceLayout
        {
            get => _selectedDeviceLayout;
            set
            {
                if (SetProperty(ref _selectedDeviceLayout, value) && value != null)
                {
                    ApplySelectedDeviceLayout(value);
                }
            }
        }

        /// <summary>FM 未识别到设备布局时为真：允许「从模板创建布局」联动入口显示。</summary>
        private bool _canCreateFromTemplate;
        public bool CanCreateFromTemplate { get => _canCreateFromTemplate; set => SetProperty(ref _canCreateFromTemplate, value); }

        // 语言
        public ObservableCollection<string> AvailableLanguages { get; } = new();
        public ObservableCollection<LanguageItem> SelectedLanguages { get; } = new();

        // 预览
        private string _previewXml = string.Empty;
        public string PreviewXml { get => _previewXml; set => SetProperty(ref _previewXml, value); }

        private string _validationErrors = string.Empty;
        public string ValidationErrors { get => _validationErrors; set => SetProperty(ref _validationErrors, value); }

        private bool _hasValidationErrors;
        /// <summary>校验是否存在错误（驱动预览页红/绿配色）。</summary>
        public bool HasValidationErrors { get => _hasValidationErrors; set => SetProperty(ref _hasValidationErrors, value); }

        private string _validationSummary = string.Empty;
        public string ValidationSummary { get => _validationSummary; set => SetProperty(ref _validationSummary, value); }

        // 步骤名称列表
        public string[] StepNames => new[]
        {
            "自动识别",
            "基本信息",
            "语言设置",
            "选择Features",
            "预览输出"
        };

        // Commands
        public ICommand GoBackCommand { get; }
        public ICommand GoNextCommand { get; }
        public ICommand GoToStartCommand { get; }
        public ICommand GoToStepCommand { get; }
        public ICommand BrowseMSPackageRootCommand { get; }
        public ICommand AddFMsCommand { get; }
        public ICommand RemoveFMCommand { get; }
        public ICommand ClearFMsCommand { get; }
        public ICommand BrowseDeviceLayoutCabCommand { get; }
        public ICommand CreateDeviceLayoutFromTemplateCommand { get; }
        public ICommand GenerateDefaultDescriptionCommand { get; }
        public ICommand AddLanguageCommand { get; }
        public ICommand RemoveLanguageCommand { get; }
        public ICommand SetDefaultLanguageCommand { get; }
        public ICommand ToggleFeatureCommand { get; }
        public ICommand SaveOEMInputCommand { get; }
        public ICommand SaveProjectCommand { get; }
        public ICommand RescanPackageCommand { get; }
        public ICommand ApplyDiscoveredFMsCommand { get; }

        public OEMInputDesignerViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;

            // 初始化步骤ViewModels
            _detectStepVM = new DetectStepViewModel(this);
            _basicInfoStepVM = new BasicInfoStepViewModel(this);
            _languagesStepVM = new LanguagesStepViewModel(this);
            _featuresStepVM = new FeaturesStepViewModel(this);
            _previewStepVM = new PreviewStepViewModel(this);
            CurrentStepViewModel = _detectStepVM;

            // 初始化命令
            GoBackCommand = new RelayCommand(GoBack);
            GoNextCommand = new RelayCommand(GoNext);
            GoToStartCommand = new RelayCommand(GoToStart);
            GoToStepCommand = new RelayCommand<int>(GoToStep);
            BrowseMSPackageRootCommand = new RelayCommand(BrowseMSPackageRoot);
            AddFMsCommand = new RelayCommand(AddFMs);
            RemoveFMCommand = new RelayCommand<string>(RemoveFM);
            ClearFMsCommand = new RelayCommand(ClearFMs);
            BrowseDeviceLayoutCabCommand = new RelayCommand(BrowseDeviceLayoutCab);
            CreateDeviceLayoutFromTemplateCommand = new RelayCommand(CreateDeviceLayoutFromTemplate);
            GenerateDefaultDescriptionCommand = new RelayCommand(GenerateDefaultDescription);
            AddLanguageCommand = new RelayCommand<string>(AddLanguage);
            RemoveLanguageCommand = new RelayCommand<LanguageItem>(RemoveLanguage);
            SetDefaultLanguageCommand = new RelayCommand<LanguageItem>(SetDefaultLanguage);
            ToggleFeatureCommand = new RelayCommand<FeatureInfo>(ToggleFeature);
            SaveOEMInputCommand = new RelayCommand(SaveOEMInput);
            SaveProjectCommand = new RelayCommand(SaveProject);
            RescanPackageCommand = new AsyncRelayCommand(RescanPackageAsync, () => !IsScanning);
            ApplyDiscoveredFMsCommand = new RelayCommand(ApplyDiscoveredFMs);

            CurrentStepIndex = 0;
            UpdateStepState();

            // 初始化默认语言
            SelectedLanguages.Add(new LanguageItem { Code = "en-us", IsDefault = true });

            // 订阅模型标量变更以自动标脏（集合类变更由各命令显式 MarkDirty）
            HookDirtyTracking();
            IsDirty = false;
        }

        private void UpdateStepState()
        {
            CanGoBack = CurrentStepIndex > 0;
            IsLastStep = CurrentStepIndex == StepNames.Length - 1;
            CanGoNext = !IsLastStep;
        }

        private void GoBack()
        {
            if (CanGoBack)
                CurrentStepIndex--;
        }

        private void GoNext()
        {
            if (CanGoNext)
                CurrentStepIndex++;
        }

        private void GoToStart()
        {
            // ICD 式关闭/离开保护：未保存时提示，取消则不离开
            if (!ConfirmDiscardIfDirty()) return;
            _mainVM.NavigateToStart();
        }

        private void GoToStep(int stepIndex)
        {
            if (stepIndex >= 0 && stepIndex < StepNames.Length)
                CurrentStepIndex = stepIndex;
        }

        #region MSPackageRoot

        private void BrowseMSPackageRoot()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 MSPackageRoot 目录 (包含 Retail\\<架构>\\fre)",
                InitialDirectory = Directory.Exists(MSPackageRoot) ? MSPackageRoot : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                MSPackageRoot = dialog.FolderName;
                ValidateMSPackageRoot();
                TryDetectTargetBuildFromPath(dialog.FolderName);
                _ = RescanPackageAsync();
            }
        }

        #region 自动发现

        /// <summary>扫描包根，填充候选 FM 列表（默认全选）。异步避免冻结 UI。</summary>
        private async Task RescanPackageAsync()
        {
            if (string.IsNullOrWhiteSpace(MSPackageRoot) || !Directory.Exists(MSPackageRoot))
            {
                ScanSummary = "请先选择有效的 MSPackageRoot 目录";
                return;
            }

            IsScanning = true;
            ScanSummary = "正在扫描包根…";
            string root = MSPackageRoot;
            string tmp = _extractTempDir;
            PackageScanResult res;
            try
            {
                res = await Task.Run(() => PackageScanner.Scan(root, tmp));
            }
            finally
            {
                IsScanning = false;
            }

            DiscoveredFMs.Clear();
            foreach (var fm in res.Fms)
                DiscoveredFMs.Add(new DiscoveredFmItem(fm, true));

            var parts = new List<string>();
            parts.Add($"架构 {string.Join("/", res.Architectures.DefaultIfEmpty("?"))}");
            parts.Add($"{res.Fms.Count} 个候选 FM");
            parts.Add($"{res.CabFileCount} 个 CAB");
            if (res.Errors.Count > 0) parts.Add($"{res.Errors.Count} 处解析告警");
            ScanSummary = string.Join(" · ", parts);
            if (res.Errors.Count > 0)
                ScanSummary += "\n" + string.Join("\n", res.Errors.Take(6));

            // 扫描到就自动把当前勾选应用一遍，让后续步骤（基本信息/Features）立即有数据
            ApplyDiscoveredFMs();
        }

        private bool _applying;
        private bool _applyPending;

        /// <summary>把候选列表里勾选的 FM 应用到工程：设置 AdditionalFMFiles → 解析 → DeviceLayout 自动识别 → 跨 FM 重名弹窗。</summary>
        public void ApplyDiscoveredFMs()
        {
            if (_applying) { _applyPending = true; return; }   // 冲突弹窗泵消息时不重入
            _applying = true;
            try
            {
                do
                {
                    _applyPending = false;
                    ApplyOnce();
                } while (_applyPending);
            }
            finally { _applying = false; }
        }

        private void ApplyOnce()
        {
            var chosen = DiscoveredFMs.Where(x => x.IsSelected).ToList();
            if (chosen.Count == 0)
            {
                // 一个都不选：清空解析结果但保留已填信息
                AdditionalFMFiles.Clear();
                ParseFMs();
                return;
            }

            AdditionalFMFiles.Clear();
            foreach (var c in chosen)
                AdditionalFMFiles.Add(c.XmlPath);

            ParseFMs();               // 复用既有解析链（含 AutoDetectDeviceLayout / SyncFeatureSelection）
            ResolveDuplicateFeaturesAcrossFms(chosen);
            MarkDirty();
            _mainVM.SetStatus($"已纳入 {chosen.Count} 个 FM");
        }

        /// <summary>
        /// 跨 FM 重名 Feature：按"冲突簇"（共享重名 Feature 的 FM 连通分量）逐个弹窗。
        /// "仅保留所选FM"取消该簇其他 FM 勾选；"保留全部"记入已解决集合，
        /// 使勾选取消引发的自动重应用不会重复追问同一簇。
        /// </summary>
        private readonly HashSet<string> _resolvedDupClusters = new();

        private void ResolveDuplicateFeaturesAcrossFms(List<DiscoveredFmItem> chosen)
        {
            if (chosen.Count < 2) return;

            // FeatureID → 定义它的已选 FM
            var byFeature = new Dictionary<string, List<DiscoveredFmItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fm in chosen)
            {
                var parsed = ParsedFMs.FirstOrDefault(p =>
                    string.Equals(p.FilePath, fm.XmlPath, StringComparison.OrdinalIgnoreCase));
                if (parsed == null) continue;
                foreach (var fid in EnumerateFeatureIds(parsed))
                {
                    if (!byFeature.TryGetValue(fid, out var list)) byFeature[fid] = list = new List<DiscoveredFmItem>();
                    if (!list.Contains(fm)) list.Add(fm);
                }
            }

            // 并查集：把共享任一重名 Feature 的 FM 聚成一簇
            var parent = new Dictionary<DiscoveredFmItem, DiscoveredFmItem>();
            DiscoveredFmItem Find(DiscoveredFmItem x)
            {
                if (!parent.ContainsKey(x)) parent[x] = x;
                while (!ReferenceEquals(parent[x], x)) x = parent[x];
                return x;
            }
            void Union(DiscoveredFmItem a, DiscoveredFmItem b)
            {
                a = Find(a); b = Find(b);
                if (!ReferenceEquals(a, b)) parent[a] = b;
            }
            foreach (var kv in byFeature)
                if (kv.Value.Count > 1)
                    for (int i = 1; i < kv.Value.Count; i++)
                        Union(kv.Value[0], kv.Value[i]);

            var clusters = parent.Keys.GroupBy(Find)
                .Select(g => g.Distinct().ToList())
                .Where(c => c.Count > 1)
                .ToList();

            foreach (var cluster in clusters)
            {
                string sig = string.Join("|", cluster.Select(x => x.XmlPath)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                if (_resolvedDupClusters.Contains(sig)) continue;   // 已选"保留全部"

                var shared = byFeature
                    .Where(kv => kv.Value.Count > 1 && cluster.Contains(kv.Value[0]))
                    .Select(kv => kv.Key).Distinct().ToList();

                var win = new OneCore_Image_Designer.Views.OEMInput.FmDuplicateWindow(shared, cluster)
                { Owner = Application.Current?.MainWindow };
                bool? ok = win.ShowDialog();
                if (ok != true) continue;                            // 跳过/关闭 → 不改动
                if (win.KeepAllRequested) { _resolvedDupClusters.Add(sig); continue; }
                if (win.Winner != null)
                    foreach (var loser in cluster)
                        if (!ReferenceEquals(loser, win.Winner))
                            loser.IsSelected = false;                // 触发一次自动重应用，簇随之消解
            }
        }

        static IEnumerable<string> EnumerateFeatureIds(FeatureManifest fm)
        {
            foreach (var pkg in fm.Features.MicrosoftPackages)
                foreach (var id in pkg.FeatureIDs)
                    if (!string.IsNullOrWhiteSpace(id)) yield return id;
            foreach (var pkg in fm.Features.OEMPackages)
                foreach (var id in pkg.FeatureIDs)
                    if (!string.IsNullOrWhiteSpace(id)) yield return id;
        }

        #endregion

        private void ValidateMSPackageRoot()
        {
            if (string.IsNullOrEmpty(MSPackageRoot) || !Directory.Exists(MSPackageRoot))
            {
                MSPackageRootStatus = "路径不存在";
                return;
            }

            var retailDir = Path.Combine(MSPackageRoot, "Retail");
            if (Directory.Exists(retailDir))
            {
                var archDirs = Directory.GetDirectories(retailDir);
                foreach (var arch in archDirs)
                {
                    var freDir = Path.Combine(arch, "fre");
                    if (Directory.Exists(freDir))
                    {
                        var archName = Path.GetFileName(arch);
                        MSPackageRootStatus = $"已识别架构: {archName}";
                        return;
                    }
                }
            }

            var cabCount = Directory.GetFiles(MSPackageRoot, "*.cab", SearchOption.AllDirectories).Length;
            if (cabCount > 0)
            {
                MSPackageRootStatus = $"找到 {cabCount} 个CAB文件 (未检测到标准Retail结构)";
            }
            else
            {
                MSPackageRootStatus = "未找到CAB文件";
            }
        }

        #endregion

        #region AdditionalFMs

        private void AddFMs()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Feature Manifest (FM) 文件",
                Filter = "FM Files (*.xml)|*.xml|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    if (!AdditionalFMFiles.Contains(file))
                        AdditionalFMFiles.Add(file);
                }
                ParseFMs();
                MarkDirty();
            }
        }

        private void RemoveFM(string path)
        {
            AdditionalFMFiles.Remove(path);
            ParseFMs();
            MarkDirty();
        }

        private void ClearFMs()
        {
            AdditionalFMFiles.Clear();
            ParsedFMs.Clear();
            AvailableFeatures.Clear();
            FeatureGroups.Clear();
            AvailableLanguages.Clear();
            MarkDirty();
        }

        private void SyncFeatureSelectionFromDoc()
        {
            if (AvailableFeatures.Count == 0) return;

            var declared = new HashSet<string>(
                OEMInputData.Features.MicrosoftFeatures.Concat(OEMInputData.Features.OEMFeatures),
                StringComparer.OrdinalIgnoreCase);

            // 文档没声明任何 Feature（新文档）时保持现状；有声明则以声明为准回填勾选
            if (declared.Count == 0) return;
            foreach (var f in AvailableFeatures)
                f.IsSelected = declared.Contains(f.ID);
        }

        /// <summary>
        /// 从磁盘 XML 装载到当前设计器（"打开 OEMInput.xml"入口）。
        /// 调用方负责脏保护；成功后清脏并跳到预览步。
        /// </summary>
        public bool LoadFromXml(string filePath)
        {
            var doc = OEMInputService.LoadFromFile(filePath);
            if (doc == null)
                return false;

            // 换 doc（setter 会重挂脏订阅）
            OEMInputData = doc;

            AdditionalFMFiles.Clear();
            foreach (var fm in doc.AdditionalFMs)
                AdditionalFMFiles.Add(fm);
            if (AdditionalFMFiles.Count > 0)
                ParseFMs();   // 内部会 SyncFeatureSelectionFromDoc

            SelectedLanguages.Clear();
            foreach (var l in doc.Languages.Languages)
                SelectedLanguages.Add(l);
            if (SelectedLanguages.Count == 0)
                SelectedLanguages.Add(new LanguageItem { Code = "en-us", IsDefault = true });

            GeneratePreview();
            CurrentStepIndex = (int)Step.Preview;
            IsDirty = false;
            _mainVM.SetStatus($"已打开: {System.IO.Path.GetFileName(filePath)}");
            return true;
        }

        /// <summary>当前关联的工程文件路径（.cwproj）；空=未保存过工程。</summary>
        public string? CurrentProjectPath { get; private set; }

        private string _projectName = "未命名工程";
        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        /// <summary>把当前设计器状态快照为工程对象。</summary>
        public DesignerProject CaptureProject()
        {
            GeneratePreview(); // 确保 OEMInputData 反映最新勾选/语言/FM
            var selected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in AvailableFeatures)
                selected[f.ID] = f.IsSelected;

            return new DesignerProject
            {
                Name = ProjectName,
                OemInputXml = OEMInputService.GenerateXml(OEMInputData),
                FmFiles = new List<string>(AdditionalFMFiles),
                MSPackageRoot = MSPackageRoot,
                DeviceLayoutCabPath = DeviceLayoutCabPath,
                StepIndex = CurrentStepIndex,
                SelectedFeatures = selected,
            };
        }

        /// <summary>从工程对象恢复设计器状态。调用方负责脏保护。</summary>
        public bool ApplyProject(DesignerProject project, string projectPath)
        {
            OEMInputDoc? doc = null;
            if (!string.IsNullOrWhiteSpace(project.OemInputXml))
                doc = OemInputSerializer.Deserialize(project.OemInputXml);
            doc ??= new OEMInputDoc();
            OEMInputData = doc;

            ProjectName = string.IsNullOrWhiteSpace(project.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(projectPath) : project.Name;

            if (!string.IsNullOrEmpty(project.MSPackageRoot))
                MSPackageRoot = project.MSPackageRoot;

            AdditionalFMFiles.Clear();
            foreach (var fm in project.FmFiles)
                if (System.IO.File.Exists(fm))
                    AdditionalFMFiles.Add(fm);
            if (AdditionalFMFiles.Count > 0)
                ParseFMs();

            // 恢复每个 Feature 的勾选（FM 已解析出可选清单后）
            if (project.SelectedFeatures.Count > 0 && AvailableFeatures.Count > 0)
            {
                foreach (var f in AvailableFeatures)
                    f.IsSelected = project.SelectedFeatures.TryGetValue(f.ID, out var on) && on;
                RebuildFeatureColumns();
            }

            SelectedLanguages.Clear();
            foreach (var l in doc.Languages.Languages)
                SelectedLanguages.Add(l);
            if (SelectedLanguages.Count == 0)
                SelectedLanguages.Add(new LanguageItem { Code = "en-us", IsDefault = true });

            if (!string.IsNullOrEmpty(project.DeviceLayoutCabPath) && System.IO.File.Exists(project.DeviceLayoutCabPath))
            {
                DeviceLayoutCabPath = project.DeviceLayoutCabPath;
                DeviceLayoutAutoDetected = false;
                ApplyDeviceLayoutInfo(project.DeviceLayoutCabPath);
            }

            CurrentStepIndex = Math.Clamp(project.StepIndex, 0, StepNames.Length - 1);
            GeneratePreview();
            IsDirty = false;
            CurrentProjectPath = projectPath;
            _mainVM.SetStatus($"已打开工程: {ProjectName}");
            return true;
        }

        public void SetProjectPath(string path)
        {
            CurrentProjectPath = path;
            if (string.IsNullOrWhiteSpace(_projectName) || _projectName == "未命名工程")
                ProjectName = System.IO.Path.GetFileNameWithoutExtension(path);
            _mainVM.RefreshTabTitle();
        }

        private void ParseFMs()
        {
            ParsedFMs = FMService.ParseFMs(AdditionalFMFiles);

            var features = FMService.ExtractAllFeatures(ParsedFMs);
            AvailableFeatures.Clear();
            foreach (var f in features)
                AvailableFeatures.Add(f);
            SyncFeatureSelectionFromDoc();
            RebuildFeatureColumns();

            // 按Category分组（用于树视图）
            FeatureCategories.Clear();
            foreach (var group in features.GroupBy(f => f.Category).OrderBy(g => g.Key))
            {
                FeatureCategories.Add(new FeatureCategoryGroup
                {
                    Name = group.Key,
                    Features = group.ToList()
                });
            }

            FeatureGroups = FMService.ExtractFeatureGroups(ParsedFMs);

            var langs = FMService.ExtractAvailableLanguages(ParsedFMs);
            AvailableLanguages.Clear();
            foreach (var l in langs)
                AvailableLanguages.Add(l);

            if (ParsedFMs.Count > 0)
            {
                OEMInputData.Product = FMService.InferProductName(ParsedFMs);
                GenerateDefaultDescription();
            }

            AutoDetectDeviceLayout();

            _mainVM.SetStatus($"已解析 {ParsedFMs.Count} 个FM, {AvailableFeatures.Count} 个Feature");
        }

        #endregion

        #region DeviceLayout

        private void AutoDetectDeviceLayout()
        {
            AvailableDeviceLayouts.Clear();
            _selectedDeviceLayout = null;
            CanCreateFromTemplate = false;

            if (ParsedFMs.Count == 0)
            {
                DeviceLayoutStatus = "等待FMs解析";
                return;
            }

            // 方法1: 从FM的DeviceLayoutPackages节点直接提取（新格式，如GenericDeviceFM.xml）
            var layoutPackages = FMService.FindDeviceLayoutPackages(ParsedFMs, MSPackageRoot);
            foreach (var pkg in layoutPackages)
            {
                AvailableDeviceLayouts.Add(pkg);
            }

            // 方法2: 从BasePackages/Features中搜索包含DeviceLayout的包ID（旧格式兼容）
            if (AvailableDeviceLayouts.Count == 0 && !string.IsNullOrEmpty(MSPackageRoot))
            {
                var packageIDs = FMService.FindDeviceLayoutPackageIDs(ParsedFMs);
                foreach (var pid in packageIDs)
                {
                    var searchPattern = $"*{pid}*.cab";
                    try
                    {
                        var found = Directory.GetFiles(MSPackageRoot, searchPattern, SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null)
                        {
                            AvailableDeviceLayouts.Add(new DeviceLayoutPackageInfo
                            {
                                SOC = "",
                                PackageID = pid,
                                PackageName = System.IO.Path.GetFileName(found),
                                Path = System.IO.Path.GetDirectoryName(found) ?? "",
                                SourceFM = "自动搜索",
                                CabPath = found
                            });
                        }
                    }
                    catch { }
                }
            }

            // 从FM中提取Device标识
            var deviceNames = FMService.FindDeviceNames(ParsedFMs);
            if (deviceNames.Count > 0 && string.IsNullOrEmpty(OEMInputData.Device))
            {
                OEMInputData.Device = deviceNames[0];
            }

            // 以 FM 识别结果为权威：OEMInput 只写 SOC 短名，不写 CAB 路径。
            // 因此只要 FM 给出了候选就直接选中第一个（优先有 CAB 的，便于预览），CAB 缺失不影响正确性。
            var preferred = AvailableDeviceLayouts.FirstOrDefault(p => p.HasCab)
                            ?? AvailableDeviceLayouts.FirstOrDefault();
            if (preferred != null)
            {
                SelectedDeviceLayout = preferred;
                DeviceLayoutAutoDetected = true;
                CanCreateFromTemplate = false;
                var cabNote = preferred.HasCab ? "" : "（无本地 CAB，仅用于预览，不影响 OEMInput 生成）";
                DeviceLayoutStatus = $"已从 FM 识别 {AvailableDeviceLayouts.Count} 个设备布局，选中 SOC={OEMInputData.SOC}{cabNote}";
            }
            else
            {
                CanCreateFromTemplate = true;
                DeviceLayoutStatus = "FM 中未声明 DeviceLayoutPackages/SOCPackages。若目标设备使用新格式，请在 FM 里补上该节点；也可用“手动选择 CAB”临时推断 SOC，或「从模板创建设备布局」。";
            }
        }

        private void ApplySelectedDeviceLayout(DeviceLayoutPackageInfo pkg)
        {
            if (pkg.HasCab && pkg.CabPath != null)
            {
                DeviceLayoutCabPath = pkg.CabPath;
                ApplyDeviceLayoutInfo(pkg.CabPath);
            }

            // 从FM中提取的SOC优先于从CAB文件名解析的
            if (!string.IsNullOrEmpty(pkg.SOC))
            {
                OEMInputData.SOC = pkg.SOC;
            }

            var deviceNames = FMService.FindDeviceNames(ParsedFMs);
            var deviceInfo = deviceNames.Count > 0 ? string.Join(", ", deviceNames) : "未识别";
            var socInfo = !string.IsNullOrEmpty(pkg.SOC) ? pkg.SOC : (string.IsNullOrEmpty(OEMInputData.SOC) ? "未识别" : OEMInputData.SOC);

            DeviceLayoutStatus = $"已选择: SOC={socInfo}, Device={deviceInfo}, 来源FM={pkg.SourceFM}";
        }

        private void BrowseDeviceLayoutCab()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 DeviceLayout CAB 文件",
                Filter = "CAB Files (*.cab)|*.cab|All Files (*.*)|*.*",
                InitialDirectory = Directory.Exists(MSPackageRoot) ? MSPackageRoot : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                DeviceLayoutCabPath = dialog.FileName;
                DeviceLayoutAutoDetected = false;
                ApplyDeviceLayoutInfo(dialog.FileName);
            }
        }

        /// <summary>FM 未识别到布局时的联动：打开内嵌「设备布局创建」编辑器标签页，供用户基于模板/图形条创建，造好后再回此选择该 SOC。</summary>
        private void CreateDeviceLayoutFromTemplate()
        {
            _mainVM.EnsureDeviceLayoutEditor();
        }

        private void ApplyDeviceLayoutInfo(string cabPath)
        {
            var info = DeviceLayoutService.ParseInfoFromCabName(cabPath);
            if (info != null && info.IsParsed)
            {
                // 只在解析出的值非空时覆盖，避免把 FM 已识别的 SOC 抹成空
                if (!string.IsNullOrEmpty(info.SOC))
                    OEMInputData.SOC = info.SOC;
                if (!string.IsNullOrEmpty(info.SV))
                    OEMInputData.SV = info.SV;
                if (!string.IsNullOrEmpty(info.Device))
                    OEMInputData.Device = info.Device;

                DeviceLayoutStatus = $"已识别: SOC={DisplayOr(OEMInputData.SOC, info.SOC)}, SV={DisplayOr(OEMInputData.SV, info.SV)}, Device={DisplayOr(OEMInputData.Device, info.Device)}";
            }
            else
            {
                // 解析失败不再清空：保留 FM 途径已获得的值，仅提示
                DeviceLayoutStatus = string.IsNullOrEmpty(OEMInputData.SOC)
                    ? "无法从CAB文件名解析设备信息，请手动填写"
                    : $"CAB文件名解析失败，沿用FM识别值: SOC={OEMInputData.SOC}, Device={OEMInputData.Device}";
            }
        }

        private static string DisplayOr(string current, string parsed)
            => !string.IsNullOrEmpty(current) ? current : (parsed ?? "");

        #endregion

        #region BasicInfo

        /// <summary>
        /// 从FM中提取OS版本号
        /// </summary>
        private string GetOSVersion()
        {
            foreach (var fm in ParsedFMs)
            {
                if (!string.IsNullOrEmpty(fm.OSVersion))
                    return fm.OSVersion;
            }
            return "Unknown";
        }

        /// <summary>
        /// 从MSPackageRoot或FM中推断CPU架构
        /// </summary>
        private string GetCPUArchitecture()
        {
            if (!string.IsNullOrEmpty(MSPackageRoot) && Directory.Exists(MSPackageRoot))
            {
                var retailDir = Path.Combine(MSPackageRoot, "Retail");
                if (Directory.Exists(retailDir))
                {
                    var archDirs = Directory.GetDirectories(retailDir);
                    foreach (var d in archDirs)
                    {
                        var name = Path.GetFileName(d).ToUpperInvariant();
                        if (name is "ARM64" or "AMD64" or "X86" or "ARM")
                            return name;
                    }
                }
            }
            // 从FM的BasePackages中推断
            foreach (var fm in ParsedFMs)
            {
                foreach (var pkg in fm.BasePackages)
                {
                    if (!string.IsNullOrEmpty(pkg.CPUType))
                        return pkg.CPUType.ToUpperInvariant();
                }
            }
            return "ARM64"; // 默认
        }

        /// <summary>
        /// 生成默认Description: "SKU 版本 架构 VM image"
        /// </summary>
        public void GenerateDefaultDescription()
        {
            var sku = string.IsNullOrEmpty(OEMInputData.Product) ? "Unknown" : OEMInputData.Product;
            var version = GetOSVersion();
            var arch = GetCPUArchitecture();
            OEMInputData.Description = $"{sku} {version} {arch} VM image";
        }

        #endregion

        #region Languages

        private void AddLanguage(string lang)
        {
            if (!SelectedLanguages.Any(l => l.Code.Equals(lang, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedLanguages.Add(new LanguageItem { Code = lang.ToLowerInvariant() });
                MarkDirty();
            }
        }

        private void RemoveLanguage(LanguageItem lang)
        {
            if (lang.IsDefault)
                return;
            SelectedLanguages.Remove(lang);
            MarkDirty();
        }

        private void SetDefaultLanguage(LanguageItem lang)
        {
            foreach (var l in SelectedLanguages)
                l.IsDefault = false;
            lang.IsDefault = true;
            OEMInputData.BootUILanguage = lang.Code;
            OEMInputData.BootLocale = lang.Code;
        }

        #endregion

        #region Features

        /// <summary>界面复选框变更后调用：标脏、执行冲突检测并刷新统计。</summary>
        public void OnFeatureCheckedChanged(FeatureInfo feature)
        {
            MarkDirty();
            if (feature.IsSelected)
                CheckFeatureConflicts(feature);
            OnPropertyChanged(nameof(SelectedFeatureCount));
        }

        private void ToggleFeature(FeatureInfo feature)
        {
            feature.IsSelected = !feature.IsSelected;
            OnFeatureCheckedChanged(feature);
        }

        private void CheckFeatureConflicts(FeatureInfo selectedFeature)
        {
            foreach (var group in FeatureGroups)
            {
                if (group.IsPublishingGroup)
                    continue; // 发布期约束，构建期不裁剪（对齐官方语义）
                if (group.ConstraintType == FeatureGroupConstraint.None)
                    continue;
                if (!FeatureConstraintRules.GroupContains(group, selectedFeature.ID))
                    continue;

                var others = AvailableFeatures
                    .Where(f => f.IsSelected && f != selectedFeature &&
                                FeatureConstraintRules.GroupContains(group, f.ID))
                    .ToList();

                if (others.Count == 0)
                    continue;

                // 冲突：让用户在互斥项中选择保留哪个（含刚勾选的项）
                var candidates = new List<FeatureInfo> { selectedFeature };
                candidates.AddRange(others);

                var win = new OneCore_Image_Designer.Views.OEMInput.FeatureConflictWindow(group, candidates) { Owner = Application.Current?.MainWindow };
                bool? ok = win.ShowDialog();

                if (ok != true)
                {
                    // 放弃本次勾选：把刚勾的取消回去
                    selectedFeature.IsSelected = false;
                    continue;
                }

                if (win.UncheckAllRequested)
                {
                    selectedFeature.IsSelected = false;
                    foreach (var o in others) o.IsSelected = false;
                    continue;
                }

                foreach (var f in candidates)
                    f.IsSelected = string.Equals(f.ID, win.KeptId, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region Preview

        private void GeneratePreview()
        {
            // 同理：FM 列表未加载时保留文档原 AdditionalFMs，避免打开文件后预览清空
            if (AdditionalFMFiles.Count > 0)
                OEMInputData.AdditionalFMs = new List<string>(AdditionalFMFiles);
            OEMInputData.Languages.Languages = new List<LanguageItem>(SelectedLanguages);

            // 仅当 FM 已解析（有可勾选项）时才用勾选状态重建清单；
            // 打开的 XML 可能引用未加载的 FM，此时保留文档原样，避免预览清空 Features。
            if (AvailableFeatures.Count > 0)
            {
                OEMInputData.Features.MicrosoftFeatures = AvailableFeatures
                    .Where(f => f.IsSelected && f.Category == "Microsoft")
                    .Select(f => f.ID)
                    .ToList();

                OEMInputData.Features.OEMFeatures = AvailableFeatures
                    .Where(f => f.IsSelected && f.Category == "OEM")
                    .Select(f => f.ID)
                    .ToList();
            }

            PreviewXml = OEMInputService.GenerateXml(OEMInputData);

            var errors = OEMInputService.Validate(OEMInputData);
            var preflight = ProjectValidationService.ValidateOemInputDocument(OEMInputData);
            errors = preflight.Diagnostics
                .Where(d => d.Severity == ValidationSeverity.Error)
                .Select(d => d.Message)
                .Distinct()
                .ToList();
            FeatureConstraintRules.SetKnownFeatures(AvailableFeatures.Select(f => f.ID));
            var selectedIds = AvailableFeatures.Where(f => f.IsSelected).Select(f => f.ID).ToList();
            foreach (var group in FeatureGroups)
            {
                var msg = FeatureConstraintRules.EvaluateFull(group, selectedIds);
                if (msg != null) errors.Add(msg);
            }
            HasValidationErrors = errors.Count > 0;
            ValidationSummary = $"{preflight.Errors} 个错误 / {preflight.Warnings} 个警告 · {preflight.Diagnostics.FirstOrDefault(d => d.Source == "版本")?.Message}";
            ValidationErrors = errors.Count > 0
                ? string.Join("\n", errors)
                : preflight.Warnings > 0
                    ? "结构验证通过，但存在警告：\n" + string.Join("\n", preflight.Diagnostics
                        .Where(d => d.Severity == ValidationSeverity.Warning).Select(d => d.Message))
                    : "验证通过，可以保存";
        }

        private void SaveOEMInput()
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存 OEMInput.xml",
                Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                FileName = "OEMInput.xml",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                if (OEMInputService.SaveToFile(OEMInputData, dialog.FileName))
                {
                    IsDirty = false;
                    MessageBox.Show($"已保存到:\n{dialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    _mainVM.SetStatus($"已保存: {dialog.FileName}");
                }
                else
                {
                    MessageBox.Show("保存失败，请检查路径权限", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Project (.cwproj)

        private void SaveProject()
        {
            // 已有路径→直接覆盖；否则弹保存框
            if (!string.IsNullOrEmpty(CurrentProjectPath) &&
                DesignerProjectService.SaveProject(CaptureProject(), CurrentProjectPath))
            {
                IsDirty = false;
                _mainVM.SetStatus($"工程已保存: {CurrentProjectPath}");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存工程",
                Filter = DesignerProjectService.Filter,
                FileName = string.IsNullOrWhiteSpace(ProjectName) ? "未命名工程" : ProjectName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog() != true) return;

            if (DesignerProjectService.SaveProject(CaptureProject(), dialog.FileName))
            {
                SetProjectPath(dialog.FileName);
                IsDirty = false;
                _mainVM.SetStatus($"工程已保存: {dialog.FileName}");
            }
            else
            {
                MessageBox.Show($"保存失败：{DesignerProjectService.LastError}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>从 .cwproj 打开工程（调用方负责脏保护）。返回 false 表示文件损坏/不可读。</summary>
        public bool OpenProject(string path)
        {
            var project = DesignerProjectService.LoadProject(path);
            if (project == null) return false;
            bool ok = ApplyProject(project, path);
            if (ok)
                DesignerProjectService.AddRecent(new RecentProjectEntry
                {
                    Name = ProjectName,
                    Path = path,
                    LastOpenedUtc = DateTime.UtcNow,
                });
            return ok;
        }

        /// <summary>重置为全新空白工程（调用方负责脏保护）。</summary>
        public void ResetForNewProject()
        {
            IsDirty = false;   // 先清，避免下面赋值链再次弹保存提示
            OEMInputData = new OEMInputDoc();
            CurrentProjectPath = null;
            ProjectName = "未命名工程";
            MSPackageRoot = string.Empty;
            DeviceLayoutCabPath = string.Empty;
            DeviceLayoutAutoDetected = true;
            AdditionalFMFiles.Clear();
            ParsedFMs = new List<FeatureManifest>();
            AvailableFeatures.Clear();
            FeatureCategories.Clear();
            FeatureGroups = new List<FeatureGroupInfo>();
            AvailableLanguages.Clear();
            MicrosoftFeatureColumn.Clear();
            OemFeatureColumn.Clear();
            SelectedLanguages.Clear();
            SelectedLanguages.Add(new LanguageItem { Code = "en-us", IsDefault = true });
            PreviewXml = string.Empty;
            ValidationErrors = string.Empty;
            HasValidationErrors = false;
            CurrentStepIndex = 0;
            IsDirty = false;
            _mainVM.SetStatus("新工程");
        }

        #endregion
    }

    /// <summary>
    /// 按Category分组的Feature集合（用于树视图）
    /// </summary>
    public class FeatureCategoryGroup
    {
        public string Name { get; set; } = string.Empty;
        public List<FeatureInfo> Features { get; set; } = new();
        public int Count => Features.Count;
    }
}
