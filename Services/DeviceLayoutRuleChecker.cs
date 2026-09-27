using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneCore_Image_Designer.Models.DeviceLayout;

namespace OneCore_Image_Designer.Services
{
    /// <summary>校验诊断条目。</summary>
    public record RuleIssue(bool IsError, string Where, string Message);

    /// <summary>一次校验的完整结果。</summary>
    public class RuleCheckResult
    {
        public bool Passed => !Issues.Any(i => i.IsError);
        public List<RuleIssue> Issues { get; } = new();
        public string RuleFile { get; set; } = string.Empty;
        public int CheckedPartitions { get; set; }

        public void SummarizeTo(Action<string> log)
        {
            log($"校验规则: {Path.GetFileName(RuleFile)} — {(Passed ? "✅ 通过" : "❌ 未通过")}" +
                $"（检查 {CheckedPartitions} 个分区，{Issues.Count(i => i.IsError)} 错误 / {Issues.Count(i => !i.IsError)} 提示）");
            foreach (var i in Issues)
                log($"   {(i.IsError ? "✗" : "ℹ")} [{i.Where}] {i.Message}");
        }
    }

    /// <summary>
    /// 设备布局官方规则校验器。
    ///
    /// 规则来源：ADK <c>DeviceLayoutValidation.dll</c> 内嵌 XML（已按 ADK 时代提取到
    /// Assets\ValidationRules\{era}\）。规则形状与 DeviceLayout.xml 同构，但每个字段是
    /// 「约束表达式」而非数值，官方语法（从 14 份规则逆向归纳）：
    ///  · *ANY*                             — 任意
    ///  · N / 0xN                           — 精确相等（扇区数按 RulesSectorSize 换算）
    ///  · greater_or_equal(N)               — ≥N
    ///  · between(A,B)                      — A≤x≤B
    ///  · one_of(v1,v2,...)                 — 枚举匹配（GUID 或 0xNN）
    ///  · allow_default_plus_equal(N)       — 缺省放行，否则须 =N
    ///  · allow_default_plus_greater_or_equal(N) — 缺省放行，否则须 ≥N
    ///  · multiple_of(0xN)                  — 对齐为 N 的倍数
    ///  · *PARENT*                          — 与去掉 BACKUP_ 前缀的兄弟分区相等（备份区）
    ///  · Position = -1 / BACKUP_BOOKEND    — -1 必须是末尾分区
    ///  · Optional=true                    — 该规则分区可整体缺席
    /// 布局侧数值与规则数值以 RulesSectorSize 为基准换算（布局 SectorSize 不同时乘倍数）。
    /// 无法识别的表达式记「提示」不记错误——宁可放过，不误杀。
    /// </summary>
    public static class DeviceLayoutRuleChecker
    {
        public static string RulesRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ValidationRules");

        /// <summary>枚举所有时代目录下的规则文件（含 era 标签）。</summary>
        public static List<(string Era, string File, string Name, string Scope)> ListRules()
        {
            var list = new List<(string, string, string, string)>();
            if (!Directory.Exists(RulesRoot)) return list;
            foreach (var dir in Directory.EnumerateDirectories(RulesRoot).OrderBy(d => d))
            {
                string era = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*.xml").OrderBy(f => f))
                {
                    string scope = string.Empty;
                    try
                    {
                        var doc = XDocument.Load(f);
                        scope = (string?)doc.Root?.Element(Ns(doc) + "Scope") ?? string.Empty;
                    }
                    catch { }
                    list.Add((era, f, Path.GetFileName(f), scope));
                }
            }
            return list;
        }

        static XNamespace Ns(XDocument? d) => d?.Root?.Name.Namespace ?? XNamespace.None;

        // ═════════════════════════ 对外入口 ═════════════════════════

