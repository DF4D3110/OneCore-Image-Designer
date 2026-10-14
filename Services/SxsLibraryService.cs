using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>库来源的类型 —— 决定它能提供什么。</summary>
    public enum SxsSourceKind
    {
        /// <summary>Repair Content 解包结果：提供【包图 + 组件载荷】。</summary>
        Rcp,
        /// <summary>原始安装介质（install.wim / ISO / 已应用的 VHD）：提供【完整包图 + 载荷】。</summary>
        Media,
        /// <summary>已装好的系统：安装后 mum 多已被消费 ⇒ 只能提供【组件载荷】，包图残缺。</summary>
        Installed,
    }

    public sealed class SxsSource
    {
        public SxsSourceKind Kind { get; init; } = SxsSourceKind.Rcp;
        /// <summary>根目录。可以是镜像根（含 Windows\）也可是 WinSxS 目录本身。</summary>
        public string Root { get; init; } = "";
        public string Describe() => $"{Kind}  {Root}";
    }

    /// <summary>
    /// SxS 库：把多个来源合并成【一个扁平文件夹】，并检查它对目标 edition 是否完整。
    ///
    /// 【为什么必须扁平】CBS 的包源 = 「每个 mum 自己所在的目录」，它在该目录里【相对地】找
    /// 兄弟包的 mum、组件载荷、组件 manifest。任何东西放进子目录，CBS 就找不到 ——
    /// 实测表现就是 Failed to resolve package / Failed to resolve execution chain。
    ///
    /// 【为什么需要多来源】单个 RCP 常常不全（实测对 clientcoreedition 的包级闭包
    /// 覆盖率仅 31%），缺的靠其它来源补。而"装好的系统"补不了包图（mum 已被消费），
    /// 只能补组件载荷 —— 所以来源要能按类型指定。
    /// </summary>
    public static class SxsLibraryService
    {
        // ════════════════════════════════════════════════════════════════
        //  建库：多来源 → 一个扁平文件夹
        // ════════════════════════════════════════════════════════════════

        public sealed class BuildResult
        {
            public int ComponentDirs, Manifests, Mums, Cats, FilesSkipped;
            public List<string> Notes = new();
            public string Summary()
                => $"组件目录 {ComponentDirs} · manifest {Manifests} · mum {Mums} · cat {Cats}"
                 + (FilesSkipped > 0 ? $" · 跳过同名 {FilesSkipped}" : "");
        }

        /// <summary>把多个来源展平进一个库目录。同名以【先到的】为准（顺序即优先级）。</summary>
        public static BuildResult Build(
            IEnumerable<SxsSource> sources, string libraryDir,
            bool hardLinkWhenSameVolume = true, Action<string>? log = null)
        {
            var r = new BuildResult();
            Directory.CreateDirectory(libraryDir);
            foreach (var s in sources)
            {
                var layout = ResolveLayout(s.Root);
                if (layout == null) { r.Notes.Add($"跳过（找不到 Windows\\ 或 WinSxS）：{s.Root}"); continue; }
                log?.Invoke($"── 来源 [{s.Kind}] {s.Root}");
                log?.Invoke($"   WinSxS={layout.Sxs}  Packages={layout.Packages}");

                // ① 组件目录
                if (Directory.Exists(layout.Sxs))
                {
                    foreach (var d in Directory.EnumerateDirectories(layout.Sxs))
                    {
                        string name = Path.GetFileName(d);
                        if (!IsComponentDir(name)) continue;
                        string dst = Path.Combine(libraryDir, name);
                        if (Directory.Exists(dst)) { r.FilesSkipped++; continue; }
                        if (!CopyTree(d, dst, hardLinkWhenSameVolume)) continue;
                        r.ComponentDirs++;
                    }
                }
                // ② manifest（从 WinSxS\Manifests 平铺到库根）
                string mfDir = Path.Combine(layout.Sxs, "Manifests");
                if (Directory.Exists(mfDir))
                    foreach (var f in Directory.EnumerateFiles(mfDir, "*.manifest"))
                        if (Put(f, libraryDir, hardLinkWhenSameVolume)) r.Manifests++;
                // ③ 包根下的 mum/cat/manifest（RCP 解包结果就已经是平铺的）
                foreach (var dir in new[] { layout.Packages, layout.Root })
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        string ext = Path.GetExtension(f);
                        if (!ext.Equals(".mum", StringComparison.OrdinalIgnoreCase)
                            && !ext.Equals(".cat", StringComparison.OrdinalIgnoreCase)
                            && !ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Put(f, libraryDir, hardLinkWhenSameVolume)) { r.FilesSkipped++; continue; }
                        if (ext.Equals(".mum", StringComparison.OrdinalIgnoreCase)) r.Mums++;
                        else if (ext.Equals(".cat", StringComparison.OrdinalIgnoreCase)) r.Cats++;
                        else r.Manifests++;
                    }
                }
                log?.Invoke("   " + r.Summary());
            }
            return r;
        }

        static bool IsComponentDir(string name)
            => name.Length > 3 && name.Contains('_')
               && (name.StartsWith("x86_") || name.StartsWith("amd64_") || name.StartsWith("arm_")
                || name.StartsWith("arm64_") || name.StartsWith("msil_") || name.StartsWith("neutral_")
                || name.StartsWith("wow64_") || name.StartsWith("ia64_"));

        sealed class Layout { public string Root = "", Sxs = "", Packages = ""; }

        /// <summary>容忍多种来源形态：镜像根（含 Windows\）或直接的 WinSxS 目录或 RCP 解包目录。</summary>
        static Layout? ResolveLayout(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            var cands = new List<Layout>();
            void Add(string r, string sxs, string pk)
                => cands.Add(new Layout { Root = r, Sxs = sxs, Packages = pk });

            Add(root, Path.Combine(root, "Windows", "WinSxS"), Path.Combine(root, "Windows", "servicing", "Packages"));
            Add(root, Path.Combine(root, "WinSxS"), Path.Combine(root, "servicing", "Packages"));
            Add(root, root, root);                                   // RCP 解包结果（本来就平铺）
            foreach (var c in cands)
                if (Directory.Exists(c.Sxs) || Directory.Exists(c.Packages)) return c;
            return null;
        }

        static bool Put(string srcFile, string dstDir, bool hardLink)
        {
            string dst = Path.Combine(dstDir, Path.GetFileName(srcFile));
            if (File.Exists(dst)) return false;
            try
            {
                if (hardLink && SameVolume(srcFile, dst))
                {
                    // 同卷用硬链接：秒级、不额外占空间
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        $"/c mklink /H \"{dst}\" \"{srcFile}\"")
                    { UseShellExecute = false, CreateNoWindow = true,
                      RedirectStandardOutput = true, RedirectStandardError = true };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p!.WaitForExit();
                    if (File.Exists(dst)) return true;
                }
                File.Copy(srcFile, dst, false);
                return true;
            }
            catch { return false; }
        }

        static bool CopyTree(string srcDir, string dstDir, bool hardLink)
        {
            try
            {
                Directory.CreateDirectory(dstDir);
                foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(srcDir, f);
                    string dst = Path.Combine(dstDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    Put(f, Path.GetDirectoryName(dst)!, hardLink && false);   // 目录树内直接复制
                }
                return true;
            }
            catch { return false; }
        }

        static bool SameVolume(string a, string b)
        {
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                       Path.GetPathRoot(Path.GetFullPath(b)),
                                       StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  完整性检查：目标 edition 的【包级】闭包
        // ════════════════════════════════════════════════════════════════

        public sealed class CompletenessReport
        {
            public int ClosureSize, Present, Missing;
            public List<string> MissingNames = new();
            public string Edition = "";
            public bool Complete => Missing == 0;
            public double Coverage => ClosureSize == 0 ? 0 : Present / (double)ClosureSize;
            public string Summary()
                => $"edition={Edition} 闭包 {ClosureSize} · 有 {Present} · 缺 {Missing}"
                 + $" · 覆盖 {Coverage:P1}" + (Complete ? "  ✅ 完整" : "  ⚠ 需要外部镜像补全");
        }

        /// <summary>
        /// 算【包级】闭包并比对库。
        ///
        /// 【关键：什么叫"包级"引用】mum 里对子包的引用形如
        ///   &lt;package identifier="Windows Foundation"&gt;
        ///     &lt;update name="CommonFoundation"&gt;          ← update 的 name 【不是】包名
        ///       &lt;package contained="false"&gt;
        ///         &lt;assemblyIdentity name="Microsoft-Windows-Common-Foundation-Package" .../&gt;  ← 这才是
        ///       &lt;/package&gt;
        ///     &lt;/update&gt;
        ///   &lt;/package&gt;
        /// ⚠ 用 `name="..."` 抓【所有】name 属性会把 update 名、组件名、自身 identity 都算进来，
        ///   导致覆盖率被严重低估（我为此白跑过一轮：12~21% 是假的）。
        /// </summary>
        public static CompletenessReport CheckCompleteness(string libraryDir, string editionMumPath)
        {
            var rep = new CompletenessReport();
            var index = BuildPackageIndex(libraryDir);
            string seed = Path.GetFileNameWithoutExtension(editionMumPath).Split('~')[0].ToLowerInvariant();
            rep.Edition = seed;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seed };
            var queue = new Queue<string>(); queue.Enqueue(seed);
            var miss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int round = 0;
            while (queue.Count > 0 && round++ < 40)
            {
                var cur = new List<string>();
                while (queue.Count > 0) cur.Add(queue.Dequeue());
                int added = 0;
                foreach (var n in cur)
                {
                    if (!index.TryGetValue(n, out var path)) { miss.Add(n); continue; }
                    foreach (var c in ChildPackages(path))
                        if (seen.Add(c)) { queue.Enqueue(c); added++; }
                }
                if (added == 0) break;
            }
            rep.ClosureSize = seen.Count;
            rep.MissingNames = miss.OrderBy(x => x).ToList();
            rep.Missing = miss.Count;
            rep.Present = seen.Count - miss.Count;
            return rep;
        }

        static Dictionary<string, string> BuildPackageIndex(string libraryDir)
        {
            var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(libraryDir)) return idx;
            foreach (var f in Directory.EnumerateFiles(libraryDir, "*.mum"))
            {
                string k = Path.GetFileNameWithoutExtension(f).Split('~')[0];
                if (!idx.ContainsKey(k)) idx[k] = f;
            }
            return idx;
        }

        /// <summary>取 mum 里【子包】的 assemblyIdentity 名（只取 package 元素之下的）。</summary>
        public static List<string> ChildPackages(string mumPath)
        {
            var outp = new List<string>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(mumPath);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("a", "urn:schemas-microsoft-com:asm.v3");
                // ⚠ 只取【package 里嵌套的 package】里的 assemblyIdentity：
                //   //a:package//a:assemblyIdentity  会把 <component> 里的组件名也抓进来，
                //   导致「完整镜像对自己」只有 23.5%（必须是 100%）。
                foreach (XmlNode n in doc.SelectNodes("//a:package//a:package/a:assemblyIdentity/@name", ns))
                    if (n?.Value != null) outp.Add(n.Value.ToLowerInvariant());
            }
            catch { }
            return outp;
        }
    }
}
