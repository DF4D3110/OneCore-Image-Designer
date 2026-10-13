using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 按目标镜像的「世代」自动选择 CBS 服务通道。
    ///
    /// 【为什么需要】
    /// 实测（2026-10-13）确认了 CBS 的一条硬性版本策略：
    ///   · 可加载的 servicing stack **一律自称 `6.2.&lt;build&gt;`**（CBS 的内部版本体系）
    ///   · 镜像声明的栈版本取自镜像内的 servicingstack **包清单（MUM）**，
    ///     老系统是 `6.2.&lt;build&gt;`，Win10 之后是 `10.0.&lt;build&gt;`
    ///   两者比较时 `6.2 &lt; 10.0`，于是 `ICbsSession::Initialize` 返回
    ///   **0x800F0823 CBS_E_NEW_SERVICING_STACK_REQUIRED**（"栈版本太低"）。
    ///
    ///   已排除的假设：
    ///     · 不是架构问题 —— arm64 镜像配 x86 栈（镜像自带，版本号完全一致）同样被拒
    ///     · `InitializeEx` + `externalDir` **不能绕过**该检查（实测：版本检查照旧发生，
    ///       随后的 E_ACCESSDENIED 只是它的后果）
    ///
    ///   已验证可用的世代：
    ///     · `6.2` 体系镜像（例：Win8 Consumer Preview 6.2.8220 amd64）→ CbsxService ✅
    ///       （实测能够 `/ep` 枚举 518 包、`/ip` 真实改写组件存储并被 DISM 确认）
    ///
    /// 【因此】对 `10.0` 体系镜像走 DISM（它有跨代能力），对 `6.2` 体系走我们自己的
    /// CbsxService（可控、可 dry-run、可逐包断言、可交叉验证）。
    /// </summary>
    public static class ServicingRouter
    {
        public enum Channel
        {
            /// <summary>未识别</summary>
            Unknown,
            /// <summary>原生 CBS COM 互操作（CbsxService）—— 仅 6.2 体系可用</summary>
            NativeCbs,
            /// <summary>DISM 兜底（支持跨代）</summary>
            Dism
        }

        public sealed class Decision
        {
            public Channel Channel = Channel.Unknown;
            /// <summary>镜像声明的 servicing stack 版本（从镜像内 MUM 读出），识别不到为 null。</summary>
            public Version? ImageStackVersion;
            /// <summary>版本体系："6.2" / "10.0" / null</summary>
            public string? Scheme;
            public string Reason = "";
            public readonly List<string> Evidence = new();
        }

        static readonly Regex VersionRe = new(@"(\d+)\.(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// 从镜像里找出 servicing stack 包的版本。
        /// 优先看 `Windows\servicing\Packages\*servicingstack*.mum` 的文件名
        /// （文件名形如 Microsoft-Windows-ServicingStack-...~31bf3856ad364e35~amd64~~10.0.17046.1000.mum）。
        /// </summary>
        public static Version? ReadImageStackVersion(string winDir, out string evidence)
        {
            evidence = "";
            try
            {
                string win = Path.GetFullPath(winDir);
                if (Path.GetFileName(win).Equals("Windows", StringComparison.OrdinalIgnoreCase) == false)
                    win = Path.Combine(win, "Windows");

                // 首选：WinSxS 里的 servicingstack 目录名直接编码了版本，最可靠。
                //   例：amd64_microsoft-windows-servicingstack_31bf3856ad364e35_6.2.8220.0_none_...
                //       x86_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.17046.1000_none_...
                string sxs = Path.Combine(win, "WinSxS");
                if (Directory.Exists(sxs))
                {
                    var fromDirs = new List<Version>();
                    foreach (var dir in Directory.EnumerateDirectories(sxs, "*servicingstack*"))
                    {
                        var m = VersionRe.Match(Path.GetFileName(dir));
                        if (!m.Success) continue;
                        fromDirs.Add(new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                                                 int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
                    }
                    if (fromDirs.Count > 0)
                    {
                        var bestDir = fromDirs.OrderByDescending(v => v).First();
                        evidence = $"WinSxS 下 {fromDirs.Count} 个 servicingstack 目录，取最高版本 {bestDir}";
                        return bestDir;
                    }
                }

                // 回退：servicing\Packages 里的 servicingstack mum 文件名
                string pkgs = Path.Combine(win, "servicing", "Packages");
                if (!Directory.Exists(pkgs)) { evidence = "WinSxS 与 " + pkgs + " 都找不到 servicing stack 线索"; return null; }

                var mums = Directory.EnumerateFiles(pkgs, "*servicingstack*.mum")
                                    .OrderByDescending(f => Path.GetFileName(f).Length)   // 主栈名较长
                                    .ToList();
                if (mums.Count == 0) { evidence = "WinSxS 无 servicingstack 目录，" + pkgs + " 下也没有对应 mum"; return null; }

                var versions = new List<Version>();
                foreach (var f in mums)
                {
                    var m = VersionRe.Match(Path.GetFileNameWithoutExtension(f));
                    if (!m.Success) continue;
                    var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                                        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
                    versions.Add(v);
                }
                if (versions.Count == 0) { evidence = "servicingstack mum 里解析不出版本号"; return null; }

                var best = versions.OrderByDescending(v => v).First();
                evidence = $"{versions.Count} 个 servicingstack mum，取最高版本 {best}（例：{Path.GetFileName(mums[0])}）";
                return best;
            }
            catch (Exception ex) { evidence = "读取失败: " + ex.Message; return null; }
        }

        /// <summary>决定该用哪条通道。</summary>
        public static Decision Decide(string winDir)
        {
            var d = new Decision();
            try
            {
                if (string.IsNullOrWhiteSpace(winDir)) { d.Reason = "winDir 为空"; return d; }

                var v = ReadImageStackVersion(winDir, out string ev);
                d.ImageStackVersion = v;
                d.Evidence.Add("镜像栈版本: " + (v?.ToString() ?? "(未知)") + " —— " + ev);

                if (v != null)
                {
                    d.Scheme = $"{v.Major}.{v.Minor}";
                    // 分界是「6.x 家族」vs「10.0 之后」：
                    //   CBS 可加载栈自称 6.2.<build>，与同为 6.x 的镜像声明能比得上；
                    //   10.0 及以后体系的镜像声明为 10.0.<build>，比较时 6.2 < 10.0 必被拒。
                    // 因此 6.0/6.1/6.2/6.3 都走原生通道（实测 6.2.8220 与 6.3.9600 均可）。
                    if (v.Major == 6)
                    {
                        d.Channel = Channel.NativeCbs;
                        d.Reason = $"镜像属 {v.Major}.{v.Minor} 体系（6.x 家族）→ 用原生 CBS 通道"
                                 + "（可控、可 dry-run、可逐包断言；已验证可用于 6.x 体系）";
                        return d;
                    }
                    d.Channel = Channel.Dism;
                    d.Reason = $"镜像属 {v.Major}.{v.Minor} 体系 → 用 DISM 通道。"
                             + $"原因：可加载的 servicing stack 一律自称 6.2.<build>，与 {v.Major}.{v.Minor} 比较时"
                             + " CBS 会判 0x800F0823（栈版本太低）；InitializeEx + externalDir 实测也无法绕过。";
                    return d;
                }

                // 识别不出世代：保守走 DISM（它跨代更宽容），并说明原因
                d.Channel = Channel.Dism;
                d.Reason = "无法识别镜像的 servicing stack 世代 → 保守用 DISM 通道";
                return d;
            }
            catch (Exception ex) { d.Reason = "判定异常: " + ex.Message; d.Channel = Channel.Dism; return d; }
        }

        /// <summary>
        /// 枚举包：按 <see cref="Decide"/> 自动选通道，并把两条通道的结果都带上便于交叉验证。
        /// </summary>
        public static (Decision decision, CbsxService.Result? native, DismServicingService.Result? dism,
                       List<string> packages) EnumeratePackages(string winDir, Action<string>? log = null)
        {
            var d = Decide(winDir);
            log?.Invoke("通道判定: " + d.Reason);
            foreach (var e in d.Evidence) log?.Invoke("  " + e);

            if (d.Channel == Channel.NativeCbs)
            {
                var r = CbsxService.EnumeratePackages(winDir, log);
                return (d, r, null, r.EnumeratedIdentities);
            }

            // DISM 通道：/get-packages 默认只列已安装，这里解析全部条目
            var (run, pkgs) = DismServicingService.GetPackages(winDir);
            return (d, null, run, pkgs.Select(p => p.Identity).ToList());
        }

        /// <summary>渲染判定结果。</summary>
        public static string Render(Decision d)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CBS 通道判定");
            sb.AppendLine("  通道            : " + d.Channel);
            sb.AppendLine("  镜像栈版本      : " + (d.ImageStackVersion?.ToString() ?? "(未知)"));
            sb.AppendLine("  版本体系        : " + (d.Scheme ?? "(未知)"));
            sb.AppendLine("  理由            : " + d.Reason);
            foreach (var e in d.Evidence) sb.AppendLine("  证据            : " + e);
            return sb.ToString();
        }
    }
}
