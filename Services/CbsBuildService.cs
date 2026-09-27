using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OneCore_Image_Designer.Models.DeviceLayout;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// CBS 构建请求：一组包 CAB（通常来自一个 FeatureManifest 递归引用，或用户直接选）
    /// 展开为目录树。可选传入 DeviceLayout 以计算分区→磁盘映射（多盘才套 diskX 层）。
    /// </summary>
    public class CbsBuildRequest
    {
        /// <summary>要构建的包 .cab 绝对路径。</summary>
        public List<string> PackageCabs { get; set; } = new();
        /// <summary>输出根目录。</summary>
        public string OutputDirectory { get; set; } = "";
        /// <summary>可选：DeviceLayout.xml 绝对路径（用于分区→磁盘映射与多盘判定）。null=全部平铺到根。</summary>
        public string? LayoutXmlPath { get; set; }
        /// <summary>true 时保留每个 cab 的展开临时目录（调试）。</summary>
        public bool KeepTemp { get; set; }
    }

    public class CbsBuildResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int PackagesBuilt { get; set; }
        public int FilesWritten { get; set; }
        public int FilesMissing { get; set; }
        public int ManifestsToWinSxS { get; set; }
        public int MumsToServicing { get; set; }
        /// <summary>已成功写入的绝对路径集合（跨包防同名静默覆盖用）。</summary>
        public HashSet<string> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Log { get; } = new();
    }

    /// <summary>
    /// CBS 构建：读每个包 update.mum 的 &lt;file name=… cabpath=…&gt;，把 cab 内文件按其声明的
    /// 目标绝对路径落进输出树（含 WinSxS 存储）。规则全部来自真实包实证：
    ///  · name 是裸名 *.manifest（无目录分隔）→ Windows\WinSxS\Manifests\{裸名}
    ///  · name = \update.mum（或 *.mum）→ Windows\servicing\Packages\{包FQN}.mum
    ///  · name 以 \ 开头 → 去掉前导 \ 直接作为分区内绝对路径（cat 落 catroot 即此类，不进 WinSxS）
    ///  · name 含 $(runtime.*) 宏 → 宏展开后作为分区内路径（bootdrive 宏 → 分区根）
    /// 多盘/多分区时用 DeviceLayout 把 targetPartition 映射到 disk{D}\Partition{N}-{名}\ 前缀；
    /// 单盘（或无布局）则平铺，与现有 FFU 提取的命名约定一致。
    /// </summary>
    public static class CbsBuildService
    {
        // mum 命名空间（asm.v3）。用 LocalName 匹配，命名空间不敏感，稳。
        static XNamespace NONE => XNamespace.None;

        public static CbsBuildResult Build(CbsBuildRequest req)
        {
            var r = new CbsBuildResult();
            try
            {
                if (req.PackageCabs.Count == 0) throw new InvalidOperationException("没有要构建的包 CAB");
                if (string.IsNullOrWhiteSpace(req.OutputDirectory)) throw new InvalidOperationException("未选择输出目录");

                // 分区名 → (diskIdx, partIdx, partName)。来自 DeviceLayout；无则 null（平铺）。
                var partMap = req.LayoutXmlPath != null && File.Exists(req.LayoutXmlPath)
                    ? BuildPartitionMap(req.LayoutXmlPath, r.Log)
                    : null;
                bool multiDisk = partMap != null && partMap.Values.Select(v => v.Disk).Distinct().Count() > 1;
                bool multiPart = partMap != null && partMap.Count > 1;
                bool nestByDisk = partMap != null && (multiDisk || multiPart);
                r.Log.Add(nestByDisk
                    ? $"按盘/分区组织输出（{partMap!.Count} 个分区，{(multiDisk ? "多磁盘" : "多分区")}）"
                    : "单盘/无布局 → 平铺到输出根（windows\\…）");

                Directory.CreateDirectory(req.OutputDirectory);
                string tempRoot = Path.Combine(Path.GetTempPath(), "cbsbuild_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempRoot);

                int ci = 0;
                foreach (var cab in req.PackageCabs)
                {
                    ci++;
                    if (!File.Exists(cab)) { r.Log.Add($"跳过不存在的 cab: {cab}"); continue; }
                    try { BuildOnePackage(cab, req, partMap, nestByDisk, tempRoot, ci, r); }
                    catch (Exception ex) { r.Log.Add($"包 {Path.GetFileName(cab)} 构建失败: {ex.Message}"); r.FilesMissing++; }
                }

                if (!req.KeepTemp) { try { Directory.Delete(tempRoot, true); } catch { } }
                r.Success = true;
                r.Log.Add($"完成：{r.PackagesBuilt} 个包，写文件 {r.FilesWritten}（WinSxS manifest {r.ManifestsToWinSxS}，servicing mum {r.MumsToServicing}），缺源 {r.FilesMissing}");
            }
            catch (Exception ex) { r.Error = ex.Message; r.Log.Add("错误: " + ex.Message); }
            return r;
        }

        static void BuildOnePackage(string cab, CbsBuildRequest req, Dictionary<string, (int Disk, int Part, string Name)>? partMap,
            bool nestByDisk, string tempRoot, int ci, CbsBuildResult r)
        {
            string exDir = Path.Combine(tempRoot, "p" + ci);
            Directory.CreateDirectory(exDir);
            // 展开整个 cab（复用 expand.exe，与 CabHelper 同路）
            if (!ExpandCab(cab, exDir, r.Log)) { r.FilesMissing++; return; }

            var mum = Directory.GetFiles(exDir, "*.mum", SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.GetFiles(exDir, "update.mum", SearchOption.AllDirectories).FirstOrDefault();
            if (mum == null) { r.Log.Add($"  包 {Path.GetFileName(cab)} 展开后找不到 update.mum"); r.FilesMissing++; return; }

            XDocument doc;
            try { doc = XDocument.Load(mum); }
            catch (Exception ex) { r.Log.Add($"  mum 解析失败 {Path.GetFileName(cab)}: {ex.Message}"); r.FilesMissing++; return; }

            var root = doc.Root!;
            var ai = root.Elements().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity");
            string pkgName = (string?)ai?.Attribute("name") ?? Path.GetFileNameWithoutExtension(cab);
            string pkgVer = (string?)ai?.Attribute("version") ?? "0.0.0.0";
            string pkgArch = (string?)ai?.Attribute("processorArchitecture") ?? "neutral";
            string pkgLang = (string?)ai?.Attribute("language") ?? "neutral";
            string pkgPkt = (string?)ai?.Attribute("publicKeyToken") ?? "628844477771337a";

            var pkgEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "package");
            string targetPartition = (string?)pkgEl?.Attribute("targetPartition") ?? "";

            string prefix = nestByDisk && partMap != null ? DiskPrefix(partMap, targetPartition) : "";

            // 展开目录里建两级索引：按文件名 → 绝对；按 none_尾串 → 绝对（宽容匹配版本段不符的 manifest）
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byTail = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(exDir, "*", SearchOption.AllDirectories))
            {
                string bn = Path.GetFileName(f);
                byName[bn] = f;
                int ni = bn.LastIndexOf("none_", StringComparison.OrdinalIgnoreCase);
                if (ni >= 0) byTail[bn.Substring(ni)] = f;   // none_<hash>.ext
            }

            int w = 0, miss = 0;
            foreach (var fe in root.Descendants().Where(e => e.Name.LocalName == "file"))
            {
                string name = (string?)fe.Attribute("name") ?? "";
                string cabpath = (string?)fe.Attribute("cabpath") ?? name;
                if (name.Length == 0) continue;
                string resolved = ResolveTargetPath(name, pkgName, pkgVer, pkgArch, pkgLang, pkgPkt);

                string? srcAbs = FindSource(exDir, cabpath, byName, byTail);
                if (srcAbs == null) { miss++; if (r.Log.Count < 60) r.Log.Add($"   [缺源] {name} ← {cabpath}"); continue; }

                string dstAbs = UniqueTarget(Path.Combine(req.OutputDirectory, prefix, resolved.TrimStart('\\', '/')), r.Written, pkgName);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
                    File.Copy(srcAbs, dstAbs, true);
                    r.Written.Add(dstAbs);
                    w++;
                    string sep = Path.DirectorySeparatorChar.ToString();
                    if (dstAbs.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)
                        && dstAbs.Contains("WinSxS" + sep + "Manifests", StringComparison.OrdinalIgnoreCase)) r.ManifestsToWinSxS++;
                    if (dstAbs.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)
                        && dstAbs.Contains("servicing" + sep + "Packages", StringComparison.OrdinalIgnoreCase)) r.MumsToServicing++;
                }
                catch (Exception ex) { miss++; if (w < 3) r.Log.Add("   写文件失败 " + resolved + ": " + ex.Message); }
            }

            r.PackagesBuilt++; r.FilesWritten += w; r.FilesMissing += miss;
            r.Log.Add($"  ✓ {pkgName}  分区='{targetPartition}'{prefix}  写 {w}{(miss > 0 ? " 缺 " + miss : "")}");
        }

        /// <summary>在展开目录里定位 cabpath：精确相对 → 按名 → 按 none_尾串（版本段不符容错）。</summary>
        static string? FindSource(string exDir, string cabpath, Dictionary<string, string> byName, Dictionary<string, string> byTail)
        {
            string rel = cabpath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string direct = Path.Combine(exDir, rel);
            if (File.Exists(direct)) return direct;

            string bn = Path.GetFileName(rel);
            if (byName.TryGetValue(bn, out var hit) && File.Exists(hit)) return hit;

            int ni = bn.LastIndexOf("none_", StringComparison.OrdinalIgnoreCase);
            if (ni >= 0 && byTail.TryGetValue(bn.Substring(ni), out var tailHit) && File.Exists(tailHit)) return tailHit;
            return null;
        }

        /// <summary>同名落位护栏：多包共享 update.cat 等通用名时，第二次起并入包 FQN 唯一化，避免静默覆盖。</summary>
        static string UniqueTarget(string dst, HashSet<string> written, string pkg)
        {
            if (!written.Contains(dst)) return dst;
            string dir = Path.GetDirectoryName(dst) ?? "";
            string id = Path.GetFileNameWithoutExtension(dst);
            string ext = Path.GetExtension(dst);
            return Path.Combine(dir, $"{id}~{pkg}{ext}");
        }

        /// <summary>
        /// 把 mum file 的 name 解析为分区内相对路径（不含盘符）。
        /// </summary>
        static string ResolveTargetPath(string name, string pkg, string ver, string arch, string lang, string pkt)
        {
            string bare = name.TrimStart('\\');
            string baseTail = Path.GetFileName(bare.Replace('\\', '/'));

            // mum：裸 update.mum 或完整 \Windows\servicing\Packages\update.mum，都归一为
            // servicing\Packages\{FQN}.mum —— 真机里每个包的 mum 以 FQN 命名，避免互相覆盖。
            bool isMumName = baseTail.Equals("update.mum", StringComparison.OrdinalIgnoreCase)
                             || (bare.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)
                                 && !bare.Contains('\\') && !bare.Contains('/'));
            if (isMumName)
                return Path.Combine("Windows", "servicing", "Packages", MumFileName(pkg, ver, arch, lang, pkt));

            // 裸名 manifest（无目录分隔）→ WinSxS\Manifests
            if (!bare.Contains('\\') && !bare.Contains('/') && bare.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                return Path.Combine("Windows", "WinSxS", "Manifests", bare);

            // 宏展开（顺序：bootdrive 先，因 systemroot 等不含它；bootdrive→分区根即 ""）
            string s = name.Replace('\\', '/');
            s = s.Replace("$(runtime.bootdrive)", "");
            s = s.Replace("$(runtime.systemroot)", "windows")
                 .Replace("$(runtime.windows)", "windows")
                 .Replace("$(runtime.winnt)", "windows")
                 .Replace("$(runtime.fonts)", "windows/fonts")
                 .Replace("$(runtime.inf)", "windows/inf")
                 .Replace("$(runtime.system)", "windows/system")
                 .Replace("$(runtime.system32)", "windows/system32")
                 .Replace("$(runtime.wbem)", "windows/system32/wbem")
                 .Replace("$(runtime.driverstore)", "windows/inf")
                 .Replace("$(runtime.tempdir)", "windows/temp")
                 .Replace("$(runtime.programfiles)", "Program Files")
                 .Replace("$(runtime.programdata)", "ProgramData")
                 .Replace("$(runtime.startmenu)", "ProgramData/Microsoft/Windows/Start Menu")
                 .Replace("$(runtime.replacemefolder)", "$")   // 罕见，保守
            ;
            // 若宏没展开干净（仍含 $(…)，如 $(targetplatform.root) 等构建期变量）→ 用兜底键替换或丢宏段
            s = RegexStripUnresolved(s);
            s = s.TrimStart('/');
            if (s.Length == 0) s = "Windows/WinSxS";   // 极端兜底，避免写到根
            return s.Replace('/', Path.DirectorySeparatorChar);
        }

        static string RegexStripUnresolved(string s)
        {
            // 去掉仍未识别的 $(...) 段前缀（保留其后子路径），避免造出非法目录名
            int guard = 0;
            while (s.Contains("$(") && guard++ < 8)
            {
                int a = s.IndexOf("$(", StringComparison.Ordinal);
                int b = s.IndexOf(')', a);
                if (b < 0) { s = s.Replace("$", ""); break; }
                s = (a > 0 ? s[..a] : "") + s[(b + 1)..];
                s = s.TrimStart('/');
            }
            return s;
        }

        /// <summary>servicing\Packages 的 .mum 文件名（官方 keyform：FQN~下划线风格）。</summary>
        static string MumFileName(string pkg, string ver, string arch, string lang, string pkt)
        {
            string langTok = string.IsNullOrEmpty(lang) || lang == "neutral" ? "" : lang;
            // 官方形态：{name}~{pkt}~{arch}~{lang}~_{ver}...mum；此处用可辨识的稳定命名，
            // 与 PackageEnumerator.GetPackageNamingForCBS 保持同源字段，避免臆造。
            return $"{pkg}~{pkt}~{arch}~{(langTok)}~_{ver}.mum";
        }

        static string DiskPrefix(Dictionary<string, (int Disk, int Part, string Name)> map, string targetPartition)
        {
            // targetPartition 常是 MainOS/EFIESP… 大小写不敏感匹配分区名
            foreach (var kv in map)
            {
                if (string.Equals(kv.Key, targetPartition, StringComparison.OrdinalIgnoreCase))
                    return $"disk{kv.Value.Disk}{Path.DirectorySeparatorChar}Partition{kv.Value.Part}-{kv.Value.Name}{Path.DirectorySeparatorChar}";
            }
            // 未命中：有 targetPartition 名但不在布局里 → 用其名建一个分区目录；否则根
            if (!string.IsNullOrWhiteSpace(targetPartition))
                return $"disk0{Path.DirectorySeparatorChar}Partition1-{targetPartition}{Path.DirectorySeparatorChar}";
            return "";
        }

        /// <summary>解析 DeviceLayout → 分区名到 (disk, partIdx, 规范名)。每个 Store 视作一块磁盘。</summary>
        static Dictionary<string, (int Disk, int Part, string Name)>? BuildPartitionMap(string layoutXml, List<string> log)
        {
            var map = new Dictionary<string, (int, int, string)>(StringComparer.OrdinalIgnoreCase);
            var doc = DeviceLayoutService.ParseFromXml(layoutXml);
            if (doc == null) { log.Add("⚠ DeviceLayout 解析失败，退回平铺: " + DeviceLayoutService.LastError); return null; }
            int disk = 0;
            // 池外/顶层 Stores 与各池 Stores 依次编号为 disk0,1,2…；每盘内分区按顺序编号
            void AddStore(Store s)
            {
                int part = 1;
                foreach (var p in s.Partitions)
                {
                    string pname = string.IsNullOrWhiteSpace(p.Name) ? $"Partition{part}" : p.Name;
                    if (!map.ContainsKey(pname)) map[pname] = (disk, part, pname);
                    part++;
                }
                disk++;
            }
            foreach (var pool in doc.StoragePools) foreach (var s in pool.Stores) AddStore(s);
            foreach (var s in doc.Stores) AddStore(s);
            if (map.Count == 0) { log.Add("⚠ DeviceLayout 未含可命名分区，退回平铺"); return null; }
            log.Add($"DeviceLayout: {disk} 块磁盘 / {map.Count} 个命名分区");
            return map;
        }

        static bool ExpandCab(string cab, string dest, List<string> log)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("expand.exe",
                    $"-F:* \"{cab}\" \"{dest}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p!.WaitForExit(120_000);
                return Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length > 0
                       || Directory.GetDirectories(dest).Length > 0;
            }
            catch (Exception ex) { log.Add("expand 失败: " + ex.Message); return false; }
        }
    }
}