        /// <summary>
        /// 用指定规则文件校验布局。layoutFile 与 layoutDoc 二选一：
        /// 传文件时按「原形状」解析（保留 Stores/Pools），传 doc 时按 doc 的三种形状校验。
        /// </summary>
        public static RuleCheckResult Check(DeviceLayoutDoc layout, string ruleFile)
        {
            var res = new RuleCheckResult { RuleFile = ruleFile };
            XDocument rd;
            try { rd = XDocument.Load(ruleFile); }
            catch (Exception ex)
            {
                res.Issues.Add(new RuleIssue(false, "规则", "规则文件无法解析: " + ex.Message));
                return res;
            }
            XElement root = rd.Root!;
            XNamespace ns = root.Name.Namespace;

            long rulesSector = ParseInt((string?)root.Element(ns + "RulesSectorSize") ?? "512") ?? 512;
            // 规则阈值以 RulesSectorSize 计；布局扇区数以自身 SectorSize 计。
            // 同一字节数在不同扇区大小下的「扇区个数」互逆：布局阈值 = 规则阈值 × 规则扇区 / 布局扇区。
            double factor = layout.SectorSize > 0 && rulesSector > 0
                ? (double)rulesSector / layout.SectorSize : 1.0;

            // ── 根级字段 ──
            CheckRootField(res, "SectorSize", (string?)root.Element(ns + "SectorSize"),
                layout.SectorSize.ToString(), factor, isSectorCount: false);
            CheckRootField(res, "ChunkSize", (string?)root.Element(ns + "ChunkSize"),
                layout.ChunkSize.ToString(), factor, isSectorCount: false);
            CheckRootField(res, "DefaultPartitionByteAlignment",
                (string?)root.Element(ns + "DefaultPartitionByteAlignment"),
                layout.DefaultPartitionByteAlignment, factor, isSectorCount: false);

            bool layoutHasPools = layout.StoragePools.Count > 0;
            bool layoutHasStores = layout.Stores.Count > 0;
            bool ruleHasPools = root.Element(ns + "StoragePools") != null;
            bool ruleHasStores = root.Element(ns + "Stores") != null;
            bool ruleHasFlat = root.Element(ns + "Partitions") != null;

            // ── 存储池 ──
            if (ruleHasPools)
            {
                var rulePools = root.Element(ns + "StoragePools")!.Elements(ns + "StoragePool").ToList();
                foreach (var rp in rulePools)
                {
                    string poolName = (string?)rp.Element(ns + "Name") ?? "?";
                    var layoutPool = layoutHasPools
                        ? layout.StoragePools.FirstOrDefault(p =>
                            string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (layoutPool == null)
                    {
                        res.Issues.Add(new RuleIssue(true, $"存储池 {poolName}",
                            layoutHasPools ? "布局中找不到同名存储池" : "布局不是存储池（Spaces）形状，无法提供该池"));
                        continue;
                    }
                    MatchStores(res, rp, layoutPool.Stores, ns, factor, $"池 {poolName}");
                }
            }

            // ── 顶层 Stores（多 LUN） ──
            if (ruleHasStores)
            {
                var ruleStores = root.Element(ns + "Stores")!.Elements(ns + "Store").ToList();
                if (!layoutHasStores)
                {
                    // 布局为平铺/单盘时，允许把规则的 Store 组当整体对平铺分区校验（官方对单盘设备同构处理）
                    if (!layoutHasPools && layout.Partitions.Count > 0 && ruleStores.Count == 1)
                        MatchPartitions(res, ruleStores[0].Element(ns + "Partitions"), layout.Partitions, ns, factor, "平铺");
                    else
                        res.Issues.Add(new RuleIssue(true, "结构",
                            $"规则要求 {ruleStores.Count} 个 Store（多 LUN），布局是平铺/单盘形状"));
                }
                else MatchStores(res, root.Element(ns + "Stores")!, layout.Stores, ns, factor, "LUN");
            }

            // ── 平铺 ──
            if (ruleHasFlat)
            {
                // 官方在单盘规则时期同样校验多 LUN 布局：按分区名展平匹配
                MatchPartitions(res, root.Element(ns + "Partitions"),
                    layoutHasStores || layoutHasPools ? layout.AllPartitions : layout.Partitions, ns, factor, "平铺");
            }

            res.CheckedPartitions = layout.AllPartitions.Count;
            return res;
        }

        // ═════════════════════════ Store 匹配 ═════════════════════════

        static void MatchStores(RuleCheckResult res, XElement ruleStoresEl, List<Store> layoutStores,
            XNamespace ns, double factor, string tag)
        {
            foreach (var rs in ruleStoresEl.Elements(ns + "Store"))
            {
                string id = ((string?)rs.Element(ns + "Id") ?? "").Trim();
                string stype = ((string?)rs.Element(ns + "StoreType") ?? "").Trim();
                var ls = layoutStores.FirstOrDefault(s =>
                    (!string.IsNullOrEmpty(id) && NormGuid(s.Id) == NormGuid(id)) ||
                    (!string.IsNullOrEmpty(stype) && string.Equals(s.StoreType, stype, StringComparison.OrdinalIgnoreCase)));
                if (ls == null)
                {
                    res.Issues.Add(new RuleIssue(true, $"{tag}/{(stype.Length > 0 ? stype : id)}",
                        $"布局缺少该 Store（Id={id}）"));
                    continue;
                }
                MatchPartitions(res, rs.Element(ns + "Partitions"), ls.Partitions, ns, factor, $"{tag}/{stype}");
            }
        }

        // ═════════════════════════ 分区匹配 ═════════════════════════

        static void MatchPartitions(RuleCheckResult res, XElement? rulePartEl,
            List<Partition> layoutParts, XNamespace ns, double factor, string where)
        {
            if (rulePartEl == null) return;
            var ruleParts = rulePartEl.Elements(ns + "Partition").ToList();
            var matchedLayout = new HashSet<Partition>();

            foreach (var rpi in ruleParts)
            {
                string namePat = ((string?)rpi.Element(ns + "Name") ?? "").Trim();
                bool optional = ParseBool((string?)rpi.Element(ns + "Optional"));
                var candidates = layoutParts
                    .Where(p => NameMatches(namePat, p.Name))
                    .Where(p => !namePat.StartsWith("BACKUP_", StringComparison.OrdinalIgnoreCase) ||
                                p.Name.StartsWith("BACKUP_", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0)
                {
                    bool wildcard = namePat.Contains('*') || namePat.Contains('?');
                    if (!optional && !wildcard)
                        res.Issues.Add(new RuleIssue(true, $"{where}:{namePat}", "规则要求的分区在布局中缺失"));
                    else if (wildcard)
                        res.Issues.Add(new RuleIssue(false, $"{where}:{namePat}", "通配模板（如备份区）本轮无匹配实例，按 0 实例放行"));
                    continue;
                }

                foreach (var lp in candidates)
                {
                    matchedLayout.Add(lp);
                    ComparePartition(res, rpi, lp, ns, factor, where, namePat);
                }
            }

            // 布局里的额外分区：官方规则是「最小要求集」（真实布局含大量规则未列分区），
            // 因此未知分区只作提示，不判失败。
            foreach (var lp in layoutParts.Where(p => !matchedLayout.Contains(p)))
                res.Issues.Add(new RuleIssue(false, $"{where}:{lp.Name}", "规则未约束该分区（官方规则为最小集，正常）"));
        }

        static void ComparePartition(RuleCheckResult res, XElement rpi, Partition lp,
            XNamespace ns, double factor, string where, string namePat)
        {
            string at = $"{where}:{lp.Name}";
            Cmp(res, at, "Type/PartitionType", (string?)rpi.Element(ns + "PartitionType"), lp.Type, factor, kind: "type");
            Cmp(res, at, "FileSystem", (string?)rpi.Element(ns + "FileSystem"), lp.FileSystem, factor, kind: "text");
            Cmp(res, at, "TotalSectors", (string?)rpi.Element(ns + "TotalSectors"),
                lp.TotalSectors == 0 ? null : lp.TotalSectors.ToString(), factor, kind: "sector");
            Cmp(res, at, "MinFreeSectors", (string?)rpi.Element(ns + "MinFreeSectors"),
                lp.MinFreeSectors == 0 ? null : lp.MinFreeSectors.ToString(), factor, kind: "sector");
            Cmp(res, at, "ClusterSize", (string?)rpi.Element(ns + "ClusterSize"),
                string.IsNullOrWhiteSpace(lp.ClusterSize) ? null : lp.ClusterSize, factor, kind: "size");
            Cmp(res, at, "ByteAlignment", (string?)rpi.Element(ns + "ByteAlignment"),
                string.IsNullOrWhiteSpace(lp.ByteAlignment) ? null : lp.ByteAlignment, factor, kind: "size");
            Cmp(res, at, "Bootable", (string?)rpi.Element(ns + "Bootable"), BoolOrNull(lp.Bootable), factor, kind: "bool");
            Cmp(res, at, "RequiredToFlash", (string?)rpi.Element(ns + "RequiredToFlash"), BoolOrNull(lp.RequiredToFlash), factor, kind: "bool");
            Cmp(res, at, "UseAllSpace", (string?)rpi.Element(ns + "UseAllSpace"), BoolOrNull(lp.UseAllSpace), factor, kind: "bool");
            Cmp(res, at, "AttachDriveLetter", (string?)rpi.Element(ns + "AttachDriveLetter"), BoolOrNull(lp.AttachDriveLetter), factor, kind: "bool");

            string? pos = (string?)rpi.Element(ns + "Position");
            if (!string.IsNullOrWhiteSpace(pos) && pos.Trim() != "*ANY*")
            {
                if (pos.Trim() == "-1")
                    res.Issues.Add(new RuleIssue(false, at, "规则要求为末尾分区（Position=-1），布局顺序请自行确认"));
            }
        }

        static string? BoolOrNull(bool b) => b ? "true" : null; // 与 allow_default 语义配合：false 视同缺省，仅显式 true 参与比较

        /// <summary>剥掉 allow_default_plus_ / equal() 包装后的核心表达式。</summary>
        static string expr_core(string e)
        {
            e = e.Trim();
            if (e.StartsWith("allow_default_plus_", StringComparison.OrdinalIgnoreCase))
                e = e.Substring("allow_default_plus_".Length);
            if (e.StartsWith("equal(", StringComparison.OrdinalIgnoreCase)) return Param1(e);
            return e;
        }

        // ═════════════════════════ 表达式求值 ═════════════════════════

        enum Kind { sector, size, text, type, @bool }

        static void Cmp(RuleCheckResult res, string at, string field, string? ruleExprRaw,
            string? layoutVal, double factor, string kind)
        {
            string ruleExpr = (ruleExprRaw ?? "").Trim();
            if (ruleExpr.Length == 0 || ruleExpr == "*ANY*") return;

            string expr = expr_core(ruleExpr);
            if (expr == "*PARENT*") return; // 备份区与正本的一致性由 CheckWithParentSemantics 处理

            bool layoutMissing = string.IsNullOrWhiteSpace(layoutVal);

            // allow_default_*：缺省放行
            if (ruleExpr.StartsWith("allow_default_plus_", StringComparison.OrdinalIgnoreCase) && layoutMissing)
                return;

            if (layoutMissing)
            {
                // bool 语义：规则要求 false 等价「缺省即满足」；规则要求 true 但布局缺省 → 失败
                if (kind == "bool" && string.Equals(expr_core(ruleExpr), "false", StringComparison.OrdinalIgnoreCase))
                    return;
                // 规则给了硬性约束但布局没写该字段：
                res.Issues.Add(new RuleIssue(true, at,
                    $"{field}: 布局缺省，但规则要求 {ruleExpr}"));
                return;
            }

            switch (expr)
            {
                case "*ANY*": return;
                case var s when s.StartsWith("greater_or_equal(", StringComparison.OrdinalIgnoreCase):
                    CompareNumeric(res, at, field, layoutVal!, Param1(s), factor, kind, "≥");
                    return;
                case var s when s.StartsWith("between(", StringComparison.OrdinalIgnoreCase):
                {
                    var ps = Params(s);
                    if (ps.Count == 2)
                    {
                        if (!InRange(res, at, field, layoutVal!, ps[0], ps[1], factor, kind)) return;
                    }
                    else Warn(res, at, field, s);
                    return;
                }
                case var s when s.StartsWith("one_of(", StringComparison.OrdinalIgnoreCase):
                {
                    var ps = Params(s);
                    bool ok = ps.Any(p => ValueEquals(p, layoutVal!, kind));
                    if (!ok)
                        res.Issues.Add(new RuleIssue(true, at, $"{field}: 值 {layoutVal} 不在规则允许集 one_of({string.Join(",", ps)})"));
                    return;
                }
                case var s when s.StartsWith("multiple_of(", StringComparison.OrdinalIgnoreCase):
                {
                    long? lv = ParseVal(layoutVal!, factor, kind);
                    var ps = Params(s);
                    long? rv = ps.Count == 1 ? ParseVal(ps[0], 1, kind) : null;
                    if (lv is > 0 && rv is > 0 && lv % rv != 0)
                        res.Issues.Add(new RuleIssue(true, at, $"{field}: {layoutVal} 不是 {ps[0]} 的倍数"));
                    return;
                }
                case var s when s.StartsWith("equal(", StringComparison.OrdinalIgnoreCase) ||
                                s.StartsWith("allow_default_plus_equal(", StringComparison.OrdinalIgnoreCase):
                {
                    string want = s.StartsWith("equal(", StringComparison.OrdinalIgnoreCase) ? Param1(s) : s;
                    if (!ValueEquals(want, layoutVal!, kind))
                        res.Issues.Add(new RuleIssue(true, at, $"{field}: 布局={layoutVal}，规则要求 = {want}"));
                    return;
                }
                case "*PARENT*":
                    return; // 由调用侧上层处理，单分区视角无从比对，不误报

                default:
                {
                    // 裸值 = 精确相等
                    if (!ValueEquals(expr, layoutVal!, kind))
                        res.Issues.Add(new RuleIssue(true, at, $"{field}: 布局={layoutVal}，规则要求 = {expr}"));
                    return;
                }
            }
        }

        static void CheckRootField(RuleCheckResult res, string field, string? ruleExpr, string? layoutVal,
            double factor, bool isSectorCount)
            => Cmp(res, "根", field, ruleExpr, layoutVal, isSectorCount ? factor : 1.0, "size");

        // ── 具体比对 ──

        static bool ValueEquals(string want, string have, string kind)
        {
            want = want.Trim(); have = have.Trim();
            if (kind == "text" || kind == "bool")
                return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
            if (kind == "type")
                return string.Equals(NormGuid(want), NormGuid(have), StringComparison.OrdinalIgnoreCase);
            long? a = ParseVal(want, 1, kind), b = ParseVal(have, 1, kind);
            return a.HasValue && b.HasValue && a == b;
        }

        static bool CompareNumeric(RuleCheckResult res, string at, string field,
            string have, string? want, double factor, string kind, string op)
        {
            long? hv = ParseVal(have, 1, kind);
            long? wv = want != null ? ParseVal(want, factor, kind) : null;
            if (hv == null || wv == null) { Warn(res, at, field, want ?? "?"); return true; }
            bool ok = op == "≥" ? hv >= wv : hv <= wv;
            if (!ok)
                res.Issues.Add(new RuleIssue(true, at, $"{field}: {have} 不满足 {op}{want}{ScaleNote(factor)}"));
            return ok;
        }

        static bool InRange(RuleCheckResult res, string at, string field,
            string have, string lo, string hi, double factor, string kind)
        {
            long? hv = ParseVal(have, 1, kind);
            long? lv = ParseVal(lo, factor, kind), rv = ParseVal(hi, factor, kind);
            if (hv == null || lv == null || rv == null) { Warn(res, at, field, $"{lo},{hi}"); return true; }
            bool ok = hv >= lv && hv <= rv;
            if (!ok)
                res.Issues.Add(new RuleIssue(true, at, $"{field}: {have} 不在 [{lo},{hi}]{ScaleNote(factor)}"));
            return ok;
        }

        static string ScaleNote(double factor) =>
            Math.Abs(factor - 1.0) < 1e-9 ? "" : $"（阈值按扇区大小换算 ×{factor:0.##}）";

        static void Warn(RuleCheckResult res, string at, string field, string expr) =>
            res.Issues.Add(new RuleIssue(false, at, $"{field}: 规则表达式 {expr} 暂不识别，已放行（以官方校验为准）"));

        // ── 解析工具 ──

        /// <summary>数值解析；sector 类型按 factor 换算（规则阈值 ×factor 与布局同基）。</summary>
        static long? ParseVal(string raw, double factor, string kind)
        {
            long? v = ParseInt(raw);
            if (v == null) return null;
            if (kind == "sector" && Math.Abs(factor - 1.0) > 1e-9)
                return (long)Math.Round(v.Value * factor, MidpointRounding.AwayFromZero);
            return v;
        }

        public static long? ParseInt(string? raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return null;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(raw.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        static string Param1(string expr)
        {
            int a = expr.IndexOf('('), b = expr.LastIndexOf(')');
            return a >= 0 && b > a ? expr.Substring(a + 1, b - a - 1).Trim() : expr;
        }

        static List<string> Params(string expr)
        {
            int a = expr.IndexOf('('), b = expr.LastIndexOf(')');
            if (a < 0 || b <= a) return new();
            // one_of 里 GUID 不含逗号，可安全 Split
            return expr.Substring(a + 1, b - a - 1).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        static bool ParseBool(string? raw) =>
            string.Equals((raw ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

        static string NormGuid(string g)
        {
            g = g.Trim().TrimStart('{').TrimEnd('}');
            return g.Length == 0 ? "" : g.ToLowerInvariant();
        }

        /// <summary>规则 Name 支持通配 * 与 +（C# RegEx 语法）。</summary>
        static bool NameMatches(string pattern, string name)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            if (!pattern.Contains('*') && !pattern.Contains('?'))
                return string.Equals(pattern.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
            try
            {
                var rx = new Regex("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase);
                return rx.IsMatch(name.Trim());
            }
            catch { return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase); }
        }

        // ═════════════════════════ 旧布局 *PARENT* 后处理 ═════════════════════════

        /// <summary>
        /// 对规则里 TotalSectors/*PARENT* 的备份分区，与正本比对。Check() 完成后调用。
        /// 这里直接在 Check 结果上追加问题。
        /// </summary>
        public static RuleCheckResult CheckWithParentSemantics(DeviceLayoutDoc layout, string ruleFile)
        {
            var res = Check(layout, ruleFile);
            try
            {
                var rd = XDocument.Load(ruleFile);
                XNamespace ns = rd.Root!.Name.Namespace;
                foreach (var rpi in rd.Descendants(ns + "Partition"))
                {
                    string pat = ((string?)rpi.Element(ns + "Name") ?? "").Trim();
                    string wantTotal = ((string?)rpi.Element(ns + "TotalSectors") ?? "").Trim();
                    if (!pat.StartsWith("BACKUP_", StringComparison.OrdinalIgnoreCase)) continue;
                    string orig = pat.Substring("BACKUP_".Length);
                    if (!NameMatches(pat, orig)) continue; // 通配模板才带 *PARENT*
                    var origPart = layout.AllPartitions.FirstOrDefault(p =>
                        string.Equals(p.Name, orig, StringComparison.OrdinalIgnoreCase));
                    if (origPart == null) continue;
                    foreach (var bk in layout.AllPartitions.Where(p => NameMatches(pat, p.Name) &&
                        !string.Equals(p.Name, orig, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (wantTotal == "*PARENT*" && bk.TotalSectors != origPart.TotalSectors)
                            res.Issues.Add(new RuleIssue(true, "备份区:" + bk.Name,
                                $"TOTALS 与正本 {origPart.Name}（{origPart.TotalSectors}）不一致（{bk.TotalSectors}）"));
                    }
                }
            }
            catch { }
            return res;
        }
    }
}
