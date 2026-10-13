using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// SKU assembly 服务：解析并预检 cbss.cmd / cbsx 使用的批处理文件（skuassembly.txt）。
    ///
    /// 【规范来源】WitherOrNot/cbsexploder（GPLv3，cbsexploder.cpp）——cbsx.exe 的真实实现：
    ///   选项：
    ///     /ip   Install package  → CbsInstallState::Installed
    ///     /sp   Stage   package  → CbsInstallState::Staged
    ///     /up   Uninstall        → CbsInstallState::Absent
    ///     /m:&lt;mum&gt;   包清单路径
    ///     /o:&lt;dir&gt;   离线镜像的 Windows 目录
    ///     /log:&lt;p&gt;   日志（默认 C:\cbsexploder.log）
    ///     /b:&lt;p&gt;     批处理文件
    ///   批处理约束：/b 不能与命令行上的 /sp /ip /up /ep /m 同时出现；
    ///              每行独立解析，格式与命令行完全一致。
    ///   cbss.cmd 实际用法：
    ///     X:\desktopimaging\cbsx.exe /o:X:\image\Windows /log:... /b:"...\files\skuassembly.txt" /s:X:\temp
    ///   样例 skuassembly.txt：
    ///     /ip /m:sxs\Microsoft-Windows-CoreEdition~31bf3856ad364e35~amd64~~10.0.26085.1.mum
    ///     /sp /m:sxs\Microsoft-Windows-ProfessionalEdition~31bf3856ad364e35~amd64~~10.0.26085.1.mum
    ///   ⇒ Core 被“安装”（成为当前 edition），Professional 被“暂存”（进入 target editions）。
    ///
    /// 本服务只做**解析 + 预检 + 计划生成**（不装载任何东西），
    /// 与 OCID 既有的“先预览再执行”安全门一致；真正的执行需要 CBS 引擎（见 CbsxService 规划）。
    /// </summary>
    public static class SkuAssemblyService
    {
        /// <summary>一次包操作的意图状态（对应 CbsInstallState 的子集）。</summary>
        public enum PackageIntent
        {
            Unknown,
            Installed,   // /ip
            Staged,      // /sp
            Absent       // /up
        }

        public sealed class Operation
        {
            public int LineNumber;
            public PackageIntent Intent = PackageIntent.Unknown;
            public string RawManifestPath = "";
            public string? ResolvedPath;
            public bool FileExists;
            public string? Err;

            // 从 mum 解析出的身份
            public string? Name, PublicKeyToken, Architecture, Language, Version;
            public string? IdentityString;

            public string? CatPath;
            public bool CatExists;
        }

        public sealed class Plan
        {
            public bool Success;
            public string Error = "";
            public string? BatchPath;
            public List<Operation> Operations { get; } = new();
            public List<string> Warnings { get; } = new();

            /// <summary>安装（成为当前 edition）的包。</summary>
            public IEnumerable<Operation> Install => Operations.WhereTrue(o => o.Intent == PackageIntent.Installed);
            /// <summary>暂存（进入 target editions）的包。</summary>
            public IEnumerable<Operation> Stage => Operations.WhereTrue(o => o.Intent == PackageIntent.Staged);
            public IEnumerable<Operation> Remove => Operations.WhereTrue(o => o.Intent == PackageIntent.Absent);
            public int Unresolved => Operations.Count(o => o.Intent != PackageIntent.Unknown && !o.FileExists);

            public string Summary()
            {
                int ip = 0, sp = 0, up = 0;
                foreach (var o in Operations)
                {
                    switch (o.Intent)
                    {
                        case PackageIntent.Installed: ip++; break;
                        case PackageIntent.Staged: sp++; break;
                        case PackageIntent.Absent: up++; break;
                    }
                }
                return $"操作 {Operations.Count} 条（安装 {ip} / 暂存 {sp} / 移除 {up}），未解析 {Unresolved}";
            }
        }

        /// <summary>把 cbss.cmd 里的 <c>/s:X:\temp</c> 之类未知选项记录为警告而不是失败（cbsx 实际会忽略）。</summary>
        static readonly Regex OptRe = new(@"^/(?<k>[A-Za-z?]+)(?::(?<v>.*))?$", RegexOptions.Compiled);

        /// <summary>
        /// 解析批处理文件。baseDirs 用于解析 /m: 的相对路径（按顺序尝试）：
        /// 默认 [当前工作目录] + [批处理文件所在目录] + [批处理文件所在目录的父目录]
        /// （cbss.cmd 把 skuassembly.txt 放在 files\ 下，而 mum 在兄弟目录 sxs\ 下）。
        /// </summary>
        public static Plan Parse(string batchPath, IReadOnlyList<string>? baseDirs = null)
        {
            var plan = new Plan { BatchPath = batchPath };
            try
            {
                if (!File.Exists(batchPath)) { plan.Error = "批处理文件不存在: " + batchPath; return plan; }

                var dirs = new List<string>();
                void AddDir(string? d)
                {
                    if (string.IsNullOrEmpty(d)) return;
                    try { d = Path.GetFullPath(d); } catch { return; }
                    if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
                }

                if (baseDirs != null) foreach (var d in baseDirs) AddDir(d);
                AddDir(Directory.GetCurrentDirectory());
                AddDir(Path.GetDirectoryName(Path.GetFullPath(batchPath)));
                AddDir(Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(batchPath))!)?.FullName);

                int lineNo = 0;
                foreach (var raw in File.ReadAllLines(batchPath))
                {
                    lineNo++;
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("::") || line.StartsWith(";"))
                        continue;

                    var op = new Operation { LineNumber = lineNo };
                    foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var m = OptRe.Match(token);
                        if (!m.Success) { op.Err = "无法识别的参数: " + token; break; }

                        string key = m.Groups["k"].Value;
                        string val = m.Groups["v"].Success ? m.Groups["v"].Value : "";

                        switch (key.ToLowerInvariant())
                        {
                            case "ip": SetIntent(op, PackageIntent.Installed); break;
                            case "sp": SetIntent(op, PackageIntent.Staged); break;
                            case "up": SetIntent(op, PackageIntent.Absent); break;
                            case "m": op.RawManifestPath = val; break;
                            // 命令行级/被 cbsx 忽略的选项，记录但不算错
                            case "ep": case "dbg": case "?":
                                plan.Warnings.Add($"L{lineNo}: 忽略命令行级选项 /{key}");
                                break;
                            case "o": case "log": case "b": case "s":
                                plan.Warnings.Add($"L{lineNo}: 选项 /{key}:{val} 应由调用方处理，批处理内已忽略");
                                break;
                            default:
                                plan.Warnings.Add($"L{lineNo}: 未知选项 /{key}，已忽略");
                                break;
                        }
                    }

                    if (op.Err == null)
                    {
                        if (op.Intent == PackageIntent.Unknown) op.Err = "缺少包操作（/ip | /sp | /up）";
                        else if (op.RawManifestPath.Length == 0) op.Err = "缺少 /m:<mum> 参数";
                    }

                    if (op.Err == null) Resolve(op, dirs);
                    plan.Operations.Add(op);
                }

                plan.Success = plan.Operations.All(o => o.Err == null);
                if (!plan.Success) plan.Error = "存在无法解析的行";
                return plan;
            }
            catch (Exception ex) { plan.Error = ex.GetType().Name + ": " + ex.Message; return plan; }
        }

        static void SetIntent(Operation op, PackageIntent intent)
        {
            if (op.Intent != PackageIntent.Unknown) { op.Err = "同一行出现多个包操作"; return; }
            op.Intent = intent;
        }

        /// <summary>解析 /m: 路径并读取 mum 身份（含 .cat 存在性）。</summary>
        static void Resolve(Operation op, List<string> baseDirs)
        {
            string p = op.RawManifestPath;
            var candidates = new List<string>();

            if (Path.IsPathRooted(p)) candidates.Add(p);
            else foreach (var d in baseDirs) candidates.Add(Path.Combine(d, p));

            foreach (var c in candidates)
            {
                if (File.Exists(c)) { op.ResolvedPath = c; op.FileExists = true; break; }
            }

            if (!op.FileExists)
            {
                op.ResolvedPath = candidates.Count > 0 ? candidates[0] : p;
                op.Err = "找不到 mum（已尝试 " + candidates.Count + " 个位置）";
                return;
            }

            // 身份：mum 是明文 XML（CBS 包清单不压缩）；压缩时走 ManifestDecompressor
            try
            {
                string? xml = ManifestDecompressor.ReadXmlText(op.ResolvedPath!, out string? why);
                if (xml == null) { op.Err = "mum 无法解析: " + (why ?? "未知"); return; }

                var doc = XDocument.Parse(xml);
                var ai = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity");
                if (ai == null) { op.Err = "mum 内无 assemblyIdentity"; return; }

                op.Name = (string?)ai.Attribute("name");
                op.PublicKeyToken = (string?)ai.Attribute("publicKeyToken");
                op.Architecture = (string?)ai.Attribute("processorArchitecture");
                op.Language = (string?)ai.Attribute("language");
                op.Version = (string?)ai.Attribute("version");
                op.IdentityString = WinSxSHashCalculator.GenerateCBSKeyform(
                    op.Name ?? "", op.PublicKeyToken ?? "", op.Architecture ?? "",
                    op.Language ?? "", op.Version ?? "");

                // 同名 .cat（与 mum 同级）
                string cat = Path.ChangeExtension(op.ResolvedPath, ".cat");
                op.CatPath = cat;
                op.CatExists = File.Exists(cat);
            }
            catch (Exception ex) { op.Err = "解析 mum 失败: " + ex.Message; }
        }

        /// <summary>把计划渲染成人可读文本（供 UI 预览）。</summary>
        public static string Render(Plan plan)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SKU assembly 计划");
            sb.AppendLine("  批处理 : " + (plan.BatchPath ?? "(none)"));
            sb.AppendLine("  " + plan.Summary());
            sb.AppendLine();
            foreach (var o in plan.Operations)
            {
                string intent = o.Intent switch
                {
                    PackageIntent.Installed => "安装 /ip",
                    PackageIntent.Staged => "暂存 /sp",
                    PackageIntent.Absent => "移除 /up",
                    _ => "?"
                };
                sb.AppendLine($"  L{o.LineNumber,3}  {intent}  {o.Name ?? Path.GetFileName(o.RawManifestPath)}");
                sb.AppendLine($"        identity : {o.IdentityString}");
                sb.AppendLine($"        路径     : {o.ResolvedPath}");
                sb.AppendLine($"        cat      : {(o.CatExists ? "有" : "缺")} {o.CatPath}");
                if (o.Err != null) sb.AppendLine($"        ⚠ {o.Err}");
            }
            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  警告:");
                foreach (var w in plan.Warnings) sb.AppendLine("    " + w);
            }
            return sb.ToString();
        }
    }

    internal static class PlanEnumerableExtensions
    {
        public static IEnumerable<T> WhereTrue<T>(this IEnumerable<T> src, Func<T, bool> pred)
        {
            foreach (var x in src) if (pred(x)) yield return x;
        }
    }
}
