using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 启用当前进程的令牌特权。
    ///
    /// 【为什么必需】CBS 离线服务与 WCP 铺底都要读写目标镜像的注册表 hive 与组件存储。
    /// 由 `dism /Apply-Image` 铺出来的镜像会**还原 WIM 里的原始 ACL**：hive 归属
    /// TrustedInstaller，Administrators 没有访问权。此时即使进程已提权，也会失败。
    ///
    /// 管理员令牌**本来就持有** `SeBackupPrivilege` / `SeRestorePrivilege` /
    /// `SeTakeOwnershipPrivilege`，但它们**默认处于禁用状态**，必须显式启用。
    ///
    /// 【实测症状】未启用时：
    ///   · `SxSFounderService.CreateNewOfflineStore` → 0x8007051A
    ///   · `ICbsSession::Initialize`                 → 0x8007051A
    ///   0x8007051A = Win32 1306 = ERROR_NO_SUCH_PRIVILEGE（The required privilege is not
    ///   held by the client）。两者报同一个错，正说明不是逻辑问题而是特权问题。
    ///
    /// 【为什么以前"看起来没问题"】早期测试镜像是手工拷文件做的，ACL 较宽松；
    /// 一旦换成 dism 正规铺出的镜像就会暴露。所以这个修复是必需的，不是可选优化。
    /// </summary>
    public static class PrivilegeHelper
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        /// <summary>CBS/WCP 离线服务所需的标准特权集合。</summary>
        public static readonly string[] ServicingPrivileges =
        {
            "SeBackupPrivilege",        // 绕过 ACL 读文件（读目标镜像的 hive）
            "SeRestorePrivilege",       // 绕过 ACL 写文件（写目标镜像的 hive / 组件存储）
            "SeTakeOwnershipPrivilege", // 必要时取得所有权
            "SeSecurityPrivilege"       // 读写 SACL（安全描述符）
        };

        /// <summary>
        /// 启用给定特权。返回成功启用的名单与失败说明。
        /// **不抛异常** —— 调用方应当在失败时给出可读提示，而不是崩掉。
        /// </summary>
        public static (List<string> enabled, List<string> failed) Enable(params string[] names)
        {
            var enabled = new List<string>();
            var failed = new List<string>();
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                {
                    failed.Add($"OpenProcessToken 失败 (Win32 {Marshal.GetLastWin32Error()})");
                    return (enabled, failed);
                }

                foreach (var name in names)
                {
                    if (!LookupPrivilegeValue(null, name, out LUID luid))
                    {
                        failed.Add($"{name}: LookupPrivilegeValue 失败 (Win32 {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new[] { new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED } }
                    };

                    if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    {
                        failed.Add($"{name}: AdjustTokenPrivileges 失败 (Win32 {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    // AdjustTokenPrivileges 即使成功也可能什么都没改，必须查 LastError
                    int err = Marshal.GetLastWin32Error();
                    if (err == 1300 /* ERROR_NOT_ALL_ASSIGNED */)
                        failed.Add($"{name}: 令牌中未持有该特权");
                    else
                        enabled.Add(name);
                }
                return (enabled, failed);
            }
            catch (Exception ex) { failed.Add("异常: " + ex.Message); return (enabled, failed); }
            // ⚠ token 是 Win32 内核句柄，不是 COM 接口指针！
            //   早前这里误用 Marshal.Release —— 它会按 COM vtable 解引用，
            //   直接访问违例 0xC0000005；而损坏状态异常 try/catch 抓不住，进程当场死。
            //   正确 API 是 CloseHandle。
            finally { if (token != IntPtr.Zero) try { CloseHandle(token); } catch { } }
        }

        /// <summary>启用 CBS/WCP 所需的全部标准特权，并把结果写进日志回调。</summary>
        public static void EnableServicingPrivileges(Action<string>? log = null)
        {
            var (ok, bad) = Enable(ServicingPrivileges);
            if (ok.Count > 0) log?.Invoke("已启用特权: " + string.Join(", ", ok));
            foreach (var b in bad) log?.Invoke("⚠ 特权未启用: " + b);
            if (bad.Count > 0)
                log?.Invoke("  提示：若随后出现 0x8007051A (ERROR_NO_SUCH_PRIVILEGE)，"
                          + "通常说明目标镜像由 dism /Apply-Image 铺出、ACL 归属 TrustedInstaller，"
                          + "需要上面的特权才能读写其 hive。");
        }

        /// <summary>
        /// 把常见 HRESULT 翻译成人话（供错误信息使用）。
        /// </summary>
        /// <remarks>
        /// ⚠ 注意：0x8007051A 是 **ERROR_REVISION_MISMATCH**，不是 ERROR_NO_SUCH_PRIVILEGE
        /// （我最初把 Win32 1306 记成了后者，是错的）。CBS 的实际报错链是：
        ///   STATUS_REVISION_MISMATCH ← Windows::COM::VerifyStoreVersionNumber
        ///   → Failed to get offline CSI store [0x8007051a]
        /// 根因是**加载的 servicing stack 与镜像的 CSI 存储版本不匹配**，
        /// 典型场景：镜像是 x86（自带 x86 栈），而进程是 x64 加载不了它，
        /// 于是回退到宿主 x64 栈（例如构建号 19041）去服务 8400 的镜像 → 版本不符。
        /// 对策：**用与镜像同架构的进程**（x86 镜像 → x86 进程），让镜像自带的栈被加载。
        /// </remarks>
        public static string? Explain(int hr)
        {
            if (hr == unchecked((int)0x8007051A))
                return "0x8007051A = ERROR_REVISION_MISMATCH：加载的 servicing stack 与目标镜像的 CSI 存储版本不匹配。"
                     + "最常见原因是目标镜像是 x86（自带 x86 栈），而当前进程是 x64 加载不了它，"
                     + "于是回退到宿主栈去服务该镜像 → 版本不符。"
                     + "对策：用与镜像同架构的进程（x86 镜像请用 x86 版本的进程）。";
            if (hr == unchecked((int)0x80070520))   // ERROR_NO_SUCH_PRIVILEGE = 1314 = 0x522，这里保留提示位
                return "权限不足：确认已以管理员运行，且备份/还原特权已启用。";
            return null;
        }
    }
}
