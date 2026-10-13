using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// pureSxS 的 C# 重写（原 ThirdParty\SxS\pureSxS\puresxs.py + havesxs.py，GPLv3 参考实现逐函数比对）。
    ///
    /// 功能：给定一个 CBS 包 .mum，递归抽出它的完整依赖闭包
    /// （包 → 更新 → 组件/驱动 → 程序集 manifest），按 keyform 命名落盘成可搬运的纯 SxS 目录
    /// （mum + cat + manifest + 组件文件目录树）。
    ///
    /// 【2026-10-13 重写】修掉上一版的 7 处缺陷：
    ///   ① 【致命·运行时】Nsm() 调用 AddNamespace("xmlns", ...) —— .NET 里 "xmlns" 是保留前缀，
    ///      会抛 ArgumentException("Prefix \"xmlns\" is reserved for use by XML.")，导出必然当场失败。
    ///      改用前缀 "d"。（已用 ns_probe 实证）
    ///   ② 【致命·逻辑】XPath 漏了 /update 层。
    ///      Python: root.findall('./package/update') -> update.findall('./package|component|driver/assemblyIdentity')
    ///      上一版从根直达 package，再找 package/package/... 永远匹配 0 项，导出结果必为空。
    ///   ③ 子包只复制 .mum 未复制 .cat —— Python 的 process_package 两者都复制。
    ///   ④ 组件/程序集只复制同名"文件"，未复制组件目录树 —— Python 用 shutil.copytree。
    ///   ⑤ res.Error ??= ... 无效（Error 初始化为 ""，非 null）。
    ///   ⑥ 缺少 verify_assembly 身份校验（Python 逐字段比对，不符即 critical 失败）。
    ///   ⑦ ParsePackage 用 doc.Load 不处理 DCM —— 统一改走 ManifestDecompressor。
    ///
    /// 保留/新增的改进（相对 Python）：
    ///   · 多源根自动探测：现代 Windows 把 .mum/.cat 放在 Windows\Servicing\Packages、
    ///     .manifest 放在 Windows\WinSxS\Manifests、组件文件放在 Windows\WinSxS\&lt;name&gt;\，
    ///     Python 版要求使用者手工搭"合并视图"目录；这里自动把这些根都纳入查找顺序。
    ///   · DCM 解压走 ManifestDecompressor（wcpex 官方通道），不依赖固定 base.manifest，
    ///     跨 build 也能解 —— Python 版只对打包者那个 build 有效，跨 build 会静默失败。
    ///   · visited 集合防循环依赖死递归。
    ///   · 可取消 + 进度日志回调。
    /// </summary>
    public static class PureSxsService
    {
        public sealed class Result
        {
            public bool Success;
            public int PackagesCopied, ComponentsCopied, AssembliesCopied, Missing;
            public string Error = "";
        }

        // ────────────────────────────────────────────────────────────────
        // 命名空间助手
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 建立 XPath 命名空间管理器。
        /// 【修正①】前缀必须是 "d" —— "xmlns" 是 XML 保留前缀，AddNamespace 会抛 ArgumentException。
        /// </summary>
        static XmlNamespaceManager Nsm(XmlDocument doc)
        {
            var nsm = new XmlNamespaceManager(doc.NameTable);
            string ns = doc.DocumentElement?.NamespaceURI ?? "";
            if (ns.Length > 0) nsm.AddNamespace("d", ns);
            return nsm;
        }

        // ────────────────────────────────────────────────────────────────
        // 身份 → 名称（havesxs 等价）
        // ────────────────────────────────────────────────────────────────

        /// <summary>identity → CBS 包名 {name}~{pkt}~{arch}~{lang}~{version}（neutral 置空）。</summary>
        static string PackageName(XmlElement id) => WinSxSHashCalculator.GenerateCBSKeyform(
            id.GetAttribute("name"), id.GetAttribute("publicKeyToken"),
            id.GetAttribute("processorArchitecture"), id.GetAttribute("language"),
            id.GetAttribute("version"));

        /// <summary>
        /// identity → WinSxS 组件短名（havesxs.generate_sxs_name 等价）。
        /// 返回 null = 该依赖不参与（无 version / language 为 *），与 Python 版语义一致。
        /// </summary>
        static string? AssemblyName(XmlElement id)
        {
            string version = id.GetAttribute("version");
            if (version.Length == 0) return null;
            string culture = id.GetAttribute("language");
            if (culture == "*") return null;

            string arch = id.GetAttribute("processorArchitecture");
            if (arch.Length == 0 || arch.Equals("neutral", StringComparison.OrdinalIgnoreCase)) arch = "none";
            if (culture.Length == 0 || culture.Equals("neutral", StringComparison.OrdinalIgnoreCase)) culture = "none";
            string ptk = id.GetAttribute("publicKeyToken");
            string name = id.GetAttribute("name");
            string vscope = id.GetAttribute("versionScope");
            // .inf 驱动类组件带 type（典型值 dualModeDriver），它插在 culture 与 version 之间
            // 参与哈希。此前漏传会让全部驱动组件的 SxS 名算错。
            string type = id.GetAttribute("type");
            if (type.Length == 0) type = id.GetAttribute("typeName");

            return WinSxSHashCalculator.GenerateWinSxFolderName(name, ptk, arch, version, culture,
                string.IsNullOrEmpty(vscope) ? null : vscope,
                string.IsNullOrEmpty(type) ? null : type);
        }

        /// <summary>
        /// Python verify_assembly 等价：把 expect（预期 identity 元素）与 doc 内部的
        /// assemblyIdentity 逐字段比对。expect == null 时直接通过。
        /// 返回 false 表示身份不符，调用方应中止（对应 Python 的 logging.critical + return False）。
        /// </summary>
        static bool VerifyAssembly(XmlElement? expect, XmlDocument doc, bool isPackage, Action<string>? log)
        {
            if (expect == null) return true;

            var ident = doc.DocumentElement?.SelectSingleNode("./*[local-name()='assemblyIdentity']") as XmlElement
                        ?? doc.SelectSingleNode("//*[local-name()='assemblyIdentity']") as XmlElement;
            if (ident == null)
            {
                log?.Invoke("  ✗ 身份校验失败：目标文档内找不到 assemblyIdentity");
                return false;
            }

            // Python 的 keys 顺序：is_package 时不含 versionScope/type
            var keys = new List<string> { "name", "language", "version", "publicKeyToken", "processorArchitecture" };
            if (!isPackage)
            {
                keys.Add("versionScope");
                keys.Add("type");
            }

            foreach (var k in keys)
            {
                string? e = expect.GetAttribute(k);
                string? g = ident.GetAttribute(k);

                if (string.IsNullOrEmpty(e)) e = null;
                if (string.IsNullOrEmpty(g)) g = null;

                e = e?.ToLowerInvariant();
                g = g?.ToLowerInvariant();

                if (k is "language" or "processorArchitecture")
                {
                    if (e is "" or "neutral") e = null;
                    if (g is "" or "neutral") g = null;
                }

                if (!string.Equals(e, g, StringComparison.Ordinal))
                {
                    log?.Invoke($"  ✗ 身份校验失败：字段 {k} 期望 {e ?? "(null)"}，实际 {g ?? "(null)"}");
                    return false;
                }
            }
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        // 源根解析（相对 Python 的改进）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析查找源根列表（按优先级）。
        /// 覆盖三种现实布局：
        ///   · 包目录（mum/cat/manifest/组件目录同处）：直接用 mum 所在目录
        ///   · 桌面 WinSxS：mum 在 Manifests\ 下 → 上跳一级
        ///   · 现代 Windows：mum/cat 在 Windows\Servicing\Packages，
        ///     manifest 在 Windows\WinSxS\Manifests，组件文件在 Windows\WinSxS\&lt;name&gt;\
        /// </summary>
        static List<string> ResolveSourceRoots(string mumDir)
        {
            var roots = new List<string>();
            void Add(string? p)
            {
                if (string.IsNullOrEmpty(p)) return;
                try { p = Path.GetFullPath(p); } catch { return; }
                if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                    roots.Add(p);
            }

            // 1) 桌面 WinSxS：mum 在 Manifests\ 下 → 上跳一级
            if (Path.GetFileName(mumDir).Equals("Manifests", StringComparison.OrdinalIgnoreCase))
                Add(Directory.GetParent(mumDir)?.FullName);
            else
                Add(mumDir);

            // 2) 从源根推导 Windows\ 及其兄弟目录
            foreach (var r in roots.ToArray())
            {
                var dir = new DirectoryInfo(r);
                DirectoryInfo? windows = null;

                if (dir.Name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase))
                    windows = dir.Parent;                       // ...\Windows\WinSxS
                else if (dir.Name.Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
                         dir.Parent?.Name.Equals("Servicing", StringComparison.OrdinalIgnoreCase) == true)
                    windows = dir.Parent!.Parent;               // ...\Windows\Servicing\Packages

                if (windows != null)
                {
                    Add(Path.Combine(windows.FullName, "WinSxS", "Manifests"));
                    Add(Path.Combine(windows.FullName, "WinSxS"));
                    Add(Path.Combine(windows.FullName, "Servicing", "Packages"));
                }
            }

            // 3) 兜底：源根下的 Manifests 子目录
            foreach (var r in roots.ToArray())
                Add(Path.Combine(r, "Manifests"));

            return roots;
        }

        // ────────────────────────────────────────────────────────────────
        // 导出主入口
        // ────────────────────────────────────────────────────────────────

        public static async Task<Result> ExportAsync(string sourceMum, string destDir,
            Action<string>? log = null, CancellationToken ct = default)
            => await Task.Run(() => Export(sourceMum, destDir, log, ct), ct);

        /// <summary>导出主入口。sourceMum 为 CBS 包 .mum；destDir 输出纯 SxS 目录。</summary>
        public static Result Export(string sourceMum, string destDir, Action<string>? log = null, CancellationToken ct = default)
        {
            var res = new Result();
            try
            {
                if (!File.Exists(sourceMum)) { res.Error = "源 mum 不存在: " + sourceMum; return res; }
                if (!sourceMum.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)) { res.Error = "输入必须是 .mum"; return res; }

                Directory.CreateDirectory(destDir);

                var roots = ResolveSourceRoots(Path.GetDirectoryName(Path.GetFullPath(sourceMum))!);
                log?.Invoke("查找源根（按优先级）:");
                foreach (var r in roots) log?.Invoke("  · " + r);

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 主 mum + cat 先复制（对应 Python 主流程）
                string mumName = Path.GetFileName(sourceMum);
                string pkgBase = Path.GetFileNameWithoutExtension(sourceMum);
                if (!CopyToDest(mumName, roots, destDir, log)) { res.Error = "主 mum 复制失败"; return res; }
                CopyToDest(pkgBase + ".cat", roots, destDir, log, quietIfMissing: true);

                if (!ParsePackage(sourceMum, roots, destDir, null, visited, res, log, ct))
                {
                    if (res.Error.Length == 0) res.Error = "递归解析失败（身份校验不符或依赖处理失败）";
                    return res;
                }

                res.Success = true;
                log?.Invoke($"完成：包 {res.PackagesCopied}、组件 {res.ComponentsCopied}、程序集 {res.AssembliesCopied}、缺失 {res.Missing}");
                return res;
            }
            catch (OperationCanceledException) { res.Error = "已取消"; return res; }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        // ────────────────────────────────────────────────────────────────
        // parse_package / parse_assembly（Python 同名函数）
        // ────────────────────────────────────────────────────────────────

        /// <summary>Python parse_package(file, destination, verify) 等价。file 指向【源】mum。</summary>
        static bool ParsePackage(string mumPath, List<string> roots, string dest, XmlElement? verify,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFullPath(mumPath);
            if (!visited.Add(key)) return true;

            XmlDocument doc = LoadXml(mumPath, log, out string? why);
            if (doc == null)
            {
                log?.Invoke($"⚠ mum 解析失败 {Path.GetFileName(mumPath)}: {why}");
                return true;
            }

            if (!VerifyAssembly(verify, doc, isPackage: true, log)) return false;

            var nsm = Nsm(doc);

            // 【修正②】Python: root.findall('./package/update')
            var updates = doc.DocumentElement!.SelectNodes("./d:package/d:update", nsm);
            if (updates == null) return true;

            foreach (XmlNode upd in updates)
            {
                foreach (XmlNode node in upd.SelectNodes("./d:package/d:assemblyIdentity", nsm) ?? EmptyNodeList())
                {
                    if (node is not XmlElement id) continue;
                    if (!ProcessPackage(id, roots, dest, visited, res, log, ct)) return false;
                }

                foreach (XmlNode node in upd.SelectNodes("./d:component/d:assemblyIdentity", nsm) ?? EmptyNodeList())
                {
                    if (node is not XmlElement id) continue;
                    if (!ProcessComponent(id, roots, dest, visited, res, log, ct)) return false;
                }

                foreach (XmlNode node in upd.SelectNodes("./d:driver/d:assemblyIdentity", nsm) ?? EmptyNodeList())
                {
                    if (node is not XmlElement id) continue;
                    if (!ProcessAssembly(id, roots, dest, visited, res, log, ct)) return false;
                }
            }

            return true;
        }

        /// <summary>Python parse_assembly(file, source, destination, verify) 等价。</summary>
        static bool ParseAssembly(string manPath, List<string> roots, string dest, XmlElement? verify,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFullPath(manPath);
            if (!visited.Add(key)) return true;

            XmlDocument doc = LoadXml(manPath, log, out string? why);
            if (doc == null)
            {
                log?.Invoke($"  （manifest 无法解析，跳过依赖: {Path.GetFileName(manPath)} — {why}）");
                return true;
            }

            if (!VerifyAssembly(verify, doc, isPackage: false, log)) return false;

            var nsm = Nsm(doc);
            var deps = doc.DocumentElement!.SelectNodes(
                "./d:dependency/d:dependentAssembly/d:assemblyIdentity", nsm);
            if (deps == null) return true;

            foreach (XmlNode node in deps)
            {
                if (node is not XmlElement id) continue;
                if (!ProcessAssembly(id, roots, dest, visited, res, log, ct)) return false;
            }
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        // process_package / process_component / process_assembly
        // ────────────────────────────────────────────────────────────────

        /// <summary>Python process_package 等价：复制包（mum + cat），然后递归。【修正③】补上 cat。</summary>
        static bool ProcessPackage(XmlElement id, List<string> roots, string dest,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            string pkgName = PackageName(id);
            string mum = pkgName + ".mum";
            string cat = pkgName + ".cat";

            // Python: 目标已同时存在 mum 与 cat 就跳过
            if (File.Exists(Path.Combine(dest, mum)) && File.Exists(Path.Combine(dest, cat)))
                return true;

            if (!CopyToDest(mum, roots, dest, log, quietIfMissing: true))
            {
                log?.Invoke($"  ⚠ 包不存在: {mum}");
                res.Missing++;
                return true;
            }
            res.PackagesCopied++;

            // 【修正③】cat 一并复制（缺失只警告，与 Python 的 logging.warning 一致）
            CopyToDest(cat, roots, dest, log, quietIfMissing: true);

            // Python 递归时传【源】mum
            string? srcMum = FindInRoots(mum, roots);
            if (srcMum == null) return true;

            if (!ParsePackage(srcMum, roots, dest, id, visited, res, log, ct))
            {
                log?.Invoke($"  ✗ 解析包失败: {pkgName}");
                return false;
            }
            return true;
        }

        /// <summary>Python process_component 等价：复制 deployment 的 .manifest，然后递归。</summary>
        static bool ProcessComponent(XmlElement id, List<string> roots, string dest,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            string? depName = AssemblyName(id);
            if (depName == null) return true;

            string man = depName + ".manifest";
            if (File.Exists(Path.Combine(dest, man))) return true;

            if (!CopyToDest(man, roots, dest, log, quietIfMissing: true))
            {
                log?.Invoke($"  ⚠ deployment 不存在: {man}");
                res.Missing++;
                return true;
            }
            res.ComponentsCopied++;

            if (!ParseAssembly(Path.Combine(dest, man), roots, dest, id, visited, res, log, ct))
            {
                log?.Invoke($"  ✗ 解析 deployment 失败: {man}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Python process_assembly 等价：复制 .manifest **和同名组件目录树**，然后递归。
        /// 【修正④】补上目录树复制（WinSxS 组件文件都在该目录里）。
        /// </summary>
        static bool ProcessAssembly(XmlElement id, List<string> roots, string dest,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            string? asmName = AssemblyName(id);
            if (asmName == null) return true;

            string man = asmName + ".manifest";
            string destMan = Path.Combine(dest, man);
            string destDir = Path.Combine(dest, asmName);

            // Python: 目标同时存在 manifest 与目录就跳过
            if (File.Exists(destMan) && Directory.Exists(destDir)) return true;

            if (!CopyToDest(man, roots, dest, log, quietIfMissing: true))
            {
                log?.Invoke($"  ⚠ 程序集不存在: {man}");
                res.Missing++;
                return true;
            }

            // 【修正④】复制组件目录树
            string? srcDir = FindDirInRoots(asmName, roots);
            if (srcDir != null) CopyDirectoryTree(srcDir, destDir, log);

            res.AssembliesCopied++;

            if (!ParseAssembly(destMan, roots, dest, id, visited, res, log, ct))
            {
                log?.Invoke($"  ✗ 解析程序集失败: {man}");
                return false;
            }
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        // 文件 / 目录 / XML 工具
        // ────────────────────────────────────────────────────────────────

        /// <summary>【修正⑦】统一用 ManifestDecompressor 载入（自动处理 DCM 压缩）。</summary>
        static XmlDocument? LoadXml(string path, Action<string>? log, out string? why)
        {
            why = null;
            string? xml = ManifestDecompressor.ReadXmlText(path, out why);
            if (xml == null) return null;

            var doc = new XmlDocument { PreserveWhitespace = false };
            try { doc.LoadXml(xml); }
            catch (Exception ex) { why = "XML 解析失败: " + ex.Message; return null; }
            return doc;
        }

        static string? FindInRoots(string name, List<string> roots)
        {
            foreach (var r in roots)
            {
                string p = Path.Combine(r, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        static string? FindDirInRoots(string name, List<string> roots)
        {
            foreach (var r in roots)
            {
                string p = Path.Combine(r, name);
                if (Directory.Exists(p)) return p;
            }
            return null;
        }

        static bool CopyToDest(string name, List<string> roots, string dest,
            Action<string>? log = null, bool quietIfMissing = false)
        {
            string d = Path.Combine(dest, name);
            if (File.Exists(d)) return true;

            string? s = FindInRoots(name, roots);
            if (s == null)
            {
                if (!quietIfMissing) log?.Invoke($"  ⚠ 源文件缺失: {name}");
                return false;
            }
            try { File.Copy(s, d, true); return true; }
            catch (Exception ex) { log?.Invoke($"  ⚠ 复制失败 {name}: {ex.Message}"); return false; }
        }

        /// <summary>Python shutil.copytree 等价（增量、单项失败不致命）。</summary>
        static void CopyDirectoryTree(string srcDir, string dstDir, Action<string>? log)
        {
            try
            {
                Directory.CreateDirectory(dstDir);
                foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(srcDir, f);
                    string target = Path.Combine(dstDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target))
                    {
                        try { File.Copy(f, target, false); } catch { }
                    }
                }
            }
            catch (Exception ex) { log?.Invoke($"  ⚠ 目录复制失败 {Path.GetFileName(srcDir)}: {ex.Message}"); }
        }

        /// <summary>SelectNodes 正常不返回 null；给个空实现统一 ?? 写法（【修正⑤】替掉 XmlNodeListCompat）。</summary>
        static XmlNodeList EmptyNodeList() => new XmlDocument().CreateDocumentFragment().ChildNodes;
    }
}
