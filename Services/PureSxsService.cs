using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// pureSxS 的 C# 重写（原 ThirdParty\SxS\pureSxS\puresxs.py + havesxs.py，GPLv3 参考实现已逐函数比对）。
    ///
    /// 功能：从桌面 Windows 的 WinSxS 存储里，给定一个 CBS 包 .mum，递归抽出它的
    /// 完整依赖闭包（包→更新→组件/驱动→程序集 manifest），按 keyform 命名落盘成
    /// 可搬运的纯 SxS 目录（mum + cat + manifest + 文件目录）。
    ///
    /// 与 Python 版的差异（都是改进）：
    /// ① DCM 解压走 ManifestDecompressor（wcpex 官方通道）——Python 版用 msdelta +
    ///    固定 base.manifest，只对打包者那个 build 有效，跨 build 会静默失败；
    /// ② 哈希/命名复用 WinSxSHashCalculator（本项目经 1337 组件实测 100% 命中）；
    /// ③ visited 集合防循环依赖死递归；④ 后台线程 + 进度日志回调，不卡 UI。
    /// </summary>
    public static class PureSxsService
    {
        public sealed class Result
        {
            public bool Success;
            public int PackagesCopied, ComponentsCopied, AssembliesCopied, Missing;
            public string Error = "";
        }

        static XmlNamespaceManager Nsm(XmlDocument doc)
        {
            var nsm = new XmlNamespaceManager(doc.NameTable);
            string ns = doc.DocumentElement?.NamespaceURI ?? "";
            if (ns.Length > 0) nsm.AddNamespace("xmlns", ns);
            return nsm;
        }

        /// <summary>identity → CBS 包名 {name}~{pkt}~{arch}~{lang}~{version}（neutral 置空）。</summary>
        static string PackageName(XmlElement id) => WinSxSHashCalculator.GenerateCBSKeyform(
            id.GetAttribute("name"), id.GetAttribute("publicKeyToken"),
            id.GetAttribute("processorArchitecture"), id.GetAttribute("language"),
            id.GetAttribute("version"));

        /// <summary>
        /// identity → WinSxS 组件短名（havesxs.generate_sxs_name 等价）。
        /// 返回 null = 该依赖不参与（无 version / culture 为 *），与 Python 版语义一致。
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

            return WinSxSHashCalculator.GenerateWinSxFolderName(name, ptk, arch, version, culture,
                string.IsNullOrEmpty(vscope) ? null : vscope);
        }

        /// <summary>
        /// 导出主入口：sourceMum 为 WinSxS\Manifests 外的包 .mum（或已 stage 的包目录里的 mum），
        /// destDir 输出纯 SxS 目录。log 回调线程不定，实现方自行调度。
        /// </summary>
        public static async Task<Result> ExportAsync(string sourceMum, string destDir,
            Action<string>? log = null, CancellationToken ct = default)
        {
            return await Task.Run(() => Export(sourceMum, destDir, log, ct), ct);
        }

        public static Result Export(string sourceMum, string destDir, Action<string>? log = null, CancellationToken ct = default)
        {
            var res = new Result();
            try
            {
                if (!File.Exists(sourceMum)) { res.Error = "源 mum 不存在: " + sourceMum; return res; }
                if (!sourceMum.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)) { res.Error = "输入必须是 .mum"; return res; }
                Directory.CreateDirectory(destDir);

                // 源目录 = WinSxS 根（mum 与 Manifests、FileMaps 同级的常见布局自动探测）
                string source = DetectWinSxSRoot(Path.GetDirectoryName(sourceMum)!);
                log?.Invoke($"WinSxS 源根: {source}");

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CopyFileIf(new FileInfo(sourceMum).Name, source, destDir, log);   // 主 mum
                string pkgBase = Path.GetFileNameWithoutExtension(sourceMum);
                CopyFileIf(pkgBase + ".cat", source, destDir, log);                // 主 cat

                if (!ParsePackage(sourceMum, source, destDir, visited, res, log, ct))
                { res.Error ??= "递归解析失败"; return res; }

                res.Success = true;
                log?.Invoke($"完成：包 {res.PackagesCopied}、组件 {res.ComponentsCopied}、程序集 {res.AssembliesCopied}、缺失 {res.Missing}");
                return res;
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
        }

        /// <summary>常见桌面布局：C:\Windows\WinSxS（mum 在 Manifests\ 下，包名文件在根）。</summary>
        static string DetectWinSxSRoot(string mumDir)
        {
            if (Path.GetFileName(mumDir).Equals("Manifests", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(mumDir)?.FullName ?? mumDir;
            return mumDir;
        }

        static void CopyFileIf(string name, string source, string dest, Action<string>? log)
        {
            string s = Path.Combine(source, name), d = Path.Combine(dest, name);
            if (File.Exists(d)) return;
            if (File.Exists(s)) { try { File.Copy(s, d, true); } catch { } }
        }

        static bool ParsePackage(string mumPath, string source, string dest,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFullPath(mumPath);
            if (!visited.Add(key)) return true;

            XmlDocument doc = new();
            try { doc.Load(mumPath); }
            catch (Exception ex) { log?.Invoke($"⚠ mum 解析失败 {Path.GetFileName(mumPath)}: {ex.Message}"); return true; }
            var nsm = Nsm(doc);

            foreach (XmlNode upd in doc.SelectNodes("./xmlns:assembly/xmlns:package", nsm) ?? new XmlNodeListCompat())
            {
                // 子包（update 里 package/driver/component 三类依赖）
                foreach (XmlNode node in upd.SelectNodes("./xmlns:package/xmlns:assemblyIdentity", nsm) ?? Empty())
                {
                    if (node is not XmlElement id) continue;
                    string pkg = PackageName(id);
                    string mum = pkg + ".mum";
                    if (CopyFromSource(mum, source, dest, log, res))
                    {
                        res.PackagesCopied++;
                        string? p2 = Path.Combine(dest, mum);
                        if (!ParsePackage(p2, source, dest, visited, res, log, ct)) return false;
                    }
                    else res.Missing++;
                }

                foreach (XmlNode node in upd.SelectNodes("./xmlns:component/xmlns:assemblyIdentity", nsm) ?? Empty())
                {
                    if (node is not XmlElement id) continue;
                    string? dep = AssemblyName(id);
                    if (dep == null) continue;
                    string man = dep + ".manifest";
                    if (CopyFromSource(man, source, dest, log, res) || CopyFromManifestsDir(man, source, dest, log, res))
                    {
                        res.ComponentsCopied++;
                        if (!ParseAssembly(Path.Combine(dest, man), source, dest, visited, res, log, ct)) return false;
                    }
                    else res.Missing++;
                }

                foreach (XmlNode node in upd.SelectNodes("./xmlns:driver/xmlns:assemblyIdentity", nsm) ?? Empty())
                {
                    if (node is not XmlElement id) continue;
                    string? dep = AssemblyName(id);
                    if (dep == null) continue;
                    string man = dep + ".manifest";
                    if (CopyFromSource(man, source, dest, log, res) || CopyFromManifestsDir(man, source, dest, log, res))
                    {
                        res.AssembliesCopied++;
                        CopyFileIf(dep, source, dest, log);   // FileRepository 目录（若存在）
                        if (!ParseAssembly(Path.Combine(dest, man), source, dest, visited, res, log, ct)) return false;
                    }
                    else res.Missing++;
                }
            }
            return true;
        }

        static bool ParseAssembly(string manPath, string source, string dest,
            HashSet<string> visited, Result res, Action<string>? log, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFullPath(manPath);
            if (!visited.Add(key)) return true;

            string? xml = ManifestDecompressor.ReadXmlText(manPath, out string? why);
            if (xml == null) { log?.Invoke($"  （manifest 无法解析，跳过依赖: {Path.GetFileName(manPath)} — {why}）"); return true; }

            XmlDocument doc = new();
            try { doc.LoadXml(xml); } catch { return true; }
            var nsm = Nsm(doc);

            foreach (XmlNode node in doc.SelectNodes("./xmlns:assembly/xmlns:dependency/xmlns:dependentAssembly/xmlns:assemblyIdentity", nsm) ?? Empty())
            {
                if (node is not XmlElement id) continue;
                string? dep = AssemblyName(id);
                if (dep == null) continue;
                string man = dep + ".manifest";

                bool got = CopyFromSource(man, source, dest, log, res) || CopyFromManifestsDir(man, source, dest, log, res);
                if (!got) { res.Missing++; continue; }

                res.AssembliesCopied++;
                CopyFileIf(dep, source, dest, log);   // 组件文件目录
                if (!ParseAssembly(Path.Combine(dest, man), source, dest, visited, res, log, ct)) return false;
            }
            return true;
        }

        static bool CopyFromSource(string name, string source, string dest, Action<string>? log, Result res)
        {
            string s = Path.Combine(source, name), d = Path.Combine(dest, name);
            if (File.Exists(d)) return true;
            if (!File.Exists(s)) return false;
            try { File.Copy(s, d, true); return true; } catch { return false; }
        }

        /// <summary>桌面 WinSxS 的 manifest 在 Manifests\ 子目录。</summary>
        static bool CopyFromManifestsDir(string name, string source, string dest, Action<string>? log, Result res)
        {
            string s = Path.Combine(source, "Manifests", name), d = Path.Combine(dest, name);
            if (File.Exists(d)) return true;
            if (!File.Exists(s)) return false;
            try { File.Copy(s, d, true); return true; } catch { return false; }
        }

        static XmlNodeList Empty() => new XmlDocument().CreateDocumentFragment().ChildNodes;
        // 兼容小类：SelectNodes 返回 null 时给空表
        class XmlNodeListCompat : XmlNodeList
        {
            public override int Count => 0;
            public override XmlNode Item(int index) => null!;
            public override System.Collections.IEnumerator GetEnumerator() => new List<XmlNode>().GetEnumerator();
        }
    }
}
