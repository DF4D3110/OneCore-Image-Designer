// OCID - CSI（组件文件落位）C# 侧编排
//
// ── 机制（全部在真实 8061 ARM 镜像上量过，不是推的）────────────────────────
//
//   packages\<组件目录>\<组件文件>          ← 载荷（包展开出来的那一份）
//   Windows\WinSxS\<组件目录>\<组件文件>    ← 正本
//   目标位置（如 Windows\System32\x.dll）  ← **与正本同一个文件**（硬链接）
//
//   **清单在 `packages\` 下是平铺的**：`packages\<组件目录>.manifest`（5664 份），
//   载荷才在 `packages\<组件目录>\` 里；WinSxS 的 118 份同样是
//   `WinSxS\Manifests\<组件目录>.manifest`。所以组件目录名只能从**文件名**推 ——
//   按所在目录名推会全算成 "packages"（载荷路径变成 `packages\packages\x.dll`）。
//
//   实测：`Windows\System32\basesrv.dll` 的 `fsutil hardlink list` 恰好两条 ——
//   目标位置 + `WinSxS\arm_microsoft-windows-basesrv_…\basesrv.dll`；`cdfs.sys`、
//   `Dism\DismCore.dll`、`kdcom.dll` 同样。所以"落位"= 建两条硬链接，不是复制。
//   硬链接建不了（跨卷等）时**退回复制**并记账 —— 宁可多占空间也要让镜像可用，
//   但绝不能默默算成功。
//
//   落点由清单里的 `destinationPath`（宏展开后）决定；**没有** `destinationPath` 的
//   `<file>`（驱动 `*.INF` 那一类）不在这里落 —— 那是 DMI 的活。
//
// 规则解析（宏、SDDL 引用、载荷路径）全部在原生侧一次性算好，本文件只做三件事：
// 调用 `ocid_manifest place-ops`、建硬链接、把 SDDL 交给原生库落到路径上。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>一条落位操作（来自原生 <c>ocid_manifest place-ops</c> 的 JSONL）。</summary>
    internal sealed class CsiPlacementOp
    {
        public string Component { get; init; } = "";
        public string Identity { get; init; } = "";
        public string ComponentDir { get; init; } = "";
        /// <summary><c>file</c> 或 <c>directory</c>。</summary>
        public string Kind { get; init; } = "";
        /// <summary>文件：清单里的 <c>name</c>。</summary>
        public string Name { get; init; } = "";
        public string StoreDir { get; init; } = "";
        public string StorePath { get; init; } = "";
        public string DestinationPath { get; init; } = "";
        public string PayloadPath { get; init; } = "";
        /// <summary>解析好的 SDDL 原文（空 = 这条没有安全描述符声明）。</summary>
        public string Sddl { get; init; } = "";
        /// <summary>清单里的 SD 引用名（SDDL 可能在别处定义，查不到时这里仍有名字，便于定位）。</summary>
        public string SdName { get; init; } = "";
        /// <summary>展开不了的宏（非空 ⇒ **不能执行**，写下去一定是错的）。</summary>
        public string Unresolved { get; init; } = "";
        /// <summary>清单里的 <c>writeableType</c> 原文（实测只有 <c>systemData</c> 一种值）。</summary>
        public string WriteableType { get; init; } = "";
        /// <summary>
        /// 声明了 <c>writeableType</c> ⇒ 落点是**独立副本**，不是硬链接。
        /// 实测依据：<c>P:\Windows\win.ini</c> 的 <c>fsutil hardlink list</c> 只有 1 条，
        /// 而同组件的 <c>basesrv.dll</c> 有 2 条（落点 + WinSxS 正本）。
        /// </summary>
        public bool Writeable { get; init; }
        /// <summary>
        /// 落点被架构红移改写过（非原生架构组件的 <c>System32</c> ⇒ <c>SysWOW64</c>）。
        /// 只是**记账**：路径本身已经由原生侧算好，这里照写即可。
        /// </summary>
        public bool Redirected { get; init; }
        /// <summary>目录：路径。</summary>
        public string Path { get; init; } = "";
        /// <summary>目录：属性文本（<c>readOnly hidden</c> 这种，空格分隔）。</summary>
        public string Attributes { get; init; } = "";
        /// <summary>目录：清单声明的 <c>owner</c>。</summary>
        public bool Owner { get; init; }

        public bool IsDirectory => string.Equals(Kind, "directory", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>落位结果统计。<see cref="Ok"/> 为 false 时**不要**当成功。</summary>
    internal sealed class CsiPlaceResult
    {
        public int Planned;
        /// <summary>载荷 → 正本的**复制**次数（真实镜像里正本与载荷是两份独立文件）。</summary>
        public int StoreCopied;
        public int DestLinked;
        public int AlreadyPresent;
        /// <summary>硬链接建不了、退回复制的次数（正常镜像应为 0）。</summary>
        public int Copied;
        /// <summary>新建的正本目录套上 WinSxS 安全策略（owner/group/DACL）的次数。</summary>
        public int StoreDirSecured;
        /// <summary>因 <c>writeableType</c> 而**故意**拷成独立副本的次数（正常镜像 = 声明了 writeableType 的文件数）。</summary>
        public int WriteableCopies;
        /// <summary>落点被架构红移（System32 ⇒ SysWOW64）的条数。</summary>
        public int Redirected;
        /// <summary>
        /// 目标被 TrustedInstaller 锁死、于是"取所有权 → 写入 → 还原安全描述符"的次数
        /// （见 <see cref="TrustedInstallerAccess"/>）。正常重装已有组件时会出现。
        /// </summary>
        public int OwnershipTaken;
        public int MissingPayload;
        public int Dirs;
        /// <summary>因未展开宏 / 无落点 / 载荷缺失而没执行。</summary>
        public int Skipped;
        public int AclApplied;
        public int AclSkipped;
        public int AclMismatched;
        public int AclFailed;
        public int Failed;
        public readonly List<string> Notes = new();

        public bool Ok => Failed == 0 && AclFailed == 0 && AclMismatched == 0;

        public string Summary()
            => $"计划 {Planned} 条；正本复制 {StoreCopied}（含目录策略 {StoreDirSecured}）、落点链接 {DestLinked}、已存在 {AlreadyPresent}" +
               $"、复制 {Copied}、writeable 副本 {WriteableCopies}、架构红移 {Redirected}" +
               $"、缺载荷 {MissingPayload}、跳过 {Skipped}、目录 {Dirs}、受保护取权 {OwnershipTaken}；" +
               $"ACL 写入 {AclApplied}、跳过 {AclSkipped}、失败 {AclFailed}、回读不一致 {AclMismatched}；错误 {Failed}";
    }

    /// <summary>一条 SDDL 落到路径上的结果（含回读比对）。</summary>
    internal readonly struct PathAclResult
    {
        public OcidServicingNative.SddlApplyOutcome Outcome { get; init; }
        /// <summary>回读比对三态：true=一致；false=**不一致**；null=没比（无 DACL 段或回读失败）。</summary>
        public bool? Verified { get; init; }
        public string Got { get; init; }
        public string Error { get; init; }
    }

    /// <summary>CSI：把组件清单声明的文件/目录落到离线镜像。</summary>
    internal static class CsiFileService
    {
        // ── 建硬链接 ─────────────────────────────────────────────────────────
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>
        /// 建硬链接；建不了就**退回复制**（返回是否走了复制）。
        /// 跨卷（`ERROR_NOT_SAME_DEVICE`）是硬链接最典型的失败原因，所以必须留着退路。
        /// </summary>
        private static bool LinkOrCopy(string linkPath, string targetPath, out string error)
        {
            error = "";
            if (CreateHardLinkW(linkPath, targetPath, IntPtr.Zero)) return false;

            int err = Marshal.GetLastWin32Error();
            try
            {
                File.Copy(targetPath, linkPath, overwrite: false);
                error = $"硬链接失败（Win32 {err}），已退回复制";
                return true;
            }
            catch (Exception ex)
            {
                error = $"硬链接失败（Win32 {err}），复制也失败：{ex.Message}";
                throw new IOException(error, ex);
            }
        }

        // ── 计划 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 让原生工具把组件清单算成落位计划。
        /// </summary>
        /// <param name="manifestsDir">
        /// 清单所在目录。真实镜像有两处：<c>&lt;image&gt;\packages</c>（5664 份**平铺**的
        /// <c>&lt;组件目录&gt;.manifest</c>）与 <c>&lt;image&gt;\Windows\WinSxS\Manifests</c>
        /// （118 份）。组件目录名一律取**文件名**去掉 <c>.manifest</c>；载荷根取
        /// <c>&lt;清单目录&gt;\&lt;组件目录&gt;\</c>，用 <c>packagesRoot</c> 可显式指定。
        /// </param>
        /// <param name="windowsDir">镜像的 <c>…\Windows</c>（宏展开的基准）。</param>
        public static List<CsiPlacementOp> BuildPlan(
            string manifestsDir, string windowsDir, string? packagesRoot = null,
            string? toolPath = null, Action<string>? log = null)
            => RunPlaceOps(windowsDir, packagesRoot, toolPath, log,
                           psi => psi.ArgumentList.Add(manifestsDir));

        /// <summary>
        /// 从一串**具体清单文件**算落位计划。真实用法：遍历
        /// <c>&lt;image&gt;\packages\*\*.manifest</c>（8061 ARM 镜像上是 5666 个）。
        /// </summary>
        /// <remarks>
        /// 走 <c>--list</c>（路径写进临时文件）而不是当命令行参数：5666 个路径会远超命令行长
        /// 上限。列表文件是 UTF-8 无 BOM，原生侧按 <c>ReadFileUtf8</c> 读。
        /// </remarks>
        public static List<CsiPlacementOp> BuildPlanFromList(
            IEnumerable<string> manifestPaths, string windowsDir, string? packagesRoot = null,
            string? toolPath = null, Action<string>? log = null)
        {
            string listFile = Path.Combine(Path.GetTempPath(),
                                           "ocid_csi_list_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllLines(listFile, manifestPaths,
                                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return RunPlaceOps(windowsDir, packagesRoot, toolPath, log, psi =>
                {
                    psi.ArgumentList.Add("--list");
                    psi.ArgumentList.Add(listFile);
                });
            }
            finally
            {
                try { File.Delete(listFile); } catch { /* 临时列表删不掉不影响结论 */ }
            }
        }

        private static List<CsiPlacementOp> RunPlaceOps(
            string windowsDir, string? packagesRoot, string? toolPath, Action<string>? log,
            Action<ProcessStartInfo> addTarget)
        {
            string tool = toolPath ?? OcidSmiService.FindManifestTool()
                ?? throw new FileNotFoundException(
                    "找不到 ocid_manifest.exe（OCID 服务栈控制台入口）。" +
                    "请先用 Source/Native/OcidServicing 构建，或用 toolPath 显式指定。");

            var psi = new ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("place-ops");
            psi.ArgumentList.Add("--windows");
            psi.ArgumentList.Add(windowsDir);
            if (!string.IsNullOrEmpty(packagesRoot))
            {
                psi.ArgumentList.Add("--packages");
                psi.ArgumentList.Add(packagesRoot);
            }
            addTarget(psi);

            var ops = new List<CsiPlacementOp>();
            using var p = Process.Start(psi)!;

            // stderr **必须并发读**：先把 stdout 读到 EOF 再去读 stderr 会死锁 ——
            // 子进程往 stderr 写满管道缓冲区（约 4 KB）后就卡住，而它卡住我们就永远读不到
            // stdout 的 EOF。实测在 40 份清单上就挂死了（工具会把每份清单的告警写 stderr）。
            Task<string> errTask = p.StandardError.ReadToEndAsync();

            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                var op = ParsePlaceOpJson(line);
                if (op != null) ops.Add(op);
            }
            string stderr = errTask.GetAwaiter().GetResult();
            p.WaitForExit();

            log?.Invoke($"[CSI] 原生工具诊断：{stderr.Trim()}");
            if (p.ExitCode != 0)
                log?.Invoke($"[CSI] ⚠ 工具退出码 {p.ExitCode}（有未展开的宏或查不到的 SD 引用 —— 这些条目不会被落位）");
            log?.Invoke($"[CSI] 落位计划 {ops.Count} 条");
            return ops;
        }

        /// <summary>
        /// 解析一行 place-ops JSON。字段固定，手写解析（与 SMI 那条线一致，不引 JSON 依赖）。
        /// 做成 internal：测试要拿**真实工具输出**钉住这条格式契约。
        /// </summary>
        internal static CsiPlacementOp? ParsePlaceOpJson(string line)
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

            var kind = Get("kind");
            if (string.IsNullOrEmpty(kind)) return null;

            return new CsiPlacementOp
            {
                Component = Get("component") ?? "",
                Identity = Get("identity") ?? "",
                ComponentDir = Get("componentDir") ?? "",
                Kind = kind!,
                Name = Get("name") ?? "",
                StoreDir = Get("storeDir") ?? "",
                StorePath = Get("storePath") ?? "",
                DestinationPath = Get("destinationPath") ?? "",
                PayloadPath = Get("payloadPath") ?? "",
                Sddl = Get("sddl") ?? "",
                SdName = Get("sdName") ?? "",
                Unresolved = Get("unresolved") ?? "",
                Path = Get("path") ?? "",
                Attributes = Get("attributes") ?? "",
                Owner = line.Contains("\"owner\":true"),
                WriteableType = Get("writeableType") ?? "",
                Writeable = line.Contains("\"writeable\":true"),
                Redirected = line.Contains("\"redirected\":true"),
            };
        }

        // ── 执行 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 执行落位计划。
        /// </summary>
        /// <param name="applyAcl">
        /// 是否把清单声明的 SDDL 落到落点上。关掉只做文件，用于"先看文件对不对"。
        /// </param>
        public static CsiPlaceResult Place(IEnumerable<CsiPlacementOp> ops,
                                           Action<string>? log = null,
                                           bool applyAcl = true)
        {
            var r = new CsiPlaceResult();

            // 新建出来的正本目录先攒着，**最后**再套安全策略。
            // 实测：一旦把 WinSxS 的 DACL 套到新目录上（owner 变成 TrustedInstaller、
            // BA 只剩读+执行），再往里 `File.Copy` 就是 Access denied —— 即使
            // SeRestorePrivilege 已启用（它能让我们在 WinSxS 下建目录，却写不进
            // 已套上该策略的子目录）。所以顺序必须是"先落文件、最后上锁"，
            // 与真实 CBS 的"组件暂存完再定 ACL"一致。
            var newStoreDirs = new List<string>();

            // ★ 真镜像上的第一个拦路虎：`Windows\WinSxS`（以及 `System32`）的所有者是
            //   **NT SERVICE\TrustedInstaller**，DACL 里只有它拿 Full Control，
            //   `SY`/`BA`/`BU`/`AC` 全都只有 `0x1200a9`（读+执行+同步，**没有**建子目录的
            //   `FILE_ADD_SUBDIRECTORY`）。所以即使以管理员身份跑，`CreateDirectory`
            //   也会直接 `Access to the path … is denied`。
            //   离线服务栈（Setup/CBS）靠的是 SeRestorePrivilege：**启用它会绕过写侧 DACL 检查**
            //   （SeBackupPrivilege 对应读侧）。管理员令牌里这几个特权默认是**禁用**的。
            PrivilegeHelper.EnableServicingPrivileges(log);

            foreach (var op in ops)
            {
                r.Planned++;

                // 未展开的宏 ⇒ **不执行**。静默写下去会得到一个"看起来成功"的错误路径，
                // 这种故障在镜像里几个月都发现不了。
                if (!string.IsNullOrEmpty(op.Unresolved))
                {
                    r.Skipped++;
                    r.Notes.Add($"[跳过] {op.Component}: 含未展开的宏 {op.Unresolved} " +
                                $"（{(op.IsDirectory ? op.Path : op.DestinationPath)}）");
                    continue;
                }

                if (op.IsDirectory) PlaceDirectory(op, r, log, applyAcl);
                else PlaceFile(op, r, log, applyAcl, newStoreDirs);
            }

            // 最后统一给新建的正本目录套 WinSxS 的安全策略（见上面 newStoreDirs 的说明）
            foreach (var dir in newStoreDirs) SecureNewStoreDir(dir, r, log);

            log?.Invoke($"[CSI] {r.Summary()}");
            foreach (var n in r.Notes) log?.Invoke("[CSI] " + n);
            return r;
        }

        /// <summary>
        /// 给**新建的**正本目录套上组件存储的安全策略：直接照搬 <c>WinSxS</c> 目录的
        /// owner / group / DACL（含 <c>P</c> 保护标志）。
        /// </summary>
        /// <remarks>
        /// 为什么必须做：实测真实组件目录是
        /// <c>O:TI G:TI D:PAI(A;OICIIO;GXGR;;;SY)(A;;0x1200a9;;;SY)…</c>（**保护**、非继承），
        /// 而 `Directory.CreateDirectory` 建出来的是 <c>O:BA G:&lt;当前用户&gt; D:AI(…)</c> ——
        /// DACL 只是继承来的、owner/group 也不对。owner 不对意味着这个目录此后
        /// **谁都改不动**（连 Administrators 都没写权），下次服务这个组件就会再次撞墙。
        /// 设 owner 到别的账户需要 SeRestorePrivilege（<see cref="Place"/> 已启用）。
        /// </remarks>
        private static void SecureNewStoreDir(string newDir, CsiPlaceResult r, Action<string>? log)
        {
            try
            {
                string? parent = System.IO.Path.GetDirectoryName(newDir);
                if (string.IsNullOrEmpty(parent)) return;

                const uint bits = OcidServicingNative.SecInfo.Owner
                                | OcidServicingNative.SecInfo.Group
                                | OcidServicingNative.SecInfo.Dacl;
                if (!OcidServicingNative.TryReadPathSddl(parent, bits, out string sddl, out string rerr))
                {
                    r.Notes.Add($"[注意] 读不到 {parent} 的安全描述符，新目录沿用继承：{rerr}");
                    return;
                }
                var res = ApplyAndVerifyPathSddl(newDir, sddl);
                if (res.Outcome == OcidServicingNative.SddlApplyOutcome.Applied)
                {
                    r.StoreDirSecured++;
                    log?.Invoke($"[CSI] 正本目录 套用 {System.IO.Path.GetFileName(parent)} 的安全策略：" +
                                $"{newDir}（owner/group/DACL）");
                }
                else
                {
                    r.Notes.Add($"[注意] 新正本目录 {newDir} 套安全策略 {res.Outcome}：{res.Error}");
                }
            }
            catch (Exception ex)
            {
                r.Notes.Add($"[注意] 新正本目录 {newDir} 套安全策略异常：{ex.Message}");
            }
        }

        private static void PlaceFile(CsiPlacementOp op, CsiPlaceResult r, Action<string>? log,
                                      bool applyAcl, List<string> newStoreDirs)
        {
            if (string.IsNullOrEmpty(op.DestinationPath))
            {
                r.Skipped++;
                r.Notes.Add($"[跳过] {op.Component}: {op.Name} 清单里没有 destinationPath（驱动类，归 DMI）");
                return;
            }

            // 记账：这条落点是被架构红移改写过的（原生侧已算好，这里只是让日志说得清）
            if (op.Redirected) r.Redirected++;

            // ① 正本：packages 里的载荷 → WinSxS\<组件目录>\<name>
            //    **必须是复制，不能硬链接**：实测真实镜像里载荷的 hardlink list 只有 1 条
            //    （basesrv.dll / cdfs.sys / kdcom.dll 都是），即 CBS 把载荷**拷**进组件存储。
            //    链过去的话，`packages\` 一旦被清理或改写，镜像正本跟着坏 —— 而 packages 是
            //    构建产物，随时可能被重新展开覆盖。
            string storeParent = System.IO.Path.GetDirectoryName(op.StorePath) ?? "";
            string destParent = System.IO.Path.GetDirectoryName(op.DestinationPath) ?? "";
            bool needStore = !File.Exists(op.StorePath);
            bool needPlace = !File.Exists(op.DestinationPath);

            if (needStore && !File.Exists(op.PayloadPath))
            {
                r.MissingPayload++;
                r.Notes.Add($"[缺载荷] {op.Component}: {op.PayloadPath} 不存在（包没展开？）");
                return;
            }

            if (needStore && storeParent.Length > 0 && !Directory.Exists(storeParent))
            {
                Directory.CreateDirectory(storeParent);
                // ★ 关键顺序（实测踩出来的）：新建目录从 WinSxS 继承到的 DACL 是
                //   `(A;ID;0x1200a9;;;BA)` —— 只有读+执行，**没有**建文件的权。
                //   实测：即使启用了 SeRestorePrivilege（它在 WinSxS 下能建目录），
                //   往这个继承了"只读"策略的新目录里 `File.Copy` 依旧 Access denied。
                //   所以先给它一条**显式**的、可写且能被子对象继承的 DACL，拷完文件
                //   再在收尾时换成 WinSxS 的真策略（见下面的 SecureNewStoreDir）。
                var tmp = ApplyAndVerifyPathSddl(storeParent,
                                                TrustedInstallerAccess.TemporaryWriteSddl);
                if (tmp.Outcome != OcidServicingNative.SddlApplyOutcome.Applied)
                    r.Notes.Add($"[注意] 新正本目录 {storeParent} 没能拿到可写 DACL：{tmp.Error}");
                newStoreDirs.Add(storeParent);
            }

            if (!needStore && !needPlace)
            {
                r.AlreadyPresent++;
            }
            else
            {
                bool storeTook = false, placeTook = false;
                string storeErr = "", placeErr = "";
                string step = "落位";

                void CopyStore()
                {
                    step = "建正本";
                    File.Copy(op.PayloadPath, op.StorePath, overwrite: false);
                    r.StoreCopied++;
                }

                // ② 落点：正本 → 目标位置
                //    默认硬链接（同一个文件）；`writeableType` 声明的文件**必须拷贝** ——
                //    实测：win.ini / hosts 这类"运行时可写的系统数据"在真实镜像里是独立副本
                //    （hardlink list 只有 1 条），链到 WinSxS 会让任何一次写入都污染正本，
                //    而正本是所有组件的共享底本。
                void PlaceDest()
                {
                    step = "建落点";
                    if (op.Writeable)
                    {
                        File.Copy(op.StorePath, op.DestinationPath, overwrite: false);
                        r.WriteableCopies++;
                        r.Notes.Add($"[writeable] {op.Component}: {op.Name} " +
                                    $"writeableType={op.WriteableType} ⇒ 拷成独立副本（不链正本）");
                    }
                    else
                    {
                        bool copied = LinkOrCopy(op.DestinationPath, op.StorePath, out string note);
                        if (copied) r.Copied++; else r.DestLinked++;
                        if (note.Length > 0) r.Notes.Add($"[落点] {op.DestinationPath}: {note}");
                    }
                }

                void DoPlaceNow()
                {
                    if (destParent.Length > 0) Directory.CreateDirectory(destParent);
                    PlaceDest();
                }

                // 落点目录（System32 / SysWOW64\Dism / drivers…）可能另归一块受保护地盘：
                // 在正本窗口里**再套一层**落点窗口（内层先还原、外层后还原）。
                void PlaceWithWindow()
                {
                    if (destParent.Length == 0) { DoPlaceNow(); return; }
                    if (!TrustedInstallerAccess.ForWrite(destParent, DoPlaceNow,
                                                         out placeTook, out placeErr, log))
                        throw new IOException("落点目录受保护，取所有权后仍写不进：" + placeErr);
                }

                // ★ 复制正本与建落点必须待在**同一个窗口**里做完（实测）：
                //   对受保护目录还原本来的安全描述符时，系统会把目录里子对象的**继承** ACE
                //   重算一遍；窗口期刚写好的正本会被重算成只剩 TI 能访问，之后再拿它当源
                //   建硬链接，`CreateHardLinkW` 会以"拒绝访问"失败。正本窗口因此包住
                //   "复制正本 + 建落点"两件事（详见 TrustedInstallerAccess.ForWrite）。
                void DoWork()
                {
                    if (needStore) CopyStore();
                    if (needPlace) PlaceWithWindow();
                }

                try
                {
                    if (storeParent.Length > 0)
                    {
                        // 正本目录**已存在**时它可能早就被套上了 WinSxS 策略（owner=TI、
                        // BA 只剩读+执行），这时直接写会被拒。统一入口：先直接试；只有权限
                        // 不足才取所有权、写完**还原**。
                        if (!TrustedInstallerAccess.ForWrite(storeParent, DoWork,
                                                             out storeTook, out storeErr, log))
                            throw new IOException("正本目录受保护，取所有权后仍写不进：" + storeErr);
                    }
                    else
                    {
                        DoWork();
                    }

                    if (storeTook) r.OwnershipTaken++;
                    if (placeTook) r.OwnershipTaken++;
                }
                catch (Exception ex)
                {
                    r.Failed++;
                    r.Notes.Add($"[失败] {step} {op.StorePath} → {op.DestinationPath}: {ex.Message}");
                    return;
                }
            }

            // ③ 安全描述符落到**落点**上（清单声明的就是这个位置的 ACL）
            if (applyAcl && op.Sddl.Length > 0)
            {
                var acl = ApplyAclWithAccess(op.DestinationPath, op.Sddl, r, log);
                Count(r, acl, op.DestinationPath, log);
            }
        }

        private static void PlaceDirectory(CsiPlacementOp op, CsiPlaceResult r, Action<string>? log, bool applyAcl)
        {
            if (string.IsNullOrEmpty(op.Path))
            {
                r.Skipped++;
                return;
            }

            if (!Directory.Exists(op.Path))
            {
                try { Directory.CreateDirectory(op.Path); }
                catch (Exception ex)
                {
                    r.Failed++;
                    r.Notes.Add($"[失败] 建目录 {op.Path}: {ex.Message}");
                    return;
                }
            }
            r.Dirs++;

            ApplyDeclaredAttributes(op, r);

            if (applyAcl && op.Sddl.Length > 0)
            {
                var acl = ApplyAclWithAccess(op.Path, op.Sddl, r, log);
                Count(r, acl, op.Path, log);
            }
        }

        /// <summary>
        /// 目录属性（清单形如 <c>attributes="readOnly hidden"</c>）。
        /// **只置位、不清位**：清单没提的属性不属于我们的处置范围，顺手清掉会破坏镜像。
        /// </summary>
        private static void ApplyDeclaredAttributes(CsiPlacementOp op, CsiPlaceResult r)
        {
            if (string.IsNullOrWhiteSpace(op.Attributes)) return;

            FileAttributes want = 0;
            bool any = false;
            foreach (var tok in op.Attributes.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                FileAttributes f = tok.ToLowerInvariant() switch
                {
                    "readonly" => FileAttributes.ReadOnly,
                    "hidden" => FileAttributes.Hidden,
                    "system" => FileAttributes.System,
                    "archive" => FileAttributes.Archive,
                    "notcontentindexed" => FileAttributes.NotContentIndexed,
                    "temporary" => FileAttributes.Temporary,
                    "normal" => FileAttributes.Normal,
                    _ => (FileAttributes)0,
                };
                if (f == 0)
                {
                    r.Notes.Add($"[属性] 不认识的属性 “{tok}”（{op.Path}）");
                    continue;
                }
                want |= f;
                any = true;
            }
            if (!any) return;

            try
            {
                var cur = File.GetAttributes(op.Path);
                var merged = cur | want;
                if (merged != cur) File.SetAttributes(op.Path, merged);
            }
            catch (Exception ex)
            {
                r.Notes.Add($"[属性] 设置 {op.Path} 失败：{ex.Message}");
            }
        }

        private static void Count(CsiPlaceResult r, PathAclResult acl, string path, Action<string>? log)
        {
            switch (acl.Outcome)
            {
                case OcidServicingNative.SddlApplyOutcome.SkippedInherit:
                    r.AclSkipped++;   // (inherit) —— 合法，不是失败
                    break;
                case OcidServicingNative.SddlApplyOutcome.Applied:
                    r.AclApplied++;
                    if (acl.Verified == false)
                    {
                        r.AclMismatched++;
                        log?.Invoke($"[CSI]   ✗ ACL 回读**不一致**：{path} — 回读 {acl.Got}" +
                                    (acl.Error.Length > 0 ? $"／原因：{acl.Error}" : ""));
                    }
                    else if (acl.Verified == null && acl.Error.Length > 0)
                    {
                        r.Notes.Add($"[ACL] {path}: 已写入但无法确认 — {acl.Error}");
                    }
                    break;
                default:
                    r.AclFailed++;
                    log?.Invoke($"[CSI]   ✗ ACL 写入失败：{path} — {acl.Error}");
                    r.Notes.Add($"[ACL] {path}: {acl.Error}");
                    break;
            }
        }

        /// <summary>
        /// 落 ACL 的**统一入口**：先直接落；只有**权限不足**才取所有权再落。
        ///
        /// 【为什么需要】清单声明的 SDDL 要落到**已存在**的受保护对象上时（重装已有组件、
        /// 落点归 TrustedInstaller），BA 没有 WRITE_DAC，直接写会被拒。这种情况取一次所有权
        /// （owner ⇒ BA）就能落 —— 而落进去的**就是清单声明的目标状态**，所以**不还原**
        /// （与 <see cref="TrustedInstallerAccess.TryWriteInto"/> 的"写完还原"不同）。
        /// </summary>
        private static PathAclResult ApplyAclWithAccess(string path, string sddl, CsiPlaceResult r,
                                                        Action<string>? log)
        {
            var acl = ApplyAndVerifyPathSddl(path, sddl);
            if (acl.Outcome != OcidServicingNative.SddlApplyOutcome.Failed) return acl;

            var again = TrustedInstallerAccess.ApplyDeclaredSddl(path, sddl, out bool took, out string err, log);
            if (again == OcidServicingNative.SddlApplyOutcome.Applied)
            {
                if (took) r.OwnershipTaken++;
                return ApplyAndVerifyPathSddl(path, sddl);
            }
            return new PathAclResult { Outcome = again, Error = err.Length > 0 ? err : acl.Error };
        }

        /// <summary>
        /// 给路径写 SDDL 并**立刻回读比对**。与键那条线（<c>OcidSmiService.ApplyAndVerifyKeySddl</c>）
        /// 同一套判断，只是 API 换成 <c>SetNamedSecurityInfoW</c>。
        /// </summary>
        /// <remarks>
        /// 回读只做**结构级**比对：SID 的写法会归一化（别名 <c>BA</c> 回读成
        /// <c>S-1-5-32-544</c>），逐字符比必然假失败。非保护 DACL 时父目录的可继承 ACE
        /// 会被并进来，回读可能**多于**写入，那种情况只要求"不少于"。
        /// </remarks>
        internal static PathAclResult ApplyAndVerifyPathSddl(string path, string sddl)
        {
            var outcome = OcidServicingNative.ApplyPathSddl(path, sddl, out string err);
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
                else if (!OcidServicingNative.TryReadPathSddl(path, OcidServicingNative.SecInfo.Dacl,
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

            return new PathAclResult { Outcome = outcome, Verified = verified, Got = got, Error = note };
        }
    }
}
