using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OneCore_Image_Designer.Models
{
    /// <summary>
    /// OEMInput 的 schema 代际。判定依据是根元素命名空间（官方 OEMInputFactory 即如此），
    /// 二者字段集不同，选错代际会产出通不过 imageapp XSD 校验的文件。
    /// </summary>
    public enum OemInputSchemaVersion
    {
        /// <summary>
        /// 2004/10：SOC + SupportedLanguages 三分组 + BootUILanguage/BootLocale + Resolutions。
        /// 适用 Phone/Andromeda/FactoryOS（15032~18941 时代），本机全部手写真实样本均为此形态。
        /// </summary>
        V1_2004_10,

        /// <summary>
        /// 2019/06：DeviceLayoutType（取代 SOC）+ 扁平 Languages(Default)。
        /// 不输出 BootUILanguage/BootLocale —— 官方 V2 里它们是"Default 语言"的计算属性，
        /// 写进 XML 反而通不过 V2 XSD。适用 ModernPC / WCOS CDG（18963 起）。
        /// </summary>
        V2_2019_06
    }

    /// <summary>
    /// OEMInput 设计器的可编辑视图模型（MVVM 模型层，不直接参与 XML 序列化）。
    ///
    /// 序列化由 OEMInputService 按 <see cref="SchemaVersion"/> 映射到 V1 / V2
    /// 两个各自带固定命名空间的 DTO 完成——字段集与命名空间在类型层面就不可能错配。
    /// （旧实现用单一类声明 2019/06 却输出 SOC 等 V1 字段，生成的文件对 V1/V2
    /// 任一 XSD 都不合法，这就是"问题不少"里最致命的一条。）
    ///
    /// 绑定兼容：Description/Product/SOC/SV/Device/ReleaseType/BuildType/Languages/
    /// BootUILanguage/BootLocale/AdditionalFMs/Features/AppXOptionalPackages 等
    /// 既有属性名与类型均保持不变；V2 下 SOC 的值写到 &lt;DeviceLayoutType&gt;
    /// 元素（也可用 <see cref="DeviceLayoutType"/> 别名读写）。
    /// </summary>
    public class OEMInputDoc : INotifyPropertyChanged
    {
        public const string NamespaceV1 = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";
        public const string NamespaceV2 = "http://schemas.microsoft.com/embedded/2019/06/ImageUpdate";
        public const string DefaultProductName = "WindowsCore";

        private OemInputSchemaVersion _schemaVersion = OemInputSchemaVersion.V1_2004_10;

        /// <summary>
        /// 目标 schema 代际。加载文件时按根命名空间自动探测；用户可在向导中覆盖。
        /// 切换会通知依赖它的视图（V1/V2 显示不同字段区）。
        /// </summary>
        public OemInputSchemaVersion SchemaVersion
        {
            get => _schemaVersion;
            set
            {
                if (_schemaVersion == value) return;
                _schemaVersion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSchemaV1));
                OnPropertyChanged(nameof(IsSchemaV2));
                OnPropertyChanged(nameof(DeviceLayoutLabel));
            }
        }

        public bool IsSchemaV1 => SchemaVersion == OemInputSchemaVersion.V1_2004_10;
        public bool IsSchemaV2 => SchemaVersion == OemInputSchemaVersion.V2_2019_06;

        /// <summary>UI 文案：V1 显示 SOC，V2 显示 DeviceLayoutType（值同源）。</summary>
        public string DeviceLayoutLabel => IsSchemaV2 ? "DeviceLayoutType" : "SOC (DeviceLayoutType)";

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        private string _product = string.Empty;
        /// <summary>留空时序列化按官方行为写默认值 WindowsCore。</summary>
        public string Product
        {
            get => _product;
            set { _product = value; OnPropertyChanged(); }
        }

        private string _soc = string.Empty;
        /// <summary>V1 输出为 &lt;SOC&gt;；V2 输出为 &lt;DeviceLayoutType&gt;。同一份值。</summary>
        public string SOC
        {
            get => _soc;
            set { _soc = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeviceLayoutType)); }
        }

        /// <summary>SOC 的 V2 别名（DeviceLayoutType 就是 SOC 换名，官方 OEMInput.cs:311 直接赋值）。</summary>
        public string DeviceLayoutType
        {
            get => _soc;
            set { SOC = value; }
        }

        private string _sv = string.Empty;
        /// <summary>可选：V1/V2 均支持；空则不写。</summary>
        public string SV
        {
            get => _sv;
            set { _sv = value; OnPropertyChanged(); }
        }

        private string _device = string.Empty;
        public string Device
        {
            get => _device;
            set { _device = value; OnPropertyChanged(); }
        }

        private string _releaseType = "Test";
        /// <summary>XSD 枚举：仅 Test / Production（首字母大写）。</summary>
        public string ReleaseType
        {
            get => _releaseType;
            set
            {
                _releaseType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTestRelease));
            }
        }

        /// <summary>TestContent 仅在此为 true 时可填（官方规则）。</summary>
        public bool IsTestRelease => _releaseType == "Test";

        private string _buildType = "fre";
        /// <summary>XSD 枚举：仅 fre / chk / %BUILDTYPE%。</summary>
        public string BuildType
        {
            get => _buildType;
            set { _buildType = value; OnPropertyChanged(); }
        }

        // ── V2：扁平语言表（<Language Default="true">en-US</Language>）──
        public LanguagesSection Languages { get; set; } = new();

        // ── V1：三分组语言（UserInterface/Keyboard/Speech）──
        public SupportedLanguagesSection SupportedLanguages { get; set; } = new();

        private string _bootUILanguage = "en-us";
        /// <summary>仅 V1 写入 XML；V2 中它等价于"Default=true 的那个语言"（计算值，不落盘）。</summary>
        public string BootUILanguage
        {
            get => _bootUILanguage;
            set { _bootUILanguage = value; OnPropertyChanged(); }
        }

        private string _bootLocale = "en-us";
        /// <summary>仅 V1 写入 XML。</summary>
        public string BootLocale
        {
            get => _bootLocale;
            set { _bootLocale = value; OnPropertyChanged(); }
        }

        /// <summary>仅 V1 schema 含此元素（如 16202 手写样本的 1440x2560）。</summary>
        public List<string> Resolutions { get; set; } = new();

        // ── 两代共用 ──
        public List<string> AdditionalFMs { get; set; } = new();

        public FeaturesSection Features { get; set; } = new();

        public List<string> AppXOptionalPackages { get; set; } = new();

        /// <summary>
        /// AppX 列表为空时是否仍输出 &lt;AppXOptionalPackages&gt; 空元素。
        /// 实证：18272+ 的 XSD 要求该元素至少含 1 个 AppXID，空元素通不过校验，
        /// 故默认 false；imageapp 自家处理产物确有空的，保留开关以兼容旧习惯。
        /// </summary>
        public bool KeepEmptyAppXSection { get; set; } = false;

        /// <summary>OEM 区为空时是否输出 &lt;OEM /&gt;（V2 真实样本 20274 存在此空元素）。</summary>
        public bool KeepEmptyOemSection { get; set; } = false;

        /// <summary>官方为 Feature 字符串列表（旧模型误作 string，无任何消费方，已纠正）。</summary>
        public List<string> UserInstallableFeatures { get; set; } = new();
        public List<string> AdditionalSKUs { get; set; } = new();
        public List<string> InternalOptionalFeatures { get; set; } = new();
        public List<string> ProductionOptionalFeatures { get; set; } = new();
        public List<string> MSOptionalFeatures { get; set; } = new();

        /// <summary>仅 ReleaseType=Test 时允许输出（官方校验规则）。</summary>
        public string TestContent { get; set; } = string.Empty;

        public string MachineInfoFile { get; set; } = string.Empty;

        /// <summary>仅 V2 schema 有。</summary>
        public string RecoveryPackagePath { get; set; } = string.Empty;

        /// <summary>仅 V2 schema 有。</summary>
        public string ImageVersionString { get; set; } = string.Empty;

        /// <summary>仅 V2 schema 有。true 时官方语义对应 REPLACEMENT 处理。</summary>
        public bool ExcludePrereleaseFeatures { get; set; } = false;

        /// <summary>仅 V2 schema 有。</summary>
        public List<string> DevicePlatformIDs { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>V2 的扁平语言区。</summary>
    public class LanguagesSection
    {
        public List<LanguageItem> Languages { get; set; } = new();
    }

    public class LanguageItem
    {
        /// <summary>XSD 布尔是字符串枚举 "true"/"false"，保持 string 以兼容旧绑定。</summary>
        public string Default { get; set; } = "false";

        public string Code { get; set; } = string.Empty;

        public bool IsDefault
        {
            get => Default == "true";
            set => Default = value ? "true" : "false";
        }
    }

    /// <summary>V1 的 SupportedLanguages：UI/键盘/语音三组，各组都是 Language 子元素。</summary>
    public class SupportedLanguagesSection
    {
        public List<string> UserInterface { get; set; } = new();
        public List<string> Keyboard { get; set; } = new();
        public List<string> Speech { get; set; } = new();
    }

    public class FeaturesSection
    {
        public List<string> MicrosoftFeatures { get; set; } = new();
        public List<string> OEMFeatures { get; set; } = new();
    }
}
