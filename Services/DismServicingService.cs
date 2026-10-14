using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// `dism /image:` 兜底通道。
    ///
    /// 【为什么需要它】我们的 CBS COM 互操作（<see cref="CbsxService"/>）有两条硬约束：
    ///   ① 加载的 cbscore.dll 必须匹配宿主进程架构；
    ///   ② servicing stack 自称版本必须 ≥ 镜像声明版本 —— 实践上表现为
    ///      "可加载的栈都自称 6.2.&lt;build&gt;，而某些镜像声明 10.0.&lt;build&gt;"，
    ///      于是报 0x800F0823 CBS_E_NEW_SERVICING_STACK_REQUIRED。
    /// DISM 用的是它自己的 dismhost.exe（x64/x86 双份，按目标挑选），对这类
    /// 跨代场景更宽容。因此凡是我们的通道覆盖不到的，走 DISM。
    ///
    /// 【定位】CbsxService 是**主通道**（可精确控制、可 dry-run、可逐包断言），
    /// 本服务是**兜底与交叉验证通道**（结果可与我们的通道互相印证）。
    ///
    /// 【安全】与 CbsxService 一致：只在挂载的 VHD/VHDX 上操作（CbsSafety 门禁）。
    /// </summary>
    public static class DismServicingService
    {
        public sealed class Result
        {
            public bool Success;
            public int ExitCode;
            public string Error = "";
            public string Output = "";
        }

        public sealed class PackageInfo
        {
            public string Identity = "";
            public string State = "";
            public string ReleaseType = "";
            public string InstallTime = "";
        }

        /// <summary>找 dism.exe。优先 SysWOW64 版（x86），因为它对 x86 镜像更稳妥；否则 System32。</summary>
        public static string? FindDism(bool preferX86 = false)
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);          // ...\System32
            string sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);    // ...\SysWOW64

            var candidates = preferX86
                ? new[] { Path.Combine(sysWow, "Dism.exe"), Path.Combine(sysWow, "Dism", "dism.exe"),
                          Path.Combine(sys, "Dism.exe") }
                : new[] { Path.Combine(sys, "Dism.exe"), Path.Combine(sys, "Dism", "dism.exe"),
                          Path.Combine(sysWow, "Dism.exe") };

            foreach (var c in candidates) if (File.Exists(c)) return c;
            // 兜底：交给 PATH
            return "dism.exe";
        }

        /// <summary>执行 dism 并捕获输出（同步）。</summary>
        public static Result Run(string arguments, int timeoutMs = 60 * 60 * 1000, bool preferX86 = false)
        {
            var r = new Result();
            try
            {
                string exe = FindDism(preferX86) ?? "dism.exe";
                var psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) { r.Error = "无法启动 dism"; return r; }

                var sb = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    r.Error = "dism 超时已终止";
                    lock (sb) r.Output = sb.ToString();
                    return r;
                }
                p.WaitForExit();   // 确保异步输出读完

                lock (sb) r.Output = sb.ToString();
                r.ExitCode = p.ExitCode;
                r.Success = p.ExitCode == 0;
                if (!r.Success) r.Error = $"dism 退出码 {p.ExitCode}";
                return r;
            }
            catch (Exception ex) { r.Error = ex.GetType().Name + ": " + ex.Message; return r; }
        }

        /// <summary>`/image:&lt;root&gt;` 形式的参数前缀。传进来的可以是 G:\ 或 G:\Windows。</summary>
        /// <summary>
        /// 拼 /scratchdir。explodeSxS 的【每一条】dism 都带 /scratchdir:X:\temp，
        /// 我们原来只有 add-package 带 —— 这是与它的明显差异，且 apply-unattend 报
        /// ERROR_ACCESS_DENIED(5) 很可能与此有关（DISM 需要可写的暂存目录）。
        /// </summary>
        static string Scratch(string? dir)
            => string.IsNullOrWhiteSpace(dir) ? "" : $" /scratchdir:\"{dir}\"";

        static string ImageArg(string pathOrWinDir)
        {
            string full = Path.GetFullPath(pathOrWinDir);
            if (Path.GetFileName(full.TrimEnd('\\')).Equals("Windows", StringComparison.OrdinalIgnoreCase))
                full = Directory.GetParent(full)!.FullName;
            if (!full.EndsWith("\\")) full += "\\";
            return $"/image:{full}";
        }

        // ────────────────────────────────────────────────────────────────
        // 常用操作
        // ────────────────────────────────────────────────────────────────

        /// <summary>`/get-packages`（全部状态；DISM 默认只列已安装，这里解析全部条目）。</summary>
        public static (Result run, List<PackageInfo> packages) GetPackages(string pathOrWinDir)
        {
            var r = Run($"{ImageArg(pathOrWinDir)} /get-packages /english");
            var list = new List<PackageInfo>();
            PackageInfo? cur = null;
            foreach (var raw in r.Output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Package Identity :", StringComparison.OrdinalIgnoreCase))
                {
                    cur = new PackageInfo { Identity = line["Package Identity :".Length..].Trim() };
                    list.Add(cur);
                }
                else if (cur != null && line.StartsWith("State :", StringComparison.OrdinalIgnoreCase))
                    cur.State = line["State :".Length..].Trim();
                else if (cur != null && line.StartsWith("Release Type :", StringComparison.OrdinalIgnoreCase))
                    cur.ReleaseType = line["Release Type :".Length..].Trim();
                else if (cur != null && line.StartsWith("Install Time :", StringComparison.OrdinalIgnoreCase))
                    cur.InstallTime = line["Install Time :".Length..].Trim();
            }
            return (r, list);
        }

        /// <summary>`/get-currentedition`。</summary>
        public static Result GetCurrentEdition(string pathOrWinDir)
            => Run($"{ImageArg(pathOrWinDir)} /get-currentedition /english");

        /// <summary>`/get-targeteditions`。</summary>
        public static Result GetTargetEditions(string pathOrWinDir)
            => Run($"{ImageArg(pathOrWinDir)} /get-targeteditions /english");

        /// <summary>`/add-package /packagepath:&lt;mum 或 cab 或目录&gt;`。</summary>
        public static Result AddPackage(string pathOrWinDir, string packagePath, string? scratchDir = null, string? logPath = null)
        {
            var sb = new StringBuilder(ImageArg(pathOrWinDir));
            if (!string.IsNullOrWhiteSpace(scratchDir)) sb.Append($" /scratchdir:\"{scratchDir}\"");
            if (!string.IsNullOrWhiteSpace(logPath)) sb.Append($" /logpath:\"{logPath}\"");
            sb.Append($" /add-package /packagepath:\"{packagePath}\"");
            return Run(sb.ToString());
        }

        /// <summary>`/apply-unattend:&lt;xml&gt;`。</summary>
        public static Result ApplyUnattend(string pathOrWinDir, string unattendXml, string? scratchDir = null)
            => Run($"{ImageArg(pathOrWinDir)} /apply-unattend:\"{unattendXml}\"" + Scratch(scratchDir));

        /// <summary>`/set-allintl:&lt;lang&gt;`。</summary>
        public static Result SetAllIntl(string pathOrWinDir, string lang, string? scratchDir = null)
            => Run($"{ImageArg(pathOrWinDir)} /set-allintl:{lang} /english" + Scratch(scratchDir));

        /// <summary>`/cleanup-image /analyzecomponentstore`。</summary>
        public static Result AnalyzeComponentStore(string pathOrWinDir, string? scratchDir = null)
            => Run($"{ImageArg(pathOrWinDir)} /cleanup-image /analyzecomponentstore /english" + Scratch(scratchDir));

        /// <summary>`/cleanup-image /startcomponentcleanup`。</summary>
        public static Result StartComponentCleanup(string pathOrWinDir, string? scratchDir = null)
            => Run($"{ImageArg(pathOrWinDir)} /cleanup-image /startcomponentcleanup /english" + Scratch(scratchDir));

        /// <summary>`/capture-image`（把目录捕获成 wim）。</summary>
        public static Result CaptureImage(string wimPath, string captureDir, string name = "Windows", string compress = "max")
            => Run($"/capture-image /imagefile:\"{wimPath}\" /capturedir:\"{captureDir}\" /name:\"{name}\" /compress:{compress}");

        /// <summary>`/apply-image`（把 wim 应用到目录）。</summary>
        public static Result ApplyImage(string wimPath, int index, string applyDir)
            => Run($"/apply-image /imagefile:\"{wimPath}\" /index:{index} /applydir:\"{applyDir}\"");
    }
}
