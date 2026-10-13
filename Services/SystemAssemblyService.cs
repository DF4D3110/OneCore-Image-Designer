using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 系统组装（system assembly）：从一份 SxS 内容出发，算清组件之间的联系，
    /// 产出可执行的组装计划，再交给 CBS 通道去真正装。
    ///
    /// 【定位】WPMT 那类工具负责"做包"（重命名/整理包树），**组装主力仍是 CBS**。
    /// 本服务补的是中间那层：**组件之间的联系（依赖图）** + 组装顺序 + 与 CBS 通道的对接。
    ///
    /// 【组件之间的联系来自两处】
    ///   1. 包的 update.mum：
    ///        assembly/package/update/package/assemblyIdentity    → 依赖子包
    ///        assembly/package/update/component/assemblyIdentity  → 依赖组件（deployment）
    ///        assembly/package/update/driver/assemblyIdentity     → 依赖驱动（按程序集处理）
    ///      以及 assembly/package/file 的 payload 落点
    ///   2. 组件的 manifest：
    ///        assembly/dependency/dependentAssembly/assemblyIdentity → 依赖程序集
    ///
    /// 【为什么需要图】组装必须按依赖顺序进行，而且要能回答"给定这套包，闭包是否完整、
    /// 缺什么、装哪些"。这与 pureSxS 的导出遍历是同一张图，只是一个用于导出、一个用于组装。
    ///
    /// 【世代】执行阶段交给 <see cref="ServicingRouter"/> 自动选通道：
    ///   6.2 体系 → 原生 CBS（可控、可 dry-run）；其它 → DISM。
    /// </summary>
    public static class SystemAssemblyService
    {
        // ────────────────────────────────────────────────────────────────
        // 依赖图
        // ────────────────────────────────────────────────────────────────

        public enum NodeKind { Package, Component, Driver, Assembly }

        public sealed class Node
        {
            public string Key = "";                 // CBS 包名 或 SxS 名
            public NodeKind Kind;
            /// <summary>该节点在本地的清单路径（.mum 或 .manifest），找不到为 null。</summary>
            public string? ManifestPath;
            /// <summary>同名 .cat 是否存在。</summary>
            public bool HasCatalog;
            /// <summary>出边：它依赖谁。</summary>
            public readonly List<string> DependsOn = new();
            /// <summary>入边：谁依赖它（组装顺序用它做拓扑）。</summary>
            public readonly List<string> RequiredBy = new();
            public string? Error;
        }

        public sealed class Graph
        {
            public readonly Dictionary<string, Node> Nodes = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Warnings = new();
            public int MissingDependencies;

            public Node GetOrAdd(string key, NodeKind kind)
            {
                if (Nodes.TryGetValue(key, out var n)) return n;
                n = new Node { Key = key, Kind = kind };
                Nodes[key] = n;
                return n;
            }
        }

        /// <summary>
        /// 在 srcDir 下扫描 SxS 内容（.mum / .manifest / .cat），构建依赖图。
        /// srcDir 可以是多根之一（如同时给包目录与 Manifest 目录）。
        /// </summary>
        public static Graph BuildGraph(IEnumerable<string> srcDirs, Action<string>? log = null)
        {
            var g = new Graph();
            var dirs = srcDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct().ToList();

            // 建立文件名索引（大小写不敏感）
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly))
                {
                    string bn = Path.GetFileName(f);
                    byName.TryAdd(bn, f);
                }
                foreach (var sub in new[] { "Manifests", "manifest" })
                {
                    string md = Path.Combine(d, sub);
                    if (!Directory.Exists(md)) continue;
                    foreach (var f in Directory.EnumerateFiles(md, "*.manifest", SearchOption.TopDirectoryOnly))
                        byName.TryAdd(Path.GetFileName(f), f);
                }
            }
            log?.Invoke($"索引到 {byName.Count} 个文件");

            // 先处理所有 .mum（包层）
            foreach (var kv in byName.Where(kv => kv.Key.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string pkgName = Path.GetFileNameWithoutExtension(kv.Key);
                var node = g.GetOrAdd(pkgName, NodeKind.Package);
                node.ManifestPath = kv.Value;
                node.HasCatalog = byName.ContainsKey(pkgName + ".cat");
                ParseMum(kv.Value, g, node, byName, log);
            }

            // 再处理 .manifest（组件层）—— 只为图中已有的组件建节点，避免把整个 WinSxS 拉进来
            foreach (var kv in byName.Where(kv => kv.Key.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string sxs = Path.GetFileNameWithoutExtension(kv.Key);
                if (!g.Nodes.TryGetValue(sxs, out var node))
                {
                    node = g.GetOrAdd(sxs, NodeKind.Component);
                }
                node.ManifestPath = kv.Value;
                ParseManifest(kv.Value, g, node, byName, log);
            }

            // 统计缺口 + 反填 RequiredBy
            foreach (var n in g.Nodes.Values)
            {
                foreach (var dep in n.DependsOn)
                {
                    if (!g.Nodes.ContainsKey(dep)) { g.MissingDependencies++; continue; }
                    g.Nodes[dep].RequiredBy.Add(n.Key);
                }
            }
            log?.Invoke($"依赖图：{g.Nodes.Count} 个节点，{g.MissingDependencies} 个依赖目标不在本地");
            return g;
        }

        static void ParseMum(string path, Graph g, Node node, Dictionary<string, string> byName, Action<string>? log)
        {
            XmlDocument doc;
            try
            {
                string? xml = ManifestDecompressor.ReadXmlText(path, out _);
                if (xml == null) { node.Error = "mum 无法读取"; return; }
                doc = new XmlDocument();
                doc.LoadXml(xml);
            }
            catch (Exception ex) { node.Error = "mum 解析失败: " + ex.Message; return; }

            AddRefs(doc, "./*[local-name()='package']/*[local-name()='update']/*[local-name()='package']/*[local-name()='assemblyIdentity']",
                    NodeKind.Package, g, node, byName);
            AddRefs(doc, "./*[local-name()='package']/*[local-name()='update']/*[local-name()='component']/*[local-name()='assemblyIdentity']",
                    NodeKind.Component, g, node, byName);
            AddRefs(doc, "./*[local-name()='package']/*[local-name()='update']/*[local-name()='driver']/*[local-name()='assemblyIdentity']",
                    NodeKind.Driver, g, node, byName);
        }

        static void ParseManifest(string path, Graph g, Node node, Dictionary<string, string> byName, Action<string>? log)
        {
            XmlDocument doc;
            try
            {
                string? xml = ManifestDecompressor.ReadXmlText(path, out _);
                if (xml == null) { node.Error = "manifest 无法读取"; return; }
                doc = new XmlDocument();
                doc.LoadXml(xml);
            }
            catch (Exception ex) { node.Error = "manifest 解析失败: " + ex.Message; return; }

            AddRefs(doc, "./*[local-name()='dependency']/*[local-name()='dependentAssembly']/*[local-name()='assemblyIdentity']",
                    NodeKind.Assembly, g, node, byName);
        }

        static void AddRefs(XmlDocument doc, string xpath, NodeKind kind, Graph g, Node node,
                            Dictionary<string, string> byName)
        {
            var list = doc.SelectNodes(xpath);
            if (list == null) return;
            foreach (XmlNode n in list)
            {
                if (n is not XmlElement id) continue;
                string key = kind switch
                {
                    NodeKind.Package => WinSxSHashCalculator.GenerateCBSKeyform(
                        id.GetAttribute("name"), id.GetAttribute("publicKeyToken"),
                        id.GetAttribute("processorArchitecture"), id.GetAttribute("language"),
                        id.GetAttribute("version")),
                    _ => SxSNameOf(id)
                };
                if (string.IsNullOrEmpty(key)) continue;

                var dep = g.GetOrAdd(key, kind);
                if (dep.ManifestPath == null)
                {
                    string file = kind == NodeKind.Package ? key + ".mum" : key + ".manifest";
                    if (byName.TryGetValue(file, out var p)) dep.ManifestPath = p;
                    if (kind == NodeKind.Package && byName.ContainsKey(key + ".cat")) dep.HasCatalog = true;
                }
                if (!node.DependsOn.Contains(key, StringComparer.OrdinalIgnoreCase)) node.DependsOn.Add(key);
            }
        }

        /// <summary>与 PureSxsService 一致的组件名推导（含 .inf 驱动的 type）。</summary>
        static string SxSNameOf(XmlElement id)
        {
            string version = id.GetAttribute("version");
            if (version.Length == 0) return "";
            string culture = id.GetAttribute("language");
            if (culture == "*") return "";
            string arch = id.GetAttribute("processorArchitecture");
            if (arch.Length == 0 || arch.Equals("neutral", StringComparison.OrdinalIgnoreCase)) arch = "none";
            if (culture.Length == 0 || culture.Equals("neutral", StringComparison.OrdinalIgnoreCase)) culture = "none";
            string type = id.GetAttribute("type");
            if (type.Length == 0) type = id.GetAttribute("typeName");
            return WinSxSHashCalculator.GenerateWinSxFolderName(
                id.GetAttribute("name"), id.GetAttribute("publicKeyToken"), arch, version, culture,
                string.IsNullOrEmpty(id.GetAttribute("versionScope")) ? null : id.GetAttribute("versionScope"),
                string.IsNullOrEmpty(type) ? null : type);
        }

        // ────────────────────────────────────────────────────────────────
        // 闭包与拓扑序
        // ────────────────────────────────────────────────────────────────

        public sealed class AssemblyPlan
        {
            public bool Success;
            public string Error = "";
            /// <summary>按依赖优先排序的安装序列（先依赖，后被依赖者）。</summary>
            public readonly List<string> Order = new();
            public readonly List<string> Missing = new();
            public readonly List<string> Cycles = new();
            public int TotalNodes;
            public readonly List<string> Log = new();

            public string Summary()
                => $"闭包 {Order.Count} 个（图中共 {TotalNodes} 个），缺失 {Missing.Count}，环 {Cycles.Count}";
        }

        /// <summary>
        /// 从种子节点算闭包并做拓扑排序 —— 这就是"组装顺序"。
        /// 依赖优先（先装被依赖的）。
        /// </summary>
        public static AssemblyPlan PlanFrom(Graph g, IEnumerable<string> seeds, Action<string>? log = null)
        {
            var plan = new AssemblyPlan { TotalNodes = g.Nodes.Count };
            try
            {
                // 闭包
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var stack = new Stack<string>(seeds);
                while (stack.Count > 0)
                {
                    string k = stack.Pop();
                    if (!seen.Add(k)) continue;
                    if (!g.Nodes.TryGetValue(k, out var n)) { plan.Missing.Add(k); continue; }
                    foreach (var d in n.DependsOn)
                    {
                        if (!g.Nodes.ContainsKey(d)) plan.Missing.Add(d);
                        else if (!seen.Contains(d)) stack.Push(d);
                    }
                }

                // 拓扑序（DFS，依赖优先）
                var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=未访问 1=进行中 2=完成
                var order = new List<string>();
                var cycleStack = new List<string>();

                void Visit(string k)
                {
                    if (state.TryGetValue(k, out int s))
                    {
                        if (s == 1) { plan.Cycles.Add(string.Join(" -> ", cycleStack.SkipWhile(x => !x.Equals(k, StringComparison.OrdinalIgnoreCase)).Append(k))); return; }
                        if (s == 2) return;
                    }
                    if (!g.Nodes.TryGetValue(k, out var n)) { state[k] = 2; return; }

                    state[k] = 1;
                    cycleStack.Add(k);
                    foreach (var d in n.DependsOn)
                        if (g.Nodes.ContainsKey(d)) Visit(d);
                    cycleStack.RemoveAt(cycleStack.Count - 1);
                    state[k] = 2;
                    order.Add(k);
                }

                foreach (var k in seen.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    Visit(k);

                plan.Order.AddRange(order);
                plan.Success = true;
                log?.Invoke($"组装计划：{plan.Summary()}");
                if (plan.Missing.Count > 0)
                    log?.Invoke("  缺失依赖（前 10）：" + string.Join(", ", plan.Missing.Take(10)));
                if (plan.Cycles.Count > 0)
                    log?.Invoke("  检测到环（前 3）：" + string.Join(" | ", plan.Cycles.Take(3)));
                return plan;
            }
            catch (Exception ex) { plan.Error = ex.GetType().Name + ": " + ex.Message; return plan; }
        }

        // ────────────────────────────────────────────────────────────────
        // 组装（执行）
        // ────────────────────────────────────────────────────────────────

        public sealed class Config
        {
            /// <summary>SxS 内容根列表（包目录、Manifest 目录等）。</summary>
            public List<string> SourceDirs = new();
            /// <summary>目标镜像的 Windows 目录（必须位于挂载的 VHD/VHDX 上）。</summary>
            public string WinDir = "";
            /// <summary>SKU assembly 文件（可空）。</summary>
            public string SkuAssemblyFile = "";
            /// <summary>unattend.xml（可空；自组装靠它）。</summary>
            public string UnattendXml = "";
            /// <summary>要安装的包（CBS 包名或 SxS 名）；空 = 用 SKU 计划推导。</summary>
            public List<string> Seeds = new();
            /// <summary>只算计划不执行。</summary>
            public bool PlanOnly = true;
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public AssemblyPlan? Plan;
            public ServicingRouter.Decision? Channel;
            public bool RebootRequired;
            public readonly List<string> Log = new();
        }

        /// <summary>
        /// 组装流程：建图 → 算闭包与顺序 → 选通道 → 执行（或只出计划）。
        /// </summary>
        public static async Task<Result> AssembleAsync(Config cfg, Action<string>? log = null, CancellationToken ct = default)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }

            try
            {
                // ① 依赖图
                L("=== ① 构建依赖图（组件之间的联系） ===");
                var g = BuildGraph(cfg.SourceDirs, log);

                // ② 组装顺序
                L("=== ② 计算闭包与组装顺序 ===");
                var seeds = new List<string>(cfg.Seeds);
                if (seeds.Count == 0 && !string.IsNullOrWhiteSpace(cfg.SkuAssemblyFile))
                {
                    var sku = SkuAssemblyService.Parse(cfg.SkuAssemblyFile);
                    foreach (var op in sku.Operations)
                    {
                        if (op.IdentityString != null) seeds.Add(op.IdentityString);
                        else if (op.Name != null) seeds.Add(op.Name);
                    }
                    L($"从 SKU 计划取到 {seeds.Count} 个种子");
                }
                if (seeds.Count == 0)
                {
                    // 没有种子：把图里所有"包"节点当种子（整包集合组装）
                    seeds.AddRange(g.Nodes.Values.Where(n => n.Kind == NodeKind.Package).Select(n => n.Key));
                    L($"未指定种子，改用图中全部 {seeds.Count} 个包节点");
                }

                res.Plan = PlanFrom(g, seeds, log);
                if (!res.Plan.Success) { res.Error = "组装计划失败: " + res.Plan.Error; return res; }
                L("  " + res.Plan.Summary());

                // ③ 选通道
                L("=== ③ 选择 CBS 通道（按镜像世代） ===");
                res.Channel = ServicingRouter.Decide(cfg.WinDir);
                L("  " + res.Channel.Reason);

                if (cfg.PlanOnly)
                {
                    L("=== 只出计划（PlanOnly），未执行 ===");
                    res.Success = true;
                    return res;
                }

                // ④ 执行
                L("=== ④ 执行组装 ===");
                if (res.Channel.Channel == ServicingRouter.Channel.NativeCbs)
                {
                    // 原生通道：把计划里的包转成 SKU 计划（全部 /sp 暂存），交给 CbsxService
                    var plan = new SkuAssemblyService.Plan();
                    int added = 0;
                    foreach (var key in res.Plan.Order)
                    {
                        if (!g.Nodes.TryGetValue(key, out var n) || n.Kind != NodeKind.Package) continue;
                        if (n.ManifestPath == null) continue;
                        plan.Operations.Add(new SkuAssemblyService.Operation
                        {
                            Intent = SkuAssemblyService.PackageIntent.Staged,
                            RawManifestPath = n.ManifestPath,
                            ResolvedPath = n.ManifestPath,
                            FileExists = true,
                            IdentityString = n.Key,
                            Name = Path.GetFileNameWithoutExtension(n.ManifestPath)
                        });
                        added++;
                    }
                    L($"  原生通道：{added} 个包进入 SKU 计划");
                    var r = CbsxService.ApplyPlan(cfg.WinDir, plan, log);
                    res.RebootRequired = r.RebootRequired;
                    res.Success = r.Success;
                    if (!r.Success) res.Error = r.Error;
                }
                else
                {
                    // DISM 通道：逐包 /add-package（DISM 自己处理依赖与顺序）
                    int ok = 0, fail = 0;
                    foreach (var key in res.Plan.Order.Where(k => g.Nodes.TryGetValue(k, out var n) && n.Kind == NodeKind.Package))
                    {
                        ct.ThrowIfCancellationRequested();
                        var n = g.Nodes[key];
                        if (n.ManifestPath == null) continue;
                        var r = DismServicingService.AddPackage(cfg.WinDir, n.ManifestPath);
                        if (r.Success) ok++; else { fail++; L($"  ✗ {key}: {r.Error}"); }
                    }
                    L($"  DISM 通道：成功 {ok}，失败 {fail}");
                    res.Success = fail == 0;
                    if (!res.Success) res.Error = $"{fail} 个包安装失败";
                }

                // unattend（自组装）
                if (res.Success && !string.IsNullOrWhiteSpace(cfg.UnattendXml))
                {
                    L("=== 应用 unattend（自组装） ===");
                    var u = DismServicingService.ApplyUnattend(cfg.WinDir, cfg.UnattendXml);
                    L("  " + (u.Success ? "OK" : u.Error));
                    if (!u.Success) { res.Success = false; res.Error = u.Error; }
                }

                foreach (var l in res.Log) { }
                return res;
            }
            catch (OperationCanceledException) { res.Error = "已取消"; return res; }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>渲染依赖图（供 UI 预览）。</summary>
        public static string RenderGraph(Graph g, int maxNodes = 60)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"依赖图：{g.Nodes.Count} 个节点，缺失依赖 {g.MissingDependencies}");
            sb.AppendLine();
            foreach (var n in g.Nodes.Values.OrderByDescending(x => x.DependsOn.Count).Take(maxNodes))
            {
                sb.AppendLine($"[{n.Kind}] {n.Key}{(n.HasCatalog ? "  (cat)" : "")}");
                if (n.DependsOn.Count > 0)
                    sb.AppendLine("    依赖 → " + string.Join(", ", n.DependsOn.Take(6))
                                  + (n.DependsOn.Count > 6 ? $" …(+{n.DependsOn.Count - 6})" : ""));
                if (n.ManifestPath == null) sb.AppendLine("    ⚠ 本地缺少清单");
                if (n.Error != null) sb.AppendLine("    ✗ " + n.Error);
            }
            if (g.Nodes.Count > maxNodes) sb.AppendLine($"…（还有 {g.Nodes.Count - maxNodes} 个节点未显示）");
            return sb.ToString();
        }
    }
}
