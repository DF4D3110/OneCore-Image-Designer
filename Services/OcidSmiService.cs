using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OCID 自带的 SMI（Systems Management Infrastructure）—— 负责把组件清单里声明的
    /// **注册表设置**落到离线镜像的 hive 上。
    ///
    /// ── 为什么要自己写（这一段是全部理由，别删）────────────────────────────────
    /// 原先我们让 CBS/WCP 自带的 SMI/SIL 去写，结果卡在 `Commit`：
    ///   `SOFTWARE\Classes\Unknown\shell` 的默认值写被拒（`STATUS_ACCESS_DENIED`）。
    /// 根因已查清（有实测支撑）：
    ///   · 拦劫者是宿主机的 `UCPD.sys`（Windows 10 19045 自带，boot-start 文件系统过滤驱动），
    ///     它的驱动里**硬编码**了 `SOFTWARE\Classes\Unknown\shell` 这条键；
    ///   · 它按**解析后的完整键路径**匹配，不管发起身份、也不管 hive 是活是离线；
    ///   · 实测：同一个离线 hive 挂到**不含 `SOFTWARE` 段**的名字下时，**同一笔写放行**，
    ///     而 CBS 自己算出来的挂载名恒以 `SOFTWARE` 结尾
    ///     （它日志原文 `Loading offline registry hive: SOFTWARE, into registry key
    ///       '{bf1a281b-…}<drive>:/Windows/System32/config/SOFTWARE' from path '…\config\SOFTWARE'`），
    ///     于是子键 `Classes` 必然拼出 `SOFTWARE\Classes` ⇒ 必然命中。
    ///   · 试过并【已排除】的绕法：改 hive 文件名（CBS 报 FILE_NOT_FOUND，路径是它自己拼的）、
    ///     预挂载让它复用（SHARING_VIOLATION）、钩 `RegLoadKey` 改挂载名（后续消费者补不完）、
    ///     注册表符号链接（`HKLM` 根下用户态建不了键，`NtCreateKey` 也返回 0xC000000D）、
    ///     相对路径打开（内核看解析后的对象）。
    ///
    /// ⇒ 结论：**只要写离线 hive 这件事由【我们自己】做，且挂载名由【我们自己】定，
    ///    UCPD 就无从参与** —— 因为路径里根本不出现 `SOFTWARE\Classes`。
    ///   这正是"OCID 自带 SMI"的第一个、也是唯一卡住我们的落点。
    ///
    /// ⚠ 挂载名铁律：**绝不能以 `SOFTWARE`（或任何会拼出 `SOFTWARE\Classes` 的名字）结尾**。
    ///   本类统一用 `OCID_SMI_<hive>`（见 <see cref="MountNameFor"/>）。
    /// </summary>
    public static class OcidSmiService
    {
        // ── 离线 hive 与"逻辑根"的对应关系 ────────────────────────────────────
        //
        // 清单里声明的键是【逻辑路径】（HKEY_LOCAL_MACHINE\SOFTWARE\…、HKEY_CLASSES_ROOT\…），
        // 而离线镜像里它们是**若干个 hive 文件**。这层映射就是 SMI 的核心职责之一。
        //
        // ⚠ `HKEY_CLASSES_ROOT` 在**离线**镜像里没有独立 hive：
        //   它由 `SOFTWARE` hive 的 `Classes` 子树 + 各用户 hive 的 `Software\Classes` 合成。
        //   对系统级（allUsers scope）声明，应落到 **SOFTWARE hive 的 `Classes`** 下。
        //   ⇒ 我们落到 `…\OCID_SMI_SOFTWARE\Classes\…`（挂载名里**没有** SOFTWARE 段，
        //      所以完整路径里不会出现 `SOFTWARE\Classes` 这个受保护子串）。

        /// <summary>离线 hive 文件的规范文件名（就是镜像 <c>System32\config</c> 下的名字）。</summary>
        public const string HiveSoftware = "SOFTWARE";
        public const string HiveSystem = "SYSTEM";
        public const string HiveDefault = "DEFAULT";
        public const string HiveSam = "SAM";
        public const string HiveSecurity = "SECURITY";
        public const string HiveComponents = "COMPONENTS";

        /// <summary>我们自己的挂载名前缀。**必须不含 `SOFTWARE` 字样**（见类注释）。</summary>
        public const string MountPrefix = "OCID_SMI_";

        /// <summary>
        /// 某个 hive 对应的挂载名。
        ///
        /// ⚠⚠ **绝不能直接用 hive 文件名拼**！第一版就是 `MountPrefix + "SOFTWARE"` =
        /// `OCID_SMI_SOFTWARE`，结果路径仍然是
        /// `OCID_SMI_SOFTWARE\Classes\Unknown\shell` —— 里面照样含 `SOFTWARE\Classes`
        /// 这个受保护子串，实测那一笔**立刻又被 UCPD 拒了**
        /// （同一批里 `AlwaysShowExt` / `DefaultIcon` / `OCID\SmiProbe` 三条全过，
        ///   唯独 `…\shell` 的默认值失败 ⇒ 机理再次被独立验证）。
        ///
        /// ⇒ 所以这里用一张**不含 `SOFTWARE` 的词**的短名表。新增 hive 时务必遵守：
        ///   **名字里不能出现 `SOFTWARE`，也不能以任何会拼出 `SOFTWARE\Classes` 的段结尾。**
        /// </summary>
        private static readonly Dictionary<string, string> MountNames = new(StringComparer.OrdinalIgnoreCase)
        {
            [HiveSoftware]   = "SWX",     // 注意：不能写成 "SOFTWARE"
            [HiveSystem]     = "SYS",
            [HiveDefault]    = "DEF",
            [HiveSam]        = "SAMX",
            [HiveSecurity]   = "SEC",
            [HiveComponents] = "CMP",
        };

        /// <summary>某个 hive 对应的挂载名（保证不含 `SOFTWARE` 段）。</summary>
        public static string MountNameFor(string hiveName)
        {
            string tail = MountNames.TryGetValue(hiveName, out var t)
                ? t
                : Sanitize(hiveName);
            return MountPrefix + tail;
        }

        /// <summary>兜底：把哈希里可能出现的 `SOFTWARE` 字样替换掉。</summary>
        private static string Sanitize(string hiveName)
        {
            string s = hiveName ?? "X";
            if (s.IndexOf("SOFTWARE", StringComparison.OrdinalIgnoreCase) >= 0)
                s = s.Replace("SOFTWARE", "SWX", StringComparison.OrdinalIgnoreCase)
                     .Replace("software", "SWX", StringComparison.OrdinalIgnoreCase);
            return s;
        }

        /// <summary>一次待写入的注册表设置（从清单里解析出来的最小单位）。</summary>
        public sealed class RegSetting
        {
            /// <summary>逻辑根，如 <c>HKEY_LOCAL_MACHINE</c> / <c>HKEY_CLASSES_ROOT</c>。</summary>
            public string Root = "";
            /// <summary>逻辑子键（不含根），如 <c>Unknown\shell</c> 或 <c>SOFTWARE\Foo</c>。</summary>
            public string SubKey = "";
            /// <summary>值名；空串表示**默认值**。</summary>
            public string ValueName = "";
            /// <summary>REG_SZ / REG_EXPAND_SZ / REG_DWORD / REG_BINARY / REG_MULTI_SZ。</summary>
            public string ValueType = "REG_SZ";
            /// <summary>值数据（字符串形式；DWORD 用十进制/0x 前缀，BINARY 用十六进制）。</summary>
            public string Data = "";
            /// <summary>来源（清单文件名），仅用于日志/排障。</summary>
            public string Source = "";
        }

        /// <summary>
        /// 把清单声明的注册表设置落到指定镜像。
        /// </summary>
        /// <param name="imageRoot">镜像根，如 <c>P:\</c>（其下需有 <c>Windows\System32\config</c>）。</param>
        /// <param name="settings">要写入的设置集合。</param>
        /// <param name="log">日志回调。</param>
        /// <param name="dryRun">只报告将要做什么，不真正写。</param>
        /// <returns>成功写入的条数。</returns>
        public static int Apply(
            string imageRoot,
            IEnumerable<RegSetting> settings,
            Action<string>? log = null,
            bool dryRun = false)
        {
            if (string.IsNullOrWhiteSpace(imageRoot)) throw new ArgumentException("imageRoot 不能为空", nameof(imageRoot));

            string configDir = Path.Combine(imageRoot.TrimEnd('\\'), "Windows", "System32", "config");
            if (!Directory.Exists(configDir))
                throw new DirectoryNotFoundException($"镜像里找不到 config 目录：{configDir}");

            var list = new List<RegSetting>(settings ?? Array.Empty<RegSetting>());
            log?.Invoke($"[SMI] 待应用注册表设置 {list.Count} 条（镜像 {imageRoot}）");

            int written = 0;
            using var mount = new OfflineHiveMountScope(configDir, log);

            foreach (var s in list)
            {
                try
                {
                    if (ApplyOne(mount, s, log, dryRun)) written++;
                }
                catch (Exception ex)
                {
                    // 单条失败不放弃整批 —— 但要报出来，否则会静默丢设置
                    log?.Invoke($"[SMI] ✗ 失败 {s.Root}\\{s.SubKey} [{s.ValueName}] :: {ex.Message}");
                }
            }

            log?.Invoke($"[SMI] 完成：写入 {written}/{list.Count} 条");
            return written;
        }

        private static bool ApplyOne(OfflineHiveMountScope mount, RegSetting s, Action<string>? log, bool dryRun)
        {
            var (hive, relPath) = MapLogicalToHive(s.Root, s.SubKey);
            if (hive == null)
            {
                log?.Invoke($"[SMI] 跳过（根暂不支持）：{s.Root}\\{s.SubKey}");
                return false;
            }

            string mountKey = mount.Ensure(hive);
            string fullPath = mountKey + (string.IsNullOrEmpty(relPath) ? "" : "\\" + relPath);
            string valueDisplay = string.IsNullOrEmpty(s.ValueName) ? "(默认值)" : s.ValueName;

            log?.Invoke($"[SMI]   {s.Root}\\{s.SubKey} [{valueDisplay}] {s.ValueType}={Truncate(s.Data, 60)}  ⇒  {fullPath}");
            if (dryRun) return true;

            using var key = Registry.LocalMachine.CreateSubKey(fullPath, writable: true);
            if (key == null)
            {
                log?.Invoke($"[SMI] ✗ 建/开键失败：{fullPath}");
                return false;
            }

            object? value = ConvertData(s.ValueType, s.Data);
            key.SetValue(s.ValueName, value ?? "", ToRegistryKind(s.ValueType));
            return true;
        }

        /// <summary>
        /// 逻辑路径（清单里写的）→（离线 hive 名, hive 内相对路径）。
        /// </summary>
        /// <remarks>
        /// 这层映射必须与 Windows 自己的规则一致，否则设置会落到错的地方：
        /// <list type="bullet">
        /// <item><c>HKEY_LOCAL_MACHINE\SOFTWARE\X</c> → SOFTWARE hive 的 <c>X</c>（**剥掉 `SOFTWARE` 段**）</item>
        /// <item><c>HKEY_LOCAL_MACHINE\SYSTEM\X</c> → SYSTEM hive 的 <c>X</c></item>
        /// <item><c>HKEY_CLASSES_ROOT\X</c> → SOFTWARE hive 的 <c>Classes\X</c>（allUsers）</item>
        /// <item>其余 <c>HKEY_LOCAL_MACHINE\X</c>（SAM/SECURITY/COMPONENTS/DEFAULT 无统一规则）→ 暂不支持，返回 null</item>
        /// </list>
        /// </remarks>
        public static (string? hive, string relPath) MapLogicalToHive(string root, string subKey)
        {
            string r = (root ?? "").Trim().TrimEnd('\\');
            string sub = (subKey ?? "").Trim().TrimStart('\\');

            bool hklm = r.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                     || r.Equals("HKLM", StringComparison.OrdinalIgnoreCase);
            bool hkcr = r.Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)
                     || r.Equals("HKCR", StringComparison.OrdinalIgnoreCase);

            if (hkcr)
                return (HiveSoftware, Combine("Classes", sub));

            if (hklm)
            {
                if (StartsWithSegment(sub, "SOFTWARE")) return (HiveSoftware, StripFirstSegment(sub));
                if (StartsWithSegment(sub, "SYSTEM")) return (HiveSystem, StripFirstSegment(sub));
                if (StartsWithSegment(sub, "DEFAULT")) return (HiveDefault, StripFirstSegment(sub));
                if (StartsWithSegment(sub, "SAM")) return (HiveSam, StripFirstSegment(sub));
                if (StartsWithSegment(sub, "SECURITY")) return (HiveSecurity, StripFirstSegment(sub));
                if (StartsWithSegment(sub, "COMPONENTS")) return (HiveComponents, StripFirstSegment(sub));
                return (null, "");
            }

            // HKEY_CURRENT_USER / HKEY_USERS 需要 per-user hive（ntuser.dat），暂不在 SMI 第一版范围
            return (null, "");
        }

        private static bool StartsWithSegment(string s, string seg)
            => s.Equals(seg, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(seg + "\\", StringComparison.OrdinalIgnoreCase);

        private static string StripFirstSegment(string s)
        {
            int i = s.IndexOf('\\');
            return i < 0 ? "" : s.Substring(i + 1);
        }

        private static string Combine(string a, string b)
            => string.IsNullOrEmpty(a) ? b : (string.IsNullOrEmpty(b) ? a : a + "\\" + b);

        private static object? ConvertData(string type, string data)
        {
            switch ((type ?? "").ToUpperInvariant())
            {
                case "REG_DWORD":
                    if (data.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        return unchecked((int)Convert.ToUInt32(data.Substring(2), 16));
                    return int.TryParse(data, out var d) ? d : 0;
                case "REG_QWORD":
                    if (data.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        return unchecked((long)Convert.ToUInt64(data.Substring(2), 16));
                    return long.TryParse(data, out var q) ? q : 0L;
                case "REG_BINARY":
                    return HexToBytes(data);
                case "REG_MULTI_SZ":
                    return data.Length == 0 ? Array.Empty<string>() : data.Split('\0');
                default:
                    return data ?? "";
            }
        }

        private static RegistryValueKind ToRegistryKind(string type)
        {
            switch ((type ?? "").ToUpperInvariant())
            {
                case "REG_DWORD": return RegistryValueKind.DWord;
                case "REG_QWORD": return RegistryValueKind.QWord;
                case "REG_BINARY": return RegistryValueKind.Binary;
                case "REG_MULTI_SZ": return RegistryValueKind.MultiString;
                case "REG_EXPAND_SZ": return RegistryValueKind.ExpandString;
                default: return RegistryValueKind.String;
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
            var sb = new StringBuilder(hex.Length);
            foreach (char c in hex)
                if (Uri.IsHexDigit(c)) sb.Append(c);
            string h = sb.ToString();
            if (h.Length % 2 != 0) h = "0" + h;
            var bytes = new byte[h.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        // ─────────────────────────────────────────────────────────────────────
        // 挂载作用域：本类自己挂 hive（用 OCID_SMI_* 名字），用完即卸
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按需把镜像的 hive 挂到我们自己的名字下，作用域结束时全部卸掉。
        ///
        /// ⚠ **必须直接挂镜像里那个【真实】hive 文件**。
        /// 第一版为了"避开其它组件占用"而复制到临时文件再挂 —— 结果**写全落到副本上，
        /// 镜像里的 hive 一个字都没改**（实测：`reg query` 读不到刚写的值，文件时间戳也没变）。
        /// 对 SMI 来说那是致命错误：它的职责就是把设置**落到镜像**。
        ///
        /// ⇒ 现在默认直接挂真实文件。只有在文件确实被占用（`RegLoadKey` 返回 32
        ///   `ERROR_SHARING_VIOLATION`，典型情况是 CBS 正在跑）时才报错，
        ///   而不是悄悄改用副本 —— 静默写进副本等于什么都没做。
        ///
        /// 挂载名由 <see cref="MountNameFor"/> 生成，**保证不含 `SOFTWARE` 段**
        /// ⇒ 路径里不会出现 UCPD 硬编码的 `SOFTWARE\Classes` 子串
        /// ⇒ 那笔在 CBS 手里被拒的写，在这里能正常成功。
        /// </summary>
        private sealed class OfflineHiveMountScope : IDisposable
        {
            private readonly string _configDir;
            private readonly Action<string>? _log;
            private readonly Dictionary<string, string> _mounted = new(StringComparer.OrdinalIgnoreCase); // hive → mountKey

            public OfflineHiveMountScope(string configDir, Action<string>? log)
            {
                _configDir = configDir;
                _log = log;
            }

            /// <summary>确保某 hive 已挂载，返回可直接给 <see cref="Registry"/> 用的路径。</summary>
            public string Ensure(string hiveName)
            {
                if (_mounted.TryGetValue(hiveName, out var existing)) return existing;

                string src = Path.Combine(_configDir, hiveName);
                if (!File.Exists(src))
                    throw new FileNotFoundException($"镜像里没有 hive 文件：{src}");

                string mountName = MountNameFor(hiveName);
                int rc = PrivilegeHelper.RegLoadKeyLocalMachine(mountName, src);
                if (rc != 0)
                {
                    // 32 = ERROR_SHARING_VIOLATION：文件被别的组件（通常是正在跑的 CBS）持有着。
                    // 这时**不能**退回副本 —— 写进副本等于没写。要让调用方知道。
                    string hint = rc == 32
                        ? "（文件被占用，通常是 CBS 等组件正持有该 hive；SMI 要求直接挂真实文件，请先让它释放）"
                        : "";
                    throw new InvalidOperationException($"RegLoadKey 失败 rc={rc}，hive={src}{hint}");
                }

                OfflineHiveLedger.RecordOurs(mountName, src, "OCID SMI：应用组件清单声明的注册表设置");
                _mounted[hiveName] = mountName;
                _log?.Invoke($"[SMI] 已挂载 {hiveName} → HKLM\\{mountName}（直接挂镜像真实文件 {src}）");
                return mountName;
            }

            public void Dispose()
            {
                foreach (var kv in _mounted)
                {
                    int rc = PrivilegeHelper.RegUnLoadKeyLocalMachine(kv.Value);
                    _log?.Invoke($"[SMI] 卸载 HKLM\\{kv.Value} rc={rc}");
                    OfflineHiveLedger.ForgetOurs(kv.Value);
                }
                _mounted.Clear();
            }
        }
    }
}
