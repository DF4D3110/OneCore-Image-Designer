using System;
using System.Diagnostics;
using System.IO;
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

        // NSUDO_USER_MODE_TYPE / PRIVILEGES_MODE / MANDATORY_LABEL / SHOW_WINDOW（NSudo 定义）
        const int NSUDO_USER_MODE_TRUSTED_INSTALLER = 3;
        const int NSUDO_USER_MODE_CURRENT = 1;
        const int NSUDO_PRIVILEGES_MODE_ENABLE_ALL = 1;
        const int NSUDO_MANDATORY_LABEL_SYSTEM = 3;
        const int NSUDO_PROCESS_PRIORITY_NORMAL = 1;
        const int NSUDO_SHOW_WINDOW_HIDE = 0;

        /// <summary>当前进程是否已是管理员。</summary>
        public static bool IsElevated() => CbsSafety.IsElevated();

        /// <summary>是否具备 TrustedInstaller 身份（严格判定，通常只有服务进程满足）。</summary>
        public static bool IsTrustedInstaller()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                // TrustedInstaller SID = S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464
                return id.User?.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
            }
            catch { return false; }
        }

        /// <summary>
        /// 找 NSudoAPI.dll（多个可能位置）。
        /// </summary>
        static string? FindNSudoApi()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NSudoAPI.dll"),
                @"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\x64\NSudoAPI.dll",
                @"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\Win32\NSudoAPI.dll",
                @"D:\UUP-Project\NSudo-master\Source\Native\Output\Binaries\Release\ARM64\NSudoAPI.dll",
            };
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
                            NSUDO_MANDATORY_LABEL_SYSTEM, NSUDO_PROCESS_PRIORITY_NORMAL,
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
                int hr = fn(NSUDO_USER_MODE_CURRENT, NSUDO_PRIVILEGES_MODE_ENABLE_ALL,
                            NSUDO_MANDATORY_LABEL_SYSTEM, NSUDO_PROCESS_PRIORITY_NORMAL,
                            NSUDO_SHOW_WINDOW_HIDE, 0xFFFFFFFF, false, cmd, Directory.GetCurrentDirectory());
                log?.Invoke($"NSudoCreateProcess(全部特权) -> 0x{hr:X8}");
                return hr >= 0;
            }
            catch { return false; }
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
            sb.AppendLine("  管理员      : " + IsElevated());
            sb.AppendLine("  TrustedInstaller : " + IsTrustedInstaller());
            sb.AppendLine("  NSudoAPI.dll: " + (FindNSudoApi() ?? "(未找到)"));
            return sb.ToString();
        }
    }
}
