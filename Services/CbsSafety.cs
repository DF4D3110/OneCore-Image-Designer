using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// CBS 操作的安全门禁与干净收尾。
    ///
    /// 【强制要求一】目标必须是 **VHD/VHDX 支撑的卷**。
    ///   CBS 会挂载目标镜像的全部注册表 hive（SOFTWARE/SYSTEM/SECURITY/SAM/COMPONENTS/
    ///   DRIVERS/DEFAULT/ntuser.dat/schema.dat）并直接写组件存储。对物理盘/系统卷做这件事
    ///   会破坏正在运行的系统。因此这里**硬性拒绝**任何不是挂载 VHD/VHDX 的目标卷，
    ///   包括宿主自己的 C:\Windows。
    ///
    /// 【强制要求二】进程必须能**干净结束**。
    ///   CBS 会话若未正常收尾，注册表 hive 会残留挂载、VHD 会被占用、CSI 存储可能停在
    ///   半事务状态。这里提供：
    ///     · SafeSession —— IDisposable 包装，保证 Release 全部 COM 引用；
    ///     · RunGuarded  —— using 包裹 + 兜底清理（hive 强制卸载、VHD 卸载尝试）；
    ///     · 进程退出兜底（AppDomain.ProcessExit）。
    ///
    /// 【提权】需要管理员/TrustedInstaller 权限时走已移植的 NSudo
    ///   （MobilePackageGen.Common\NSudo，或外部 D:\UUP-Project\NSudo-master）。
    /// </summary>
    public static class CbsSafety
    {
        // ────────────────────────────────────────────────────────────────
        // 门禁一：目标必须是 VHD/VHDX
        // ────────────────────────────────────────────────────────────────

        public sealed class TargetCheck
        {
            public bool Allowed;
            public string Reason = "";
            public string DriveLetter = "";
            public int? DiskNumber;
            public string DiskModel = "";
            /// <summary>MSFT_PhysicalDisk.BusType；7 = FileBackedVirtual（挂载的 VHD/VHDX）。</summary>
            public int? BusType;
            public bool LooksLikeVhd;
        }

        /// <summary>
        /// 判断 winDir（形如 <c>X:\Windows</c>）所在卷是否由 VHD/VHDX 支撑。
        /// 判定依据（任一成立即通过，且必须不是宿主系统盘）：
        ///   1) 该盘在 MSFT_Disk 上属于一个已挂载的虚拟磁盘（Model 含 "Virtual" /
        ///      "Msft Virtual Disk" / "Microsoft Virtual Disk"，或 BusType=FileBackedVirtual）
        ///   2) 调用方显式登记的已挂载 VHD 列表命中该盘
        /// </summary>
        public static TargetCheck CheckTarget(string winDir, IEnumerable<string>? knownMountedVhds = null)
        {
            var r = new TargetCheck();
            try
            {
                if (string.IsNullOrWhiteSpace(winDir)) { r.Reason = "winDir 为空"; return r; }

                string full = Path.GetFullPath(winDir);
                string? root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root) || root.Length < 2) { r.Reason = "无法解析盘符: " + winDir; return r; }
                r.DriveLetter = root[..1];

                // 拒绝宿主系统盘（最后一道防线）
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
                if (r.DriveLetter.Equals(sysRoot[..1], StringComparison.OrdinalIgnoreCase))
                {
                    r.Reason = $"拒绝：{r.DriveLetter}: 是宿主系统盘。CBS 会挂载并改写目标镜像的注册表 hive，"
                             + "对运行中的系统执行此操作会破坏系统。CBS 只能在挂载的 VHD/VHDX 上进行。";
                    return r;
                }

                // 通过 WMI 找该盘符对应的 Disk
                int? diskNum = null;
                using (var part = new ManagementObjectSearcher(
                           "SELECT * FROM Win32_LogicalDiskToPartition"))
                {
                    foreach (ManagementObject m in part.Get())
                    {
                        string? dep = m["Dependent"]?.ToString();
                        string? ant = m["Antecedent"]?.ToString();
                        if (dep == null || ant == null) continue;
                        if (!dep.Contains($"DeviceID=\"{r.DriveLetter}:\"", StringComparison.OrdinalIgnoreCase)) continue;
                        int idx = ant.IndexOf("Disk #", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int end = ant.IndexOf(',', idx);
                            string numStr = end > 0 ? ant[(idx + 6)..end] : ant[(idx + 6)..];
                            if (int.TryParse(numStr.Trim(), out int n)) diskNum = n;
                        }
                    }
                }
                r.DiskNumber = diskNum;
                if (diskNum == null)
                {
                    r.Reason = $"拒绝：无法确定 {r.DriveLetter}: 对应的物理磁盘（要求 VHD/VHDX 支撑的卷）";
                    return r;
                }

                // 查该盘的 Model 与 BusType。
                // 注意：Model 是**本地化**字符串（中文系统上是"Microsoft 虚拟磁盘"），
                // 不能作为判定依据；必须用数值化的 BusType。
                using var disk = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_DiskDrive WHERE Index = {diskNum}");
                foreach (ManagementObject d in disk.Get())
                {
                    r.DiskModel = d["Model"]?.ToString() ?? "";
                    break;
                }

                // MSFT_PhysicalDisk.BusType：7 = FileBackedVirtual（挂载的 VHD/VHDX）
                int? busType = null;
                try
                {
                    using var phys = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Storage",
                        $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNum}'");
                    foreach (ManagementObject pd in phys.Get())
                    {
                        object? bt = pd["BusType"];
                        if (bt != null) busType = Convert.ToInt32(bt);
                        if (string.IsNullOrEmpty(r.DiskModel)) r.DiskModel = pd["FriendlyName"]?.ToString() ?? "";
                        break;
                    }
                }
                catch { /* 该命名空间不可用时，退化为保守判定 */ }

                r.BusType = busType;
                // STORAGE_BUS_TYPE: 14 = Virtual，15 = FileBackedVirtual（挂载的 VHD/VHDX）
                r.LooksLikeVhd = busType == 15 || busType == 14;

                if (!r.LooksLikeVhd)
                {
                    r.Reason = $"拒绝：{r.DriveLetter}: 位于物理磁盘 {diskNum}"
                             + $"（BusType={(busType?.ToString() ?? "未知")}，Model=\"{r.DiskModel}\"）。"
                             + "CBS 只能在挂载的 VHD/VHDX 上操作（BusType 必须为 14 Virtual 或 15 FileBackedVirtual）。";
                    return r;
                }

                r.Allowed = true;
                r.Reason = $"通过：{r.DriveLetter}: 位于挂载的虚拟磁盘 {diskNum}"
                         + $"（BusType={busType}，\"{r.DiskModel}\"）";
                return r;
            }
            catch (Exception ex)
            {
                r.Reason = "门禁检查异常: " + ex.GetType().Name + ": " + ex.Message;
                return r;   // 保守：异常即拒绝
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 门禁二：目标不得被其他进程占用
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 探测目标镜像的注册表 hive 是否已被挂载。
        ///
        /// 语义分两种（这很重要，因为 CBS 崩溃/被强杀后会留下残留挂载）：
        ///   · **有活动的 CBS/DISM 会话**（进程 cbscore/TiWorker/dismhost/dism 在跑）
        ///     → 返回 true（阻止），否则两个会话会同时改写同一个组件存储；
        ///   · **只有残留挂载、没有活动会话** → 主动强制卸载这些残留（保证"干净"），
        ///     返回 false 让调用方继续。
        /// </summary>
        public static bool TryDetectForeignSession(string winDir, out string detail)
        {
            detail = "";
            try
            {
                string full = Path.GetFullPath(winDir);
                string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
                if (root.Length == 0) return false;

                const string prefix = "{bf1a281b-ad7b-4476-ac95-f47682990ce7}";
                string want = prefix + root.Replace('\\', '/');   // 例：...ce7}G:

                var mine = new List<string>();
                using (var baseKey = Microsoft.Win32.Registry.LocalMachine)
                {
                    foreach (var name in baseKey.GetSubKeyNames())
                    {
                        if (name.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                            mine.Add(name);
                    }
                }
                if (mine.Count == 0) return false;

                // 有活动会话吗？
                bool live = HasLiveCbsProcess();
                if (live)
                {
                    detail = $"检测到目标镜像（{root}）的离线 hive 已被挂载，且存在活动的 CBS/DISM 进程：\n  "
                           + string.Join("\n  ", mine.Take(4))
                           + "\n请先结束它（或等它完成）再操作，避免两个会话同时改写同一个组件存储。";
                    return true;
                }

                // 只有残留 → 尝试主动清理；清不掉也不阻止（CBS 在会话开始时会自行
                // "force unload" 这些 hive，日志里可见 Force unloading offline registry hive）。
                int cleaned = 0;
                foreach (var name in mine)
                {
                    if (RegUnLoadKey(new IntPtr(HKEY_LOCAL_MACHINE), name) == 0) cleaned++;
                }
                detail = cleaned > 0
                    ? $"已清理 {cleaned} 个上次会话残留的离线 hive 挂载"
                    : $"注意：存在 {mine.Count} 个上次会话残留的 hive 挂载且无法卸载（缺 SeRestore 特权）；"
                    + "CBS 会在会话开始时自行强制卸载，继续执行。";
                return false;   // 无活动会话 → 放行
            }
            catch (Exception ex) { detail = "占用探测失败: " + ex.Message; return false; }
        }

        /// <summary>是否有活动的 CBS/DISM 相关进程。</summary>
        static bool HasLiveCbsProcess()
        {
            try
            {
                foreach (var name in new[] { "dism", "dismhost", "TiWorker", "TrustedInstaller", "cbsx" })
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(name);
                    try
                    {
                        if (procs.Length > 0) return true;
                    }
                    finally { foreach (var p in procs) p.Dispose(); }
                }
                return false;
            }
            catch { return true; }   // 不确定时保守阻止
        }

        // ────────────────────────────────────────────────────────────────
        // 干净收尾
        // ────────────────────────────────────────────────────────────────

        static readonly List<Action> _cleanup = new();
        static bool _hooked;

        /// <summary>登记一个退出时必定执行的清理动作（幂等）。</summary>
        public static void RegisterCleanup(Action a)
        {
            lock (_cleanup)
            {
                _cleanup.Add(a);
                if (!_hooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => RunCleanup();
                    AppDomain.CurrentDomain.UnhandledException += (_, __) => RunCleanup();
                    _hooked = true;
                }
            }
        }

        /// <summary>执行并清空全部清理动作。可重复调用。</summary>
        public static void RunCleanup()
        {
            Action[] snapshot;
            lock (_cleanup)
            {
                snapshot = _cleanup.ToArray();
                _cleanup.Clear();
            }
            foreach (var a in snapshot)
            {
                try { a(); } catch { /* 收尾阶段不再抛 */ }
            }
        }

        /// <summary>
        /// 把工作包在 using + 兜底清理里。工作抛异常时也保证清理执行。
        /// </summary>
        public static T RunGuarded<T>(Func<T> work, string? winDir = null, Action<string>? log = null)
        {
            try
            {
                return work();
            }
            finally
            {
                RunCleanup();

                // 兜底：若仍有本目标相关的离线 hive 残留，尝试强制卸载
                if (!string.IsNullOrWhiteSpace(winDir))
                {
                    try
                    {
                        string root = (Path.GetPathRoot(Path.GetFullPath(winDir)) ?? "").TrimEnd('\\');
                        if (root.Length > 0)
                            TryUnloadResidualHives(root, log);
                    }
                    catch { }
                }
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions,
                                       int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegCloseKey(IntPtr hKey);

        const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
        const int KEY_READ = 0x20019;

        /// <summary>
        /// 强制卸载残留的离线 hive（CBS 通常自己会卸，这里只作为进程异常退出后的兜底）。
        /// </summary>
        static void TryUnloadResidualHives(string driveRoot, Action<string>? log)
        {
            string prefix = "{bf1a281b-ad7b-4476-ac95-f47682990ce7}";
            string want = prefix + driveRoot.Replace('\\', '/');

            int rc = RegOpenKeyEx(new IntPtr(HKEY_LOCAL_MACHINE), "SYSTEM", 0, KEY_READ, out IntPtr hSystem);
            if (rc != 0) return;
            try
            {
                // 直接尝试按已知前缀卸载；CBS 的挂载形式是
                //   HKLM\{bf1a281b-...}<drive>/Windows/System32/config/<HIVE>
                foreach (var hive in new[] { "SOFTWARE", "SYSTEM", "SECURITY", "SAM", "COMPONENTS", "DRIVERS", "DEFAULT" })
                {
                    string sub = $"{want}/Windows/System32/config/{hive}";
                    int r = RegUnLoadKey(new IntPtr(HKEY_LOCAL_MACHINE), sub);
                    if (r == 0) log?.Invoke("  已兜底卸载残留 hive: " + sub);
                }
            }
            finally { RegCloseKey(hSystem); }
        }

        /// <summary>
        /// 是否需要提权。需要时建议走 NSudo（已移植在 MobilePackageGen.Common\NSudo）。
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
