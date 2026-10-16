using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 按"目标 SKU（edition）"生成组装计划。
    ///
    /// 【为什么需要】SKU 由装哪个 `Microsoft-Windows-Editions-*` 包决定。
    /// 早前实测时的错误做法是**一次装了 5 个 edition**（Core / CoreSingleLanguage /
    /// Professional / ServerDatacenter / ServerStandard）—— 那**不是一个正常单 SKU 镜像**该有的样子。
    /// 真实组装应当：**只装一个 edition**，加上它的依赖闭包。
    ///
    /// 【本服务做什么】
    ///   ① 在给定的 SxS 内容里**发现可用的 SKU**（扫描 editions 包）
    ///   ② 以**选定的那个 edition 为唯一种子**，用 <see cref="SystemAssemblyService"/>
    ///      算依赖闭包并排拓扑序
    ///   ③ 产出可直接执行的 SKU 计划（`/ip /m:&lt;mum&gt;` 行），供 `CbsxService` / DISM 使用
    ///
    /// 【产品线】属 **desktop**（CBS）。edition 包是 desktop 的概念
    /// （`Microsoft-Windows-Editions-*`），WCOS 侧的 SKU 由 FM 的 feature 组合定义。
    /// </summary>
    public static class SkuPlanBuilder
    {
        public sealed class Edition
        {
            public string Name = "";        // 例 core / professional / enterprise
            public string Identity = "";    // 完整 CBS 包名
            public string Architecture = "";
            public string Version = "";
            /// <summary>语言段；空 = 语言中性（~~）。中性变体才是"这个 SKU 本身"。</summary>
            public string Language = "";
            public string MumPath = "";
            /// <summary>原始文件名里的 edition 段（小写）。</summary>
            public string RawName => Name;
        }

        // 形如 microsoft-windows-editions-<name>-package~<ptk>~<arch>~<lang>~<ver>.mum
        static readonly Regex EditionsRe = new(
            @"^microsoft-windows-editions-(?<name>[a-z0-9]+)-package~(?<ptk>[0-9a-f]+)~(?<arch>[a-z0-9]+)~(?<lang>[^~]*)~(?<ver>[0-9.]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>把 edition 名归一化：允许用户写 "Professional" / "professional" / "pro"。</summary>
        public static string NormalizeEdition(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            // 去掉常见后缀/前缀写法
            if (s.StartsWith("microsoft-windows-editions-")) s = s["microsoft-windows-editions-".Length..];
            if (s.EndsWith("-package")) s = s[..^"-package".Length];
            // 常见别名
            return s switch
            {
                "pro" => "professional",
                "proN" => "professionaln",
                "home" => "core",
                "homepremium" => "core",
                "ent" => "enterprise",
                "serverdc" => "serverdatacenter",
                "serverstd" => "serverstandard",
                _ => s
            };
        }

        /// <summary>
        /// 在给定 SxS 内容里发现可用的 SKU。
        /// </summary>
        /// <param name="sourceDirs">SxS 内容目录（会递归找 .mum）</param>
        /// <param name="archFilter">只保留该架构（null/空 = 全部）</param>
        public static List<Edition> DiscoverEditions(IEnumerable<string> sourceDirs, string? archFilter = null,
                                                    Action<string>? log = null)
        {
            var found = new Dictionary<string, Edition>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            foreach (var dir in sourceDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)))
            {
                IEnumerable<string> mums;
                try { mums = Directory.EnumerateFiles(dir, "*.mum", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var m in mums)
                {
                    scanned++;
                    string stem = Path.GetFileNameWithoutExtension(m);
                    var mm = EditionsRe.Match(stem);
                    if (!mm.Success) continue;

                    string arch = mm.Groups["arch"].Value;
                    if (!string.IsNullOrWhiteSpace(archFilter)
                        && !arch.Equals(archFilter, StringComparison.OrdinalIgnoreCase)) continue;

                    string name = NormalizeEdition(mm.Groups["name"].Value);
                    var e = new Edition
                    {
                        Name = name,
                        Identity = stem,
                        Architecture = arch,
                        Version = mm.Groups["ver"].Value,
                        Language = mm.Groups["lang"].Value,
                        MumPath = m
                    };
                    // 同名的挑选顺序：**语言中性优先**（那才是这个 SKU 本身，而不是某个语言的
                    // 本地化变体），其次版本更高的。
                    // 早前只比版本，结果从 36 个候选中挑中了 ar-sa 变体 —— 错的。
                    if (!found.TryGetValue(name, out var prev))
                        found[name] = e;
                    else
                    {
                        bool eNeutral = string.IsNullOrWhiteSpace(e.Language);
                        bool pNeutral = string.IsNullOrWhiteSpace(prev.Language);
                        if (eNeutral && !pNeutral) found[name] = e;
                        else if (eNeutral == pNeutral && CompareVersion(e.Version, prev.Version) > 0)
                            found[name] = e;
                    }
                }
            }
            log?.Invoke($"扫描 {scanned} 个 mum，发现 {found.Count} 个可用 SKU");
            return found.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static int CompareVersion(string a, string b)
        {
            var pa = a.Split('.'); var pb = b.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }

        public sealed class Plan
        {
            public bool Success;
            public string Error = "";
            public Edition? Edition;
            /// <summary>按依赖优先排序的安装序列（CBS 包名）。</summary>
            public readonly List<string> Order = new();
            /// <summary>与 Order 对应的 mum 路径（找不到为 null）。</summary>
            public readonly List<string?> MumPaths = new();
            public readonly List<string> Missing = new();
            public readonly List<string> Cycles = new();
            public readonly List<string> Log = new();

            public string Summary()
                => $"SKU={Edition?.Name ?? "(未选)"} 架构={Edition?.Architecture} 版本={Edition?.Version}；"
                 + $"闭包 {Order.Count} 个，缺失 {Missing.Count}，环 {Cycles.Count}";

            /// <summary>渲染成 skuassembly.txt 内容（`/ip /m:&lt;mum&gt;` 每行一条）。</summary>
            public string ToSkuAssemblyText(bool staged = false)
            {
                string verb = staged ? "/sp" : "/ip";
                var sb = new StringBuilder();
                for (int i = 0; i < Order.Count; i++)
                {
                    string? p = MumPaths[i];
                    if (p == null) continue;
                    sb.Append(verb).Append(" /m:").Append(p).Append('\n');
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 以选定的 edition 为唯一种子，算闭包与安装顺序。
        /// </summary>
        /// <param name="sourceDirs">SxS 内容目录</param>
        /// <param name="edition">目标 SKU（可写 professional / Professional / pro）</param>
        /// <param name="archFilter">架构过滤（例 x86 / amd64）</param>
        /// <param name="staged">true = 用 /sp（暂存），false = /ip（安装）</param>
        public static Plan Build(IEnumerable<string> sourceDirs, string edition, string? archFilter = null,
                                 Action<string>? log = null)
        {
            var plan = new Plan();
            try
            {
                // ⚠ BuildGraph 只扫【顶层】（外加 Manifests 子目录）。而实际拿到的
                // "SxS 存储根"（例：某个解包出来的 rc_store 目录）下面是一层包目录，mum 在包目录里 ——
                // 直接传根目录会得到 0 个节点。所以这里把每个来源展开成
                // 「自身 + 其一级子目录」。
                var dirs = new List<string>();
                foreach (var d in sourceDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)))
                {
                    dirs.Add(d);
                    try { dirs.AddRange(Directory.GetDirectories(d)); } catch { }
                }
                dirs = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                string want = NormalizeEdition(edition);
                if (want.Length == 0) { plan.Error = "未指定目标 SKU（edition）"; return plan; }

                var editions = DiscoverEditions(dirs, archFilter, log);
                var chosen = editions.FirstOrDefault(e => e.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
                if (chosen == null)
                {
                    plan.Error = $"在给定内容里找不到 SKU『{want}』。可用："
                               + string.Join(", ", editions.Select(e => e.Name));
                    log?.Invoke("✗ " + plan.Error);
                    return plan;
                }
                plan.Edition = chosen;
                log?.Invoke($"目标 SKU: {chosen.Name}（{chosen.Identity}）");

                // 依赖闭包：以该 edition 为**唯一**种子
                var g = SystemAssemblyService.BuildGraph(dirs, log);
                log?.Invoke($"依赖图: {g.Nodes.Count} 个节点");

                var closure = SystemAssemblyService.PlanFrom(g, new[] { chosen.Identity }, log);
                if (!closure.Success) { plan.Error = "依赖闭包失败: " + closure.Error; return plan; }
                plan.Missing.AddRange(closure.Missing);
                plan.Cycles.AddRange(closure.Cycles);

                // 拓扑序 → 只保留包节点，并带上 mum 路径
                foreach (var key in closure.Order)
                {
                    if (!g.Nodes.TryGetValue(key, out var node)) continue;
                    if (node.Kind != SystemAssemblyService.NodeKind.Package) continue;
                    plan.Order.Add(key);
                    plan.MumPaths.Add(node.ManifestPath);
                }

                // 种子本身必须在最前（它是"要装的那个 SKU"）
                int idx = plan.Order.FindIndex(o => o.Equals(chosen.Identity, StringComparison.OrdinalIgnoreCase));
                if (idx > 0)
                {
                    var k = plan.Order[idx]; plan.Order.RemoveAt(idx); plan.MumPaths.RemoveAt(idx);
                    plan.Order.Insert(0, k); plan.MumPaths.Insert(0, chosen.MumPath);
                }
                else if (idx < 0)
                {
                    // 图中没找到它（例如只管了 mum 目录）→ 至少把种子本身放进去
                    plan.Order.Insert(0, chosen.Identity);
                    plan.MumPaths.Insert(0, chosen.MumPath);
                }

                plan.Success = true;
                log?.Invoke("结果: " + plan.Summary());
                log?.Invoke($"安装顺序前 8 项: {string.Join(" | ", plan.Order.Take(8))}");
                return plan;
            }
            catch (Exception ex) { plan.Error = ex.GetType().Name + ": " + ex.Message; return plan; }
        }

        /// <summary>渲染可用 SKU 列表（供界面/日志显示）。</summary>
        public static string RenderEditions(List<Edition> editions)
        {
            var sb = new StringBuilder();
            if (editions.Count == 0) { sb.AppendLine("(未发现可用 SKU)"); return sb.ToString(); }
            sb.AppendLine($"可用 SKU（{editions.Count} 个）：");
            foreach (var e in editions)
                sb.AppendLine($"  {e.Name,-28} {e.Architecture,-6} {e.Version}");
            return sb.ToString().TrimEnd();
        }
    }
}
