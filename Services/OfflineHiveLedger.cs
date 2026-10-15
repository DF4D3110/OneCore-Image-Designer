using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>离线 hive 是谁挂的。</summary>
    public enum HiveMountSource
    {
        /// <summary>CBS / WCP 挂的（前缀 <c>{bf1a281b-ad7b-4476-ac95-f47682990ce7}</c>，挂的是**镜像里的真 hive**）。</summary>
        Wcp,
        /// <summary>我们自己挂的（<c>OCID_*</c>，<see cref="OfflineRegistryService"/> 挂的是**临时目录里的副本**）。</summary>
        Ours,
        /// <summary>名字不认识，但看起来像离线 hive 挂载。</summary>
        Unknown,
    }

    /// <summary>
    /// 一条离线 hive 挂载记录。
    ///
    /// 【为什么需要这个台账】此前程序对"hive 挂在哪"是**用完就忘**的，于是反复吃三种亏：
    ///   1. **卸不干净** —— 收尾靠"猜前缀"，前缀不匹配就漏（实测一次泄漏 69 个；
    ///      另外我自己起过 <c>OCID_P_</c>/<c>OCID_WA_</c> 这类临时名字，前缀没覆盖，
    ///      直接让镜像锁死、后续 <c>ICbsSession::Initialize</c> 报
    ///      <c>0x80070020 ERROR_SHARING_VIOLATION</c>）。
    ///   2. **不知道自己挂的是副本还是原件** —— <see cref="OfflineRegistryService"/> 是把 hive
    ///      **复制到临时目录**再挂的（改的是副本）；而 CBS 挂的是**镜像里的真 hive**。
    ///      两者混为一谈就会误判"我改过了，为什么 CBS 没生效"。
    ///   3. **出问题时说不清"写到哪去了"** —— 与 CBS 的 Commit 失败排障直接相关：
    ///      失败日志里同时出现带 <c>{bf1a281b-…}</c> 前缀的路径和不带前缀的逻辑路径，
    ///      没有台账就无法断言那笔写到底落在**宿主机**还是**离线 hive**。
    /// </summary>
    public sealed class OfflineHiveMount
    {
        public HiveMountSource Source { get; init; } = HiveMountSource.Unknown;
        /// <summary>HKLM 下的子键名（挂载点）。</summary>
        public string MountKey { get; init; } = "";
        /// <summary>hive 文件的路径。CBS 的是镜像里的**真文件**；我们的是**临时副本**。</summary>
        public string HiveFile { get; init; } = "";
        /// <summary>盘符（能从挂载名解析出来时）。</summary>
        public string? Drive { get; init; }
        /// <summary>hive 名（SOFTWARE / SYSTEM / COMPONENTS / ntuser.dat / schema.dat …）。</summary>
        public string? HiveName { get; init; }
        /// <summary>挂它的用途（只有我们主动登记时才有）。</summary>
        public string? Purpose { get; init; }
        /// <summary>登记时的进程号。</summary>
        public int OwnerPid { get; init; }

        public string FullPathInRegistry => @"HKEY_LOCAL_MACHINE\" + MountKey;

        public override string ToString()
        {
            string src = Source switch
            {
                HiveMountSource.Wcp => "CBS/WCP(真 hive)",
                HiveMountSource.Ours => "OCID(临时副本)",
                _ => "未知",
            };
            var sb = new StringBuilder();
            sb.Append($"[{src}] {MountKey}");
            if (!string.IsNullOrEmpty(Drive)) sb.Append($"  drive={Drive}");
            if (!string.IsNullOrEmpty(HiveName)) sb.Append($"  hive={HiveName}");
            sb.Append($"  file={HiveFile}");
            if (!string.IsNullOrEmpty(Purpose)) sb.Append($"  purpose={Purpose}");
            if (OwnerPid != 0) sb.Append($"  pid={OwnerPid}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 离线 hive 挂载台账：让程序**自己知道**「在做什么、在哪儿做、东西放哪儿」。
    ///
    /// 用法（三条纪律）：
    ///   1. 自己挂 hive 前后，必须 <see cref="RecordOurs"/> / <see cref="ForgetOurs"/>；
    ///   2. 要动手前先 <see cref="Explain"/> 打印一次地图，避免"以为没有残留其实有"；
    ///   3. 收尾一律走 <see cref="ReleaseAll"/>（枚举驱动，不靠猜前缀）。
    ///
    /// 【主动登记 + 被动枚举】两者都要：
    ///   · 我们自己的挂载：名字里没有盘符/hive 信息 ⇒ 只能靠登记；
    ///   · CBS/WCP 的挂载：名字本身把盘符和 hive 路径编码进去了 ⇒ 可以解析出来，
    ///     这正是"知道 CBS 把镜像 hive 挂在哪"的依据。
    /// </summary>
    public static class OfflineHiveLedger
    {
        /// <summary>WCP/CBS 挂载离线 hive 用的固定前缀。</summary>
        public const string WcpMountPrefix = "{bf1a281b-ad7b-4476-ac95-f47682990ce7}";

        /// <summary>我们自己起的挂载名前缀（放宽到 OCID_，把临时排障用的名字也兜住）。</summary>
        public const string OurMountPrefix = "OCID_";

        /// <summary>
        /// 当前正在做的操作的名字。挂在台账上的每一条都会带上它 ——
        /// 出问题时日志能直接说清"这是在【CBS 装包】期间挂的"。
        /// </summary>
        public static string CurrentOperation { get; set; } = "(未标注)";

        sealed class OurRecord
        {
            public string HiveFile = "";
            public string Purpose = "";
            public int Pid;
            public DateTime At;
        }

        static readonly Dictionary<string, OurRecord> _ours = new(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new();

        const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

        // ────────────────────────────────────────────────────────────────
        // 我们自己挂载的登记
        // ────────────────────────────────────────────────────────────────

        /// <summary>登记一次"我们挂的" hive。必须在 <c>RegLoadKey</c> 成功之后立即调用。</summary>
        public static void RecordOurs(string mountKey, string hiveFile, string? purpose = null)
        {
            if (string.IsNullOrWhiteSpace(mountKey)) return;
            lock (_lock)
            {
                _ours[mountKey] = new OurRecord
                {
                    HiveFile = hiveFile ?? "",
                    Purpose = purpose ?? CurrentOperation,
                    Pid = Environment.ProcessId,
                    At = DateTime.Now,
                };
            }
        }

        /// <summary>注销一次"我们挂的" hive。卸载成功后调用（失败也应调用，让台账反映真实意图）。</summary>
        public static void ForgetOurs(string mountKey)
        {
            if (string.IsNullOrWhiteSpace(mountKey)) return;
            lock (_lock) _ours.Remove(mountKey);
        }

        // ────────────────────────────────────────────────────────────────
        // 枚举（主动 + 被动）
        // ────────────────────────────────────────────────────────────────

        /// <summary>枚举当前**实际挂着**的全部离线 hive（以 HKLM 顶层实际存在为准）。</summary>
        public static List<OfflineHiveMount> Enumerate()
        {
            var found = new List<OfflineHiveMount>();
            try
            {
                using var hklm = Microsoft.Win32.Registry.LocalMachine;
                using var root = hklm.OpenSubKey("");
                if (root == null) return found;

                foreach (var name in root.GetSubKeyNames())
                {
                    var m = Parse(name);
                    if (m != null) found.Add(m);
                }
            }
            catch { }
            return found;
        }

        /// <summary>只枚举某个盘（例如 <c>P:</c>）上的离线 hive。</summary>
        public static List<OfflineHiveMount> Enumerate(string? drive)
        {
            var all = Enumerate();
            if (string.IsNullOrWhiteSpace(drive)) return all;
            string d = drive!.TrimEnd('\\', '/');
            return all.Where(m =>
                string.Equals(m.Drive, d, StringComparison.OrdinalIgnoreCase)
                // 我们自己的挂载名里没有盘符 ⇒ 用 hive 文件路径来判断
                || (!string.IsNullOrEmpty(m.HiveFile)
                    && m.HiveFile.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// 把一个挂载点名字解析成结构化记录；不是离线 hive 挂载则返回 null。
        ///
        /// WCP 的命名形如：
        ///   <c>{bf1a281b-…}P:/Windows/System32/config/SOFTWARE</c>
        ///   <c>{bf1a281b-…}M:/Users/default/ntuser.dat</c>
        ///   <c>{bf1a281b-…}M:/Windows/system32/smi/store/Machine/schema.dat</c>
        /// </summary>
        public static OfflineHiveMount? Parse(string mountKey)
        {
            if (string.IsNullOrWhiteSpace(mountKey)) return null;

            // ── WCP/CBS 的 ──
            if (mountKey.StartsWith(WcpMountPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = mountKey.Substring(WcpMountPrefix.Length);   // "P:/Windows/System32/config/SOFTWARE"
                string? drive = null;
                int colon = rest.IndexOf(':');
                if (colon == 1) drive = rest.Substring(0, 2);              // "P:"

                string rel = rest;
                if (drive != null && rest.Length > 2)
                    rel = rest.Substring(2);                               // "/Windows/..."
                string file = drive != null
                    ? drive + "\\" + rel.TrimStart('/').Replace('/', '\\')
                    : rest.TrimStart('/').Replace('/', '\\');

                string hiveName = Path.GetFileName(file.Replace('\\', '/'));

                return new OfflineHiveMount
                {
                    Source = HiveMountSource.Wcp,
                    MountKey = mountKey,
                    HiveFile = file,
                    Drive = drive,
                    HiveName = hiveName,
                    Purpose = "CBS/WCP 服务镜像时挂载（挂的是镜像里的真 hive）",
                    OwnerPid = 0,
                };
            }

            // ── 我们自己的 ──
            if (mountKey.StartsWith(OurMountPrefix, StringComparison.OrdinalIgnoreCase))
            {
                OurRecord? rec = null;
                lock (_lock) _ours.TryGetValue(mountKey, out rec);

                string file = rec?.HiveFile ?? "";
                string? drive = null;
                try
                {
                    if (!string.IsNullOrEmpty(file))
                    {
                        string? r = Path.GetPathRoot(Path.GetFullPath(file));
                        if (!string.IsNullOrEmpty(r)) drive = r!.TrimEnd('\\');
                    }
                }
                catch { }

                return new OfflineHiveMount
                {
                    Source = HiveMountSource.Ours,
                    MountKey = mountKey,
                    HiveFile = file,
                    Drive = drive,
                    HiveName = string.IsNullOrEmpty(file) ? null : Path.GetFileName(file),
                    Purpose = rec?.Purpose ?? "(本进程未登记：可能是其它进程/上次会话留下)",
                    OwnerPid = rec?.Pid ?? 0,
                };
            }

            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // 报告 / 诊断
        // ────────────────────────────────────────────────────────────────

        /// <summary>人类可读的挂载地图。建议在每次"动手改镜像"之前打一次。</summary>
        public static string Describe(string? drive = null)
        {
            var list = Enumerate(drive);
            var sb = new StringBuilder();
            sb.AppendLine($"=== 离线 hive 挂载台账（当前操作：{CurrentOperation}" +
                          (string.IsNullOrWhiteSpace(drive) ? "" : $"；范围 {drive}") + $"，共 {list.Count} 条）===");
            if (list.Count == 0) { sb.AppendLine("  （没有离线 hive 挂着）"); return sb.ToString().TrimEnd(); }

            foreach (var g in list.GroupBy(m => m.Source))
            {
                sb.AppendLine($"  ── {g.Key}（{g.Count()} 条）──");
                foreach (var m in g)
                    sb.AppendLine("    " + m);
            }

            // 额外说明：CBS 挂的 hive 文件是否还被别的进程持有 —— 这决定了下一轮能不能直接跑
            var wcpFiles = list.Where(m => m.Source == HiveMountSource.Wcp && !string.IsNullOrEmpty(m.HiveFile))
                               .Select(m => m.HiveFile).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var f in wcpFiles)
            {
                bool held = IsFileHeld(f);
                if (held) sb.AppendLine($"  ⚠ {f} 仍被进程持有 ⇒ 直接再跑会报 0x80070020(SHARING_VIOLATION)");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>这个 hive 文件现在能不能被我们独占打开（判断是否还被持有）。</summary>
        public static bool IsFileHeld(string hiveFile)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hiveFile) || !File.Exists(hiveFile)) return false;
                using var fs = File.Open(hiveFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// 枚举某个 hive 文件是不是**已经被挂载**了（按文件路径比对）。
        /// 用于"挂之前先问一句"，避免同一个文件挂两次 —— 那是 SHARING_VIOLATION 的常见来源。
        /// </summary>
        public static OfflineHiveMount? FindByHiveFile(string hiveFile)
        {
            if (string.IsNullOrWhiteSpace(hiveFile)) return null;
            string want = "";
            try { want = Path.GetFullPath(hiveFile); } catch { return null; }

            foreach (var m in Enumerate())
            {
                if (string.IsNullOrEmpty(m.HiveFile)) continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(m.HiveFile), want, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
                catch { }
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // 释放
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 卸载**全部**离线 hive（枚举驱动，不靠猜前缀），返回仍挂着的数量。
        /// 这是收尾的最后一道网。
        /// </summary>
        public static int ReleaseAll(Action<string>? log = null)
        {
            try
            {
                var before = Enumerate();
                if (before.Count == 0) { log?.Invoke("  无残留离线 hive"); return 0; }
                try { PrivilegeHelper.EnableServicingPrivileges(log); } catch { }

                // 两轮：第一轮可能因"别处还持有"而失败，第二轮再试
                for (int round = 0; round < 2; round++)
                {
                    foreach (var m in Enumerate())
                    {
                        int r = -1;
                        try { r = RegUnLoadKey(new IntPtr(HKEY_LOCAL_MACHINE), m.MountKey); } catch { }
                        if (r == 0)
                        {
                            log?.Invoke($"  已卸载 [{m.Source}] {m.MountKey}");
                            ForgetOurs(m.MountKey);
                        }
                    }
                }

                var after = Enumerate();
                log?.Invoke($"  清空离线 hive: {before.Count} → {after.Count}");
                if (after.Count > 0)
                {
                    CbsSafety.NoteCleanupWarning(
                        $"仍有 {after.Count} 个离线 hive 未卸载（例 {after[0].MountKey}）");
                    foreach (var m in after)
                        log?.Invoke($"    ✗ 卸不掉: [{m.Source}] {m.MountKey}  file={m.HiveFile}");
                }
                return after.Count;
            }
            catch (Exception ex) { log?.Invoke("  清空离线 hive 异常: " + ex.Message); return -1; }
        }
    }
}
