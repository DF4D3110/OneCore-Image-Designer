using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 提权助手。CBS/WCP 操作通常需要管理员，某些步骤（写组件存储、挂载离线 hive）
    /// 在严格环境下还需要 TrustedInstaller 身份。
    ///
    /// 【实现选择】项目里已经移植了 NSudo（<c>MobilePackageGen.Common\NSudo</c>，
    /// 以及 <c>D:\UUP-Project\NSudo-master</c> 的 <c>NSudoAPI.dll</c>）。
    /// 为了避免对 NSudo 的命名空间/版本产生硬依赖，这里**动态加载**
    /// <c>NSudoAPI.dll</c> 的 <c>NSudoCreateProcess</c> 导出；找不到时，
    /// 退化为标准 UAC 提权（ShellExecute "runas"）。
    ///
    /// 【为什么需要它】CbsxCli / SxSFounderCli 在没有提权时会在
    /// ICbsSession::Initialize 或 CreateNewOfflineStore 处失败，
    /// 且错误码（如 0x80070005 E_ACCESSDENIED）不如"请以管理员运行"直观。
    /// </summary>
    public static class ElevationHelper
    {
        // ── NSudoAPI.dll 导出（动态解析，避免硬依赖） ──
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        delegate int NSudoCreateProcessFn(
            int userModeType, int privilegesModeType, int mandatoryLabelType,
            int processPriorityClassType, int showWindowModeType,
            uint waitInterval, bool createNewConsole,
            string commandLine, string currentDirectory);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        // ── NSudo 枚举值（必须与 NSudoAPI.h / M2.NSudo 的 C# 枚举逐一对齐）──
        //
        // 【血泪】这里原来写错了，而且错得很隐蔽 —— 全是"看起来合理"的数字，
        // 但对照真实枚举每一个都不对：
        //
        //   NSUDO_USER_MODE_TYPE       DEFAULT=0, TRUSTED_INSTALLER=1, SYSTEM=2,
        //                              CURRENT_USER=3, CURRENT_PROCESS=4, CURRENT_PROCESS_DROP_RIGHT=5
        //   NSUDO_PRIVILEGES_MODE_TYPE DEFAULT=0, ENABLE_ALL_PRIVILEGES=1, DISABLE_ALL_PRIVILEGES=2
        //   NSUDO_MANDATORY_LABEL_TYPE UNTRUSTED=0, LOW=1, MEDIUM=2, MEDIUM_PLUS=3,
        //                              HIGH=4, SYSTEM=5, PROTECTED_PROCESS=6
        //   NSUDO_PROCESS_PRIORITY_CLASS_TYPE  IDLE=0, BELOW_NORMAL=1, NORMAL=2,
        //                              ABOVE_NORMAL=3, HIGH=4, REALTIME=5
        //   NSUDO_SHOW_WINDOW_MODE_TYPE        DEFAULT=0, SHOW=1, HIDE=2, MAXIMIZE=3, MINIMIZE=4
        //
        // 原值把 USER_MODE 的 TRUSTED_INSTALLER 与 CURRENT_USER 张冠李戴（3 / 1），
        // 一个"提权到 TrustedInstaller"的调用实际会以**当前用户**启动 —— 完全无声地失败。
        //
        // ⚠ MANDATORY_LABEL 用 **UNTRUSTED(0)** 是【对的】：NSudo 的启动器默认就是它，
        //    且实测该值表示"不改变"（我们以它启动后 `whoami /all` 显示
        //    `Mandatory Label\System Mandatory Level`，即沿用了 token 自带的级别）。
        //    这不是"降到 untrusted"。
        const int NSUDO_USER_MODE_TRUSTED_INSTALLER = 1;   // 原误写为 3（那是 CURRENT_USER）
        const int NSUDO_USER_MODE_SYSTEM = 2;
        const int NSUDO_USER_MODE_CURRENT_USER = 3;        // 原误写为 1（那是 TRUSTED_INSTALLER）
        const int NSUDO_PRIVILEGES_MODE_DEFAULT = 0;
        const int NSUDO_PRIVILEGES_MODE_ENABLE_ALL = 1;
        const int NSUDO_MANDATORY_LABEL_DEFAULT = 0;       // "不改变"；原名为 SYSTEM 且值为 3(错)
        const int NSUDO_PROCESS_PRIORITY_NORMAL = 2;       // 原误写为 1（那是 BELOW_NORMAL）
        const int NSUDO_SHOW_WINDOW_HIDE = 2;              // 原误写为 0（那是 DEFAULT）

        /// <summary>
        /// TrustedInstaller 的 SID。
        /// </summary>
        public const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        /// <summary>当前进程是否已是管理员。</summary>
        public static bool IsElevated() => CbsSafety.IsElevated();

        /// <summary>
        /// 当前进程是否具备 TrustedInstaller 身份。
        ///
        /// 【为什么判"组"而不是判 User】经 NSudo 以 TrustedInstaller 启动时，
        /// `whoami /all` 的 **User Name 仍显示 `nt authority\system`**，因为
        /// TrustedInstaller 服务（TiWorker）本身就跑在 SYSTEM 下。
        /// TI 的身份体现在 **token 的组**里：
        ///     NT SERVICE\TrustedInstaller  S-1-5-80-956008885-…  Group owner, Enabled
        /// 原来这里判 `id.User.Value == TI_SID`，所以**永远返回 false**（User 是 S-1-5-18）。
        /// </summary>
        public static bool IsTrustedInstaller()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                // ① token 的 User 直接就是 TI（少数情形）
                if (string.Equals(id.User?.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
                    return true;
                // ② TI 作为【组】出现在 token 里（NSudo 启用 TI 后的实际形态）
                foreach (var g in id.Groups)
                {
                    try
                    {
                        if (string.Equals(g?.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { /* 无法解析的 SID 忽略 */ }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>当前进程是否已是 SYSTEM（S-1-5-18）。</summary>
        public static bool IsSystem()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return string.Equals(id.User?.Value, "S-1-5-18", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 找 NSudoAPI.dll（多个可能位置）。
        ///
        /// 查找顺序刻意把**【与当前进程架构匹配的子目录】放在最前** ——
        /// NSudo 官方约定是 `&lt;程序目录&gt;\x64\NSudoAPI.dll` / `Win32\` / `ARM64\`
        /// （见 <c>M2.NSudo.NSudoInstance</c> 的默认 BinaryPaths）。
        /// 若不查子目录就直接回落到 D:\UUP-Project 的 x64 版本，
        /// **x86 进程会去加载 x64 的 DLL 而失败**。
        /// </summary>
        static string? FindNSudoApi()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 当前进程架构对应的子目录名（NSudo 约定）
            string archDir = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "Win32",
                Architecture.Arm64 => "ARM64",
                _ => "x64",
            };
            var candidates = new List<string>
            {
                // ① 与进程架构匹配的子目录（首选）
                Path.Combine(baseDir, archDir, "NSudoAPI.dll"),
                // ② 平铺在程序目录
                Path.Combine(baseDir, "NSudoAPI.dll"),
            };
            // ③ 依次回落到全部架构子目录（同架构的优先，其次其它）
            foreach (var d in new[] { archDir, "x64", "Win32", "ARM64" }.Distinct())
                candidates.Add(Path.Combine(baseDir, d, "NSudoAPI.dll"));
            // ④ 开发机上的 NSudo 构建产物
            candidates.Add(@"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\x64\NSudoAPI.dll");
            candidates.Add(@"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\Win32\NSudoAPI.dll");
            candidates.Add(@"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\ARM64\NSudoAPI.dll");

            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>
        /// 用 NSudo 以 TrustedInstaller 身份启动自己（同一个 exe，带原参数）。
        /// 返回 true 表示已成功派生子进程（调用方应当**直接退出**，不要继续执行）。
        /// </summary>
        /// <param name="arguments">原样传给子进程的参数串</param>
        /// <param name="log">日志回调</param>
        public static bool TryRelaunchAsTrustedInstaller(string arguments, Action<string>? log = null)
        {
            string? dll = FindNSudoApi();
            if (dll == null) { log?.Invoke("未找到 NSudoAPI.dll"); return false; }

            IntPtr h = LoadLibraryW(dll);
            if (h == IntPtr.Zero) { log?.Invoke("NSudoAPI.dll 加载失败"); return false; }

            IntPtr proc = GetProcAddress(h, "NSudoCreateProcess");
            if (proc == IntPtr.Zero) { log?.Invoke("NSudoAPI.dll 没有 NSudoCreateProcess 导出"); return false; }

            var fn = Marshal.GetDelegateForFunctionPointer<NSudoCreateProcessFn>(proc);

            string exe = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) { log?.Invoke("拿不到当前 exe 路径"); return false; }

            string cmd = "\"" + exe + "\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
            string cwd = Directory.GetCurrentDirectory();

            try
            {
                // waitInterval = 0xFFFFFFFF 表示 INFINITE：等子进程结束再返回
                int hr = fn(NSUDO_USER_MODE_TRUSTED_INSTALLER, NSUDO_PRIVILEGES_MODE_ENABLE_ALL,
                            NSUDO_MANDATORY_LABEL_DEFAULT, NSUDO_PROCESS_PRIORITY_NORMAL,
                            NSUDO_SHOW_WINDOW_HIDE, 0xFFFFFFFF, false, cmd, cwd);
                log?.Invoke($"NSudoCreateProcess(TrustedInstaller) -> 0x{hr:X8}");
                return hr >= 0;
            }
            catch (Exception ex) { log?.Invoke("NSudoCreateProcess 异常: " + ex.Message); return false; }
            finally { /* h 不释放：进程即将退出 */ }
        }

        /// <summary>
        /// 以当前用户（但启用全部特权）经 NSudo 重启自己。
        /// </summary>
        public static bool TryRelaunchWithAllPrivileges(string arguments, Action<string>? log = null)
        {
            string? dll = FindNSudoApi();
            if (dll == null) return false;
            IntPtr h = LoadLibraryW(dll);
            if (h == IntPtr.Zero) return false;
            IntPtr proc = GetProcAddress(h, "NSudoCreateProcess");
            if (proc == IntPtr.Zero) return false;

            var fn = Marshal.GetDelegateForFunctionPointer<NSudoCreateProcessFn>(proc);
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return false;
            string cmd = "\"" + exe + "\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);

            try
            {
                int hr = fn(NSUDO_USER_MODE_CURRENT_USER, NSUDO_PRIVILEGES_MODE_ENABLE_ALL,
                            NSUDO_MANDATORY_LABEL_DEFAULT, NSUDO_PROCESS_PRIORITY_NORMAL,
                            NSUDO_SHOW_WINDOW_HIDE, 0xFFFFFFFF, false, cmd, Directory.GetCurrentDirectory());
                log?.Invoke($"NSudoCreateProcess(全部特权) -> 0x{hr:X8}");
                return hr >= 0;
            }
            catch { return false; }
        }

        /// <summary>用哪个身份跑子进程。</summary>
        public enum Identity
        {
            /// <summary>TrustedInstaller —— CBS 离线服务的 Commit 阶段写得进它属主的 hive 键。</summary>
            TrustedInstaller,
            /// <summary>LocalSystem —— 比管理员高，但写不进 TrustedInstaller 属主的键。</summary>
            System,
            /// <summary>当前用户（但启用全部特权）。</summary>
            CurrentUser,
        }

        /// <summary>
        /// 以指定身份运行任意命令行（经 NSudo）。这是**通用入口** ——
        /// 产品里凡是"必须在更高身份下做"的一步（CBS 装包、离线 hive 读写、
        /// 驱动库落地等）都应该走它，而不是各自去 P/Invoke。
        ///
        /// 【实测依据】离线 CBS 的权限是分层的，逐级才通：
        ///   管理员 + SeBackup/Restore/TakeOwnership/Security + takeown/icacls  → 卡在驱动 q-unstage
        ///   LocalSystem                                                        → 能过 StageFile/Doqe，
        ///                                                                        卡在 Commit 写 hive 键
        ///   TrustedInstaller                                                   → 才拥有那些键
        /// </summary>
        /// <param name="identity">目标身份</param>
        /// <param name="commandLine">完整命令行（含 exe 路径；请自行加引号）</param>
        /// <param name="workingDirectory">工作目录，空则用当前目录</param>
        /// <param name="waitMs">等待上限；INFINITE = 0xFFFFFFFF(4294967295)</param>
        /// <param name="log">日志回调</param>
        /// <returns>true = NSudo 返回成功（S_OK）</returns>
        public static bool RunAs(Identity identity, string commandLine,
                                 string? workingDirectory = null, uint waitMs = 0xFFFFFFFF,
                                 Action<string>? log = null)
        {
            string? dll = FindNSudoApi();
            if (dll == null) { log?.Invoke("✗ 未找到 NSudoAPI.dll（提权不可用）"); return false; }

            IntPtr h = LoadLibraryW(dll);
            if (h == IntPtr.Zero)
            {
                log?.Invoke($"✗ NSudoAPI.dll 加载失败: {dll}（win32 {Marshal.GetLastWin32Error()}）");
                return false;
            }
            IntPtr proc = GetProcAddress(h, "NSudoCreateProcess");
            if (proc == IntPtr.Zero) { log?.Invoke("✗ NSudoAPI.dll 没有 NSudoCreateProcess 导出"); return false; }

            var fn = Marshal.GetDelegateForFunctionPointer<NSudoCreateProcessFn>(proc);
            int userMode = identity switch
            {
                Identity.TrustedInstaller => NSUDO_USER_MODE_TRUSTED_INSTALLER,
                Identity.System => NSUDO_USER_MODE_SYSTEM,
                _ => NSUDO_USER_MODE_CURRENT_USER,
            };
            string cwd = string.IsNullOrWhiteSpace(workingDirectory)
                         ? Directory.GetCurrentDirectory() : workingDirectory!;

            try
            {
                log?.Invoke($"→ NSudo 以 {identity} 启动: {commandLine}");
                int hr = fn(userMode, NSUDO_PRIVILEGES_MODE_ENABLE_ALL,
                            NSUDO_MANDATORY_LABEL_DEFAULT, NSUDO_PROCESS_PRIORITY_NORMAL,
                            NSUDO_SHOW_WINDOW_HIDE, waitMs, false, commandLine, cwd);
                if (hr < 0) log?.Invoke($"✗ NSudoCreateProcess({identity}) -> 0x{hr:X8}");
                else log?.Invoke($"✓ NSudoCreateProcess({identity}) 完成");
                return hr >= 0;
            }
            catch (Exception ex) { log?.Invoke("✗ NSudoCreateProcess 异常: " + ex.Message); return false; }
        }

        /// <summary>
        /// 以 TrustedInstaller 身份运行一个 exe（便捷重载）。
        /// </summary>
        public static bool RunAsTrustedInstaller(string exePath, string arguments,
                                                 string? workingDirectory = null,
                                                 uint waitMs = 0xFFFFFFFF, Action<string>? log = null)
        {
            string cmd = "\"" + exePath + "\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
            return RunAs(Identity.TrustedInstaller, cmd, workingDirectory, waitMs, log);
        }

        /// <summary>防止自我重启无限递归：重启出去的子进程里会带上这个标记。</summary>
        public const string RelaunchedMarker = "OCID_ELEVATED_TI";

        /// <summary>
        /// 【入口级】确保当前进程是 TrustedInstaller —— 不是就用 NSudo 以 TI 重启自己。
        ///
        /// 用法（在 Program.Main / App.OnStartup 的最前面）：
        /// <code>
        /// if (ElevationHelper.EnsureTrustedInstallerOrRelaunch(log)) return;   // 已派生子进程，本进程退出
        /// </code>
        ///
        /// 【为什么整流程都要 TI，而不是只提权某一步】
        /// CBS 的 Commit 阶段要写离线 hive 里归 TrustedInstaller 属主的注册表键；
        /// 身份不对就在那里 E_ACCESSDENIED，而**失败会把镜像标记成
        /// CBS_E_IMAGE_UNSERVICEABLE**（必须重新 apply 才能再试）。
        /// 与其在流程中途换身份、还要处理"前半段已经改了镜像"的残留，
        /// 不如让整条流程一开始就在 TI 下跑。
        ///
        /// 【何时不提权】已经是 TI；或已带重启标记（避免递归）；或 NSudo 不可用
        /// （此时如实返回 false，让调用方按原身份继续，并自己承担可能的失败）。
        /// </summary>
        /// <returns>true = 已成功派生子进程，调用方**应立即返回/退出**</returns>
        public static bool EnsureTrustedInstallerOrRelaunch(Action<string>? log = null)
        {
            if (IsTrustedInstaller()) { log?.Invoke("身份: TrustedInstaller（无需重启）"); return false; }
            if (Environment.GetEnvironmentVariable(RelaunchedMarker) == "1")
            {
                log?.Invoke("身份: 已带重启标记（避免递归），按当前身份继续");
                return false;
            }

            string? dll = FindNSudoApi();
            if (dll == null)
            {
                log?.Invoke("身份: 非 TrustedInstaller，且找不到 NSudoAPI.dll —— 按当前身份继续"
                          + "（若 CBS 在 Commit 阶段 E_ACCESSDENIED，请手工以 TI 运行）");
                return false;
            }

            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exe)) { log?.Invoke("拿不到当前 exe 路径，无法重启"); return false; }

            // 原样转发参数（含空格/引号的参数要重新加引号）
            var parts = new List<string>();
            foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                parts.Add(a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a);
            string args = string.Join(" ", parts);

            // 带上标记，子进程不会再重启
            Environment.SetEnvironmentVariable(RelaunchedMarker, "1");
            string cmd = "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");
            log?.Invoke($"身份: 非 TrustedInstaller —— 用 NSudo 以 TI 重启自己（标记 {RelaunchedMarker}=1）");
            bool ok = RunAs(Identity.TrustedInstaller, cmd, Directory.GetCurrentDirectory(), 0xFFFFFFFF, log);
            if (!ok) Environment.SetEnvironmentVariable(RelaunchedMarker, null);
            return ok;
        }

        /// <summary>
        /// 退化方案：标准 UAC 提权（ShellExecute "runas"）。不等待子进程。
        /// </summary>
        public static bool TryRelaunchElevated(string arguments, Action<string>? log = null)
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) { log?.Invoke("拿不到 exe 路径"); return false; }

                var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" };
                Process.Start(psi);
                log?.Invoke("已通过 UAC 重新启动（子进程独立运行）");
                return true;
            }
            catch (Exception ex) { log?.Invoke("UAC 提权失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 统一入口：需要提权就提权重启，并告诉调用方"你应该退出了"。
        /// </summary>
        /// <returns>true = 已派生子进程，调用方应立即 return（不要再干活）</returns>
        public static bool EnsureElevatedOrRelaunch(string arguments, Action<string>? log = null,
            bool preferTrustedInstaller = false)
        {
            if (IsElevated())
            {
                log?.Invoke("已是管理员权限，继续");
                return false;
            }

            log?.Invoke("当前不是管理员权限，尝试提权重启…");
            if (preferTrustedInstaller && TryRelaunchAsTrustedInstaller(arguments, log)) return true;
            if (TryRelaunchWithAllPrivileges(arguments, log)) return true;
            if (TryRelaunchElevated(arguments, log)) return true;

            log?.Invoke("提权失败 —— 请手动以管理员身份运行本程序");
            return false;
        }

        /// <summary>供 CLI 打印的一段人话说明。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("权限状态:");
            sb.AppendLine("  管理员           : " + IsElevated());
            sb.AppendLine("  LocalSystem      : " + IsSystem());
            sb.AppendLine("  TrustedInstaller : " + IsTrustedInstaller()
                          + "   ← 判 token 的【组】，不判用户名（TI 的 User 仍是 SYSTEM）");
            sb.AppendLine("  NSudoAPI.dll     : " + (FindNSudoApi() ?? "(未找到)"));
            sb.AppendLine($"  重启标记         : {RelaunchedMarker}={Environment.GetEnvironmentVariable(RelaunchedMarker) ?? "(未设)"}");
            return sb.ToString();
        }
    }
}
