using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

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
            /// <summary>
            /// `owner`（清单里 <c>owner="true"</c>）—— 该组件是否**拥有**这个值。
            /// ⚠ 这是真实语义，不能省：它关系到组件卸载时"该不该删"以及冲突判定。
            /// </summary>
            public bool Owner;
            /// <summary>`operationHint`，实测几乎全是 <c>replace</c>（以本声明覆盖既有值）。</summary>
            public string OperationHint = "replace";
            /// <summary>
            /// 作用域：<c>allUsers</c>（落 SOFTWARE/SYSTEM 等机器 hive）或
            /// <c>perUser</c>（落默认用户 hive —— 见 <see cref="MapLogicalToHive"/>）。
            /// </summary>
            public string Scope = "";
            /// <summary>来源（清单里的设置项名 / 清单文件名），仅用于日志与排障。</summary>
            public string Source = "";
            /// <summary>来源组件名（用于日志）。</summary>
            public string Component = "";
            /// <summary>来源机制：`registryKeys`（清单里第一套声明）或 `wcm`（configurationSchema）。两套是不同集合。</summary>
            public string Mechanism = "";

            // ── 由 C++ 侧（ocid_manifest.exe）预先编码好的最终字节 ─────────────────
            //
            // ⚠ 为什么不在这里解释 valueType：把"清单文本 → hive 字节"当成**服务栈语义**
            //   只有一份实现（在 Source/Native/OcidServicing/src/RegistryValue.cpp）。
            //   这些规则不是格式偏好，是从反编译 + 70224 份真实清单 + 本机活注册表
            //   三重证据里钉出来的，例如：
            //     · REG_QWORD 的 16 个 hex 字符是【8 字节内存镜像】，不是数值。
            //       按数值解析 `0001000000000000` 会得到 0x0001000000000000，
            //       而活注册表里它是 0x100 —— 差 2^48 倍，而且**不报错**。
            //     · REG_MULTI_SZ 的 `"a","b"` 里那对引号是【列表项定界符】，不是数据。
            //   之前 C# 侧的 ConvertData() 正是这两条都做错了（加上 REG_NONE 与
            //   REG_LINK/REG_RESOURCE_* 落成 REG_SZ 一共四个 bug），只是那两份清单里
            //   恰好没有这些类型，所以一直没暴露。
            /// <summary>原生工具是否成功把值编码成字节。false ⇒ **不要写**，应报错。</summary>
            public bool Encoded;
            /// <summary>真实 Win32 `REG_*` 类型码（写 <c>RegSetValueExW</c> 用的那个）。</summary>
            public uint TypeCode;
            /// <summary>要写进 hive 的**精确字节**（大写 hex，无前缀）。</summary>
            public string DataHex = "";
            /// <summary>值里含未展开的 <c>$(…)</c> 宏 —— 写下去一定是错的，需上层决策。</summary>
            public bool HasMacro;
            /// <summary>编码失败原因（<see cref="Encoded"/> 为 false 时有效）。</summary>
            public string EncodeError = "";

            // ── 键级属性（ACL 属于**键**，不属于值）───────────────────────────
            //
            // ⚠ 为什么 ACL 搭在"值设置"上：一条设置代表"落到某个键"，键级属性只能跟着走。
            //   而且必须允许**只有键、没有值**的那种（<see cref="KeyOnly"/>）——
            //   实测带安全描述符的键里有 18.3% 一个 `<registryValue>` 都没有。

            /// <summary>
            /// 这条设置只建键、不写值（原生侧 <c>keyOnly</c>）。
            /// ⚠ **绝不能**当成"把默认值写成空串" —— 那会在镜像里凭空多出一条
            /// <c>(Default)=""</c>，而且看起来像成功。
            /// </summary>
            public bool KeyOnly;

            /// <summary>
            /// 该键引用的安全描述符**原文**（原生侧已按清单里的
            /// <c>&lt;securityDescriptorDefinition&gt;</c> 表把引用名解析成 SDDL）。
            /// 空串 = 这个键没有引用 SD，**不要动它的 ACL**。
            /// </summary>
            public string KeySddl = "";
            /// <summary>SD 的引用名（如 <c>WRP_REGKEY_DEFAULT_SDDL</c>），排障用。</summary>
            public string KeySdName = "";
            /// <summary><c>&lt;securityDescriptor&gt;</c> 上的 <c>buildFilter</c>（实测 7177 处）。</summary>
            public string KeySdBuildFilter = "";
            /// <summary>
            /// <c>&lt;registryKey perUserVirtualization="Disable|DisableRecurse"&gt;</c> ——
            /// 控制这个机器级键要不要被"按用户虚拟化"（实测 577 处）。
            /// </summary>
            public string PerUserVirtualization = "";
        }

        /// <summary>
        /// 从组件清单批量应用注册表设置。
        ///
        /// 实现方式：调用原生工具 <c>ocid_manifest.exe export-ops</c>（C++ 服务栈的控制台入口）
        /// 把清单里的 <c>configurationSchema</c> 推导成注册表操作（JSONL），再逐条落到离线 hive。
        ///
        /// 为什么走这个方式（而不是在 C# 里再实现一遍）：
        /// 解析规则是从反编译与真实数据里解出来的（例如组件名缩写、`wcm:handler`/`legacy*` 语义），
        /// **只应有一份实现**，否则两边一定会漂移。C++ 侧拥有服务栈逻辑，C# 侧只负责编排与落盘。
        ///
        /// ⚠ 需要原生工具在同目录或 <paramref name="toolPath"/> 指定；找不到时抛异常而不是静默跳过
        /// （静默跳过 = 设置全丢，最难发现的那类故障）。
        /// </summary>
        public static int ApplyManifests(
            string imageRoot,
            IEnumerable<string> manifestPaths,
            Action<string>? log = null,
            bool dryRun = false,
            string? toolPath = null)
        {
            string tool = toolPath ?? FindManifestTool();
            if (tool == null)
                throw new FileNotFoundException(
                    "找不到 ocid_manifest.exe（OCID 服务栈控制台入口）。" +
                    "请先用 Source/Native/OcidServicing 构建，或用 toolPath 显式指定。");

            var paths = manifestPaths.ToList();
            var listFile = Path.Combine(Path.GetTempPath(), "ocid_manifest_list_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            File.WriteAllLines(listFile, paths);
            log?.Invoke($"[SMI] 从清单应用：{paths.Count} 份，工具 {Path.GetFileName(tool)}");

            var settings = new List<RegSetting>();
            int failed = 0;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tool,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("export-ops");
                psi.ArgumentList.Add("--list");
                psi.ArgumentList.Add(listFile);

                using var p = System.Diagnostics.Process.Start(psi)!;

                // stderr **必须并发读**：先读到 stdout 的 EOF 再去读 stderr 会死锁 ——
                // 子进程写满 stderr 管道缓冲区（约 4 KB）后就阻塞，它一阻塞
                // stdout 的 EOF 就永远不来。40 份清单就能踩到（CSI 那条线实测挂死过）。
                var errTask = p.StandardError.ReadToEndAsync();

                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    var s = ParseOpJson(line);
                    if (s != null) settings.Add(s);
                }
                string stderr = errTask.GetAwaiter().GetResult();
                p.WaitForExit();
                log?.Invoke($"[SMI] 原生工具诊断：{stderr.Trim()}");
                if (p.ExitCode != 0)
                {
                    failed = 1;
                    log?.Invoke($"[SMI] ⚠ 原生工具退出码 {p.ExitCode}（部分清单解析失败）");
                }
            }
            finally
            {
                try { File.Delete(listFile); } catch { }
            }

            log?.Invoke($"[SMI] 推导出注册表操作 {settings.Count} 条");
            int written = Apply(imageRoot, settings, log, dryRun);
            return failed == 0 ? written : -written;
        }

        /// <summary>在若干常见位置找 <c>ocid_manifest.exe</c>。</summary>
        public static string? FindManifestTool()
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new List<string>
            {
                Path.Combine(baseDir, "ocid_manifest.exe"),
                Path.Combine(baseDir, "CBS", "ocid_manifest.exe"),
            };
            // ⚠ 这里原来写的是固定的 `"..","..","..",".."`，等于假设调用方在
            //   `Tests\bin\Release\net8.0-windows\`。主程序在 `bin\Release\net8.0-windows\`
            //   （少一层），于是**开发期永远找不到** ocid_manifest.exe，而失败只是"工具不可用"。
            //   改成逐层往上找，两种布局都能命中。
            var dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "Source", "Native", "Output", "x86", "ocid_manifest.exe"));
            }
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }

        /// <summary>解析一行 export-ops 的 JSON。字段固定，用手写解析避免引入 JSON 依赖。</summary>
        // internal 而非 private：Tests 里要拿**真实 export-ops 输出**钉住这条线格式契约
        // （字段名/语义一改，测试就得红，而不是等到写坏镜像才发现）。
        internal static RegSetting? ParseOpJson(string line)
        {
            string? Get(string key)
            {
                string pat = "\"" + key + "\":\"";
                int i = line.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return null;
                i += pat.Length;
                var sb = new StringBuilder();
                for (; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        char n = line[++i];
                        sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => n });
                        continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                }
                return sb.ToString();
            }

            var root = Get("root");
            if (string.IsNullOrEmpty(root)) return null;

            return new RegSetting
            {
                Root = root!,
                SubKey = Get("subKey") ?? "",
                ValueName = Get("valueName") ?? "",
                ValueType = Get("valueType") ?? "REG_SZ",
                Data = Get("data") ?? "",
                Scope = Get("scope") ?? "",
                Source = Get("sourceItem") ?? "",
                Component = Get("component") ?? "",
                Mechanism = Get("mechanism") ?? "",
                // owner/operationHint 由原生工具从 `<registryKeys>` 那套声明导出
                // （实测 `owner="true"/"false"`、`operationHint="replace"`）。
                Owner = line.Contains("\"owner\":true"),
                OperationHint = Get("operationHint") is { Length: > 0 } oh ? oh : "replace",
                // 编码结果由 C++ 侧给出（见 RegSetting 里的说明）。
                Encoded = line.Contains("\"encoded\":true"),
                TypeCode = ParseUIntField(line, "typeCode"),
                DataHex = Get("dataHex") ?? "",
                HasMacro = line.Contains("\"hasMacro\":true"),
                EncodeError = Get("encodeError") ?? "",
                KeyOnly = line.Contains("\"keyOnly\":true"),
                KeySddl = Get("keySddl") ?? "",
                KeySdName = Get("keySdName") ?? "",
                KeySdBuildFilter = Get("keySdBuildFilter") ?? "",
                PerUserVirtualization = Get("perUserVirtualization") ?? "",
            };
        }

        /// <summary>取 JSON 里的无引号数字字段（`"typeCode":11`）。</summary>
        private static uint ParseUIntField(string line, string key)
        {
            string pat = "\"" + key + "\":";
            int i = line.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += pat.Length;
            int start = i;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            return start == i ? 0 : uint.Parse(line.AsSpan(start, i - start));
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

            // `$(runtime.*)` 宏的基准目录。**只能这样算**：离线 hive 里根本没有
            // SystemRoot / ProgramFiles / ProgramData（SYSTEM 的 Environment 只有
            // windir=%SystemRoot% 这种循环项，且 CurrentControlSet 离线不存在）。
            string windowsDir = Path.Combine(imageRoot.TrimEnd('\\'), "Windows");

            var list = new List<RegSetting>(settings ?? Array.Empty<RegSetting>());
            log?.Invoke($"[SMI] 待应用注册表设置 {list.Count} 条（镜像 {imageRoot}）");

            int written = 0;
            var acl = new AclTracker();

            // ACL 落盘要动特权：写 owner/group 需要 `SeRestorePrivilege`、写 SACL 需要
            // `SeSecurityPrivilege`。管理员令牌本来就有这两个，但**默认处于禁用状态**，
            // 必须显式启用；只写 DACL 时不需要（键的 owner 天然能改自己的 DACL）。
            // 只在真的有条目带 SDDL 时才去动令牌，避免无谓的特权开启。
            if (!dryRun && list.Exists(s => s.KeySddl.Length > 0))
                PrivilegeHelper.EnableServicingPrivileges(log);

            using var mount = new OfflineHiveMountScope(configDir, log);

            foreach (var s in list)
            {
                try
                {
                    if (ApplyOne(mount, s, windowsDir, log, dryRun, acl)) written++;
                }
                catch (Exception ex)
                {
                    // 单条失败不放弃整批 —— 但要报出来，否则会静默丢设置
                    log?.Invoke($"[SMI] ✗ 失败 {s.Root}\\{s.SubKey} [{s.ValueName}] :: {ex.Message}");
                }
            }

            // ACL 的账必须**单独**报：它是键级属性，跟"写了几条值"根本不是一回事。
            // 之前这里只报"有多少条带 SDDL 的键没写"，现在真的写了，就得报清四个数。
            if (acl.Written + acl.SkippedInherit + acl.Mismatched + acl.Failed > 0)
            {
                var parts = new List<string> { $"写入 {acl.Written} 个键" };
                if (acl.SkippedInherit > 0) parts.Add($"跳过 {acl.SkippedInherit} 个（(inherit) 保持继承）");
                if (acl.Mismatched > 0) parts.Add($"**回读不一致 {acl.Mismatched} 个**");
                if (acl.Failed > 0) parts.Add($"**失败 {acl.Failed} 个**");
                log?.Invoke($"[SMI] ACL：{string.Join("，", parts)}");
            }

            log?.Invoke($"[SMI] 完成：写入 {written}/{list.Count} 条");
            return written;
        }

        /// <summary>
        /// ACL 落盘的统计与去重状态。
        /// </summary>
        /// <remarks>
        /// <see cref="Applied"/> 按**键全路径**去重：同一个键在清单里往往被多条设置命中
        /// （多个值、默认值、仅键各一条），全路径相同、SDDL 也相同时重复设 ACL 没有意义，
        /// 还会让日志里的 ACL 行数远超实际键数，把"到底给多少个键设了安全属性"淹掉。
        /// 记录的是"这个键上最后写入的 SDDL"，用于发现同一键被声明了两套不同 SDDL 的冲突。
        /// </remarks>
        private sealed class AclTracker
        {
            public readonly Dictionary<string, string> Applied = new(StringComparer.OrdinalIgnoreCase);
            public int Written;
            public int SkippedInherit;
            public int Mismatched;
            public int Failed;
        }

        /// <summary>
        /// 把一个键的 SDDL 写下去（<c>RegSetKeySecurity</c>），写完立刻回读比对。
        /// </summary>
        /// <remarks>
        /// 失败**不**中断这条设置：值才是主体载荷，ACL 写不上却把值也丢掉只会更糟。
        /// 但失败一定计数 + 逐条打日志 + 末尾汇总，绝不静默降级。
        /// </remarks>
        private static void ApplyKeySddl(string fullPath, RegSetting s, Action<string>? log, AclTracker acl)
        {
            if (acl.Applied.TryGetValue(fullPath, out string? done))
            {
                if (string.Equals(done, s.KeySddl, StringComparison.Ordinal))
                    return;   // 同一个键、同一套 SDDL：已经设过，不重复设

                // 同一个键被两个组件声明了**不同**的安全描述符。真实服务栈里后写者生效，
                // 这里照做，但必须报出来 —— 否则"最终 ACL 是哪一套"事后无法追溯。
                log?.Invoke($"[SMI]   ⚠ ACL 冲突（同一键两套 SDDL），以本条为准：{fullPath}");
            }

            if (!OcidServicingNative.Available)
            {
                acl.Failed++;
                log?.Invoke($"[SMI]   ✗ ACL 未写：原生库 {OcidServicingNative.DllName} 不可用：{fullPath}");
                return;
            }

            // 真正干活的在 ApplyAndVerifyKeySddl（它自己开带 WRITE_DAC 的句柄，写完回读比对）。
            // 这里只负责去重、计数、日志 —— 自检调的是**同一个**方法，保证"测的就是产品路径"。
            var r = ApplyAndVerifyKeySddl(Registry.LocalMachine, fullPath, s.KeySddl);

            switch (r.Outcome)
            {
                case OcidServicingNative.SddlApplyOutcome.Applied:
                    acl.Applied[fullPath] = s.KeySddl;
                    acl.Written++;
                    log?.Invoke($"[SMI]   + ACL ⇐ {Truncate(s.KeySddl, 90)}：{fullPath}");

                    if (r.Verified == true)
                    {
                        log?.Invoke($"[SMI]   ✓ ACL 回读一致（DACL {AceCountOf(r.Got)} 条 ACE）：{fullPath}");
                    }
                    else if (r.Verified == null)
                    {
                        log?.Invoke($"[SMI]   · ACL 已写入，未做 DACL 回读比对：{fullPath} — {r.Error}");
                    }
                    else
                    {
                        // 不一致必须落成一行：否则"写进去了"只是自称。
                        acl.Mismatched++;
                        log?.Invoke($"[SMI]   ✗ ACL 回读**不一致**：{fullPath}\n" +
                                    $"[SMI]       写入 {Truncate(r.Want, 90)}\n" +
                                    $"[SMI]       回读 {Truncate(r.Got, 90)}\n" +
                                    (r.Error.Length > 0 ? $"[SMI]       原因：{r.Error}" : ""));
                    }
                    break;

                case OcidServicingNative.SddlApplyOutcome.SkippedInherit:
                    acl.SkippedInherit++;
                    log?.Invoke($"[SMI]   ⊘ ACL 跳过（(inherit)：保持继承，不显式设置）：{fullPath}");
                    break;

                default:
                    acl.Failed++;
                    log?.Invoke($"[SMI]   ✗ ACL 写失败：{fullPath} — {r.Error}");
                    break;
            }
        }

        /// <summary>回读结果里的 DACL ACE 条数（日志用；解不开给 -1）。</summary>
        private static int AceCountOf(string sddl)
            => OcidServicingNative.TryDescribeSddl(sddl, out var f, out _) ? f.DaclAceCount : -1;

        /// <summary>给一个键设 SDDL 的结果。</summary>
        internal readonly struct KeyAclResult
        {
            public OcidServicingNative.SddlApplyOutcome Outcome { get; init; }
            /// <summary>回读比对三态：true=一致；false=**不一致**；null=没比（无 DACL 段或回读失败）。</summary>
            public bool? Verified { get; init; }
            /// <summary>要求写入的 SDDL。</summary>
            public string Want { get; init; }
            /// <summary>回读到的 SDDL。</summary>
            public string Got { get; init; }
            /// <summary>失败原因，或"为什么没比对"。</summary>
            public string Error { get; init; }
        }

        /// <summary>
        /// 给一个键设安全描述符，并**立刻回读比对**。SMI 与自检都走这里。
        /// </summary>
        /// <remarks>
        /// 句柄由本方法**自己开**，且显式带上 `WRITE_DAC` + `READ_CONTROL`：
        /// `WRITE_DAC(0x40000)` **不在 `KEY_WRITE(0x20006)` 里**（含它的是 KEY_ALL_ACCESS），
        /// 所以拿 `CreateSubKey(..., writable: true)` 的句柄调 `RegSetKeySecurity`
        /// 只会得到 `ERROR_ACCESS_DENIED(5)` —— 现象像"缺特权"，实际缺的是句柄上的访问权。
        /// 实测踩过一次：C++ 自检用 KEY_ALL_ACCESS 全过，C# 走 CreateSubKey 全挂。
        ///
        /// 回读只做**结构级**比对：`RegGetKeySecurity` 回读时 SID 的写法可能变
        /// （别名 `BA` 会回读成 `S-1-5-32-544`），逐字符比必然假失败。DACL 是否存在、
        /// ACE 条数、是否带 `P` 保护标志，才是"确实写进去了"的凭据。
        /// 非保护 DACL（无 `P`）时系统会把父键的可继承 ACE 并进来，回读可能**多于**写入，
        /// 因此那种情况只要求"不少于"。
        /// </remarks>
        internal static KeyAclResult ApplyAndVerifyKeySddl(RegistryKey root, string relativePath, string sddl)
        {
            using var key = root.OpenSubKey(
                relativePath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);

            if (key == null)
            {
                return new KeyAclResult
                {
                    Outcome = OcidServicingNative.SddlApplyOutcome.Failed,
                    Want = sddl ?? "",
                    Got = "",
                    Error = "无法以 WRITE_DAC|READ_CONTROL 打开键（拿不到 WRITE_DAC 就写不了 ACL）",
                };
            }

            IntPtr hKey = key.Handle.DangerousGetHandle();
            var outcome = OcidServicingNative.ApplyKeySddl(hKey, sddl, out string err);

            bool? verified = null;
            string got = "";
            string note = err ?? "";

            if (outcome == OcidServicingNative.SddlApplyOutcome.Applied)
            {
                if (!OcidServicingNative.TryDescribeSddl(sddl, out var want, out string werr))
                {
                    note = "已写入，但原文解读失败，未比对：" + werr;
                }
                else if (!want.HasDacl)
                {
                    note = "已写入；该 SDDL 无 DACL 段，没有可比的 DACL 事实";
                }
                else if (!OcidServicingNative.TryReadKeySddl(hKey, OcidServicingNative.SecInfo.Dacl,
                                                             out got, out string rerr))
                {
                    note = "已写入，但回读失败（无法确认）：" + rerr;
                }
                else if (!OcidServicingNative.TryDescribeSddl(got, out var rb, out string gerr))
                {
                    note = "已写入，但回读的 SDDL 解读失败：" + gerr;
                }
                else
                {
                    // 比 **SID 集合**（不是逐字符、也不是只比条数）：
                    // 回读的写法会变（`BA` → `S-1-5-32-544`），条数也会变（系统会把"仅继承"
                    // ACE 拆成两条），只有集合是稳定事实。详见 SidsCompatible 的注释。
                    var why = new List<string>();
                    if (want.DaclProtected != rb.DaclProtected)
                        why.Add($"保护标志：声明 {(want.DaclProtected ? "带 P" : "无 P")}、" +
                                $"回读 {(rb.DaclProtected ? "带 P" : "无 P")}");
                    if (rb.DaclAceCount < want.DaclAceCount)
                        why.Add($"ACE 条数：声明 {want.DaclAceCount}、回读 {rb.DaclAceCount}（回读更少）");
                    if (!OcidServicingNative.SidsCompatible(want, rb, out string sidNote) && sidNote.Length > 0)
                        why.Add(sidNote);

                    verified = why.Count == 0;
                    note = verified == true ? "" : string.Join("；", why);
                }
            }

            return new KeyAclResult
            {
                Outcome = outcome,
                Verified = verified,
                Want = sddl ?? "",
                Got = got,
                Error = note,
            };
        }

        private static bool ApplyOne(OfflineHiveMountScope mount, RegSetting s, string windowsDir,
                                     Action<string>? log, bool dryRun, AclTracker acl)
        {
            var (hive, relPath) = MapLogicalToHive(s.Root, s.SubKey);
            if (hive == null)
            {
                log?.Invoke($"[SMI] 跳过（根暂不支持）：{s.Root}\\{s.SubKey}");
                return false;
            }

            string mountKey = mount.Ensure(hive);
            string fullPath = mountKey + (string.IsNullOrEmpty(relPath) ? "" : "\\" + relPath);
            string valueDisplay = s.KeyOnly
                ? "(仅键，无值)"
                : (string.IsNullOrEmpty(s.ValueName) ? "(默认值)" : s.ValueName);

            log?.Invoke($"[SMI]   {s.Root}\\{s.SubKey} [{valueDisplay}] {s.ValueType}={Truncate(s.Data, 60)}  ⇒  {fullPath}");
            if (dryRun) return true;

            using var key = Registry.LocalMachine.CreateSubKey(fullPath, writable: true);
            if (key == null)
            {
                log?.Invoke($"[SMI] ✗ 建/开键失败：{fullPath}");
                return false;
            }

            // ★ 键级属性：安全描述符（ACL）属于**键**，不属于值 —— 所以在"键已建好"之后、
            //   写值之前落下去（真实服务栈里安全属性就是随键创建一起设的）。
            //   实测带 SD 引用的键有 30712 个（占全部键的 38.6%，8400 x86 语料）。
            if (s.KeySddl.Length > 0) ApplyKeySddl(fullPath, s, log, acl);

            // ★ 只有键、没有值的设置（实测带安全描述符的键里 18.3% 是这种）：
            //   到这里"建键"已经做完了，**绝不能**再往下走 —— 下面会把 valueName="" 当成
            //   默认值、把空的 data 编码后写一条 `(Default)=""` 出来，凭空多一个值。
            if (s.KeyOnly)
            {
                log?.Invoke($"[SMI]   + 建键（无值）：{fullPath}");
                return true;
            }

            // ⚠ 这里**必须**走 RegSetValueExW 写原始字节，不能再用 RegistryKey.SetValue。
            //   原因见 RegSetting 里那段：.NET 的 SetValue 会按它自己的类型系统重新解释数据，
            //   REG_QWORD 的 8 字节内存镜像与 REG_MULTI_SZ 的引号定界符都会被它改掉或抛错。
            //
            // ★ 字节与"该不该写"都问 C++ 侧（OcidServicing.dll），这里只搬运。
            //   决策必须看 hive 现状：owner=true + hint=replace + 值已存在 ⇒ **不覆盖**
            //   （真实服务栈 CMI::Utility::RegistryInstallerImpl::InternalInstall 的分支）。
            //   只看清单写，会把 owner 组件已有的值无条件盖掉。
            if (!OcidServicingNative.Available)
            {
                log?.Invoke($"[SMI] ✗ 原生库 {OcidServicingNative.DllName} 不可用，**跳过不写**" +
                            $"（宁缺勿错，绝不退回 C# 自己解释）：{fullPath} [{valueDisplay}]");
                return false;
            }

            // ── `$(runtime.*)` 宏：**先展开再编码** ──────────────────────────────
            // 语料实测 5715 处值含宏（如 `$(runtime.system32)`）。原样写进 hive 一定是错的
            // —— `%` 号不会被展开，落盘的是一串字面 `$(runtime.…)`。
            // 展开表来自服务栈反编译（smiengine 的 ExpandDollarVar + winsockai 的 12 项表）
            // 与目标镜像实测目录，见 Source\Native\...\include\ocid\MacroExpander.h。
            string data = s.Data;
            string macroLeftover = "";
            if (s.HasMacro)
            {
                if (!OcidServicingNative.TryExpandMacros(windowsDir, s.Data, out string expanded,
                                                         out macroLeftover, out string macroErr))
                {
                    // 展不开就**不写**：写半截路径比不写更坏（而且很难查）。
                    log?.Invoke($"[SMI] ✗ 宏展开失败，**跳过不写**：{s.Root}\\{s.SubKey} " +
                                $"[{valueDisplay}] — {macroErr}");
                    return false;
                }
                data = expanded;

                if (macroLeftover.Length > 0)
                {
                    // `$(string.*)` / `$(build.nttree)` / `$(resourceString.*)` 本来就不是路径宏，
                    // 这里展开不了是正常的；报出来是为了不让人以为"所有宏都解决了"。
                    log?.Invoke($"[SMI] ⚠ 仍含未展开的宏（非路径宏，原样写入）：" +
                                $"{s.Root}\\{s.SubKey} [{valueDisplay}] — {macroLeftover}");
                }
                else if (!string.Equals(data, s.Data, StringComparison.Ordinal))
                {
                    log?.Invoke($"[SMI]   · 宏已展开：{Truncate(s.Data, 50)} ⇒ {Truncate(data, 80)}");
                }
            }

            bool exists = TryReadExistingValue(key.Handle, s.ValueName, out uint curType, out byte[] curData);

            if (!OcidServicingNative.TryResolveWrite(
                    s.Mechanism, s.OperationHint, s.Owner, s.ValueType, data,
                    exists, curType, curData, out var decision, out string decideErr))
            {
                log?.Invoke($"[SMI] ✗ 落地决策失败，**跳过不写**：{s.Root}\\{s.SubKey} [{valueDisplay}] " +
                            $"{s.ValueType} — {decideErr}");
                return false;
            }

            string actName = OcidServicingNative.ActionName(decision.Action);
            if (!decision.WillWrite)
            {
                // SkipOwned 是正常路径（不是失败）：owner 组件已经放了这个值，我们不该盖。
                log?.Invoke($"[SMI]   ⊘ {actName}：{fullPath} [{valueDisplay}] — {decision.Note}");
                return false;
            }

            if (exists && decision.TypeCode != curType && decision.Action == OcidWriteAction.Write
                && string.Equals(s.OperationHint, "replace", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s.Mechanism, "registryKeys", StringComparison.OrdinalIgnoreCase))
            {
                // 仅排障用：非 CMI 那套（WCM/SMI）语义就是"设成这个值"，类型变了也照写。
                log?.Invoke($"[SMI]   · 覆盖类型 {curType} ⇒ {decision.TypeCode}：{fullPath} [{valueDisplay}]");
            }

            byte[] bytes = decision.Data;
            int rc = RegSetValueExW(key.Handle, s.ValueName, 0, decision.TypeCode, bytes, (uint)bytes.Length);
            if (rc != 0)
            {
                log?.Invoke($"[SMI] ✗ RegSetValueExW 失败 rc={rc}（{new System.ComponentModel.Win32Exception(rc).Message}）：" +
                            $"{fullPath} [{valueDisplay}] type={decision.TypeCode} len={bytes.Length}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 读出目标值在 hive 里的现状（类型 + 原始字节）。不存在返回 false。
        /// </summary>
        /// <remarks>
        /// 一定要读原始字节：决策里的 Merge（append/prepend）与"类型是否相同"都基于字节，
        /// 用 .NET 的 <c>GetValue</c> 会把 REG_QWORD/REG_MULTI_SZ 重新解释一遍，那正是
        /// 我们要避免的第二份解释逻辑。
        /// </remarks>
        private static bool TryReadExistingValue(SafeRegistryHandle hKey, string valueName,
                                                 out uint typeCode, out byte[] data)
        {
            typeCode = 0;
            data = Array.Empty<byte>();

            uint size = 0;
            int rc = RegQueryValueExW(hKey, valueName, IntPtr.Zero, out typeCode, null, ref size);
            uint err = (uint)Marshal.GetLastWin32Error();
            // ERROR_FILE_NOT_FOUND(2) / ERROR_MORE_DATA(234)：前者=不存在，后者=需要缓冲。
            if (rc != 0 && err != 234) return false;

            if (size == 0) return rc == 0;

            var buf = new byte[size];
            rc = RegQueryValueExW(hKey, valueName, IntPtr.Zero, out typeCode, buf, ref size);
            if (rc != 0) return false;
            if (size != buf.Length) Array.Resize(ref buf, (int)size);
            data = buf;
            return true;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegQueryValueExW(
            SafeRegistryHandle hKey,
            string lpValueName,
            IntPtr lpReserved,
            out uint lpType,
            byte[]? lpData,
            ref uint lpcbData);

        // 直接写原始字节（绕开 .NET 的类型解释）。lpValueName 传空串即**默认值**。
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegSetValueExW(
            SafeRegistryHandle hKey,
            string lpValueName,
            uint reserved,
            uint dwType,
            byte[] lpData,
            uint cbData);

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

            // ⚠ HKEY_CURRENT_USER 在【离线】镜像里没有当前用户，正确的落点是把设置写进
            //   **默认用户**的 hive，这样新建用户时才会带上这些设置。
            //   镜像里对应 `Windows\System32\config\DEFAULT`。
            //   实测 CBS 自己也会挂载 DEFAULT 与 `Users\default\ntuser.dat` 两份。
            //   清单里的判据是 `wcm:scope="perUser"`（handler 根为 HKEY_CURRENT_USER）。
            bool hkcu = r.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                     || r.Equals("HKCU", StringComparison.OrdinalIgnoreCase);
            if (hkcu)
                return (HiveDefault, sub);

            // HKEY_USERS\<SID>\...：离线时只认 .DEFAULT（其余 SID 不在镜像里）
            bool hku = r.Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("HKU", StringComparison.OrdinalIgnoreCase);
            if (hku)
            {
                if (StartsWithSegment(sub, ".DEFAULT")) return (HiveDefault, StripFirstSegment(sub));
                return (null, "");   // 具名 SID 的 hive 不在镜像里，属未支持
            }

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

        private static byte[] HexToBytes(string hex)
        {
            // ⚠ 严格解析，**故意不宽容**。
            //   原来的版本会 `if (Uri.IsHexDigit(c)) sb.Append(c)` 把非 hex 字符静默丢掉、
            //   奇数长度还会前置补 '0' —— 那正是"看起来成功、字节全错"的典型故障。
            //   现在这个输入来自 C++ 侧算好的 `dataHex`（大写 hex、无前缀、偶数长度），
            //   出现异常就说明上游坏了，必须炸出来而不是猜。
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
            if ((hex.Length & 1) != 0)
                throw new FormatException($"dataHex 长度必须为偶数，实际 {hex.Length}：\"{hex}\"");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                    throw new FormatException($"dataHex 含非 hex 字符：\"{hex}\"");
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;

            static int HexVal(char c) => c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
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
