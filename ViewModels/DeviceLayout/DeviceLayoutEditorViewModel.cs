using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OneCore_Image_Designer.Models.DeviceLayout;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels.DeviceLayout
{
    /// <summary>一块「盘」（单盘 / 存储池内虚拟盘 / LUN）——图形条的分组单元，支持盘级编辑。</summary>
    public class DiskNode : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";        // 单盘 / 池 OSPool·MainOSDisk / LUN MainOSStore
        public int SectorSize { get; set; } = 512;
        public bool InPool { get; set; }
        public ObservableCollection<Partition> Parts { get; } = new();
        /// <summary>图形条 tile（比例宽度）；每次编辑提交后 RebuildTiles。</summary>
        public ObservableCollection<PartitionTile> Tiles { get; } = new();

        /// <summary>盘级可编辑属性（StoreType 必填；Id/DevicePath 仅 stores/pools 有意义）。</summary>
        public string StoreType
        {
            get => _storeType;
            set { if (_storeType == value) return; _storeType = value; OnPropertyChanged(nameof(StoreType)); OnPropertyChanged(nameof(DisplayTitle)); }
        }
        string _storeType = "";
        public string Id { get => _id; set { if (_id == value) return; _id = value; OnPropertyChanged(nameof(Id)); } }
        string _id = "";
        public string DevicePath { get => _devicePath; set { if (_devicePath == value) return; _devicePath = value; OnPropertyChanged(nameof(DevicePath)); } }
        string _devicePath = "";
        /// <summary>盘容量（扇区或字节，取决于 doc 是否字节制；0=未指定）。</summary>
        public long SizeValue { get => _sizeValue; set { if (_sizeValue == value) return; _sizeValue = value; OnPropertyChanged(nameof(SizeValue)); OnPropertyChanged(nameof(DiskSizeText)); } }
        long _sizeValue;
        public bool OnlyDefinedGptEntries { get => _onlyGpt; set { if (_onlyGpt == value) return; _onlyGpt = value; OnPropertyChanged(nameof(OnlyDefinedGptEntries)); } }
        bool _onlyGpt;

        bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }

        /// <summary>展示标题：盘类型；无类型回退原标题。</summary>
        public string DisplayTitle => string.IsNullOrWhiteSpace(StoreType) ? Title :
            (InPool ? $"{Title.Split('·').FirstOrDefault()?.Trim()} · {StoreType}" : StoreType);

        static double EstimateSectors(Partition p) =>
            p.TotalSectors > 0 ? p.TotalSectors :
            p.TotalBytes > 0 ? p.TotalBytes / (p.SectorSize > 0 ? p.SectorSize : 512.0) :
            p.MinFreeSectors;

        public void RebuildTiles()
        {
            Tiles.Clear();
            double total = Parts.Sum(EstimateSectors);
            foreach (var p in Parts)
            {
                double own = EstimateSectors(p);
                double ratio = total <= 0 || own <= 0 ? 0.08 : own / total;
                Tiles.Add(new PartitionTile(p, ratio));
            }
            OnPropertyChanged(nameof(DiskSizeText));
            OnPropertyChanged(nameof(PartsCount));
        }

        public string PartsCount => $"{Parts.Count} 分区";

        /// <summary>条带最小宽度（DIP）：分区多时保证可横向滚动而非挤压。</summary>
        public double BandMinWidth => Math.Max(0, Tiles.Count) * 96;

        /// <summary>容量数值单位：true=字节（MS 字节制布局），false=扇区。</summary>
        public bool SizeIsBytes { get => _sizeIsBytes; set { if (_sizeIsBytes == value) return; _sizeIsBytes = value; OnPropertyChanged(nameof(SizeIsBytes)); OnPropertyChanged(nameof(DiskSizeText)); } }
        bool _sizeIsBytes;

        public string DiskSizeText
        {
            get
            {
                double mb = Parts.Sum(p => p.SizeInMB);
                string s = mb >= 1024 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";
                if (SizeValue <= 0) return s;
                double bytes = SizeIsBytes ? SizeValue : SizeValue * (SectorSize > 0 ? SectorSize : 512.0);
                string cap = bytes >= 1073741824 ? $"{bytes / 1073741824.0:F1} GB" : $"{bytes / 1048576.0:F1} MB";
                return $"{s} / 容量 {cap}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>图形条上的一段分区。Ratio 决定宽度占比；颜色按名字哈希（复用 PartitionColorConverter）。</summary>
    public class PartitionTile : INotifyPropertyChanged
    {
        public Partition P { get; }
        public double Ratio { get; }
        bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        }
        public PartitionTile(Partition p, double ratio) { P = p; Ratio = ratio; }
        public string Name => P.Name;
        public string FileSystem => P.FileSystem;
        public string SizeText => P.DisplaySize;
        public string SizeBar => P.UseAllSpace ? "填满" : $"{Math.Round(P.SizeInMB, 1):0.#} MB";
        public string TipText => $"{P.Name}  ·  {P.FileSystem}  ·  {P.SizeInBytes:#,0} B" +
                                 (P.UseAllSpace ? "（用满剩余）" : P.TotalSectors > 0 ? $"（{P.TotalSectors:#,0} 扇区）" : "") +
                                 (P.Bootable ? "  · Bootable" : "") + (P.RequiredToFlash ? "  · RequiredToFlash" : "");
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>一步的验证状态（向导步进条用）。</summary>
    public enum StepState { Locked, Current, Done, Failed }

    /// <summary>
    /// 设备布局编辑器（标签页内嵌）：DiskGenius 式图形分区条 + 4 步流程
    /// ①信息 →②编辑（图形条）→③校验（结构自检 + 官方规则，硬闸门）→④打包 FM+CAB。
    /// ③未通过时「打包」不可进入；布局形状（平铺/多LUN/存储池/MS 字节制）全程保留。
    /// </summary>
    public class DeviceLayoutEditorViewModel : INotifyPropertyChanged
    {
        MainViewModel _main;

        public DeviceLayoutEditorViewModel(MainViewModel main)
        {
            _main = main;
            Disks = new ObservableCollection<DiskNode>();
            Rules = DeviceLayoutRuleChecker.ListRules();
            // 规则下拉：默认「自动（按模板绑定）」
            RuleItems.Insert(0, "");
            foreach (var r in Rules) RuleItems.Add($"{r.Era}/{r.Name}");
            _ruleChoice = "";
            RefreshTemplates();
            GoToStep(1);
        }

        // ═════════ 模板库下拉 ═════════
        public ObservableCollection<DeviceLayoutTemplateInfo> Templates { get; } = new();

        public void RefreshTemplates()
        {
            var sel = SelectedTemplate;
            Templates.Clear();
            Templates.Add(new DeviceLayoutTemplateInfo { Title = "（不用模板 — 从空白开始）" });
            foreach (var t in DeviceLayoutTemplates.ListAll()) Templates.Add(t);
            SelectedTemplate = sel != null && Templates.Contains(sel) ? sel : Templates[0];
        }

        DeviceLayoutTemplateInfo? _selectedTemplate;
        public DeviceLayoutTemplateInfo? SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (!ReferenceEquals(_selectedTemplate, value))
                {
                    _selectedTemplate = value;
                    OnPropertyChanged();
                    if (value != null && !string.IsNullOrEmpty(value.Key)) LoadTemplate(value);
                }
            }
        }

        // ═════════ 步骤机 ═════════
        int _step = 1;
        public int Step { get => _step; private set { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(StepInfoText)); RefreshStepStates(); } }
        public string StepInfoText => Step switch
        {
            1 => "第 1 步 · 包信息：时代 / 包 ID / SOC / 版本 / 架构 / 扇区 / 布局形状",
            2 => "第 2 步 · 编辑分区：在图形条上选中分区编辑，或从 XML / CAB / 模板载入起点",
            3 => "第 3 步 · 验证：官方规则校验（硬闸门）。通过才能进入驱动/打包",
            4 => "第 4 步 · 驱动：选择驱动源目录、扫描预览、命名空间与 Feature 分组（可跳过）",
            5 => "第 5 步 · 打包：生成 FM + CAB（可签名，可含 BSP 组件）",
            _ => ""
        };

        public StepState State1 => Step > 1 || _metaComplete ? StepState.Done : StepState.Current;
        public StepState State2 => Step > 2 ? StepState.Done : Step == 2 ? StepState.Current : StepState.Locked;
        public StepState State3 => _validatedOk == true ? StepState.Done : _validatedOk == false ? StepState.Failed : Step >= 3 ? StepState.Current : StepState.Locked;
        public StepState State4 => Step > 4 ? StepState.Done : Step == 4 ? StepState.Current : StepState.Locked;
        public StepState State5 => Step == 5 ? StepState.Current : _validatedOk == true ? StepState.Done : StepState.Locked;
        void RefreshStepStates()
        {
            foreach (var n in new[] { nameof(State1), nameof(State2), nameof(State3), nameof(State4), nameof(State5) })
                OnPropertyChanged(n);
            foreach (var n in new[] { nameof(ShowStep1), nameof(ShowStep2), nameof(ShowStep3), nameof(ShowStep4), nameof(ShowStep5) })
                OnPropertyChanged(n);
            OnPropertyChanged(nameof(CanNext));
            OnPropertyChanged(nameof(CanPrev));
        }

        public bool CanNext => Step switch
        {
            1 => _metaComplete,
            2 => Disks.Sum(d => d.Parts.Count) > 0,
            3 => _validatedOk == true,   // 硬闸门：验证通过才能进驱动/打包
            4 => true,                   // 驱动步可跳过（不配驱动也能进打包）
            _ => false
        };
        public bool CanPrev => Step > 1;

        void GoToStep(int s) { Step = s; }
        public void Next() { if (!CanNext) return; GoToStep(Step + 1); }
        public void Prev() { if (CanPrev) GoToStep(Step - 1); }
        /// <summary>从校验步直接跳打包步（校验通过后）。</summary>
        public void JumpToPackage() { if (_validatedOk == true) GoToStep(5); }

        bool _metaComplete;
        public bool MetaComplete { get => _metaComplete; set { _metaComplete = value; OnPropertyChanged(); RefreshStepStates(); } }
        void RecomputeMeta() => MetaComplete =
            !string.IsNullOrWhiteSpace(PackageId) && !string.IsNullOrWhiteSpace(Soc)
            && !string.IsNullOrWhiteSpace(Version) && Disks.Count > 0;

        // ═════════ ① 包信息 ═════════
        int _era = 0;                     // 0=Modern 1=Phone
        public int Era { get => _era; set { if (_era == value) return; _era = value; OnPropertyChanged(); OnEraChanged(); } }
        public bool IsModern => _era == 0;

        string _packageId = "Contoso-OneCore-MyLayout-Package";
        public string PackageId { get => _packageId; set { _packageId = value; OnPropertyChanged(); RecomputeMeta(); OnPropertyChanged(nameof(FipPreview)); } }

        string _soc = "MyDev_Test";
        public string Soc { get => _soc; set { _soc = value; OnPropertyChanged(); RecomputeMeta(); } }

        string _version = "10.0.20279.1002";
        public string Version { get => _version; set { _version = value; OnPropertyChanged(); RecomputeMeta(); } }

        string _architecture = "arm64";
        public string Architecture { get => _architecture; set { _architecture = value; OnPropertyChanged(); } }

        string _partition = "MainOS";
        public string PartitionTarget { get => _partition; set { _partition = value; OnPropertyChanged(); } }

        int _sectorSize = 512;
        public int SectorSize { get => _sectorSize; set { if (_sectorSize == value) return; _sectorSize = value; OnPropertyChanged(); foreach (var d in Disks) d.SectorSize = value; } }

        /// <summary>形状：flat=单盘平铺 / stores=多LUN(Stores) / pools=存储池(DeviceLayout) / mspools=存储池(MSDeviceLayout 字节制 SpaceSplit)。</summary>
        string _shape = "flat";
        public string Shape { get => _shape; set { _shape = value; OnPropertyChanged(); RebuildDiskGrouping(); } }

        string _fipPreview = "";
        public string FipPreview => _fipPreview;

        void OnEraChanged()
        {
            OnPropertyChanged(nameof(IsModern));
            if (IsModern)
            { PackageId = "Contoso-OneCore-MyLayout-Package"; Version = "10.0.20279.1002"; }
            else
            { PackageId = "Contoso.DEVICELAYOUT_MYDEV.MainOS"; Version = "8.15.14393.1000"; }
            _fipPreview = IsModern ? DeriveFip() : "—";
            RecomputeMeta();
        }

        string DeriveFip()
        {
            try
            {
                string owner = PackageId.Split('-', '.').FirstOrDefault() ?? "Contoso";
                string cap = Soc.StartsWith("DEVICELAYOUT_") ? Soc : "DEVICELAYOUT_" + Soc.ToUpperInvariant();
                return $"{(owner.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ? "FactoryOS" : owner + ".FactoryOS")}.{cap}.FOSGENDEV.FIP";
            }
            catch { return ""; }
        }

        // ═════════ ② 图形分区条 ═════════
        public ObservableCollection<DiskNode> Disks { get; }
        DiskNode? _selectedDisk;
        public DiskNode? SelectedDisk { get => _selectedDisk; set { _selectedDisk = value; OnPropertyChanged(); SelectedPartition = null; } }

        Partition? _selectedPart;
        public Partition? SelectedPartition { get => _selectedPart; set { _selectedPart = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(ShowPropPanel)); OnPropertyChanged(nameof(CanMove)); } }
        public bool HasSelection => SelectedPartition != null;
        public Visibility ShowPropPanel => SelectedPartition != null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>点击某色块：清除其它高亮，选中该项并同步 SelectedPartition / SelectedDisk。</summary>
        public void SelectTile(PartitionTile tile)
        {
            if (tile == null) return;
            foreach (var d in Disks) foreach (var t in d.Tiles) t.IsSelected = ReferenceEquals(t, tile);
            SelectedPartition = tile.P;
            foreach (var d in Disks) if (d.Parts.Contains(tile.P)) { _selectedDisk = d; OnPropertyChanged(nameof(SelectedDisk)); break; }
        }

        // ── 分步可见性 ──
        public Visibility ShowStep1 => Step == 1 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowStep2 => Step == 2 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowStep3 => Step == 3 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowStep4 => Step == 4 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowStep5 => Step == 5 ? Visibility.Visible : Visibility.Collapsed;
        public string DiskCountText => $"{Disks.Count} 块盘 / {TotalPartCount} 个分区";
        public int SectorIndex { get => SectorSize >= 4096 ? 1 : 0; set { if (SectorIndex == value) return; SectorSize = value == 1 ? 4096 : 512; OnPropertyChanged(); } }

        bool _wantSign;
        public bool WantSign { get => _wantSign; set { _wantSign = value; OnPropertyChanged(); } }

        // 规则校验（模板绑定或手动）
        public List<(string Era, string File, string Name, string Scope)> Rules { get; }
        public ObservableCollection<string> RuleItems { get; } = new();
        string _ruleChoice;
        public string RuleChoice { get => _ruleChoice; set { _ruleChoice = value; OnPropertyChanged(); _validatedOk = null; RefreshStepStates(); } }
        public bool RuleAuto => string.IsNullOrEmpty(_ruleChoice);

        DeviceLayoutTemplateInfo? _loadedTemplate;
        HashSet<string> _baseline = new();

        /// <summary>从模板载入（供 OEMInput 联动与自身模板下拉调用）。</summary>
        public void LoadTemplate(DeviceLayoutTemplateInfo t)
        {
            var doc = DeviceLayoutService.ParseFromXml(t.LayoutXml);
            if (doc == null) { LastLog = "模板解析失败: " + DeviceLayoutService.LastError; return; }
            ApplyDoc(doc);
            _loadedTemplate = t;
            RuleChoice = t.DefaultRule;
            Era = t.Era == "Phone" ? 1 : 0;
            Soc = t.Key; Architecture = t.Architecture;
            PackageId = t.Custom ? PackageId : ModernNameFor(t.Key);
            _baseline = ComputeBaseline(t);
            _validatedOk = null;
            GoToStep(2);
            LastLog = $"已载入模板：{t.Title}（{t.DeviceType} · {SourceFull(t)}）";
            RecomputeMeta();
        }

        static string ModernNameFor(string key) => key switch
        {
            "gptspace512_x86" or "gptspace512_amd64" => "Microsoft-OneCore-GenericGPTSpaceDeviceLayout512-Package",
            "gptspace4096_x86" or "gptspace4096_amd64" => "Microsoft-OneCore-GenericGPTSpaceDeviceLayout4096-Package",
            "spacesplit_arm" or "spacesplit_arm64" => "Microsoft-OneCore-GenericGPTSpaceSplitDeviceLayout-Package",
            "vmgen1_x86" or "vmgen1_amd64" => "Microsoft-OneCore-VMGen1-DeviceLayout-Package",
            "talkman_arm32_18968" => "Microsoft-OneCore-Talkman-Space-DeviceLayout-Package",
            "qc8998_15032" or "qc8998_16202" => "Qualcomm.QC8998.DeviceLayout_Test",
            "sdm845_17047" => "Microsoft-OneCore-SDM845-DeviceLayout-StateSep-Test-Package",
            "qc850_18259" => "Microsoft-OneCore-SDM845-DeviceLayout-StateSep-Space-Test-Package",
            _ => "Microsoft-OneCore-GenericGPTDeviceLayout-Package",
        };

        static string SourceFull(DeviceLayoutTemplateInfo t) => t.Source;

        HashSet<string> ComputeBaseline(DeviceLayoutTemplateInfo t)
        {
            string? rule = t.ResolveRulePath();
            if (rule == null) return new();
            var orig = DeviceLayoutService.ParseFromXml(t.LayoutXml);
            if (orig == null) return new();
            orig.ApplySectorSize();
            return new(DeviceLayoutRuleChecker.CheckWithParentSemantics(orig, rule).Issues
                .Where(i => i.IsError).Select(i => i.Where + "|" + i.Message));
        }

        // ═════════ 形状 → 文档 / 分组的还原 ═════════
        // 保留载入时的「原形状」结构（平铺 / Stores / StoragePools / MS），编辑时把分区
        // 映射回对应 Store.Partitions，打包/序列化不丢结构。
        DeviceLayoutDoc? _doc;    // 当前活动文档（保留形状）
        string? _sourceXmlPath;   // 载入模板时的原样 XML（打包直接复制，保形）

        public void ApplyDoc(DeviceLayoutDoc doc)
        {
            _doc = doc;
            doc.ApplySectorSize(); doc.DecorateProvenance();
            SectorSize = doc.SectorSize;
            Shape = doc.RootTag == "MSDeviceLayout" ? "mspools"
                  : doc.StoragePools.Count > 0 ? "pools"
                  : doc.Stores.Count > 0 ? "stores" : "flat";
            RebuildDiskGrouping();
            _validatedOk = null;
            RecomputeMeta();
        }

        void RebuildDiskGrouping()
        {
            if (_doc == null) return;
            Disks.Clear(); SelectedDisk = null; SelectedPartition = null;
            switch (Shape)
            {
                case "stores":
                    foreach (var s in _doc.Stores)
                        AddDisk($"{s.StoreType}", s, inPool: false);
                    break;
                case "pools":
                case "mspools":
                    foreach (var pool in _doc.StoragePools)
                        foreach (var s in pool.Stores)
                            AddDisk($"池 {pool.Name} · {s.StoreType}", s, inPool: true);
                    if (_doc.Stores.Count > 0)
                    {
                        var last = _doc.Stores[^1];
                        AddDisk($"池外物理盘 · {last.StoreType}", last, inPool: false);
                    }
                    break;
                default: // flat
                    AddDisk("单盘", null, inPool: false);
                    break;
            }
            SelectedDisk = Disks.FirstOrDefault();
            OnPropertyChanged(nameof(TotalPartCount));
        }

        void AddDisk(string title, Store? store, bool inPool)
        {
            var d = new DiskNode { Title = title, InPool = inPool, SectorSize = _doc?.SectorSize ?? 512 };
            if (store != null)
            {
                _storeMap[d] = store;
                d.StoreType = store.StoreType;
                d.Id = store.Id;
                d.DevicePath = store.DevicePath;
                d.SizeIsBytes = _doc != null && _doc.UsesBytes;
                d.SizeValue = _doc != null && _doc.UsesBytes ? store.SizeInBytesXml : store.SizeInSectors;
                d.OnlyDefinedGptEntries = store.OnlyAllocateDefinedGptEntries;
                foreach (var p in store.Partitions) d.Parts.Add(p);
            }
            else if (Shape == "flat") { foreach (var p in _doc!.Partitions) d.Parts.Add(p); }
            d.RebuildTiles();
            Disks.Add(d);
        }
        readonly Dictionary<DiskNode, Store> _storeMap = new();

        public int TotalPartCount => Disks.Sum(d => d.Parts.Count);

        /// <summary>分区字段编辑提交后调用：刷新该盘图形条 + 作废校验。</summary>
        public void OnPartitionEdited()
        {
            if (SelectedDisk != null) SelectedDisk.RebuildTiles();
            else foreach (var d in Disks) d.RebuildTiles();
            InvalidateValidation();
        }

        /// <summary>当前活动布局文档（打包/校验前的形状快照）。</summary>
        public DeviceLayoutDoc? CurrentDoc => _doc;

        /// <summary>载入外部 XML/CAB（"从文件导入起点"）。</summary>
        public void LoadFromFile(string path)
        {
            var doc = path.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)
                ? DeviceLayoutService.ParseFromCab(path)
                : DeviceLayoutService.ParseFromXml(path);
            if (doc == null) { LastLog = "导入失败: " + DeviceLayoutService.LastError; return; }
            ClearTemplateBinding();
            ApplyDoc(doc);
            _sourceXmlPath = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? path : null;
            LastLog = "已载入 " + Path.GetFileName(path);
            GoToStep(2);
        }

        /// <summary>把当前 DiskNode 集合写回 _doc 的形状结构（编辑保存点）。</summary>
        public DeviceLayoutDoc CommitDoc()
        {
            _doc ??= new DeviceLayoutDoc();
            _doc.RootTag = Shape == "mspools" ? "MSDeviceLayout" : "DeviceLayout";
            _doc.UsesBytes = Shape == "mspools";
            _doc.SectorSize = SectorSize;
            switch (Shape)
            {
                case "stores":
                    _doc.Partitions.Clear(); _doc.StoragePools.Clear();
                    foreach (var d in Disks)
                    {
                        var store = _storeMap.TryGetValue(d, out var s) ? s : new Store { StoreType = d.Title };
                        _storeMap[d] = store;
                        store.Partitions = d.Parts.ToList();
                        if (!_doc.Stores.Contains(store)) _doc.Stores.Add(store);
                    }
                    break;
                case "pools":
                case "mspools":
                    _doc.Partitions.Clear(); _doc.Stores.Clear();
                    var pool = _doc.StoragePools.FirstOrDefault() ?? new StoragePool { Name = "OSPool" };
                    if (!_doc.StoragePools.Contains(pool)) _doc.StoragePools.Add(pool);
                    foreach (var d in Disks)
                    {
                        var store = _storeMap.TryGetValue(d, out var s) ? s : new Store { StoreType = d.Title.Split("·").Last().Trim() };
                        _storeMap[d] = store;
                        store.Partitions = d.Parts.ToList();
                        if (d.InPool && !pool.Stores.Contains(store)) pool.Stores.Add(store);
                        if (!d.InPool && !_doc.Stores.Contains(store)) _doc.Stores.Add(store);
                    }
                    break;
                default:
                    _doc.Stores.Clear(); _doc.StoragePools.Clear();
                    _doc.Partitions = Disks.FirstOrDefault()?.Parts.ToList() ?? new();
                    break;
            }
            _doc.ApplySectorSize(); _doc.DecorateProvenance();
            return _doc;
        }

        /// <summary>把当前布局存为自定义模板（保留形状/字节制），供工具条「另存为自定义模板」调用。</summary>
        public DeviceLayoutTemplateInfo SaveAsTemplate(string key, string title, string deviceType,
            string notes, string ruleChoice)
        {
            var doc = CommitDoc();
            bool isV2 = Shape != "flat";
            var meta = new DeviceLayoutTemplateInfo
            {
                Key = key,
                Title = string.IsNullOrWhiteSpace(title) ? key : title,
                DeviceType = string.IsNullOrWhiteSpace(deviceType) ? "自定义设备" : deviceType,
                Architecture = Architecture,
                Source = "用户保存 " + DateTime.Now.ToString("yyyy-MM-dd"),
                Era = IsModern ? "Modern" : "Phone",
                Notes = notes ?? "",
                DefaultRule = ruleChoice ?? "",
            };
            var saved = DeviceLayoutTemplates.SaveCustom(doc, isV2, meta);
            return saved;
        }

        // ═════════ 分区增删（图形条上的操作）════════
        public void AddPartitionToSelectedDisk()
        {
            if (SelectedDisk == null) return;
            var p = new Partition { Name = "NewPartition", FileSystem = "NTFS", TotalSectors = 1048576, SectorSize = SectorSize };
            SelectedDisk.Parts.Add(p);
            SelectedPartition = p;
            _validatedOk = null; RefreshStepStates();
            OnPropertyChanged(nameof(TotalPartCount));
        }
        public void RemoveSelectedPartition()
        {
            if (SelectedDisk == null || SelectedPartition == null) return;
            SelectedDisk.Parts.Remove(SelectedPartition);
            SelectedPartition = null;
            _validatedOk = null; RefreshStepStates();
            OnPropertyChanged(nameof(TotalPartCount));
        }
        public void MoveSelectedPartition(DiskNode target)
        {
            if (SelectedPartition == null || SelectedDisk == null || target == null || target == SelectedDisk) return;
            SelectedDisk.Parts.Remove(SelectedPartition);
            target.Parts.Add(SelectedPartition);
            SelectedDisk = target;
            _loadedTemplate = null; _sourceXmlPath = null;   // 结构已改：不再原样复制，重建序列化
            _validatedOk = null; RefreshStepStates();
            OnPropertyChanged(nameof(TotalPartCount));
            OnPropertyChanged(nameof(DiskCountText));
        }

        /// <summary>选中分区在当前盘内上移一位（GPT 顺序即刷写顺序）。</summary>
        public void MovePartitionUp()
        {
            var d = SelectedDisk; var p = SelectedPartition;
            if (d == null || p == null) return;
            int i = d.Parts.IndexOf(p);
            if (i <= 0) return;
            d.Parts.RemoveAt(i);
            d.Parts.Insert(i - 1, p);
            TouchPartitionOrder();
        }

        /// <summary>选中分区在当前盘内下移一位。</summary>
        public void MovePartitionDown()
        {
            var d = SelectedDisk; var p = SelectedPartition;
            if (d == null || p == null) return;
            int i = d.Parts.IndexOf(p);
            if (i < 0 || i >= d.Parts.Count - 1) return;
            d.Parts.RemoveAt(i);
            d.Parts.Insert(i + 1, p);
            TouchPartitionOrder();
        }

        void TouchPartitionOrder()
        {
            _loadedTemplate = null; _sourceXmlPath = null;
            _validatedOk = null; RefreshStepStates();
            SelectedDisk?.RebuildTiles();
        }

        // ═══ 磁盘级操作（加盘/加池盘/删盘/属性提交/选中） ═══

        // 官方样本里出现过的 StoreType，按常用顺序作为新盘推荐名（占用则取下一候选）
        static readonly string[] PoolMemberTypes = { "EFIESPDisk", "MainOSDisk", "BSPDisk", "PreInstalledDisk", "OSDataDisk", "DataDisk", "VIRT_EFIESPDisk" };
        static readonly string[] PhysicalTypes   = { "Default", "MainOSStore", "SV", "Ramdisk", "XBL" };

        static string PickStoreType(IEnumerable<string> usedNames, string[] candidates)
        {
            var used = new HashSet<string>(usedNames.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) if (!used.Contains(c)) return c;
            int n = used.Count + 1;
            string t; do { t = "NewDisk" + n; n++; } while (used.Contains(t));
            return t;
        }

        IEnumerable<string> CurrentStoreTypes() => Disks.Select(d => d.StoreType);

        DiskNode? _selectedDiskNode;
        public DiskNode? SelectedDiskNode
        {
            get => _selectedDiskNode;
            set
            {
                if (SetSelectedDiskNode(value))
                {
                    OnPropertyChanged(nameof(HasDisk));
                    OnPropertyChanged(nameof(ShowDiskPanel));
                    OnPropertyChanged(nameof(CanMove));
                }
            }
        }
        bool SetSelectedDiskNode(DiskNode? v)
        {
            if (ReferenceEquals(_selectedDiskNode, v)) return false;
            _selectedDiskNode = v; OnPropertyChanged(nameof(SelectedDiskNode)); return true;
        }

        public void SelectDisk(DiskNode? d)
        {
            foreach (var x in Disks) x.IsSelected = ReferenceEquals(x, d);
            SelectedDiskNode = d;
            if (d != null) SelectedDisk = d;
        }

        public bool HasDisk => SelectedDiskNode != null;
        public Visibility ShowDiskPanel => SelectedDiskNode != null ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>有选中的分区、且选中了另一块盘时才能移动。</summary>
        public bool CanMove => SelectedPartition != null && SelectedDiskNode != null
                               && !ReferenceEquals(SelectedDiskNode, SelectedDisk);

        /// <summary>添加磁盘：平铺时自动升级为多盘（stores）；给每个新盘一个默认虚拟盘 Store。</summary>
        public void AddDiskLevel()
        {
            _doc ??= new DeviceLayoutDoc();
            if (Shape == "flat")
            {
                // 升级：原平铺分区迁入第 1 块盘，再加第 2 块
                var first = Disks.FirstOrDefault();
                var s0 = new Store { StoreType = "MainOSDisk", Id = Guid.NewGuid().ToString("B"), Partitions = first?.Parts.ToList() ?? new() };
                _doc.Stores.Clear(); _doc.Stores.Add(s0);
                Shape = "stores";                     // 触发 RebuildDiskGrouping
                _storeMap[Disks[0]] = s0; Disks[0].StoreType = s0.StoreType; Disks[0].Id = s0.Id;
            }
            var st = new Store { StoreType = PickStoreType(CurrentStoreTypes(), PhysicalTypes), Id = Guid.NewGuid().ToString("B") };
            _doc.Stores.Add(st);
            var nd = new DiskNode { Title = $"池外物理盘 · {st.StoreType}", InPool = false, SectorSize = SectorSize,
                                    StoreType = st.StoreType, Id = st.Id };
            nd.RebuildTiles();
            Disks.Add(nd);
            _storeMap[nd] = st;
            SelectDisk(nd);
            _loadedTemplate = null; _sourceXmlPath = null;
            InvalidateValidation();
            OnPropertyChanged(nameof(TotalPartCount)); OnPropertyChanged(nameof(DiskCountText));
            LastLog = $"已添加磁盘（默认 StoreType={st.StoreType}，取自官方样本常用名，可在下方改）";
        }

        /// <summary>存储池形状下添加池内成员虚拟盘；非池形状时先提示升级为池。</summary>
        public void AddPoolDiskLevel()
        {
            _doc ??= new DeviceLayoutDoc();
            if (Shape != "pools" && Shape != "mspools")
            {
                UpgradeToPool();
            }
            var pool = _doc.StoragePools.FirstOrDefault()!;
            var st = new Store { StoreType = PickStoreType(CurrentStoreTypes(), PoolMemberTypes), Id = Guid.NewGuid().ToString("B") };
            pool.Stores.Add(st);
            var nd = new DiskNode { Title = $"池 {pool.Name} · {st.StoreType}", InPool = true, SectorSize = SectorSize,
                                    StoreType = st.StoreType, Id = st.Id };
            nd.RebuildTiles();
            Disks.Add(nd);
            _storeMap[nd] = st;
            SelectDisk(nd);
            _loadedTemplate = null; _sourceXmlPath = null;
            InvalidateValidation();
            OnPropertyChanged(nameof(TotalPartCount)); OnPropertyChanged(nameof(DiskCountText));
            LastLog = "已在存储池内添加成员盘";
        }

        /// <summary>把当前布局升级为存储池形状（平铺/多盘 → pools：每块盘连其分区并入 OSPool）。</summary>
        public void UpgradeToPool()
        {
            _doc ??= new DeviceLayoutDoc();
            if (Shape == "pools" || Shape == "mspools")
            {
                LastLog = "已是存储池结构，无需升级";
                return;
            }
            var pool = new StoragePool { Name = "OSPool" };
            foreach (var d in Disks)
            {
                if (!_storeMap.TryGetValue(d, out var st) || st == null)
                {
                    st = new Store { StoreType = "MainOSDisk", Id = Guid.NewGuid().ToString("B") };
                    _storeMap[d] = st;
                    d.StoreType = st.StoreType; d.Id = st.Id;
                }
                st.Partitions = d.Parts.ToList();   // 把当前盘上的分区固化进其 Store
                pool.Stores.Add(st);
            }
            _doc.Partitions.Clear();   // 平铺分区已迁入 pool.Stores
            _doc.Stores.Clear();
            _doc.StoragePools.Clear();
            _doc.StoragePools.Add(pool);
            Shape = _doc.RootTag == "MSDeviceLayout" ? "mspools" : "pools";   // 触发 RebuildDiskGrouping 按池重建
            _loadedTemplate = null; _sourceXmlPath = null;
            InvalidateValidation();
            OnPropertyChanged(nameof(DiskCountText));
            LastLog = $"已升级为存储池（OSPool，{pool.Stores.Count} 块成员盘）；池外物理盘可再用「＋磁盘」添加";
        }

        public void RemoveSelectedDisk()
        {
            var d = SelectedDiskNode ?? SelectedDisk;
            if (d == null || Disks.Count <= 1) { LastLog = "至少保留一块磁盘"; return; }
            if (_doc != null)
            {
                if (_storeMap.TryGetValue(d, out var st))
                {
                    _doc.Stores.Remove(st);
                    foreach (var pool in _doc.StoragePools) pool.Stores.Remove(st);
                }
                else if (Shape == "flat") _doc.Partitions.Clear();
            }
            if (SelectedPartition != null && d.Parts.Contains(SelectedPartition)) SelectedPartition = null;
            _storeMap.Remove(d);
            Disks.Remove(d);
            _loadedTemplate = null; _sourceXmlPath = null;
            InvalidateValidation();
            OnPropertyChanged(nameof(TotalPartCount)); OnPropertyChanged(nameof(DiskCountText));
            LastLog = "已删除磁盘 " + d.DisplayTitle;
        }

        /// <summary>把选中磁盘的可编辑盘属性写回（StoreType/Id/DevicePath/容量）。</summary>
        public void CommitDiskEdits()
        {
            var d = SelectedDiskNode ?? SelectedDisk;
            if (d == null || _doc == null) return;
            var st = _storeMap.TryGetValue(d, out var s) ? s : null;
            if (st == null)
            {
                st = new Store { StoreType = d.StoreType, Id = d.Id };
                _storeMap[d] = st;
                if (Shape == "pools" || Shape == "mspools")
                {
                    var pool = _doc.StoragePools.FirstOrDefault();
                    if (pool == null) { pool = new StoragePool { Name = "OSPool" }; _doc.StoragePools.Add(pool); }
                    pool.Stores.Add(st);
                }
                else _doc.Stores.Add(st);
            }
            st.StoreType = d.StoreType; st.Id = d.Id; st.DevicePath = d.DevicePath;
            st.OnlyAllocateDefinedGptEntries = d.OnlyDefinedGptEntries;
            if (_doc.UsesBytes) { st.SizeInBytesXml = d.SizeValue; st.SizeInSectors = 0; }
            else { st.SizeInSectors = d.SizeValue; st.SizeInBytesXml = 0; }
            d.Title = d.InPool ? $"池 {_doc.StoragePools.FirstOrDefault()?.Name} · {d.StoreType}" : $"池外物理盘 · {d.StoreType}";
            _loadedTemplate = null; _sourceXmlPath = null;
            InvalidateValidation();
            OnPropertyChanged(nameof(DiskCountText));
            LastLog = "盘属性已写回";
        }

        // 分区修改后须使校验作废
        public void InvalidateValidation() { _validatedOk = null; RefreshStepStates(); }

        // ═════════ ③ 验证 ═════════
        bool? _validatedOk;
        public bool? ValidatedOk { get => _validatedOk; private set { _validatedOk = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValidationVerdict)); RefreshStepStates(); } }
        public string ValidationVerdict => ValidatedOk switch
        {
            null => "尚未验证",
            true => "✅ 验证通过",
            false => "❌ 验证未通过——按硬闸门规则不可进入打包步"
        };
        public ObservableCollection<string> ValidationLines { get; } = new();
        string _preflightSummary = "统一预检将在验证时汇总布局结构、版本形状和规则结果。";
        public string PreflightSummary { get => _preflightSummary; private set { _preflightSummary = value; OnPropertyChanged(); } }

        string _lastLog = "";
        public string LastLog { get => _lastLog; set { _lastLog = value; OnPropertyChanged(); } }

        public async Task RunValidationAsync()
        {
            ValidationLines.Clear();
            var doc = CommitDoc();
            string? rulePath = ResolveRule();
            if (rulePath == null)
            {
                var selfErr = StructureSelfCheck(doc);
                AddLine(selfErr == null ? "✅ 结构自检通过（未绑定官方规则——ModernPC/VM 布局属正常情况）" : "❌ 结构自检：" + selfErr);
                ValidatedOk = selfErr == null;
                PreflightSummary = $"{(selfErr == null ? 0 : 1)} 个错误 / 0 个警告 · {doc.AllPartitions.Count} 个分区 · {doc.SectorSize} 字节扇区 · 未绑定时代规则";
                return;
            }
            AddLine("规则: " + Path.GetFileName(rulePath));
            var res = await Task.Run(() => { doc.ApplySectorSize(); return DeviceLayoutRuleChecker.CheckWithParentSemantics(doc, rulePath); });
            int shown = 0;
            foreach (var i in res.Issues)
            {
                bool exempt = i.IsError && _baseline.Contains(i.Where + "|" + i.Message);
                AddLine($"{(exempt ? "ℹ(官方基线豁免)" : i.IsError ? "✗" : "ℹ")} [{i.Where}] {i.Message}");
                if (++shown >= 60) { AddLine("…（更多略）"); break; }
            }
            var newErrs = res.Issues.Where(i => i.IsError && !_baseline.Contains(i.Where + "|" + i.Message)).ToList();
            int warnings = res.Issues.Count(i => !i.IsError);
            PreflightSummary = $"{newErrs.Count} 个错误 / {warnings} 个警告 · {doc.AllPartitions.Count} 个分区 · {doc.SectorSize} 字节扇区 · {Path.GetFileName(rulePath)}";
            if (newErrs.Count == 0) { AddLine("✅ 验证通过（相对官方基线无新增违规）"); ValidatedOk = true; }
            else { AddLine($"❌ {newErrs.Count} 处新增违规——修复后才能进入打包"); ValidatedOk = false; }
        }

        string? ResolveRule()
        {
            if (!RuleAuto)
            {
                var parts = _ruleChoice.Split('/', 2);
                string p = parts.Length == 2
                    ? Path.Combine(DeviceLayoutRuleChecker.RulesRoot, parts[0], parts[1])
                    : Path.Combine(DeviceLayoutRuleChecker.RulesRoot, parts[0]);
                return File.Exists(p) ? p : null;
            }
            return _loadedTemplate?.ResolveRulePath();
        }

        static string? StructureSelfCheck(DeviceLayoutDoc doc)
        {
            var parts = doc.AllPartitions;
            if (parts.Count == 0) return "没有分区";
            var names = parts.Select(p => (p.Name ?? "").Trim()).ToList();
            if (names.Any(string.IsNullOrEmpty)) return "存在未命名分区";
            var dup = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) return "分区名重复：" + dup.Key;
            if (!names.Any(n => n.Contains("MainOS", StringComparison.OrdinalIgnoreCase)))
                return "缺少 MainOS 分区";
            if (parts.Any(p => !p.UseAllSpace && p.TotalSectors <= 0 && p.TotalBytes <= 0 && p.MinFreeSectors <= 0))
                return "存在无大小且不用满空间的分区";
            return null;
        }

        void AddLine(string s)
        {
            var disp = App.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => ValidationLines.Add(s));
            else ValidationLines.Add(s);
        }

        // ═════════ ④ 打包 ═════════
        public string OutputDir { get; set; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeviceLayoutOut");
        public string? SigningPfx { get; set; }

        // ═════════ BSP 组件（OEMDevicePlatform / DeviceInfo / machineinfo / SMBIOS）════════
        bool _wantDevicePlatform;
        public bool WantDevicePlatform { get => _wantDevicePlatform; set { _wantDevicePlatform = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAnyBspComponent)); } }
        bool _wantDeviceInfo;
        public bool WantDeviceInfo { get => _wantDeviceInfo; set { _wantDeviceInfo = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAnyBspComponent)); } }
        bool _wantMachineInfo;
        public bool WantMachineInfo { get => _wantMachineInfo; set { _wantMachineInfo = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAnyBspComponent)); } }
        bool _wantSmbios;
        public bool WantSmbios { get => _wantSmbios; set { _wantSmbios = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAnyBspComponent)); } }
        public bool HasAnyBspComponent => WantDevicePlatform || WantDeviceInfo || WantMachineInfo || WantSmbios || WantDrivers;

        string _deviceName = "";
        public string DeviceName { get => _deviceName; set { _deviceName = value; OnPropertyChanged(); OnPropertyChanged(nameof(MachineInfoEraHint)); } }

        string _dpIds = "*";
        /// <summary>OEMDevicePlatform 的 DevicePlatformIDs（逗号分隔；* = 任意）。</summary>
        public string DevicePlatformIds { get => _dpIds; set { _dpIds = value; OnPropertyChanged(); } }

        string _minSectorCount = "20971520";
        public string MinSectorCount { get => _minSectorCount; set { _minSectorCount = value; OnPropertyChanged(); } }

        int _biosModeIndex = 0;   // 0=UEFI 1=不写
        public int BiosModeIndex { get => _biosModeIndex; set { _biosModeIndex = value; OnPropertyChanged(); } }

        // DeviceInfo 注册表包
        string _socVersion = "arm64";
        public string SocVersion { get => _socVersion; set { _socVersion = value; OnPropertyChanged(); } }
        string _hardwareVersion = "0.0.0.1";
        public string HardwareVersion { get => _hardwareVersion; set { _hardwareVersion = value; OnPropertyChanged(); } }
        string _deviceManufacturer = "";
        public string DeviceManufacturer { get => _deviceManufacturer; set { _deviceManufacturer = value; OnPropertyChanged(); } }
        string _deviceModel = "";
        public string DeviceModel { get => _deviceModel; set { _deviceModel = value; OnPropertyChanged(); } }

        // machineinfo.xml
        string _miManufacturer = "";
        public string MiManufacturer { get => _miManufacturer; set { _miManufacturer = value; OnPropertyChanged(); } }
        string _miFamily = "";
        public string MiFamily { get => _miFamily; set { _miFamily = value; OnPropertyChanged(); } }
        string _miProductName = "";
        public string MiProductName { get => _miProductName; set { _miProductName = value; OnPropertyChanged(); } }
        string _miSku = "1";
        public string MiSku { get => _miSku; set { _miSku = value; OnPropertyChanged(); } }

        /// <summary>machineinfo 时代界限提示（17666+ 才有 MachineInfoFile 机制）。</summary>
        public string MachineInfoEraHint
        {
            get
            {
                int b = Services.BspComponentBuilderService.BuildOfVersion(Version);
                if (b > 0 && b < Services.BspComponentBuilderService.MachineInfoFirstBuild)
                    return $"⚠ machineinfo.xml 是 {Services.BspComponentBuilderService.MachineInfoFirstBuild}+ 的机制（当前版本 {b} 的 imageapp 不读取）";
                return "";
            }
        }
        public bool ShowMachineInfoEraHint => MachineInfoEraHint.Length > 0;

        // SMBIOS（IoT）
        string _smbiosProduct = "";
        public string SmbiosProduct { get => _smbiosProduct; set { _smbiosProduct = value; OnPropertyChanged(); } }
        string _smbiosSku = "";
        public string SmbiosSku { get => _smbiosSku; set { _smbiosSku = value; OnPropertyChanged(); } }
        string _smbiosFamily = "";
        public string SmbiosFamily { get => _smbiosFamily; set { _smbiosFamily = value; OnPropertyChanged(); } }
        string _smbiosBbProd = "";
        public string SmbiosBbProd { get => _smbiosBbProd; set { _smbiosBbProd = value; OnPropertyChanged(); } }

        // 驱动处理
        bool _wantDrivers;
        public bool WantDrivers { get => _wantDrivers; set { _wantDrivers = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAnyBspComponent)); } }
        string _driverScanRoot = "";
        /// <summary>驱动源目录：递归扫描 *.inf，按所在目录分组各成一个 Drivers.&lt;InfName&gt; 包。</summary>
        public string DriverScanRoot { get => _driverScanRoot; set { _driverScanRoot = value; OnPropertyChanged(); } }
        bool _driverRunPkgGen = true;
        /// <summary>勾选=生成源包后直接调 ADK 的 PkgGen.exe 出 cab；不勾=只出 wm.xml 源包交 addonkit 构建。</summary>
        public bool DriverRunPkgGen { get => _driverRunPkgGen; set { _driverRunPkgGen = value; OnPropertyChanged(); } }
        string _driverNamespace = "Drivers";
        /// <summary>驱动包命名空间段（addonkit 默认 Drivers；NXP 等厂商用自定义如 iMX）。包名=&lt;ns&gt;.&lt;InfName&gt;。</summary>
        public string DriverNamespace { get => _driverNamespace; set { _driverNamespace = value; OnPropertyChanged(); } }
        string _driverFeatureGroup = "";
        /// <summary>FeatureID 分组（NXP 范式：全部驱动共用 IMX_DRIVERS）；留空=每包 &lt;NS&gt;_&lt;INF&gt;（addonkit 范式）。</summary>
        public string DriverFeatureGroup { get => _driverFeatureGroup; set { _driverFeatureGroup = value; OnPropertyChanged(); } }

        /// <summary>驱动扫描预览行（Step4 列表可见：包名/FeatureID/INF 数/目录）。</summary>
        public ObservableCollection<DriverPreviewRow> DriverPreview { get; } = new();
        public string DriverPreviewSummary => DriverPreview.Count == 0
            ? "尚未扫描。选择驱动源目录后点「扫描预览」。"
            : $"共 {DriverPreview.Count} 个驱动包，{DriverPreview.Sum(r => r.InfCount)} 个 INF";

        public void ScanDriversPreview()
        {
            DriverPreview.Clear();
            var ns = string.IsNullOrWhiteSpace(DriverNamespace) ? "Drivers" : DriverNamespace.Trim();
            foreach (var p in Services.DriverPackageBuilder.Scan(DriverScanRoot, ns, DriverFeatureGroup.Trim()))
                DriverPreview.Add(new DriverPreviewRow
                {
                    OutputName = p.OutputName,
                    FeatureId = p.FeatureId,
                    InfCount = p.InfFiles.Count,
                    InfList = string.Join("、", p.InfFiles),
                    SourceDir = p.SourceDir
                });
            OnPropertyChanged(nameof(DriverPreviewSummary));
        }

        public void RemoveDriverPreviewAt(int i)
        {
            if (i < 0 || i >= DriverPreview.Count) return;
            DriverPreview.RemoveAt(i);
            OnPropertyChanged(nameof(DriverPreviewSummary));
        }

        // 最近一次布局打包结果（组件生成时并入统一 FM）
        string _lastLayoutPkg = "", _lastLayoutComponent = "", _lastLayoutFip = "", _lastLayoutSoc = "";

        /// <summary>生成所选 BSP 组件（可单独造，不依赖本次布局打包）。返回结果供日志展示。</summary>
        public async Task<Services.BspComponentResult> BuildComponentsAsync()
        {
            bool buildLayoutFirst = HasAnyBspComponent && string.IsNullOrEmpty(_lastLayoutPkg) && _validatedOk == true;
            if (buildLayoutFirst)
            {
                // 纯组件模式下若布局已验证过，先补一次布局打包以便统一 FM 引用
                var lr = await BuildAsync();
                if (lr.Success) RememberLayout(lr);
            }
            var req = new Services.BspComponentRequest
            {
                OutputDirectory = OutputDir,
                Architecture = Architecture.Trim(),
                Version = Version.Trim(),
                OwnerName = OwnerOfPackage(PackageId),
                DeviceName = DeviceName.Trim(),
                Partition = PartitionTarget.Trim(),
                Modern = IsModern,
                BuildDevicePlatform = WantDevicePlatform,
                BuildDeviceInfo = WantDeviceInfo,
                BuildMachineInfo = WantMachineInfo,
                BuildSmbios = WantSmbios,
                MinSectorCount = long.TryParse(MinSectorCount, out long msc) ? msc : 20971520,
                DevicePlatformIDs = DevicePlatformIds.Split(',', ';', ' ')
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
                BiosMode = BiosModeIndex == 0 ? "UEFI" : "",
                SocVersion = SocVersion.Trim(),
                HardwareVersion = HardwareVersion.Trim(),
                BspVersion = (Version.Split('.').Length >= 3 ? Version.Split('.')[2] + "." + Version.Split('.')[3] : Version).Trim(),
                DeviceManufacturer = string.IsNullOrWhiteSpace(DeviceManufacturer) ? OwnerOfPackage(PackageId) : DeviceManufacturer.Trim(),
                DeviceModel = string.IsNullOrWhiteSpace(DeviceModel) ? DeviceName.Trim() + " Device" : DeviceModel.Trim(),
                OemSoftwareVersion = Version.Trim(),
                MachineInfoManufacturer = string.IsNullOrWhiteSpace(MiManufacturer) ? OwnerOfPackage(PackageId) : MiManufacturer.Trim(),
                MachineInfoFamily = MiFamily.Trim(),
                MachineInfoProductName = string.IsNullOrWhiteSpace(MiProductName) ? DeviceName.Trim() : MiProductName.Trim(),
                MachineInfoSku = string.IsNullOrWhiteSpace(MiSku) ? "1" : MiSku.Trim(),
                SmbiosOemName = OwnerOfPackage(PackageId),
                SmbiosProduct = SmbiosProduct.Trim(),
                SmbiosSku = SmbiosSku.Trim(),
                SmbiosFamily = SmbiosFamily.Trim(),
                SmbiosBbProd = SmbiosBbProd.Trim(),
                DriverScanRoot = WantDrivers ? DriverScanRoot.Trim() : "",
                DriverRunPkgGen = DriverRunPkgGen,
                DriverNamespace = string.IsNullOrWhiteSpace(DriverNamespace) ? "Drivers" : DriverNamespace.Trim(),
                DriverFeatureGroup = DriverFeatureGroup.Trim(),
                LayoutPackageName = _lastLayoutPkg,
                LayoutComponentName = _lastLayoutComponent,
                LayoutFipName = _lastLayoutFip,
                LayoutSoc = _lastLayoutSoc,
                SigningPfx = SigningPfx,
            };
            return await Task.Run(() => Services.BspComponentBuilderService.BuildAll(req));
        }

        void RememberLayout(Services.DeviceLayoutBuildResult lr)
        {
            _lastLayoutSoc = Soc.Trim();
            if (IsModern)
            {
                _lastLayoutPkg = !string.IsNullOrWhiteSpace(lr.CabPath)
                    ? Path.GetFileNameWithoutExtension(lr.CabPath).Replace($"~{Services.DeviceLayoutBuilderService.TokenMicrosoft}~{Architecture}~~", "")
                    : "";
                _lastLayoutComponent = Services.DeviceLayoutBuilderService.StripPackageSuffix(_lastLayoutPkg);
                _lastLayoutFip = Services.DeviceLayoutBuilderService.DefaultFipName(PackageId.Trim(), Soc.Trim(), "");
            }
        }

        static string OwnerOfPackage(string pkg) =>
            (pkg ?? "").Split('-', '.').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "OEM";

        public async Task<DeviceLayoutBuildResult> BuildAsync()
        {
            var doc = CommitDoc();
            var req = new DeviceLayoutBuildRequest
            {
                Layout = doc,
                LayoutIsV2 = doc.RootTag == "MSDeviceLayout" || doc.Stores.Count > 0 || doc.StoragePools.Count > 0,
                LayoutSourceXml = _loadedTemplate != null && _sourceXmlPath != null && File.Exists(_sourceXmlPath)
                    ? _sourceXmlPath : null,
                Era = IsModern ? DeviceLayoutEra.FactoryOs : DeviceLayoutEra.Phone,
                PackageId = PackageId.Trim(), Version = Version.Trim(),
                Architecture = Architecture.Trim(), Soc = Soc.Trim(),
                Partition = PartitionTarget.Trim(), OutputDirectory = OutputDir,
                SigningPfx = SigningPfx,
            };
            return await Task.Run(() => DeviceLayoutBuilderService.Build(req));
        }

        // 手工编辑/导入/新建时模板绑定作废（除显式选规则）
        public void ClearTemplateBinding() { _loadedTemplate = null; _sourceXmlPath = null; _baseline.Clear(); }

        // ═════════ INPC ═════════
        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>Step4 驱动扫描预览行。</summary>
    public class DriverPreviewRow
    {
        public string OutputName { get; set; } = "";
        public string FeatureId { get; set; } = "";
        public int InfCount { get; set; }
        public string InfList { get; set; } = "";
        public string SourceDir { get; set; } = "";
        public string Display => $"{OutputName}   [{FeatureId}]   {InfCount} INF";
    }

    /// <summary>步进状态 → 步进条底色。</summary>
    public class StepStateToBrushConverter : IValueConverter
    {
        static SolidColorBrush Make(byte r, byte g, byte b)
        { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
        static readonly SolidColorBrush Done   = Make(0xD6, 0xEF, 0xDA);
        static readonly SolidColorBrush Cur    = Make(0xD8, 0xF6, 0xFE);
        static readonly SolidColorBrush Fail   = Make(0xFB, 0xDC, 0xDC);
        static readonly SolidColorBrush Locked = Make(0xEE, 0xEE, 0xEE);
        static readonly SolidColorBrush BandIn = Make(0xEE, 0xF5, 0xFC);
        static readonly SolidColorBrush BandOut= Make(0xF5, 0xF5, 0xF5);
        static readonly SolidColorBrush SelBr  = Make(0x00, 0x7A, 0xCC);
        static readonly SolidColorBrush NoBr   = Make(0, 0, 0);
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
        {
            string mode = p?.ToString() ?? "";
            if (mode == "band") return v is true ? BandIn : BandOut;
            if (mode == "sel")  return v is true ? SelBr  : NoBr;
            return v switch
            {
                StepState.Done => Done, StepState.Current => Cur,
                StepState.Failed => Fail, _ => Locked
            };
        }
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>步进状态 → 步进条图标。</summary>
    public class StepStateToTextConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v switch
        {
            StepState.Done => "✓", StepState.Failed => "✗", StepState.Current => "●", _ => "○"
        };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>宽度比例（0–1）→ 像素：条宽按容器绑定换算。</summary>
    public class RatioToWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type t, object p, System.Globalization.CultureInfo c)
        {
            double ratio = values[0] is double r ? r : 0;
            double total = values[1] is double w ? w : 0;
            if (total <= 0) return 28.0;
            return Math.Max(28, ratio * (total - 6));
        }
        public object[] ConvertBack(object v, Type[] t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
