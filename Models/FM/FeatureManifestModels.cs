using System.Collections.Generic;
using System.Xml.Serialization;

namespace OneCore_Image_Designer.Models.FM
{
    /// <summary>
    /// Feature Manifest (FM) 根元素
    /// </summary>
    [XmlRoot("FeatureManifest", Namespace = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate")]
    public class FeatureManifest
    {
        [XmlAttribute("Revision")]
        public string Revision { get; set; } = string.Empty;

        [XmlAttribute("SchemaVersion")]
        public string SchemaVersion { get; set; } = string.Empty;

        [XmlAttribute("ReleaseType")]
        public string ReleaseType { get; set; } = string.Empty;

        [XmlAttribute("OwnerType")]
        public string OwnerType { get; set; } = string.Empty;

        [XmlAttribute("OwnerName")]
        public string OwnerName { get; set; } = string.Empty;

        [XmlAttribute("OSVersion")]
        public string OSVersion { get; set; } = string.Empty;

        [XmlAttribute("BuildInfo")]
        public string BuildInfo { get; set; } = string.Empty;

        [XmlArray("BasePackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> BasePackages { get; set; } = new();

        [XmlElement("Features")]
        public FeaturesSection Features { get; set; } = new();

        [XmlArray("ReleasePackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> ReleasePackages { get; set; } = new();

        /// <summary>
        /// 设备平台包（含Device属性，用于识别Device字段）
        /// </summary>
        [XmlArray("OEMDevicePlatformPackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> OEMDevicePlatformPackages { get; set; } = new();

        /// <summary>
        /// 设备布局包（含SOC属性和CAB路径，用于识别设备布局）
        /// </summary>
        [XmlArray("DeviceLayoutPackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> DeviceLayoutPackages { get; set; } = new();

        /// <summary>
        /// SOC 包（旧格式 Phone FM 里承载设备布局）。当 FM 没有 DeviceLayoutPackages 时，
        /// 官方 FeatureManifest.GetDeviceLayoutPackages 的回退是取 SOCPackages 中
        /// FeatureIdentifierPackage=true 的项，其 SOC 属性即布局类型。
        /// </summary>
        [XmlArray("SOCPackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> SOCPackages { get; set; } = new();

        /// <summary>
        /// 设备特定包（含Device属性）
        /// </summary>
        [XmlArray("DeviceSpecificPackages")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> DeviceSpecificPackages { get; set; } = new();

        /// <summary>
        /// FM文件路径（运行时填充，不序列化）
        /// </summary>
        [XmlIgnore]
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// FM文件名（运行时填充）
        /// </summary>
        [XmlIgnore]
        public string FileName => System.IO.Path.GetFileName(FilePath);
    }

    public class FeaturesSection
    {
        [XmlArray("Microsoft")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> MicrosoftPackages { get; set; } = new();

        [XmlArray("OEM")]
        [XmlArrayItem("PackageFile")]
        public List<PackageFile> OEMPackages { get; set; } = new();

        [XmlArray("MSFeatureGroups")]
        [XmlArrayItem("FeatureGroup")]
        public List<FeatureGroup> MSFeatureGroups { get; set; } = new();

        [XmlArray("OEMFeatureGroups")]
        [XmlArrayItem("FeatureGroup")]
        public List<FeatureGroup> OEMFeatureGroups { get; set; } = new();
    }

    public class PackageFile
    {
        [XmlAttribute("Path")]
        public string Path { get; set; } = string.Empty;

        [XmlAttribute("ID")]
        public string ID { get; set; } = string.Empty;

        [XmlAttribute("Name")]
        public string Name { get; set; } = string.Empty;

        [XmlAttribute("Language")]
        public string Language { get; set; } = string.Empty;

        [XmlAttribute("Wow")]
        public string Wow { get; set; } = string.Empty;

        [XmlAttribute("CPUType")]
        public string CPUType { get; set; } = string.Empty;

        [XmlAttribute("ReleaseType")]
        public string ReleaseType { get; set; } = string.Empty;

        [XmlAttribute("FeatureIdentifierPackage")]
        public string FeatureIdentifierPackage { get; set; } = string.Empty;

        /// <summary>
        /// SOC标识（仅在DeviceLayoutPackages中使用，如GPT、GPT_SPACES_4096）
        /// </summary>
        [XmlAttribute("SOC")]
        public string SOC { get; set; } = string.Empty;

        /// <summary>
        /// SV标识（Silicon Vendor，如QCOM、INTEL）
        /// </summary>
        [XmlAttribute("SV")]
        public string SV { get; set; } = string.Empty;

        /// <summary>
        /// Device标识（如MobileDeviceSpace、QC8998）
        /// </summary>
        [XmlAttribute("Device")]
        public string Device { get; set; } = string.Empty;

        [XmlArray("FeatureIDs")]
        [XmlArrayItem("FeatureID")]
        public List<string> FeatureIDs { get; set; } = new();
    }

    public class FeatureGroup
    {
        [XmlAttribute("Name")]
        public string Name { get; set; } = string.Empty;

        [XmlAttribute("Constraint")]
        public string Constraint { get; set; } = string.Empty;

        /// <summary>官方 PublishingFeatureGroup：发布阶段用的组，构建期一般不参与互斥裁剪。</summary>
        [XmlAttribute("PublishingFeatureGroup")]
        public bool PublishingFeatureGroup { get; set; }

        [XmlArray("FeatureIDs")]
        [XmlArrayItem("FeatureID")]
        public List<string> FeatureIDs { get; set; } = new();

        /// <summary>官方支持的嵌套子组（FeatureGroup 递归）。</summary>
        [XmlArray("SubGroups")]
        [XmlArrayItem("FeatureGroup")]
        public List<FeatureGroup> SubGroups { get; set; } = new();

        /// <summary>约束类型枚举</summary>
        [XmlIgnore]
        public FeatureGroupConstraint ConstraintType
        {
            get
            {
                return Constraint switch
                {
                    "OneOrMore" => FeatureGroupConstraint.OneOrMore,
                    "ZeroOrOne" => FeatureGroupConstraint.ZeroOrOne,
                    "OneAndOnlyOne" => FeatureGroupConstraint.OneAndOnlyOne,
                    _ => FeatureGroupConstraint.None
                };
            }
        }

        /// <summary>自身 + 全部后代子组的 FeatureID 扁平集合（对齐官方 AllFeatureIDs）。</summary>
        [XmlIgnore]
        public List<string> AllFeatureIDs
        {
            get
            {
                var list = new List<string>();
                if (FeatureIDs != null) list.AddRange(FeatureIDs);
                if (SubGroups != null)
                    foreach (var sub in SubGroups)
                        list.AddRange(sub.AllFeatureIDs);
                return list;
            }
        }
    }

    public enum FeatureGroupConstraint
    {
        /// <summary>无约束（官方默认：Constraint 属性缺省或无法识别）</summary>
        None,
        /// <summary>组内（含后代）至少选一个</summary>
        OneOrMore,
        /// <summary>
        /// 组内最多选一个（互斥）
        /// </summary>
        ZeroOrOne,
        /// <summary>
        /// 组内必须且只能选一个
        /// </summary>
        OneAndOnlyOne
    }
}
