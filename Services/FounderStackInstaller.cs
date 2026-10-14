using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 把 servicing stack 装进一个**刚铺底出来的空骨架**。
    ///
    /// 【为什么必需 —— 这是"从头组装"的缺环】
    /// <see cref="SxSFounderService"/> 铺底只造出目录骨架与一组空 hive：**没有 WinSxS、
    /// 没有组件存储、没有 servicing stack**。CBS 在这种骨架上是起不来的 ——
    /// `ICbsSession::Initialize` 会找不到服务栈。
    ///
    /// 原 `prepare.cmd` 的做法就是**直接把一个 servicing stack 目录 xcopy 进去**，
    /// 本服务把那一步正规化：
    ///   1. 在给定来源里找到 &lt;arch&gt; 的 servicing stack 目录
    ///      （形如 `amd64_microsoft-windows-servicingstack_31bf3856ad364e35_6.2.8400.0_none_....`）
    ///   2. 整目录复制到 `&lt;镜像&gt;\Windows\WinSxS\`
    ///   3. 对应的 `*.mum` 复制到 `&lt;镜像&gt;\Windows\servicing\Packages\`
    ///      （CBS 靠它识别"镜像声明了哪个版本的服务栈"）
    ///   4. 校验落地结果：`cbscore.dll` 与 `wcp.dll` 是否就位，并报出解析到的版本
    ///
    /// 【为什么 ③ 的 mum 很重要】CBS 会把"已加载栈自称的版本"与"镜像声明的栈版本"
    /// 做比较（见 <see cref="ServicingRouter"/>）。镜像侧的版本就来自这个 mum，
    /// 所以 **mum 必须与服务栈目录同版本**，否则会重演 0x8007051A / 0x800F0823。
    /// </summary>
    public static class FounderStackInstaller
    {
        public sealed class Config
        {
            /// <summary>镜像根（铺底出来的骨架根，例 G:\）。</summary>
            public string ImageRoot = "";
            /// <summary>目标架构：x86 / amd64 / arm / arm64。</summary>
            public string Arch = "amd64";
            /// <summary>服务栈来源目录（递归查找 *servicingstack*）。</summary>
            public List<string> StackSourceDirs = new();
            /// <summary>是否复制对应的 .mum（**强烈建议 true**，否则 CBS 判不出镜像侧版本）。</summary>
            public bool CopyMum = true;
            /// <summary>只报告将要做什么，不落盘。</summary>
            public bool DryRun = true;
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public int StacksCopied;
            public int MumsCopied;
            public int FilesCopied;
            public long BytesCopied;
            public string? InstalledStackDir;      // 落地后的服务栈目录
            public string? ResolvedVersion;        // 从目录名解析出的版本
            public bool CbscorePresent;
            public bool WcpPresent;
            public readonly List<string> Candidates = new();
            public readonly List<string> Log = new();

            public string Summary()
                => $"服务栈 {StacksCopied} 套 / mum {MumsCopied} 个 / 文件 {FilesCopied} 个 / {BytesCopied / 1024.0 / 1024.0:N1} MB"
                 + $"；cbscore={(CbscorePresent ? "有" : "无")} wcp={(WcpPresent ? "有" : "无")}"
                 + (ResolvedVersion != null ? $"；版本 {ResolvedVersion}" : "");
        }

        static readonly Regex VerRe = new(@"(\d+\.\d+\.\d+\.\d+)", RegexOptions.Compiled);

        /// <summary>在来源目录里找某个架构的 servicing stack 目录（取版本最高的一个）。</summary>
        public static (string? dir, string? version) FindStack(IEnumerable<string> sourceDirs, string arch,
                                                              List<string>? candidates = null)
        {
            var found = new List<(string dir, Version ver, string verText)>();
            foreach (var root in sourceDirs.Where(Directory.Exists))
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root, "*servicingstack*", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    // 架构前缀必须匹配，否则会把 x64 栈塞进 x86 镜像
                    if (!name.StartsWith(arch + "_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(Path.Combine(d, "cbscore.dll"))) continue;

                    var m = VerRe.Match(name);
                    if (!m.Success) continue;
                    if (!Version.TryParse(m.Groups[1].Value, out var v)) continue;

                    candidates?.Add($"{name}  ({d})");
                    found.Add((d, v, m.Groups[1].Value));
                }
            }
            if (found.Count == 0) return (null, null);
            var best = found.OrderByDescending(f => f.ver).First();
            return (best.dir, best.verText);
        }

        /// <summary>
        /// 找与给定服务栈目录同版本的 mum。
        /// ⚠ mum **不在 WinSxS 里**，而在 `&lt;镜像&gt;\Windows\servicing\Packages`。
        /// 调用方常常只传 `...\Windows\WinSxS`，所以这里会**自动探测兄弟目录**
        /// （`..\servicing\Packages`、`..\..\servicing\Packages`、`&lt;root&gt;\servicing\Packages`），
        /// 否则会误报"没找到同版本 mum"，而那个 mum 正是 CBS 判定镜像侧服务栈版本的依据。
        /// </summary>
        public static string? FindStackMum(IEnumerable<string> sourceDirs, string version)
        {
            // 先把每个来源展开成"自身 + 可能的 servicing\Packages 兄弟目录"
            var expanded = new List<string>();
            foreach (var d in sourceDirs.Where(Directory.Exists))
            {
                expanded.Add(d);
                foreach (var cand in new[]
                {
                    Path.Combine(d, "servicing", "Packages"),
                    Path.Combine(d, "Packages"),
                    Path.Combine(d, "..", "servicing", "Packages"),
                    Path.Combine(d, "..", "..", "servicing", "Packages"),
                })
                {
                    try { if (Directory.Exists(cand)) expanded.Add(Path.GetFullPath(cand)); } catch { }
                }
            }

            foreach (var root in expanded.Distinct())
            {
                IEnumerable<string> mums;
                try { mums = Directory.EnumerateFiles(root, "*servicingstack*.mum", SearchOption.AllDirectories); }
                catch { continue; }
                var hit = mums.FirstOrDefault(f => Path.GetFileName(f).Contains(version, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        public static Result Install(Config cfg, Action<string>? log = null)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }

            try
            {
                if (string.IsNullOrWhiteSpace(cfg.ImageRoot)) { res.Error = "ImageRoot 为空"; return res; }
                string root = Path.GetFullPath(cfg.ImageRoot);
                string win = Path.Combine(root, "Windows");
                if (!Directory.Exists(win)) { res.Error = $"骨架里没有 Windows 目录：{win}（请先铺底）"; return res; }

                // ★ 必须启用特权：WCP 铺底出来的骨架带**限制性 ACL**（目录归属系统/TrustedInstaller），
                //   普通提权进程也会被拒（实测 UnauthorizedAccessException）。
                //   SeRestorePrivilege 正是用来绕过 ACL 写文件的，但它默认是**关闭**的。
                PrivilegeHelper.EnableServicingPrivileges(log);

                // ① 找服务栈
                var (stackDir, version) = FindStack(cfg.StackSourceDirs, cfg.Arch, res.Candidates);
                if (stackDir == null)
                {
                    res.Error = $"在给定来源里找不到 {cfg.Arch} 的 servicing stack（需要包含 cbscore.dll 且目录名以 {cfg.Arch}_ 开头的 *servicingstack* 目录）。";
                    foreach (var c in res.Candidates.Take(10)) L("  候选: " + c);
                    return res;
                }
                res.ResolvedVersion = version;
                L($"选定服务栈: {Path.GetFileName(stackDir)}");
                L($"  版本: {version}   架构: {cfg.Arch}");

                // ② 目标位置
                string sxs = Path.Combine(win, "WinSxS");
                string destStack = Path.Combine(sxs, Path.GetFileName(stackDir));
                string pkgDir = Path.Combine(win, "servicing", "Packages");
                res.InstalledStackDir = destStack;

                // ③ mum
                string? mum = cfg.CopyMum ? FindStackMum(cfg.StackSourceDirs, version ?? "") : null;
                if (cfg.CopyMum && mum == null)
                {
                    // 实测（8400 Win8 RC x86）：servicing\Packages 里有 576 个 mum，
                    // 但**一个 servicingstack mum 都没有** —— 服务栈是靠它自己的 WinSxS
                    // 目录（及目录内的 manifest）被识别的，不是靠 servicing\Packages 里的 mum。
                    // 所以这不是失败，只是该构建的形态。目录本身已整份复制，manifest 随之就位。
                    L(@"  说明：本构建的服务栈没有独立的 mum（servicing\Packages 里没有 servicingstack 条目）；"
                    + "服务栈由它自己的 WinSxS 目录识别，目录已整份复制。");
                }

                if (cfg.DryRun)
                {
                    L("[dry-run] 将执行：");
                    L($"   复制目录  {stackDir}");
                    L($"        →    {destStack}");
                    if (mum != null) L($"   复制 mum  {Path.GetFileName(mum)}  →  {pkgDir}");
                    res.Success = true;
                    return res;
                }

                // ④ 落盘
                Directory.CreateDirectory(sxs);
                Directory.CreateDirectory(pkgDir);
                // 骨架 ACL 限制：先把目标目录弄可写，否则文件复制会被拒
                EnsureWritable(sxs, log);
                if (Directory.Exists(destStack)) EnsureWritable(destStack, log);
                CopyTree(stackDir, destStack, ref res);
                res.StacksCopied = 1;

                if (mum != null)
                {
                    string destMum = Path.Combine(pkgDir, Path.GetFileName(mum));
                    File.Copy(mum, destMum, true);
                    res.FilesCopied++; res.BytesCopied += new FileInfo(mum).Length;
                    res.MumsCopied++;
                    L($"已放 mum: {Path.GetFileName(mum)}");
                }

                // ⑤ 校验
                res.CbscorePresent = File.Exists(Path.Combine(destStack, "cbscore.dll"));
                // 服务栈目录内的 manifest（若该构建以 manifest 识别服务栈）
                try
                {
                    var man = Directory.EnumerateFiles(destStack, "*servicingstack*.manifest").FirstOrDefault()
                           ?? Directory.EnumerateFiles(destStack, "*.manifest").FirstOrDefault();
                    if (man != null) L($"  目录内 manifest: {Path.GetFileName(man)}");
                    else L("  目录内没有 manifest（该构建可能只用目录名标识服务栈）");
                }
                catch { }
                res.WcpPresent = File.Exists(Path.Combine(destStack, "wcp.dll"));
                L($"落地校验: cbscore={(res.CbscorePresent ? "有" : "无")}  wcp={(res.WcpPresent ? "有" : "无")}");
                if (!res.CbscorePresent) { res.Error = "服务栈落地后仍缺 cbscore.dll"; return res; }
                if (!res.WcpPresent) L("⚠ 缺 wcp.dll —— CBS 可以走，但『铺底/离线存储』相关 WCP 调用会不可用。");

                res.Success = true;
                L("结果: " + res.Summary());
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>
        /// 让目标目录可写。
        ///
        /// 【为什么必需】WCP 铺底出来的骨架带**限制性 ACL**（归属系统/TrustedInstaller），
        /// 即使以管理员运行、即使已启用 SeRestorePrivilege，.NET 的 File.Copy 仍会被拒
        /// （实测：目录能创建，但往里写 appxreg.dll 被拒）。SeRestorePrivilege 只对
        /// 某些 CreateFile 调用模式生效，不足以让普通文件复制绕过目标目录的拒绝 ACL。
        ///
        /// 所以这里用 Windows 自带工具做正规处理：
        ///   ① takeown  /F &lt;dir&gt; /R /D Y        取得所有权
        ///   ② icacls   &lt;dir&gt; /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q
        ///      （S-1-5-32-544 = Administrators，用 SID 而不是名称，避免本地化名称差异）
        /// </summary>

        /// <summary>
        /// CBS 在空骨架上启动所需的**最小系统文件集**。
        ///
        /// 【怎么来的】实测出来的：铺底 + 装服务栈后，CBS 仍然失败在
        ///   `Failed to open file: ...\Windows\system32\kernelbase.dll [ERROR_FILE_NOT_FOUND]`
        ///   `Failed to load ntoskrnl.exe to detect bitness from offline image`
        ///   `Failed to populate information about offline store`
        /// CBS 需要读几个系统 DLL 来**探测镜像位数**并**填充离线存储信息**。
        /// 补齐下面这组后，会话立刻成功：
        ///   CSI Store initialized / Session initialized / Session finalized (clean)。
        ///
        /// 【为什么不整份复制】整份 system32 会带来大量无关文件与许可/体积问题；
        /// 实测这最小一组就够 CBS 起来了。缺哪个再补哪个即可（日志会明确指出）。
        /// </summary>
        public static readonly string[] CoreFilesForCbs =
        {
            @"system32\kernelbase.dll",        // ★ 日志明确点的就是它
            @"system32\ntdll.dll",
            @"system32\kernel32.dll",
            @"system32\advapi32.dll",
            @"system32\msvcrt.dll",
            @"system32\rpcrt4.dll",
            @"system32\bcrypt.dll",
            @"system32\bcryptprimitives.dll",
            @"system32\sechost.dll",
            @"system32\ci.dll",
            @"system32\ssshim.dll",            // ★ DISM 服务离线镜像必需 —— 缺它 DISM 报
                                               //   Error 50 "check that SSShim.DLL is present"
            @"system32\cbsmsg.dll",            // CBS 的消息资源，服务栈的一部分
            @"system32\ntoskrnl.exe",          // 用于探测位数
        };

        /// <summary>
        /// 把最小系统文件集放进骨架。返回实际放入的数量与缺失清单。
        /// </summary>
        public static (int copied, List<string> missing) InstallCoreFiles(
            string imageRoot, string sourceWindowsDir, Action<string>? log = null)
        {
            var missing = new List<string>();
            int copied = 0;
            try
            {
                string destWin = Path.Combine(Path.GetFullPath(imageRoot), "Windows");
                if (!Directory.Exists(destWin)) { log?.Invoke("  (骨架里没有 Windows 目录)"); return (0, missing); }
                if (!Directory.Exists(sourceWindowsDir)) { log?.Invoke($"  (来源不可用: {sourceWindowsDir})"); return (0, missing); }

                // 目标目录可能带限制性 ACL（骨架来自 WCP 铺底），先弄可写
                string destSys32 = Path.Combine(destWin, "system32");
                Directory.CreateDirectory(destSys32);
                EnsureWritable(destWin, log);

                foreach (var rel in CoreFilesForCbs)
                {
                    string src = Path.Combine(sourceWindowsDir, rel);
                    string dst = Path.Combine(destWin, rel);
                    if (!File.Exists(src)) { missing.Add(rel); continue; }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst, true);
                        copied++;
                    }
                    catch (Exception ex) { missing.Add($"{rel}（复制失败: {ex.Message}）"); }
                }
                log?.Invoke($"  最小系统文件集: 放入 {copied} 个" + (missing.Count > 0 ? $"，缺失 {missing.Count} 个" : ""));
                foreach (var m in missing.Take(6)) log?.Invoke("    缺: " + m);
            }
            catch (Exception ex) { log?.Invoke("  InstallCoreFiles 异常: " + ex.Message); }
            return (copied, missing);
        }


        /// <summary>
        /// 从【还原出的 SxS 存储】里按【文件名】找核心文件并放进骨架的 system32。
        ///
        /// 【为什么需要这个变体】<see cref="InstallCoreFiles"/> 期望来源形如
        /// `&lt;源&gt;\system32\kernelbase.dll`；但 Repair Content 还原出来的素材是**散在
        /// WinSxS 组件目录**里的：
        ///   .../kb777777-arm-neutral-common_foundation_&lt;hash&gt;/
        ///        arm_microsoft-windows-kernelbase_&lt;ptk&gt;_6.2.8400.0_none_&lt;hash&gt;/kernelbase.dll
        /// 当**没有完整系统镜像**可做来源时（例如 arm32），这是唯一可行的取法。
        ///
        /// 【注意】只按文件名取，因此调用方应确认存储的架构与目标一致
        /// （例如 arm32 存储里取到的 PE 头是 0x01C4 ARMNT）。
        /// </summary>
        public static (int copied, List<string> missing, List<string> sources) InstallCoreFilesFromStore(
            string imageRoot, IEnumerable<string> storeDirs, Action<string>? log = null, string? arch = null)
        {
            var missing = new List<string>();
            var sources = new List<string>();
            int copied = 0;
            try
            {
                string destWin = Path.Combine(Path.GetFullPath(imageRoot), "Windows");
                if (!Directory.Exists(destWin)) { log?.Invoke("  (骨架里没有 Windows 目录)"); return (0, missing, sources); }

                // 建立"文件名 → 路径"索引（只收核心文件那几个名字，避免索引整个存储）
                var want = new HashSet<string>(CoreFilesForCbs.Select(p => Path.GetFileName(p)!),
                                               StringComparer.OrdinalIgnoreCase);
                // 架构前缀：优先匹配路径里含 `<arch>_` 的组件目录。
                // 【为什么必须过滤】只按文件名找会捞到别的架构：实测给 arm32 目标
                // 捞到了 x86 的 ssshim.dll（PE 头 0x014C 而非 0x01C4）。
                string wantPrefix = string.IsNullOrWhiteSpace(arch) ? "" : arch!.Trim().ToLowerInvariant() + "_";
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in storeDirs.Where(Directory.Exists))
                {
                    foreach (var f in UpdatePackageService.SafeEnumerateFiles(dir))
                    {
                        string name = Path.GetFileName(f);
                        if (!want.Contains(name)) continue;
                        bool archMatch = wantPrefix.Length == 0 || f.Contains(wantPrefix, StringComparison.OrdinalIgnoreCase);
                        if (archMatch)
                        {
                            if (!found.ContainsKey(name)) found[name] = f;
                        }
                        else if (!fallback.ContainsKey(name)) fallback[name] = f;
                    }
                }
                // 架构对不上的仅在"该名字完全没有匹配架构的版本"时兜底，并告警
                foreach (var kv in fallback)
                {
                    if (found.ContainsKey(kv.Key)) continue;
                    found[kv.Key] = kv.Value;
                    log?.Invoke($"  ⚠ {kv.Key} 没有 {wantPrefix.TrimEnd('_')} 架构版本，用找到的代替");
                }
                log?.Invoke($"  在存储里找到 {found.Count}/{want.Count} 个核心文件");

                string destSys32 = Path.Combine(destWin, "system32");
                Directory.CreateDirectory(destSys32);
                EnsureWritable(destWin, log);

                foreach (var rel in CoreFilesForCbs)
                {
                    string name = Path.GetFileName(rel);
                    if (!found.TryGetValue(name, out var src)) { missing.Add(name); continue; }
                    string dst = Path.Combine(destWin, "system32", name);
                    try
                    {
                        File.Copy(src, dst, true);
                        copied++;
                        sources.Add($"{name} ← {src}");
                    }
                    catch (Exception ex) { missing.Add($"{name}（复制失败: {ex.Message}）"); }
                }
                log?.Invoke($"  核心文件（取自存储）: 放入 {copied} 个" + (missing.Count > 0 ? $"，缺失 {missing.Count} 个" : ""));
                foreach (var m in missing.Take(6)) log?.Invoke("    缺: " + m);
            }
            catch (Exception ex) { log?.Invoke("  InstallCoreFilesFromStore 异常: " + ex.Message); }
            return (copied, missing, sources);
        }

        /// <summary>
        /// 把镜像的版本信息写进骨架的 SOFTWARE / COMPONENTS hive。
        ///
        /// 【为什么必须写、且必须在装包之前写】
        /// 实测：铺出来的裸骨架里没有任何版本信息，DISM/SSShim 判不出 OS 版本，
        /// 于是报
        ///     Error: 50  DISM does not support servicing a Windows Vista RTM or earlier
        ///                operating system. check that SSShim.DLL is present.
        /// 这【不是】shim 缺失（ssshim.dll 已就位），而是判不出版本。
        /// 对照真实 arm32 镜像（8419 CLIENT_COREARM）—— 它有这些值，DISM 就完全可用，
        /// 甚至成功装入了比镜像版本低的 arm32 包。
        ///
        /// 时机很关键：装包发生在收尾之前，所以不能放在 FinishImageStep 里 ——
        /// 必须放在"装好服务栈与核心文件"之后、"装包"之前。
        /// </summary>
        /// <param name="winDir">镜像的 Windows 目录</param>
        /// <param name="arch">x86 / arm / amd64 / arm64（用于 StoreArchitecture）</param>
        /// <param name="build">构建号（例 8400）；0 表示不写具体值</param>
        public static (int written, string detail) SeedImageVersionInfo(
            string winDir, string arch, int build, Action<string>? log = null)
        {
            int written = 0;
            var detail = new System.Text.StringBuilder();
            try
            {
                string win = Path.GetFullPath(winDir);
                string software = Path.Combine(win, "System32", "config", "SOFTWARE");
                string components = Path.Combine(win, "System32", "config", "COMPONENTS");

                // 版本号：6.2.<build>（Win8 世代）。没给 build 就只写 CurrentVersion。
                string curBuild = build > 0 ? build.ToString() : "8400";

                // ── SOFTWARE\Microsoft\Windows NT\CurrentVersion ──
                if (File.Exists(software))
                {
                    // 先释放 CBS 可能仍持有的 hive，否则 LoadHive 会报"被占用"
                    try
                    {
                        string dr = (Path.GetPathRoot(win) ?? "").TrimEnd('\\');
                        if (dr.Length > 0) CbsSafety.ReleaseResidualHives(dr, log);
                    }
                    catch { }
                    using var reg = new OfflineRegistryService();
                    if (reg.LoadHive(software, writable: true))
                    {
                        string k = @"Microsoft\Windows NT\CurrentVersion";
                        void W(string n, object v) { if (reg.SetValue(k, n, v, Microsoft.Win32.RegistryValueKind.String)) written++; }
                        W("CurrentVersion", "6.2");
                        W("CurrentBuild", curBuild);
                        W("CurrentBuildNumber", curBuild);
                        W("InstallationType", "Client");
                        W("ProductName", arch.Equals("arm", StringComparison.OrdinalIgnoreCase)
                                          || arch.Equals("arm64", StringComparison.OrdinalIgnoreCase)
                                          ? "Windows RT" : "Windows 8");
                        if (string.IsNullOrEmpty(reg.GetValue(k, "EditionID") as string))
                            W("EditionID", "Core");
                        detail.Append($"SOFTWARE: CurrentVersion=6.2 CurrentBuild={curBuild} 等; ");
                    }
                    else detail.Append("SOFTWARE: 打开失败; ");
                }
                else detail.Append("SOFTWARE: 不存在; ");

                // ── COMPONENTS 根值 ──
                if (File.Exists(components))
                {
                    using var reg2 = new OfflineRegistryService();
                    if (reg2.LoadHive(components, writable: true))
                    {
                        // StoreFormatVersion 是 REG_BINARY，内容为 UTF-16LE 的 "0.0.0.6"
                        // （实测真实镜像里就是这 14 个字节：48 00 2E 00 ... 36 00）
                        var vBytes = System.Text.Encoding.Unicode.GetBytes("0.0.0.6");
                        if (reg2.SetValue("", "StoreFormatVersion", vBytes,
                                          Microsoft.Win32.RegistryValueKind.Binary)) written++;
                        if (reg2.SetValue("", "StoreArchitecture", (int)SxSFounderService.ArchitectureValue(arch),
                                          Microsoft.Win32.RegistryValueKind.DWord)) written++;
                        detail.Append($"COMPONENTS: StoreFormatVersion=0.0.0.6 StoreArchitecture={(int)SxSFounderService.ArchitectureValue(arch)}; ");
                    }
                    else detail.Append("COMPONENTS: 打开失败; ");
                }
                else detail.Append("COMPONENTS: 不存在; ");
            }
            catch (Exception ex) { detail.Append("异常: " + ex.Message); }
            log?.Invoke("  版本信息: " + detail.ToString().TrimEnd(' ', ';'));
            return (written, detail.ToString().TrimEnd(' ', ';'));
        }


        /// <summary>
        /// 让骨架"能被 DISM 服务"所必需的最小结构。
        ///
        /// 【为什么需要】实测判据：`dism /image:&lt;路径&gt; /?` 的
        ///   · 成功 → 会打印 <c>Image Version: 6.2.xxxx.0</c>
        ///   · 失败 → <c>Error: 50  DISM does not support servicing a Windows Vista RTM or
        ///            earlier operating system</c>
        /// 官方 unstaged 系统（WoA 8061 ARM32 / Nano 10586 ARM64）**都能打印版本**，
        /// 而我们铺出来的骨架不能。结构对照显示最一致的差异就是
        /// <c>System32\Dism\</c>（DISM 的全部 provider）我们完全没有。
        ///
        /// 【来源】这些文件在还原出的 SxS 存储里都能按名找到（已实测）。
        ///
        /// 【出处】思路借鉴 explodeSxS（asdcorp，GPL-3.0）"从内容构造可服务骨架"的做法；
        /// 本方法为本项目自写实现。
        /// </summary>
        public static (int copied, List<string> missing) SeedDismServicingStructure(
            string imageRoot, IEnumerable<string> storeDirs, string? arch = null, Action<string>? log = null)
        {
            var missing = new List<string>();
            int copied = 0;
            try
            {
                string root = Path.GetFullPath(imageRoot);
                string win = Directory.Exists(Path.Combine(root, "Windows")) ? Path.Combine(root, "Windows") : root;
                string sys32 = Path.Combine(win, "System32");
                Directory.CreateDirectory(sys32);

                // ── 要补齐的文件清单（照官方 unstaged 形态） ──
                // System32\Dism\* —— DISM 的全部 provider，缺一不可
                var dismProviders = new[]
                {
                    "DismCore.dll", "DismCorePS.dll", "DismHost.exe", "DismProv.dll",
                    "CbsProvider.dll", "CompatProvider.dll", "DmiProvider.dll", "FfuProvider.dll",
                    "FolderProvider.dll", "GenericProvider.dll", "ImagingProvider.dll", "IntlProvider.dll",
                    "LogProvider.dll", "OfflineSetupProvider.dll", "OSProvider.dll", "ProvProvider.dll",
                    "SmiProvider.dll", "UnattendProvider.dll", "VhdProvider.dll", "WimProvider.dll",
                };
                // System32 根下的 staging/servicing 基础设施
                var sys32Files = new[]
                {
                    "ssshim.dll", "sxs.dll", "wdscore.dll", "SmiEngine.dll",
                    "PkgMgr.exe", "poqexec.exe", "sxstrace.exe", "CbsApi.dll", "CbsMsg.dll",
                };

                // 建立"文件名 → 路径"索引（架构优先匹配，与 InstallCoreFilesFromStore 同策略）
                string wantPrefix = string.IsNullOrWhiteSpace(arch) ? "" : arch!.Trim().ToLowerInvariant() + "_";
                var want = new HashSet<string>(dismProviders.Concat(sys32Files), StringComparer.OrdinalIgnoreCase);
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in storeDirs.Where(Directory.Exists))
                {
                    foreach (var f in UpdatePackageService.SafeEnumerateFiles(dir))
                    {
                        string name = Path.GetFileName(f);
                        if (!want.Contains(name)) continue;
                        bool archMatch = wantPrefix.Length == 0
                                      || f.Contains(wantPrefix, StringComparison.OrdinalIgnoreCase);
                        if (archMatch) { if (!found.ContainsKey(name)) found[name] = f; }
                        else if (!fallback.ContainsKey(name)) fallback[name] = f;
                    }
                }
                foreach (var kv in fallback)
                    if (!found.ContainsKey(kv.Key)) found[kv.Key] = kv.Value;

                log?.Invoke($"  在存储里找到 {found.Count}/{want.Count} 个 DISM/staging 文件");

                // ── 落盘：System32\Dism\ ──
                string dismDir = Path.Combine(sys32, "Dism");
                Directory.CreateDirectory(dismDir);
                foreach (var n in dismProviders)
                {
                    if (!found.TryGetValue(n, out var src)) { missing.Add("Dism\\" + n); continue; }
                    try { File.Copy(src, Path.Combine(dismDir, n), true); copied++; }
                    catch (Exception ex) { missing.Add($"Dism\\{n}（{ex.Message}）"); }
                }
                // ── 落盘：System32 根 ──
                foreach (var n in sys32Files)
                {
                    if (!found.TryGetValue(n, out var src)) { missing.Add(n); continue; }
                    try { File.Copy(src, Path.Combine(sys32, n), true); copied++; }
                    catch (Exception ex) { missing.Add($"{n}（{ex.Message}）"); }
                }

                // ── servicing\Version\<ver>\<arch>_installed 标记 ──
                // 官方 unstaged 形态里有这两个 9 字节文件，声明"本镜像装了哪个架构的栈"。
                try
                {
                    string? verDir = null;
                    foreach (var d in Directory.EnumerateDirectories(Path.Combine(win, "WinSxS"), "*servicingstack*"))
                    {
                        // 目录名形如 <arch>_microsoft-windows-servicingstack_<ptk>_<ver>_<opt>_<hash>
                        var parts = Path.GetFileName(d).Split('_');
                        if (parts.Length >= 4) { verDir = parts[3]; break; }
                    }
                    verDir ??= "6.2.8400.0";
                    string sv = Path.Combine(win, "servicing", "Version", verDir);
                    Directory.CreateDirectory(sv);
                    string tok = string.IsNullOrWhiteSpace(arch) ? "x86" : arch!.Trim().ToLowerInvariant();
                    File.WriteAllText(Path.Combine(sv, tok + "_installed"), tok + "_installed");
                    copied++;
                    log?.Invoke($"  写下 servicing\\Version\\{verDir}\\{tok}_installed");
                }
                catch (Exception ex) { missing.Add("servicing\\Version 标记（" + ex.Message + "）"); }

                // ── CatRoot：DISM 判版本会读目录里的 .cat ──
                try
                {
                    string catRoot = Path.Combine(sys32, "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");
                    Directory.CreateDirectory(catRoot);
                    var cats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dir in storeDirs.Where(Directory.Exists))
                        foreach (var f in UpdatePackageService.SafeEnumerateFiles(dir))
                        {
                            string n = Path.GetFileName(f);
                            if (!n.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)) continue;
                            if (cats.Count >= 12) break;               // 只需少量根证书目录
                            if (!cats.ContainsKey(n)) cats[n] = f;
                        }
                    foreach (var kv in cats)
                    {
                        try { File.Copy(kv.Value, Path.Combine(catRoot, kv.Key), true); copied++; }
                        catch { }
                    }
                    log?.Invoke($"  CatRoot: 放入 {cats.Count} 个 .cat");
                }
                catch (Exception ex) { missing.Add("CatRoot（" + ex.Message + "）"); }

                log?.Invoke($"  DISM 可服务结构: 放入 {copied} 个文件"
                          + (missing.Count > 0 ? $"，缺失 {missing.Count}" : ""));
                foreach (var m in missing.Take(8)) log?.Invoke("    缺: " + m);
            }
            catch (Exception ex) { log?.Invoke("  SeedDismServicingStructure 异常: " + ex.Message); }
            return (copied, missing);
        }

        static void EnsureWritable(string dir, Action<string>? log)
        {
            if (!Directory.Exists(dir)) return;
            RunTool("takeown.exe", $"/F \"{dir}\" /R /D Y", log);
            RunTool("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q", log);
        }

        static void RunTool(string exe, string args, Action<string>? log)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) { log?.Invoke($"  ({exe} 启动失败)"); return; }
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10 * 60 * 1000);
                log?.Invoke($"  {exe} 退出码 {p.ExitCode}");
            }
            catch (Exception ex) { log?.Invoke($"  ({exe} 异常: {ex.Message})"); }
        }

        static void CopyTree(string src, string dst, ref Result res)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                string t = Path.Combine(dst, Path.GetFileName(f));
                File.Copy(f, t, true);
                res.FilesCopied++;
                try { res.BytesCopied += new FileInfo(t).Length; } catch { }
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                // 跳过重解析点，避免把外面的东西拖进来
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), ref res);
            }
        }

        /// <summary>一行结论，供界面显示。</summary>
        public static string Describe(Config cfg)
        {
            var (dir, ver) = FindStack(cfg.StackSourceDirs, cfg.Arch);
            var sb = new StringBuilder();
            sb.AppendLine($"目标架构 : {cfg.Arch}");
            if (dir == null)
            {
                sb.AppendLine("服务栈   : 未找到（需要包含 cbscore.dll 且目录名以架构前缀开头的 *servicingstack* 目录）");
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine($"服务栈   : {Path.GetFileName(dir)}");
            sb.AppendLine($"版本     : {ver}");
            var mum = FindStackMum(cfg.StackSourceDirs, ver ?? "");   // 已含兄弟目录探测
            sb.AppendLine($"mum      : {(mum == null ? "(未找到同版本 mum)" : Path.GetFileName(mum))}");
            sb.AppendLine($"落点     : {Path.Combine(Path.GetFullPath(cfg.ImageRoot), "Windows", "WinSxS", Path.GetFileName(dir))}");
            return sb.ToString().TrimEnd();
        }
    }
}
