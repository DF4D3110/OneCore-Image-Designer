using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>目标路径的安全描述符快照（取所有权**之前**拍下，用完还原）。</summary>
    internal sealed class PathSecuritySnapshot
    {
        public string Path = "";
        /// <summary>OWNER|GROUP|DACL 的完整 SDDL 文本（含 <c>P</c> 保护标志）。</summary>
        public string Sddl = "";
    }

    /// <summary>
    /// OCID 自带的"TrustedInstaller 权限"链路 —— 不依赖任何第三方提权库。
    ///
    /// 【为什么需要】由 <c>dism /Apply-Image</c> 铺出来的镜像里，<c>Windows\WinSxS</c>、
    /// <c>Windows\System32</c> 等目录的 **owner 是 NT SERVICE\TrustedInstaller**，DACL 里
    /// 只有它拿 Full Control，<c>SY</c>/<c>BA</c>/<c>BU</c>/<c>AC</c> 只有 <c>0x1200a9</c>
    /// （读+执行+同步，**没有**建文件/建子目录的权）。往这样的目录里落文件会直接
    /// `Access to the path … is denied`。
    ///
    /// 【实测：为什么"偷 TI 令牌"这条路走不通】（宿主 Win10 19045）
    ///   · <c>TrustedInstaller.exe</c>（LocalSystem，demand-start）是 **PPL**
    ///     （<c>NtQueryInformationProcess(ProcessProtectionInformation=61)</c> → type=1、signer=159）。
    ///   · 即使**已启用 SeDebugPrivilege**，<c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c>
    ///     能成功，但 <c>OpenProcessToken(TOKEN_DUPLICATE|TOKEN_QUERY)</c> 一律
    ///     **Win32 5 (ACCESS_DENIED)** —— 受保护进程的令牌不对普通提权进程开放。
    ///   所以 NSudo 那套"拿 TI 服务进程令牌再 CreateProcessAsUser"的做法在新系统上不可靠；
    ///   本模块把它降级成**可选尝试**（<see cref="TryImpersonateTrustedInstaller"/>），
    ///   真正的兜底是下面这条**必然可行**的路。
    ///
    /// 【实测：真正可行的机制】管理员令牌本来就持有
    /// <c>SeTakeOwnershipPrivilege</c> / <c>SeRestorePrivilege</c>（默认禁用），
    /// 显式启用后：
    ///   · <c>SeRestorePrivilege</c> 单独**不够**：实测它能在 TI 属主目录下
    ///     `CreateDirectory`（建子目录成功），但 `File.WriteAllText` 建**文件**仍被拒。
    ///   · <c>SeTakeOwnershipPrivilege</c> + <c>SetNamedSecurityInfo(OWNER|DACL|PROTECTED)</c>
    ///     把 owner 改成 <c>BA</c>、DACL 换成可继承的可写策略 → **成功**，随后写文件、
    ///     删目录都正常。用完再把 owner/DACL 按快照**原样还原**。
    ///   · 注意：还原 owner 到 TI 也需要 <c>SeTakeOwnershipPrivilege</c> 处于启用态
    ///     （owner 只隐式拥有 READ_CONTROL/WRITE_DAC，**没有** WRITE_OWNER；
    ///     `icacls /setowner` 不会自己启用该特权，实测它在这一步就是 "Access is denied"）。
    ///
    /// 【链路位置】CSI 落位（<see cref="CsiFileService"/>）遇到已存在且被锁死的目录时
    /// 自动走这里；产品入口的提权重启见 <see cref="TryRelaunchElevated"/>。
    /// </summary>
    internal static class TrustedInstallerAccess
    {
        // ── 常量 ────────────────────────────────────────────────────────────

        /// <summary>NT SERVICE\TrustedInstaller 的 SID。</summary>
        public const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        /// <summary>
        /// 临时给自己放写权用的 SDDL：owner 改成 <c>BA</c>，DACL 换成**受保护、可继承、
        /// 可写**（SY/BA/OW 全权）。与 <see cref="CsiFileService"/> 新建正本目录时用的是同一条。
        /// </summary>
        public const string TemporaryWriteSddl =
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;OW)";

        const string TrustedInstallerServiceName = "TrustedInstaller";

        // ── 身份 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 当前进程是否具备 TrustedInstaller 身份。
        /// 判"用户或组"两种形态：TI 服务自己跑在 SYSTEM 下、TI 是它的**组**；
        /// 而经提权启动的进程里 TI 会是**用户**。
        /// </summary>
        public static bool IsTrustedInstaller()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                if (string.Equals(id.User?.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
                    return true;
                var groups = id.Groups;
                if (groups != null)
                    foreach (var g in groups)
                        if (string.Equals(g?.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
                            return true;
            }
            catch { }
            return false;
        }

        /// <summary>当前进程是否在管理员组里（是否"已提权"由调用方另行判断）。</summary>
        public static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ── 安全描述符快照 / 取所有权 / 还原 ────────────────────────────────

        /// <summary>读目标路径的 owner/group/DACL 快照。读不到就返回 false（调用方**不要**继续改）。</summary>
        public static bool TrySnapshot(string path, out PathSecuritySnapshot? snapshot, out string error)
        {
            snapshot = null;
            error = "";
            if (!OcidServicingNative.TryReadPathSddl(path, OcidServicingNative.SecInfo.DefaultRead,
                                                     out string sddl, out string err))
            {
                error = err;
                return false;
            }
            snapshot = new PathSecuritySnapshot { Path = path, Sddl = sddl };
            return true;
        }

        /// <summary>
        /// 取所有权并给自己写权（owner ⇒ BA，DACL ⇒ <see cref="TemporaryWriteSddl"/>）。
        /// **会改变目标的安全描述符** —— 调用方负责用 <see cref="TryRestore"/> 还原。
        /// </summary>
        public static bool TryTakeOwnershipForWrite(string path, out string error, Action<string>? log = null)
        {
            PrivilegeHelper.EnableServicingPrivileges(log);
            var outcome = OcidServicingNative.ApplyPathSddl(path, TemporaryWriteSddl, out error);
            if (outcome == OcidServicingNative.SddlApplyOutcome.Applied) return true;
            if (outcome == OcidServicingNative.SddlApplyOutcome.SkippedInherit)
            {
                error = "目标带继承标记，SDDL 被跳过：" + error;
                return false;
            }
            return false;
        }

        /// <summary>
        /// 从一段 SDDL 里取出开头的 <c>O:</c>/<c>G:</c> 两段（拼成只带属主/组的 SDDL）。
        ///
        /// 用途：取所有权写入意味着我们临时把自己的属主位改成 <c>BA</c>；而落**声明** ACL
        /// 那条路（<see cref="ApplyDeclaredSddl"/>）不还原快照，清单里绝大多数 SDDL 又只写
        /// <c>D:</c>（不声明属主）—— 那就该把属主还给原主：取所有权只是拿到 WRITE_DAC 的
        /// **手段**，不该留在目标上（清单没声明属主时，目标属主应当保持原样）。
        /// 段值字符集只可能字母/数字/连字符（`S-1-5-32-544`、`BA`、`SY`），照这个扫。
        /// </summary>
        public static string OwnerGroupPrefix(string sddl)
        {
            if (string.IsNullOrEmpty(sddl)) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i + 1 < sddl.Length && (sddl[i] == 'O' || sddl[i] == 'G') && sddl[i + 1] == ':')
            {
                int j = i + 2;
                while (j < sddl.Length && (char.IsLetterOrDigit(sddl[j]) || sddl[j] == '-')) j++;
                sb.Append(sddl[i]).Append(':').Append(sddl, i + 2, j - (i + 2));
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 按快照**原样**还原 owner/group/DACL（含 <c>P</c> 保护标志与 <c>AI</c> 状态位）。
        /// </summary>
        ///
        /// 【实测：对**容器**设置 DACL 会把窗口期内新建的子对象"重新继承"一遍 —— Windows 语义】
        /// 系统在给目录设 DACL 时，会把新 DACL 里可继承的 ACE 传播给已存在的子对象：
        /// 子对象原有的**继承** ACE（带 <c>ID</c>）被替换，**显式** ACE 原样保留。三条实测：
        ///   · `icacls &lt;dir&gt; /inheritance:r /grant:r "*&lt;TI&gt;:(OI)(CI)F"` 与直接调
        ///     <c>SetNamedSecurityInfoW</c> 的结果**完全一致** —— 不是我们调用方式的问题；
        ///   · 快照里的 <c>AI</c> 剥不剥都一样会传播（曾为此专门写过 StripAutoInherit，
        ///     实测无效、已删）—— 而且 <c>AI</c> 是对象的真实状态位，还原就该原样写回；
        ///   · 子对象若持**显式且受保护**的 ACL（`D:P(A;;FA;;;BA)…`），传播**不会**动它。
        /// 由此得到调用方两条纪律：
        ///   ① 窗口期内写出来、**之后还要用**的文件，必须在窗口仍开着时用完
        ///      （见 <see cref="ProtectedWriteWindow"/>：CSI"正本拷完立刻建落点硬链接"就是这个原因
        ///      —— <c>CreateHardLinkW</c> 打不开一个自己没权限的源文件，实测以"拒绝访问"失败）；
        ///   ② 窗口关闭后子文件若被传播成父目录那条策略（可能只剩 TI），**事后仍可修**：
        ///      子对象的属主是创建者（BA），而属主永远隐式持有 READ_CONTROL/WRITE_DAC，
        ///      <c>OcidApplyPathSddl</c> 依旧写得进去（实测通过）。
        public static bool TryRestore(string path, PathSecuritySnapshot snapshot, out string error,
                                     Action<string>? log = null)
        {
            if (snapshot == null) { error = "快照为空"; return false; }
            PrivilegeHelper.EnableServicingPrivileges(log);
            var outcome = OcidServicingNative.ApplyPathSddl(path, snapshot.Sddl, out error);
            return outcome == OcidServicingNative.SddlApplyOutcome.Applied
                || outcome == OcidServicingNative.SddlApplyOutcome.SkippedInherit;
        }

        // ── 组合：往受保护目录里写东西 ──────────────────────────────────────

        /// <summary>是不是"权限不足"类异常（File.Copy/建链 抛的 UnauthorizedAccessException 或 0x80070005）。</summary>
        public static bool IsAccessDenied(Exception ex)
            => ex is UnauthorizedAccessException || unchecked((uint)ex.HResult) == 0x80070005u;

        /// <summary>
        /// 在 <paramref name="dirForWrite"/> 里执行一次写动作：先直接试；只有**权限不足**时
        /// 才取所有权重试，并在成功后**原样还原**安全描述符（窗口在此期间一直开着）。
        /// 窗口期内还要接着做别的事（例如拿刚写进去的文件当源去建硬链接）就用
        /// <see cref="ForWrite"/>。
        /// </summary>
        /// <param name="ownershipTaken">true = 走了取所有权那条路（调用方可以记账）。</param>
        public static bool TryWriteInto(string dirForWrite, Action write, out bool ownershipTaken,
                                       out string error, Action<string>? log = null)
            => ForWrite(dirForWrite, write, out ownershipTaken, out error, log);

        /// <summary>
        /// 同上，但整个 <paramref name="work"/> 都跑在窗口期内，跑完才还原安全描述符。
        /// **实测必须这样**：窗口期新建的子对象会被父目录的还原"重新继承"（见 <see cref="TryRestore"/>），
        /// 一旦还原，那个子对象可能只剩 TI 能访问 —— 之后再拿它当源建硬链接就以"拒绝访问"失败。
        /// 可以嵌套调用（两份不同的受保护目录各自开自己的窗口），内层先还原、外层后还原。
        ///
        /// 注意：权限不足重试时会把 <paramref name="work"/> 整个重跑一遍，所以它要**可重入**
        /// （幂等；不要留"做了一半"的状态，例如带 <c>overwrite:true</c> 的复制）。
        /// </summary>
        public static bool ForWrite(string dirForWrite, Action work, out bool ownershipTaken,
                                    out string error, Action<string>? log = null)
        {
            ownershipTaken = false;
            error = "";
            PrivilegeHelper.EnableServicingPrivileges(log);

            PathSecuritySnapshot? snap = null;
            bool secured = false;

            void EnsureWindow()
            {
                if (secured) return;
                if (snap == null && !TrySnapshot(dirForWrite, out snap, out string serr))
                    throw new UnauthorizedAccessException(
                        $"读不到受保护目录 {dirForWrite} 的安全描述符（{serr}）");
                if (snap == null)
                    throw new UnauthorizedAccessException(
                        $"读不到受保护目录 {dirForWrite} 的安全描述符");
                if (!TryTakeOwnershipForWrite(dirForWrite, out string terr, log))
                    throw new UnauthorizedAccessException(
                        $"对受保护目录 {dirForWrite} 取所有权失败（{terr}）");
                secured = true;
            }

            bool ok = false;
            try
            {
                try { work(); ok = true; }
                catch (Exception ex) when (IsAccessDenied(ex) && !secured)
                {
                    error = ex.Message;
                    EnsureWindow();
                    work();
                    ok = true;
                }
            }
            catch (Exception ex) { error = ex.Message; }

            if (secured && snap != null)
            {
                if (!TryRestore(dirForWrite, snap, out string rerr, log))
                    log?.Invoke($"[TI] ⚠ 还原 {dirForWrite} 的安全描述符失败：{rerr}" +
                                "（目标此后归 BA 属主，需人工修回）");
                else if (ok)
                    log?.Invoke($"[TI] 受保护目录：取所有权写入后已还原安全描述符 —— {dirForWrite}");
            }

            ownershipTaken = secured;
            return ok;
        }

        /// <summary>
        /// 把清单声明的 SDDL 落到**受保护**路径上。与 <see cref="TryWriteInto"/> 的区别：
        /// 这里落的 SDDL **就是目标状态**，所以取完所有权后**不还原**。
        /// </summary>
        public static OcidServicingNative.SddlApplyOutcome ApplyDeclaredSddl(
            string path, string sddl, out bool ownershipTaken, out string error, Action<string>? log = null)
        {
            ownershipTaken = false;
            error = "";
            PrivilegeHelper.EnableServicingPrivileges(log);

            var outcome = OcidServicingNative.ApplyPathSddl(path, sddl, out error);
            if (outcome == OcidServicingNative.SddlApplyOutcome.Applied) return outcome;

            // 失败且**确实是权限不足** ⇒ 取所有权（owner=BA）后再落一次 = 声明状态
            // 只认"Win32 错误 5"（原生那层的原话，见 SecurityDescriptor.cpp 的
            // `SetNamedSecurityInfo 失败，Win32 错误 <rc>`）；解析失败之类的**绝不能**
            // 走到取所有权这一步，否则会白白改掉别人对象的属主。
            bool looksDenied = outcome == OcidServicingNative.SddlApplyOutcome.Failed
                            && error.Contains("Win32 错误 5");
            if (!looksDenied) return outcome;

            // 先拍快照：取完所有权属主就变了，而"声明里没写属主"时要按原值还回去
            bool haveSnap = TrySnapshot(path, out var before, out _);
            string declaredOwner = OwnerGroupPrefix(sddl);

            if (!TryTakeOwnershipForWrite(path, out string terr, log))
            {
                error = $"取所有权失败（{terr}）→ 原失败：{error}";
                return OcidServicingNative.SddlApplyOutcome.Failed;
            }
            ownershipTaken = true;
            var again = OcidServicingNative.ApplyPathSddl(path, sddl, out string err2);
            if (again != OcidServicingNative.SddlApplyOutcome.Applied)
            {
                error = $"取所有权后仍然失败：{err2}（原失败：{error}）";
                return again;
            }

            // 清单声明的 SDDL 大多只写 `D:`（DACL），不声明属主/组 —— 这种情形下
            // 属主应当保持原样，不能因为"我们用了取所有权这个手段"就把属主留成 BA。
            if (declaredOwner.Length == 0 && haveSnap && before != null)
            {
                string originalOwner = OwnerGroupPrefix(before.Sddl);
                if (originalOwner.Length > 0)
                {
                    var oret = OcidServicingNative.ApplyPathSddl(path, originalOwner, out string oerr);
                    if (oret != OcidServicingNative.SddlApplyOutcome.Applied)
                        log?.Invoke($"[TI] ⚠ 声明未写属主，把属主还原成 {originalOwner} 失败：{oerr} —— {path}");
                }
            }

            log?.Invoke($"[TI] 受保护路径：取所有权后落入声明 ACL —— {path}");
            return again;
        }

        // ── 可选：TI 令牌（新系统上 TI 是 PPL，拿不到） ─────────────────────

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool StartService(IntPtr service, uint argc, string[]? argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatusEx(IntPtr service, int level, IntPtr buffer, uint size, out uint needed);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs,
                                            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ImpersonateLoggedOnUser(IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool RevertToSelf();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        static IntPtr _tiToken = IntPtr.Zero;

        /// <summary>
        /// 尝试以 TrustedInstaller 身份**模拟**当前线程（TI 服务令牌 → DuplicateTokenEx →
        /// ImpersonateLoggedOnUser）。成功返回 true，失败把原因写在 <paramref name="diag"/>。
        ///
        /// ⚠ 现代 Windows 上 TI 是 PPL，这一步会以 Win32 5 (ACCESS_DENIED) 失败 —— 这是**预期**
        /// 结果，不是 bug；调用方应当退到 <see cref="TryWriteInto"/> 那条必然可行的路。
        /// </summary>
        public static bool TryImpersonateTrustedInstaller(out string diag)
        {
            diag = "";
            try { PrivilegeHelper.Enable("SeDebugPrivilege", "SeImpersonatePrivilege"); } catch { }

            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero, buf = IntPtr.Zero,
                   proc = IntPtr.Zero, tok = IntPtr.Zero, dup = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, 0x0001 /*CONNECT*/ | 0x0020 /*CREATE*/);
                if (scm == IntPtr.Zero) { diag = $"OpenSCManager 失败 Win32 {Marshal.GetLastWin32Error()}"; return false; }

                svc = OpenService(scm, TrustedInstallerServiceName, 0x0004 /*QUERY_STATUS*/ | 0x0010 /*START*/);
                if (svc == IntPtr.Zero) { diag = $"打开服务 {TrustedInstallerServiceName} 失败 Win32 {Marshal.GetLastWin32Error()}"; return false; }

                buf = Marshal.AllocHGlobal(64);
                QueryServiceStatusEx(svc, 0, buf, 64, out _);
                int state = Marshal.ReadInt32(buf, 4);
                uint pid = (uint)Marshal.ReadInt32(buf, 28);
                if (state != 4 /*RUNNING*/ || pid == 0)
                {
                    StartService(svc, 0, null);
                    for (int i = 0; i < 40 && pid == 0; i++)
                    {
                        System.Threading.Thread.Sleep(250);
                        QueryServiceStatusEx(svc, 0, buf, 64, out _);
                        if (Marshal.ReadInt32(buf, 4) == 4) pid = (uint)Marshal.ReadInt32(buf, 28);
                    }
                }
                if (pid == 0) { diag = "TrustedInstaller 服务没有跑起来，拿不到 PID"; return false; }

                proc = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
                if (proc == IntPtr.Zero) { diag = $"OpenProcess({pid}) 失败 Win32 {Marshal.GetLastWin32Error()}"; return false; }

                if (!OpenProcessToken(proc, 0x0002 /*DUPLICATE*/ | 0x0008 /*QUERY*/, out tok))
                {
                    int e = Marshal.GetLastWin32Error();
                    diag = e == 5
                        ? $"OpenProcessToken 被拒 (Win32 5)：TI 进程是受保护进程（PPL），令牌不对提权进程开放 —— 本系统上必须走取所有权那条路"
                        : $"OpenProcessToken 失败 Win32 {e}";
                    return false;
                }
                if (!DuplicateTokenEx(tok, 0xF01FF /*TOKEN_ALL_ACCESS*/, IntPtr.Zero, 2 /*SecurityImpersonation*/, 2 /*TokenImpersonation*/, out dup))
                {
                    diag = $"DuplicateTokenEx 失败 Win32 {Marshal.GetLastWin32Error()}";
                    return false;
                }
                if (!ImpersonateLoggedOnUser(dup))
                {
                    diag = $"ImpersonateLoggedOnUser 失败 Win32 {Marshal.GetLastWin32Error()}";
                    return false;
                }

                _tiToken = dup; dup = IntPtr.Zero;   // 交给 RevertImpersonation 释放
                diag = "已以 TrustedInstaller 身份模拟当前线程";
                return true;
            }
            catch (Exception ex) { diag = "异常：" + ex.Message; return false; }
            finally
            {
                if (dup != IntPtr.Zero) CloseHandle(dup);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (svc != IntPtr.Zero) CloseHandle(svc);
                if (scm != IntPtr.Zero) CloseHandle(scm);
            }
        }

        /// <summary>结束 <see cref="TryImpersonateTrustedInstaller"/> 的模拟并释放令牌。</summary>
        public static void RevertImpersonation()
        {
            try { if (_tiToken != IntPtr.Zero) { RevertToSelf(); CloseHandle(_tiToken); _tiToken = IntPtr.Zero; } }
            catch { }
        }

        // ── 自提权（不依赖 NSudo） ─────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        /// <summary>
        /// 用系统自带的 UAC 提权（<c>ShellExecuteEx(runas)</c>）重启自己 ——
        /// 这一条**不需要任何第三方提权库**。返回 true 表示已派生提权进程
        /// （调用方应当直接退出当前进程）。
        /// </summary>
        public static bool TryRelaunchElevated(string arguments, Action<string>? log = null)
        {
            if (IsAdministrator()) return false;
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exe.Length == 0) { log?.Invoke("[提权] 取不到自身 exe 路径"); return false; }

                var sei = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    fMask = 0x00000040 /*NOCLOSEPROCESS*/ | 0x00000100 /*NOASYNC*/,
                    lpVerb = "runas",
                    lpFile = exe,
                    lpParameters = arguments,
                    lpDirectory = Directory.GetCurrentDirectory(),
                    nShow = 1,
                };
                if (!ShellExecuteEx(ref sei))
                {
                    log?.Invoke($"[提权] ShellExecuteEx(runas) 失败 Win32 {Marshal.GetLastWin32Error()}（用户拒绝 UAC？）");
                    return false;
                }
                if (sei.hProcess != IntPtr.Zero) CloseHandle(sei.hProcess);
                log?.Invoke("[提权] 已用系统 UAC 以管理员身份重启");
                return true;
            }
            catch (Exception ex) { log?.Invoke("[提权] 异常：" + ex.Message); return false; }
        }

        // ── 诊断 ────────────────────────────────────────────────────────────

        /// <summary>把当前权限链路的实测状态拼成一段可读文本（排障用）。</summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  身份: {(IsTrustedInstaller() ? "TrustedInstaller" : (IsAdministrator() ? "管理员" : "普通用户"))}");
            sb.AppendLine($"  TI 令牌: {(TryImpersonateTrustedInstaller(out string d) ? "可用" : "不可用（" + d + "）")}");
            RevertImpersonation();
            var (ok, bad) = PrivilegeHelper.Enable(PrivilegeHelper.ServicingPrivileges);
            sb.AppendLine($"  已启用特权: {string.Join(", ", ok)}");
            if (bad.Count > 0) sb.AppendLine($"  未启用: {string.Join("; ", bad)}");
            return sb.ToString().TrimEnd();
        }
    }
}
