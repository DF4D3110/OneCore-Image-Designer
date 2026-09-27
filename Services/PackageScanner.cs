using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>步骤1确认页的一行候选 FM（可勾选）。</summary>
    public class DiscoveredFmItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private readonly DiscoveredFm _src;
        public DiscoveredFmItem(DiscoveredFm src, bool selected) { _src = src; _isSelected = selected; }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        public string DisplayName => _src.DisplayName;
        public string SourceKind => _src.FromCab ? "CAB" : "XML";
        public string XmlPath => _src.XmlPath;
        public string ContainingCab => _src.ContainingCab;
        public bool FromCab => _src.FromCab;
        public string FeatureCountText => _src.FeatureCount > 0 ? $"{_src.FeatureCount} Features" : "";
    }

    /// <summary>扫描发现的 FM 候选（只到“文件在哪”，实际解析交给 FMService）。</summary>
    public class DiscoveredFm
    {
        public string DisplayName = "";   // 展示名（文件名/包名）
        public string XmlPath = "";       // 磁盘上可直接喂给 FMService.ParseFM 的 XML（散装或已从 CAB 抽出）
        public string SourceKind = "Xml"; // Xml | Cab
        public string ContainingCab = ""; // Cab 来源时的 CAB 路径（仅展示/追溯）
        public int FeatureCount;          // 轻量统计（仅展示，不做强解析）
        public bool FromCab => SourceKind == "Cab";
    }

    /// <summary>包根扫描结果。</summary>
    public class PackageScanResult
    {
        public List<string> Architectures = new();   // arm / arm64 / amd64 / x86
        public List<string> BuildTypes = new();      // fre / checked
        public List<DiscoveredFm> Fms = new();
        public List<string> Errors = new();
        public int CabFileCount;
    }

    /// <summary>
    /// MSPackageRoot 自动发现（对齐官方行为 + 14355/17047/20274 样本实证）：
    /// 找 FM = FMFiles 下散装 *.xml + MSPackages 下 *FM*.cab 内解出的 FeatureManifest XML。
    /// 按内部 PlatformID/根名去重，散装优先；解析（含 DeviceLayoutPackages/SOCPackages 回退、
    /// Feature 清单）复用 FMService，不在此重复实现。
    /// CAB 抽出的 XML 落到调用方持有的临时目录（Scan 返回值携带，供生命周期管理）。
    /// </summary>
    public static class PackageScanner
    {
        /// <summary>扫描包根，返回候选 FM（含已抽出的 CAB 内 XML）。extractTempDir 用于放置从 CAB 抽出的临时 XML。</summary>
        public static PackageScanResult Scan(string root, string extractTempDir)
        {
            var r = new PackageScanResult();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                r.Errors.Add("目录不存在");
                return r;
            }

            foreach (var arch in new[] { "arm64", "arm", "amd64", "x86" })
                if (HasDirLike(root, arch)) r.Architectures.Add(arch);
            foreach (var bt in new[] { "fre", "checked" })
                if (HasDirLike(root, bt)) r.BuildTypes.Add(bt);

            // ── 散装 FM XML：只认 FMFiles 目录下的 xml（避免把 DeviceLayout/OEMInput 当 FM）──
            var loose = EnumerateFiles(root, "*.xml")
                .Where(f => f.Contains("\\FMFiles\\", StringComparison.OrdinalIgnoreCase)
                         || f.Contains("/FMFiles/", StringComparison.OrdinalIgnoreCase)
                         || f.Contains("MergedFMCache", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var f in loose)
            {
                if (!ProbeFm(f, out int fc)) continue;
                r.Fms.Add(new DiscoveredFm
                {
                    DisplayName = Path.GetFileNameWithoutExtension(f),
                    XmlPath = f,
                    SourceKind = "Xml",
                    FeatureCount = fc,
                });
            }

            // ── CAB 内 FM：名字含 FM 且不是 DEVICELAYOUT 的 cab，抽其中的 *.xml ──
            var cabs = EnumerateFiles(root, "*.cab").ToList();
            r.CabFileCount = cabs.Count;
            foreach (var cab in cabs)
            {
                string bn = Path.GetFileName(cab);
                if (bn.Contains("DEVICELAYOUT", StringComparison.OrdinalIgnoreCase)) continue;
                if (!bn.Contains("FM", StringComparison.OrdinalIgnoreCase)
                    && !bn.Contains("FeatureManifest", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var (entry, bytes) in CabHelper.EnumerateXmlEntries(cab, r.Errors))
                {
                    if (!LooksLikeFeatureManifestBytes(bytes, out int fc)) continue;
                    string outName = Sanitize(Path.GetFileNameWithoutExtension(cab) + "__" + Path.GetFileNameWithoutExtension(entry)) + ".xml";
                    string outPath = Path.Combine(extractTempDir, outName);
                    try
                    {
                        Directory.CreateDirectory(extractTempDir);
                        File.WriteAllBytes(outPath, bytes);
                    }
                    catch (Exception ex) { r.Errors.Add($"抽取 CAB 失败 {bn}\\{entry}: {ex.Message}"); continue; }

                    r.Fms.Add(new DiscoveredFm
                    {
                        DisplayName = Path.GetFileNameWithoutExtension(entry) + "  (" + bn + ")",
                        XmlPath = outPath,
                        SourceKind = "Cab",
                        ContainingCab = cab,
                        FeatureCount = fc,
                    });
                }
            }

            // ── 去重：同一 FM 名（散装 vs CAB）→ 保留散装；同名保留首个 ──
            r.Fms = r.Fms
                .GroupBy(x => CoreName(x), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => !x.FromCab).First())
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return r;
        }

        static string CoreName(DiscoveredFm fm)
        {
            string s = fm.FromCab
                ? Path.GetFileNameWithoutExtension(fm.XmlPath).Split(new[] { "__" }, StringSplitOptions.None).Last()
                : Path.GetFileNameWithoutExtension(fm.XmlPath);
            return s;
        }

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }

        static bool HasDirLike(string root, string token)
        {
            try
            {
                return Directory.EnumerateDirectories(root, "*" + token + "*", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(root, "*" + token + "*", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        static IEnumerable<string> EnumerateFiles(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\WinSxS\\", StringComparison.OrdinalIgnoreCase));
            }
            catch { return Enumerable.Empty<string>(); }
        }

        static bool LooksLikeFeatureManifest(string path) => ProbeFm(path, out _);

        /// <summary>判定根节点是否 FeatureManifest，并顺手统计 FeatureID 数（-1=未统计）。</summary>
        static bool ProbeFm(string path, out int featureCount)
        {
            featureCount = 0;
            try
            {
                using var fs = File.OpenRead(path);
                return ProbeFm(fs, out featureCount);
            }
            catch { return false; }
        }

        static bool LooksLikeFeatureManifestBytes(byte[] bytes, out int featureCount)
        {
            try { using var ms = new MemoryStream(bytes); return ProbeFm(ms, out featureCount); }
            catch { featureCount = 0; return false; }
        }

        static bool ProbeFm(Stream s, out int featureCount)
        {
            featureCount = 0;
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit };
            using var xr = System.Xml.XmlReader.Create(s, settings);
            bool rootOk = false;
            while (xr.Read())
            {
                if (!rootOk)
                {
                    if (xr.NodeType != System.Xml.XmlNodeType.Element) continue;
                    if (xr.LocalName != "FeatureManifest") return false;
                    rootOk = true;
                    continue;
                }
                if (xr.NodeType == System.Xml.XmlNodeType.Element && xr.LocalName == "FeatureID")
                    featureCount++;
            }
            return rootOk;
        }
    }
}
