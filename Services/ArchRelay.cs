using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 架构转接（子程序代跑）。
    ///
    /// 【为什么需要】CBS 的 servicing stack 是**按架构**的：x64 进程加载不了 x86 的
    /// `cbscore.dll`。实测后果很隐蔽 —— 会**静默回退到宿主栈**（例如 6.2.19041），
    /// 然后拿它去服务一个 8400 的镜像，CBS 报
    /// `0x8007051A ERROR_REVISION_MISMATCH`（`Windows::COM::VerifyStoreVersionNumber`）。
    /// 换用 **x86 进程**后就会加载镜像自带的 `x86_..._servicingstack_..._6.2.8400.0_...`，
    /// 会话随即成功。
    ///
    /// 【为什么用子程序而不是重启自己】重启当前进程会丢掉界面状态、命令行上下文与
    /// 用户正在看的内容。这里改为**只把需要跨架构的那一步交给对应架构的子程序**，
    /// 主进程继续运行并实时转发子进程输出。
    ///
    /// 【子程序从哪来】与本程序同目录下的架构子目录里的同名 CLI：
    ///     &lt;程序目录&gt;\x86\CbsxCli.exe
    ///     &lt;程序目录&gt;\arm64\CbsxCli.exe
    /// 找不到就如实报告，不做假装成功的降级。
    /// </summary>
    public static class ArchRelay
    {
        public enum Arch { Unknown, X86, Amd64, Arm, Arm64 }

        /// <summary>当前进程的架构。</summary>
        public static Arch CurrentProcessArch()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => Arch.X86,
                Architecture.X64 => Arch.Amd64,
                Architecture.Arm => Arch.Arm,
                Architecture.Arm64 => Arch.Arm64,
                _ => Arch.Unknown
            };
        }

        /// <summary>把 SxS 前缀 / 目录名里的架构词归一化。</summary>
        public static Arch Parse(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return Arch.Unknown;
            return token.Trim().ToLowerInvariant() switch
            {
                "x86" or "i386" or "win32" => Arch.X86,
                "amd64" or "x64" or "x86_64" => Arch.Amd64,
                "arm" or "arm32" => Arch.Arm,
                "arm64" or "aarch64" => Arch.Arm64,
                _ => Arch.Unknown
            };
        }

        public static string ToToken(Arch a) => a switch
        {
            Arch.X86 => "x86",
            Arch.Amd64 => "amd64",
            Arch.Arm => "arm",
            Arch.Arm64 => "arm64",
            _ => "unknown"
        };

        /// <summary>
        /// 从镜像里判断架构：看 WinSxS 下 servicingstack 目录的前缀，
        /// 再退回看 `Windows\System32` 里某个系统 DLL 的 PE 头。
        /// </summary>
        public static (Arch arch, string evidence) DetectImageArchitecture(string winDir)
        {
            try
            {
                string win = Path.GetFullPath(winDir);
                if (!Path.GetFileName(win).Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    win = Path.Combine(win, "Windows");

                // ① WinSxS\*servicingstack* 目录名的前缀最可靠
                string sxs = Path.Combine(win, "WinSxS");
                if (Directory.Exists(sxs))
                {
                    var counts = new Dictionary<Arch, int>();
                    foreach (var d in Directory.EnumerateDirectories(sxs, "*servicingstack*"))
                    {
                        var a = Parse(Path.GetFileName(d).Split('_')[0]);
                        if (a == Arch.Unknown) continue;
                        counts[a] = counts.TryGetValue(a, out int c) ? c + 1 : 1;
                    }
                    if (counts.Count > 0)
                    {
                        var best = counts.OrderByDescending(kv => kv.Value).First();
                        return (best.Key, $"WinSxS 下 servicingstack 目录以 {ToToken(best.Key)} 为主（{best.Value} 个）");
                    }
                }

                // ② 退回 PE 头
                foreach (var probe in new[] { "System32\\ntdll.dll", "System32\\kernel32.dll", "explorer.exe" })
                {
                    string f = Path.Combine(win, probe);
                    if (!File.Exists(f)) continue;
                    var a = PeArchitecture(f);
                    if (a != Arch.Unknown) return (a, $"{probe} 的 PE 头为 {ToToken(a)}");
                }
                return (Arch.Unknown, "无法判定镜像架构");
            }
            catch (Exception ex) { return (Arch.Unknown, "判定失败: " + ex.Message); }
        }

        /// <summary>读 PE 头判断架构（IMAGE_FILE_MACHINE）。</summary>
        public static Arch PeArchitecture(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var br = new BinaryReader(fs);
                if (br.ReadUInt16() != 0x5A4D) return Arch.Unknown;      // MZ
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOff = br.ReadInt32();
                fs.Seek(peOff, SeekOrigin.Begin);
                if (br.ReadUInt32() != 0x00004550) return Arch.Unknown;  // PE\0\0
                ushort machine = br.ReadUInt16();
                return machine switch
                {
                    0x014C => Arch.X86,
                    0x8664 => Arch.Amd64,
                    0x01C0 or 0x01C4 => Arch.Arm,
                    0xAA64 => Arch.Arm64,
                    0x0200 => Arch.Unknown,   // IA64
                    _ => Arch.Unknown
                };
            }
            catch { return Arch.Unknown; }
        }

        /// <summary>是否需要为这个镜像换一个架构的进程来干活。</summary>
        public static bool NeedsRelay(Arch imageArch, out string reason)
        {
            var me = CurrentProcessArch();
            if (imageArch == Arch.Unknown) { reason = "镜像架构未知，按当前架构继续"; return false; }
            if (imageArch == me) { reason = $"镜像与进程同为 {ToToken(me)}，无需转接"; return false; }
            reason = $"镜像是 {ToToken(imageArch)}，当前进程是 {ToToken(me)} —— "
                   + $"必须用 {ToToken(imageArch)} 的子程序代跑，否则会加载到宿主服务栈并报 0x8007051A。";
            return true;
        }

        /// <summary>
        /// 找某个架构的子程序。约定：与主程序同目录下的架构子目录里的同名 exe。
        /// </summary>
        public static string? FindHelper(Arch arch, string? preferredName = null)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string token = ToToken(arch);
            string name = preferredName
                          ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "CbsxCli") + ".exe";

            var candidates = new List<string>
            {
                Path.Combine(baseDir, token, name),
                Path.Combine(baseDir, "runtimes", token, name),
                Path.Combine(baseDir, token, "CbsxCli.exe"),
                Path.Combine(baseDir, "CbsxCli-" + token + ".exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;

            // 退回：同目录下任何名字含架构词的 CbsxCli
            try
            {
                return Directory.EnumerateFiles(baseDir, "*", SearchOption.TopDirectoryOnly)
                                .FirstOrDefault(f => Path.GetFileName(f).Contains("CbsxCli", StringComparison.OrdinalIgnoreCase)
                                                  && Path.GetFileName(f).Contains(token, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        public sealed class RelayResult
        {
            public bool Started;
            public int ExitCode = -1;
            public string Error = "";
            public readonly List<string> Lines = new();
            public string Output => string.Join(Environment.NewLine, Lines);
        }

        /// <summary>
        /// 用指定架构的子程序执行一次 CBS 操作，实时转发其输出。
        /// </summary>
        public static async Task<RelayResult> RunAsync(
            Arch targetArch, IEnumerable<string> args,
            Action<string>? log = null, CancellationToken ct = default, int timeoutMs = 60 * 60 * 1000)
        {
            var res = new RelayResult();
            try
            {
                string? helper = FindHelper(targetArch);
                if (helper == null)
                {
                    res.Error = $"找不到 {ToToken(targetArch)} 架构的子程序。"
                              + $"请把对应架构的 CbsxCli 放在 {ToToken(targetArch)}\\ 子目录下"
                              + $"（例如 {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ToToken(targetArch), "CbsxCli.exe")}）。";
                    log?.Invoke("✗ " + res.Error);
                    return res;
                }

                string argLine = string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                log?.Invoke($"→ 转接给 {ToToken(targetArch)} 子程序: {helper}");
                log?.Invoke($"  参数: {argLine}");

                var psi = new ProcessStartInfo(helper, argLine)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) { res.Error = "无法启动子程序"; return res; }
                res.Started = true;

                p.OutputDataReceived += (_, e) => { if (e.Data != null) { res.Lines.Add(e.Data); log?.Invoke("  " + e.Data); } };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) { res.Lines.Add(e.Data); log?.Invoke("  " + e.Data); } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    res.Error = "子程序超时已终止";
                    return res;
                }
                p.WaitForExit();
                res.ExitCode = p.ExitCode;
                if (p.ExitCode != 0) res.Error = $"{ToToken(targetArch)} 子程序退出码 {p.ExitCode}";
                log?.Invoke(p.ExitCode == 0 ? $"✓ {ToToken(targetArch)} 子程序完成" : "✗ " + res.Error);
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>一行结论，供界面/日志显示。</summary>
        public static string Describe(string winDir)
        {
            var (imgArch, ev) = DetectImageArchitecture(winDir);
            var me = CurrentProcessArch();
            bool need = NeedsRelay(imgArch, out string reason);
            var sb = new StringBuilder();
            sb.AppendLine($"镜像架构 : {ToToken(imgArch)}   （{ev}）");
            sb.AppendLine($"进程架构 : {ToToken(me)}");
            sb.AppendLine($"是否需要转接 : {(need ? "是" : "否")} —— {reason}");
            if (need)
            {
                string? h = FindHelper(imgArch);
                sb.AppendLine($"子程序 : {(h ?? "(未找到，请放置对应架构的 CbsxCli)")}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
