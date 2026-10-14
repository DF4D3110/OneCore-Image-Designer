using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// CBS 装包【之后】的全部步骤，放在独立进程里跑。
    ///
    /// 【为什么必须独立进程】实测（DISM 日志 + 对照实验）：
    ///   · 流程中间跑 apply-unattend → <c>dism 退出码 5 (ERROR_ACCESS_DENIED)</c>
    ///   · 流程结束后、在另一个进程里跑同一条命令 → <c>exit 0</c> 成功
    /// 原因：CBS 服务在会话结束后仍【独占】目标镜像的 hive，而 apply-unattend 要改
    /// SOFTWARE hive 的 unattend 状态、需要独占访问，于是被拒。
    /// 我们自己释放（ReleaseServicingStack + ReleaseResidualHives）实测 <c>9 → 9</c> 卸不掉 ——
    /// 持有者是 CBS 服务，不是本进程；服务要到调用方进程退出才放。
    /// 这与此前查明的"收尾必须跨进程"是同一个根因。
    ///
    /// 【步骤顺序照 explodeSxS 的 cbss.cmd】
    ///   apply-unattend（先取 servicing\Editions\&lt;Edition&gt;Edition.xml）
    ///   get-currentedition / get-targeteditions
    ///   set-allintl
    ///   cleanup-image analyzecomponentstore / startcomponentcleanup
    ///   收尾（State.ini + imagestate）
    /// </summary>
    public static class PostInstallService
    {
        public sealed class Options
        {
            /// <summary>镜像的 Windows 目录。</summary>
            public string WinDir = "";
            /// <summary>SKU 名（取 Edition XML 用；留空则从 skuassembly 推导）。</summary>
            public string Edition = "Core";
            /// <summary>skuassembly.txt（用它推导 edition）。</summary>
            public string SkuAssemblyFile = "";
            public string Lang = "en-US";
            public string ScratchDir = "";
            /// <summary>结果也要写一份到这个文件（供父进程/界面读取）。</summary>
            public string ResultFile = "";
        }

        public static (bool ok, string summary) Run(Options o, Action<string>? log = null)
        {
            var lines = new List<string>();
            void L(string s) { lines.Add(s); log?.Invoke(s); }
            bool allOk = true;

            try
            {
                string win = Path.GetFullPath(o.WinDir);
                string root = Directory.Exists(Path.Combine(win, "Windows")) ? win : Directory.GetParent(win)?.FullName ?? win;
                if (!Directory.Exists(Path.Combine(root, "Windows"))) root = win;

                if (string.IsNullOrWhiteSpace(o.ScratchDir))
                {
                    try { o.ScratchDir = Path.Combine(root, "OCID_scratch"); Directory.CreateDirectory(o.ScratchDir); }
                    catch { }
                }

                L("=== 后置阶段（独立进程） ===");
                L($"  镜像: {win}");
                L($"  暂存: {o.ScratchDir}");

                // ── ① apply-unattend：先从 servicing\Editions 取 Edition XML ──
                L("=== apply-unattend ===");
                string? xml = ResolveEditionXml(win, o, L);
                if (xml != null)
                {
                    var r = DismServicingService.ApplyUnattend(win, xml, o.ScratchDir);
                    L($"  apply-unattend: {(r.Success ? "OK" : "FAIL " + r.Error)}");
                    if (!r.Success) allOk = false;
                }
                else L("  找不到 Edition XML —— 跳过");

                // ── ② edition 校验（宿主 DISM 可能不支持这些子命令，失败不算致命） ──
                var cur = DismServicingService.GetCurrentEdition(win);
                L($"  get-currentedition: {(cur.Success ? FirstLine(cur.Output) : "不支持/失败 (" + cur.Error + ")")}");
                var tgt = DismServicingService.GetTargetEditions(win);
                L($"  get-targeteditions: {(tgt.Success ? FirstLine(tgt.Output) : "不支持/失败 (" + tgt.Error + ")")}");

                // ── ③ 语言 ──
                if (!string.IsNullOrWhiteSpace(o.Lang))
                {
                    var r = DismServicingService.SetAllIntl(win, o.Lang, o.ScratchDir);
                    L($"  set-allintl {o.Lang}: {(r.Success ? "OK" : "FAIL " + r.Error)}");
                    if (!r.Success) allOk = false;
                }

                // ── ④ 组件存储清理 ──
                var a = DismServicingService.AnalyzeComponentStore(win, o.ScratchDir);
                L($"  analyzecomponentstore: {(a.Success ? "OK" : "不支持/失败 (" + a.Error + ")")}");
                var c = DismServicingService.StartComponentCleanup(win, o.ScratchDir);
                L($"  startcomponentcleanup: {(c.Success ? "OK" : "FAIL " + c.Error)}");
                if (!c.Success) allOk = false;

                // ── ⑤ 收尾（State.ini + imagestate） ──
                var (fok, fdetail) = FinishImageService.Finish(win, L);
                L($"  finishimg: {(fok ? "OK  " : "FAIL ") + fdetail}");
                if (!fok) allOk = false;
            }
            catch (Exception ex)
            {
                allOk = false;
                L("后置阶段异常: " + ex.Message);
            }

            string summary = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrWhiteSpace(o.ResultFile))
            {
                try { File.WriteAllText(o.ResultFile, summary); } catch { }
            }
            return (allOk, summary);
        }

        /// <summary>取 Edition XML：优先按 skuassembly 里的 editions 包名推导，其次扫目录。</summary>
        static string? ResolveEditionXml(string win, Options o, Action<string> L)
        {
            string edDir = Path.Combine(win, "servicing", "Editions");
            if (!Directory.Exists(edDir))
            {
                string alt = Path.Combine(win, "Windows", "servicing", "Editions");
                if (Directory.Exists(alt)) edDir = alt;
                else { L("  servicing\\Editions 不存在"); return null; }
            }

            // ① 从 skuassembly.txt 推导 token（权威来源）
            string token = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(o.SkuAssemblyFile) && File.Exists(o.SkuAssemblyFile))
                    foreach (var line in File.ReadAllLines(o.SkuAssemblyFile))
                    {
                        var mm = System.Text.RegularExpressions.Regex.Match(line,
                            @"microsoft-windows-editions-([a-z0-9]+)-package",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mm.Success) { token = mm.Groups[1].Value.ToLowerInvariant(); break; }
                    }
            }
            catch { }

            if (token.Length > 0)
            {
                string bySku = Path.Combine(edDir, char.ToUpperInvariant(token[0]) + token.Substring(1) + "Edition.xml");
                if (File.Exists(bySku)) { L($"  按 SKU 包名取 Edition XML: {Path.GetFileName(bySku)}"); return Stage(win, bySku, L); }
            }

            // ② 扫目录（排除两个 Matrix）
            var cands = Directory.EnumerateFiles(edDir, "*Edition.xml")
                .Where(f => !Path.GetFileName(f).Equals("EditionMatrix.xml", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(f).Equals("UpgradeMatrix.xml", StringComparison.OrdinalIgnoreCase))
                .ToList();
            L($"  {edDir} 下有: " + string.Join(", ", Directory.EnumerateFiles(edDir, "*.xml").Select(Path.GetFileName)));
            var pick = cands.FirstOrDefault(f => Path.GetFileName(f).StartsWith(o.Edition, StringComparison.OrdinalIgnoreCase))
                       ?? (cands.Count >= 1 ? cands.OrderBy(f => Path.GetFileName(f).Length).First() : null);
            if (pick == null) return null;
            return Stage(win, pick, L);
        }

        /// <summary>照 explodeSxS：copy …\Editions\&lt;Edition&gt;Edition.xml → &lt;镜像&gt;\Windows\&lt;Edition&gt;.xml</summary>
        static string? Stage(string win, string srcXml, Action<string> L)
        {
            try
            {
                string name = Path.GetFileName(srcXml);
                if (name.EndsWith("Edition.xml", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - "Edition.xml".Length);
                string dst = Path.Combine(win, name + ".xml");
                File.Copy(srcXml, dst, true);
                L($"  已就位: {Path.GetFileName(srcXml)} -> {Path.GetFileName(dst)}");
                return dst;
            }
            catch (Exception ex) { L("  复制 Edition XML 失败: " + ex.Message); return null; }
        }

        static string FirstLine(string s)
        {
            foreach (var l in (s ?? "").Split('\n'))
                if (l.Trim().Length > 0) return l.Trim();
            return "(空)";
        }
    }
}
