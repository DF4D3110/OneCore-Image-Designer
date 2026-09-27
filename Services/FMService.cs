using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using OneCore_Image_Designer.Models.FM;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// FM (Feature Manifest) 解析服务
    /// </summary>
    public class FMService
    {
        private static readonly XmlSerializer _serializer = new(typeof(FeatureManifest));

        /// <summary>
        /// 解析单个FM文件
        /// </summary>
        public static FeatureManifest? ParseFM(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var fm = (FeatureManifest?)_serializer.Deserialize(stream);
                if (fm != null)
                {
                    fm.FilePath = filePath;
                }
                return fm;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析FM失败: {filePath}, 错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 批量解析FM文件
        /// </summary>
        public static List<FeatureManifest> ParseFMs(IEnumerable<string> filePaths)
        {
            var result = new List<FeatureManifest>();
            foreach (var path in filePaths)
            {
                var fm = ParseFM(path);
                if (fm != null)
                    result.Add(fm);
            }
            return result;
        }

        /// <summary>
        /// 从FM列表中提取所有FeatureID（去重）
        /// </summary>
        public static List<FeatureInfo> ExtractAllFeatures(List<FeatureManifest> fms)
        {
            var featureDict = new Dictionary<string, FeatureInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var fm in fms)
            {
                // Microsoft Features
                foreach (var pkg in fm.Features.MicrosoftPackages)
                {
                    foreach (var fid in pkg.FeatureIDs)
                    {
                        AddFeature(featureDict, fid, "Microsoft", fm.FileName, pkg.ID);
                    }
                }

                // OEM Features
                foreach (var pkg in fm.Features.OEMPackages)
                {
                    foreach (var fid in pkg.FeatureIDs)
                    {
                        AddFeature(featureDict, fid, "OEM", fm.FileName, pkg.ID);
                    }
                }
            }

            return featureDict.Values.OrderBy(f => f.Category).ThenBy(f => f.ID).ToList();
        }

        private static void AddFeature(Dictionary<string, FeatureInfo> dict, string featureId, string category, string fmFile, string packageId)
        {
            if (string.IsNullOrWhiteSpace(featureId))
                return;

            if (!dict.TryGetValue(featureId, out var info))
            {
                info = new FeatureInfo
                {
                    ID = featureId,
                    Category = category
                };
                dict[featureId] = info;
            }
            info.SourceFMs.Add(fmFile);
            info.SourcePackages.Add(packageId);
        }

        /// <summary>
        /// 从FM列表中提取所有FeatureGroup（冲突约束）
        /// </summary>
        public static List<FeatureGroupInfo> ExtractFeatureGroups(List<FeatureManifest> fms)
        {
            var groups = new List<FeatureGroupInfo>();

            foreach (var fm in fms)
            {
                CollectGroups(fm.Features.MSFeatureGroups, "MS", fm.FileName, groups);
                CollectGroups(fm.Features.OEMFeatureGroups, "OEM", fm.FileName, groups);
            }

            return groups;
        }

        /// <summary>
        /// 递归收集 FeatureGroup 及其 SubGroups。官方把每个（子）组当作独立约束分别校验，
        /// 这里同样把嵌套子组拉平成各自一条约束记录；父组自身保留 AllFeatureIDs（含后代）
        /// 以便按官方 AllFeatureIDs 口径统计"组内选中数"。
        /// </summary>
        private static void CollectGroups(List<FeatureGroup> list, string prefix, string sourceFM, List<FeatureGroupInfo> output)
        {
            if (list == null) return;

            foreach (var fg in list)
            {
                output.Add(new FeatureGroupInfo
                {
                    Name = string.IsNullOrEmpty(fg.Name) ? $"{prefix}_{fg.FeatureIDs.FirstOrDefault() ?? "Group"}_Group" : fg.Name,
                    Constraint = fg.Constraint,
                    ConstraintType = fg.ConstraintType,
                    // 组内自身成员 + 全部后代成员 = 官方 AllFeatureIDs，用于约束计数
                    FeatureIDs = new List<string>(fg.AllFeatureIDs),
                    // 仅本层直属成员，用于"用户能勾到的是哪些"展示与冲突配对
                    DirectFeatureIDs = new List<string>(fg.FeatureIDs ?? new List<string>()),
                    IsPublishingGroup = fg.PublishingFeatureGroup,
                    HasSubGroups = fg.SubGroups != null && fg.SubGroups.Count > 0,
                    SourceFM = sourceFM
                });

                CollectGroups(fg.SubGroups, prefix, sourceFM, output);
            }
        }

        /// <summary>
        /// 从FM中提取可用语言（从PackageFile的Language属性）
        /// </summary>
        public static List<string> ExtractAvailableLanguages(List<FeatureManifest> fms)
        {
            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fm in fms)
            {
                foreach (var pkg in fm.Features.MicrosoftPackages.Concat(fm.Features.OEMPackages).Concat(fm.BasePackages))
                {
                    if (!string.IsNullOrEmpty(pkg.Language) && pkg.Language != "*")
                    {
                        languages.Add(pkg.Language.ToLowerInvariant());
                    }
                }
            }

            return languages.OrderBy(l => l).ToList();
        }

        /// <summary>
        /// 从FM文件名推断Product名称
        /// </summary>
        public static string InferProductName(List<FeatureManifest> fms)
        {
            foreach (var fm in fms)
            {
                var name = Path.GetFileNameWithoutExtension(fm.FilePath);
                // 常见FM文件名模式
                if (name.Contains("Andromeda", StringComparison.OrdinalIgnoreCase))
                    return "Andromeda OS";
                if (name.Contains("ModernPC", StringComparison.OrdinalIgnoreCase))
                    return "Modern PC";
                if (name.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
                    return "Mobile";
                if (name.Contains("IoT", StringComparison.OrdinalIgnoreCase))
                    return "IoT Core";
                if (name.Contains("HoloLens", StringComparison.OrdinalIgnoreCase))
                    return "HoloLens";
                if (name.Contains("Team", StringComparison.OrdinalIgnoreCase))
                    return "Surface Hub";
            }
            return "Unknown";
        }

        /// <summary>
        /// 从FM中搜索DeviceLayout相关的包ID（旧方法，兼容BasePackages/Features中的引用）
        /// </summary>
        public static List<string> FindDeviceLayoutPackageIDs(List<FeatureManifest> fms)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fm in fms)
            {
                foreach (var pkg in fm.Features.MicrosoftPackages.Concat(fm.Features.OEMPackages).Concat(fm.BasePackages))
                {
                    if (pkg.ID.Contains("DeviceLayout", StringComparison.OrdinalIgnoreCase) ||
                        pkg.Name.Contains("DeviceLayout", StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(pkg.ID);
                    }
                }
            }

            return ids.ToList();
        }

        /// <summary>
        /// 从FM的DeviceLayoutPackages节点中提取设备布局包信息（新方法，支持SOC/Path/Name）
        /// 返回列表中每个元素包含: SOC, CAB路径(如果可解析), 包ID, 来源FM
        ///
        /// 官方回退链（FeatureManifest.GetDeviceLayoutPackages，实证自
        /// featureapi FeatureManifest.cs:1376-1396）：
        ///   ① 优先 DeviceLayoutPackages 节点的 SOC 属性；
        ///   ② 该 FM 没有此节点时，回退 SOCPackages 中 FeatureIdentifierPackage="true"
        ///      的包（旧 Phone FM 用 SOCPackages 承载布局，SOC 属性语义相同）。
        /// 注意 Path 可能含 "\?\C:\..." 长路径前缀或 "$(mspackageroot)" 占位符，
        /// 均无法直接命中文件，调用方需保留按文件名/ID 搜索的兜底。
        /// </summary>
        public static List<DeviceLayoutPackageInfo> FindDeviceLayoutPackages(List<FeatureManifest> fms, string packageRoot = "")
        {
            var result = new List<DeviceLayoutPackageInfo>();

            foreach (var fm in fms)
            {
                foreach (var pkg in fm.DeviceLayoutPackages)
                {
                    result.Add(NewLayoutInfo(pkg, fm, "DeviceLayoutPackages", packageRoot));
                }

                // 回退：无 DeviceLayoutPackages 时取 SOCPackages 的 FIP 项
                if (fm.DeviceLayoutPackages.Count == 0)
                {
                    foreach (var pkg in fm.SOCPackages)
                    {
                        bool isFip = IsFeatureIdentifier(pkg);
                        if (isFip && !string.IsNullOrEmpty(pkg.SOC))
                            result.Add(NewLayoutInfo(pkg, fm, "SOCPackages(FIP)", packageRoot));
                    }
                }
            }

            return result;
        }

        private static DeviceLayoutPackageInfo NewLayoutInfo(PackageFile pkg, FeatureManifest fm, string origin, string packageRoot)
            => new()
            {
                SOC = pkg.SOC,
                PackageID = pkg.ID,
                PackageName = pkg.Name,
                Path = pkg.Path,
                SourceFM = fm.FileName,
                Origin = origin,
                CabPath = ResolveCabPath(pkg, fm.FilePath, packageRoot)
            };

        /// <summary>FeatureIdentifierPackage="true"（大小写宽容，缺失即 false）。</summary>
        private static bool IsFeatureIdentifier(PackageFile pkg)
            => pkg.FeatureIdentifierPackage.Equals("true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 收集所有可用作 SOC / DeviceLayoutType 的候选值
        /// （DeviceLayoutPackages 与 SOCPackages 的 SOC 属性去重），供界面下拉选择。
        /// </summary>
        public static List<string> FindSocCandidateValues(List<FeatureManifest> fms)
        {
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fm in fms)
            {
                foreach (var pkg in fm.DeviceLayoutPackages.Concat(fm.SOCPackages))
                {
                    if (!string.IsNullOrEmpty(pkg.SOC) && seen.Add(pkg.SOC))
                        values.Add(pkg.SOC);
                }
            }

            values.Sort(StringComparer.OrdinalIgnoreCase);
            return values;
        }

        /// <summary>
        /// 从FM的OEMDevicePlatformPackages和DeviceSpecificPackages中提取Device标识
        /// </summary>
        public static List<string> FindDeviceNames(List<FeatureManifest> fms)
        {
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fm in fms)
            {
                foreach (var pkg in fm.OEMDevicePlatformPackages.Concat(fm.DeviceSpecificPackages))
                {
                    if (!string.IsNullOrEmpty(pkg.Device))
                        devices.Add(pkg.Device);
                }
            }

            return devices.ToList();
        }

        /// <summary>
        /// 解析PackageFile的CAB完整路径
        /// 优先级: 1) Path+Name  2) Path目录下搜索ID对应的CAB  3) 空
        /// </summary>
        private static string? ResolveCabPath(PackageFile pkg, string fmFilePath, string packageRoot = "")
        {
            // 先展开 FM 里的占位变量（$(mspackageroot) 等）。样本实证 $(mspackageroot) 指向 {root}\MSPackages
            string path = ExpandFmPath(pkg.Path, packageRoot);

            // 情况1: 有Path和Name
            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(pkg.Name))
            {
                var fullPath = System.IO.Path.Combine(path, pkg.Name);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            // 情况2: 有Path（目录），在目录下搜索包含ID的CAB
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                var searchPattern = $"*{pkg.ID}*.cab";
                try
                {
                    var found = Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (found != null)
                        return found;
                }
                catch { }
            }

            // 情况3: Path是相对路径，结合FM所在目录
            if (!string.IsNullOrEmpty(path) && !System.IO.Path.IsPathRooted(path) && !string.IsNullOrEmpty(fmFilePath))
            {
                var fmDir = System.IO.Path.GetDirectoryName(fmFilePath);
                if (!string.IsNullOrEmpty(fmDir))
                {
                    var combinedPath = System.IO.Path.Combine(fmDir, path);
                    if (!string.IsNullOrEmpty(pkg.Name))
                    {
                        var fullPath = System.IO.Path.Combine(combinedPath, pkg.Name);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                    if (Directory.Exists(combinedPath))
                    {
                        var searchPattern = $"*{pkg.ID}*.cab";
                        try
                        {
                            var found = Directory.GetFiles(combinedPath, searchPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (found != null)
                                return found;
                        }
                        catch { }
                    }
                }
            }

            // 情况4: 已知包根但上面都没命中——按包名在整棵包根里搜一次
            // （处理 FM 写的是异机绝对路径、或占位目录与实际布局不一致的情况）
            if (!string.IsNullOrEmpty(packageRoot) && Directory.Exists(packageRoot)
                && (!string.IsNullOrEmpty(pkg.Name) || !string.IsNullOrEmpty(pkg.ID)))
            {
                string pattern = !string.IsNullOrEmpty(pkg.Name)
                    ? pkg.Name
                    : $"*{pkg.ID}*.cab";
                try
                {
                    var found = Directory.EnumerateFiles(packageRoot, pattern, SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains("\\WinSxS\\", StringComparison.OrdinalIgnoreCase));
                    if (found != null) return found;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 从一组 FM 收集其引用的全部包 CAB（去重）。onlyLayout=true 时仅收集 DeviceLayoutPackages
        /// （含旧格式 SOCPackages 里的 FeatureIdentifierPackage）。复用 ResolveCabPath 的宏展开与四级兜底。
        /// </summary>
        public static List<string> CollectPackageCabs(IEnumerable<FeatureManifest> fms, string packageRoot = "", bool onlyLayout = false)
        {
            var outList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddFrom(PackageFile pkg, string fmPath)
            {
                if (pkg == null) return;
                string? cab = ResolveCabPath(pkg, fmPath, packageRoot);
                if (!string.IsNullOrEmpty(cab) && File.Exists(cab) && seen.Add(cab!))
                    outList.Add(cab!);
            }
            foreach (var fm in fms)
            {
                if (onlyLayout)
                {
                    foreach (var p in fm.DeviceLayoutPackages) AddFrom(p, fm.FilePath);
                    foreach (var p in fm.SOCPackages)
                        if (IsFeatureIdentifier(p)) AddFrom(p, fm.FilePath);
                    continue;
                }
                foreach (var p in fm.BasePackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.ReleasePackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.OEMDevicePlatformPackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.DeviceLayoutPackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.SOCPackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.DeviceSpecificPackages) AddFrom(p, fm.FilePath);
                foreach (var p in fm.Features?.MicrosoftPackages ?? new()) AddFrom(p, fm.FilePath);
                foreach (var p in fm.Features?.OEMPackages ?? new()) AddFrom(p, fm.FilePath);
            }
            return outList;
        }

        /// <summary>展开 FM 路径里的官方占位变量。$(mspackageroot)→{packageRoot}\MSPackages（14355 样本实证）。</summary>
        internal static string ExpandFmPath(string p, string packageRoot)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (!string.IsNullOrEmpty(packageRoot))
            {
                string msp = System.IO.Path.Combine(packageRoot, "MSPackages");
                p = p.Replace("$(mspackageroot)", msp, StringComparison.OrdinalIgnoreCase)
                     .Replace("$(MSPackageRoot)", msp)
                     .Replace("%(mspackageroot)%", msp, StringComparison.OrdinalIgnoreCase);
            }
            return p.Replace('\\', System.IO.Path.DirectorySeparatorChar)
                    .Replace('/', System.IO.Path.DirectorySeparatorChar);
        }
    }

    /// <summary>
    /// 设备布局包信息（从FM DeviceLayoutPackages / SOCPackages(FIP) 提取）
    /// </summary>
    public class DeviceLayoutPackageInfo
    {
        public string SOC { get; set; } = string.Empty;
        public string PackageID { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string SourceFM { get; set; } = string.Empty;
        /// <summary>来源节点：DeviceLayoutPackages / SOCPackages(FIP) / 自动搜索。</summary>
        public string Origin { get; set; } = string.Empty;
        public string? CabPath { get; set; }

        public bool HasCab => !string.IsNullOrEmpty(CabPath) && File.Exists(CabPath);
        public string Display => string.IsNullOrEmpty(SOC) ? PackageID : $"{SOC} ({PackageID})";
    }

    /// <summary>
    /// Feature信息（运行时使用）
    /// </summary>
    public class FeatureInfo
    {
        public string ID { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // Microsoft / OEM
        public bool IsSelected { get; set; }
        public List<string> SourceFMs { get; set; } = new();
        public List<string> SourcePackages { get; set; } = new();

        public string SourceFMDisplay => string.Join(", ", SourceFMs.Distinct());
    }

    /// <summary>
    /// FeatureGroup信息（冲突约束）
    /// </summary>
    public class FeatureGroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Constraint { get; set; } = string.Empty;
        public FeatureGroupConstraint ConstraintType { get; set; }
        /// <summary>组内全部成员（含嵌套子组），对齐官方 AllFeatureIDs，用于约束计数。</summary>
        public List<string> FeatureIDs { get; set; } = new();
        /// <summary>本层直属成员（不含子组）。</summary>
        public List<string> DirectFeatureIDs { get; set; } = new();
        /// <summary>官方 PublishingFeatureGroup：发布期约束，构建期不裁剪。</summary>
        public bool IsPublishingGroup { get; set; }
        /// <summary>是否含嵌套子组。</summary>
        public bool HasSubGroups { get; set; }
        public string SourceFM { get; set; } = string.Empty;
    }

    /// <summary>
    /// FeatureGroup 约束的匹配与评估（对齐官方 featureapi FMFeatureGrouping.ValidateConstraints）。
    /// </summary>
    public static class FeatureConstraintRules
    {
        /// <summary>
        /// 归一化 Feature ID：剥掉组引用侧的 MS_/OEM_ 命名前缀，使声明侧裸名（TEST）
        /// 能与组引用（MS_TEST）对上。实测声明侧从不含 MS_/OEM_ 前缀，组引用几乎全含，
        /// 不归一则互斥匹配命中率为 0（历史 bug 根因）。
        /// </summary>
        public static string Normalize(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return featureId;
            if (featureId.StartsWith("MS_", StringComparison.OrdinalIgnoreCase))
                return featureId.Substring(3);
            if (featureId.StartsWith("OEM_", StringComparison.OrdinalIgnoreCase))
                return featureId.Substring(4);
            return featureId;
        }

        /// <summary>组是否包含某 feature（归一后比较，忽略大小写）。</summary>
        public static bool GroupContains(FeatureGroupInfo group, string featureId)
        {
            string key = Normalize(featureId);
            foreach (var member in group.FeatureIDs)
                if (string.Equals(Normalize(member), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // 承载"全部可选 feature"的键集合，供欠选判定使用（VM 在解析/预览时设置）
        private static readonly HashSet<string> _allKnown = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>预览校验时注入当前全部可选 feature（归一键），用于欠选判定。</summary>
        public static void SetKnownFeatures(IEnumerable<string> allFeatureIds)
        {
            _allKnown.Clear();
            foreach (var id in allFeatureIds)
                _allKnown.Add(Normalize(id));
        }

        /// <summary>
        /// 完整评估：含欠选判定。仅当组的成员确实有可选项（present&gt;0）时才要求满足，
        /// 避免对引用了未加载 FM / 伪 feature（RELEASE_* 等）的组误报。
        /// </summary>
        public static string? EvaluateFull(FeatureGroupInfo group, ICollection<string> selectedIds)
        {
            if (group.ConstraintType == FeatureGroupConstraint.None) return null;

            var selected = new HashSet<string>(selectedIds.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var members = group.FeatureIDs.Select(Normalize).ToList();

            int present = members.Count(m => _allKnown.Contains(m));
            if (present == 0) return null;                 // 组内成员都不可选 → 不适用
            int chosen = members.Count(selected.Contains);

            switch (group.ConstraintType)
            {
                case FeatureGroupConstraint.ZeroOrOne:
                    return chosen > 1 ? $"组 \"{group.Name}\"（{group.SourceFM}）：最多选一个，当前 {chosen}" : null;
                case FeatureGroupConstraint.OneAndOnlyOne:
                    if (chosen > 1) return $"组 \"{group.Name}\"（{group.SourceFM}）：必须且只能选一个，当前 {chosen}";
                    if (chosen == 0) return $"组 \"{group.Name}\"（{group.SourceFM}）：必须选一个（可选：{string.Join(", ", members.Take(6))}…）";
                    return null;
                case FeatureGroupConstraint.OneOrMore:
                    if (chosen == 0) return $"组 \"{group.Name}\"（{group.SourceFM}）：至少选一个";
                    return null;
            }
            return null;
        }
    }
}
