using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>DISM 操作的目标类型。</summary>
    public enum DismTargetKind
    {
        /// <summary>离线镜像（挂载点 / VHD 盘根）。**这是本程序推荐的方式。**</summary>
        OfflineImage,
        /// <summary>当前正在运行的系统。危险 —— 会改动活着的 OS。</summary>
        Online,
    }

    /// <summary>
    /// 一次 DISM 操作的目标。
    ///
    /// 【为什么要有这个抽象】DISM 的绝大多数子命令在"离线镜像"与"在线系统"下**参数形式不同**：
    ///   离线：dism /image:&lt;目录&gt; /&lt;操作&gt;
    ///   在线：dism /&lt;操作&gt;              ← 没有 /image
    /// 以前这两者散在各处（离线写死 /image:，在线只能手工拼字符串），
    /// 既容易拼错，也无法在架构上把"危险操作"挡住。
    /// 把"目标"抽出来之后，所有操作只写一次，参数由目标决定；
    /// 在线目标另加一道确认闸（见 <see cref="DismOperations.OnlineGuard"/>）。
    /// </summary>
    public sealed class DismTarget
    {
        public DismTargetKind Kind { get; init; }

        /// <summary>离线：镜像根（形如 X:\ 或 X:\mount）。在线：忽略。</summary>
        public string ImageRoot { get; init; } = "";

        public bool IsOnline => Kind == DismTargetKind.Online;

        /// <summary>该目标的 Windows 目录（用于读 hive、找栈等）。</summary>
        public string WindowsDir => IsOnline
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : Path.Combine(Path.GetFullPath(ImageRoot), "Windows");

        /// <summary>
        /// 镜像根，已去掉结尾反斜杠。
        /// ⚠ 必须去 —— 否则拼出 <c>/image:"H:\"</c>，结尾的反斜杠会把引号转义掉，
        /// dism 直接报 1639 ERROR_INVALID_COMMAND_LINE。这是实测踩到的坑。
        /// </summary>
        public string CleanRoot
        {
            get
            {
                string p = Path.GetFullPath(ImageRoot);
                while (p.Length > 3 && (p.EndsWith("\\") || p.EndsWith("/"))) p = p[..^1];
                // "C:\" 保留成 "C:"（dism 接受这种写法）
                return p.Length == 3 && p[1] == ':' ? p[..2] : p;
            }
        }

        /// <summary>
        /// 拼进命令行的目标参数；在线为空串。
        ///
        /// ⚠ 这里的引号处理是实测踩出来的：
        ///   · 加引号且路径以反斜杠结尾 → <c>/image:"H:\"</c>，结尾反斜杠转义了引号，
        ///     dism 报 1639 ERROR_INVALID_COMMAND_LINE；
        ///   · 干脆去掉反斜杠变成 <c>/image:"H:"</c> → dism 报 161 ERROR_BAD_PATHNAME；
        ///   · 正确做法：**无空格就不加引号**（<c>/image:H:\</c>，dism 接受），
        ///     有空格时才加引号，并在加引号时去掉结尾反斜杠以免转义。
        /// </summary>
        public string ArgPrefix
        {
            get
            {
                if (IsOnline) return "";
                string p = Path.GetFullPath(ImageRoot);
                if (!p.Contains(' ')) return $"/image:{p} ";
                while (p.Length > 3 && (p.EndsWith("\\") || p.EndsWith("/"))) p = p[..^1];
                return $"/image:\"{p}\" ";
            }
        }

        /// <summary>供 <see cref="DismServicingService.GetPackages"/> 这类按路径工作的老接口使用。</summary>
        public string PathOrWinDir => IsOnline ? WindowsDir : Path.GetFullPath(ImageRoot);

        public string Describe() => IsOnline
            ? "在线系统（当前正在运行的 Windows）"
            : "离线镜像 " + Path.GetFullPath(ImageRoot);

        public static DismTarget Offline(string imageRoot) =>
            new() { Kind = DismTargetKind.OfflineImage, ImageRoot = imageRoot };

        public static DismTarget OnlineSystem() => new() { Kind = DismTargetKind.Online };
    }

    /// <summary>
    /// 离线 / 在线 DISM 操作。
    ///
    /// 与 <see cref="DismServicingService"/> 的分工：后者是"能跑 dism 并解析结果"的底层；
    /// 这里在其之上给出**统一的操作面**，并用 <see cref="DismTarget"/> 抹平离线/在线的差异。
    ///
    /// 【安全策略】本程序的一贯立场是：会写镜像的 CBS/DISM 操作必须作用在
    /// **挂载的 VHD/VHDX** 上。在线目标因此默认被拒 —— 必须显式传
    /// <c>allowOnline: true</c> 且用户已确认，且只允许"读"类操作无条件执行。
    /// </summary>
    public static class DismOperations
    {
        // ════════════════════════════════════════════════════════════════
        //  安全闸
        // ════════════════════════════════════════════════════════════════

        /// <summary>不需要额外确认的只读操作（黑名单式：未列出的写操作都要确认）。</summary>
        static readonly HashSet<string> ReadOnlyOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "get-packages", "get-features", "get-drivers", "get-imageinfo",
            "get-currentedition", "get-targeteditions", "get-intl",
            "analyzecomponentstore", "get-mountedimage", "get-appinfo",
        };

        /// <summary>
        /// 判断一次操作是否被允许执行。
        /// 返回 (允许, 拒绝理由)。理由为空表示允许。
        /// </summary>
        public static (bool allowed, string reason) OnlineGuard(DismTarget target, string operation, bool allowOnline)
        {
            if (!target.IsOnline) return (true, "");

            string op = operation.TrimStart('/').ToLowerInvariant();
            if (ReadOnlyOps.Contains(op)) return (true, "");

            if (!allowOnline)
                return (false,
                    $"拒绝对【在线系统】执行写操作 /{op}。\n" +
                    "本程序的一贯要求是：会写镜像的服务操作必须作用在【挂载的 VHD/VHDX】上。\n" +
                    "若确要在当前运行的系统上执行，需显式传入 allowOnline 并完成确认 —— " +
                    "该操作会改动正在运行的操作系统，且无法回退。");

            return (true, "");
        }

        // ════════════════════════════════════════════════════════════════
        //  通用执行
        // ════════════════════════════════════════════════════════════════

        /// <summary>在指定目标上执行一条 dism 参数串（不含 dism.exe 本身与 /image:）。</summary>
        public static DismServicingService.Result Exec(
            DismTarget target, string arguments, bool allowOnline = false, int timeoutMs = 60 * 60 * 1000)
        {
            // 取出操作名用于闸门判断（第一个以 / 开头的词，忽略 /English 之类的开关）
            string op = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                 .FirstOrDefault(a => a.StartsWith("/") &&
                                     !a.Equals("/English", StringComparison.OrdinalIgnoreCase))
                          ?? "";
            var (allowed, reason) = OnlineGuard(target, op, allowOnline);
            if (!allowed) return new DismServicingService.Result { Success = false, Error = reason };

            // 离线镜像的盘根/挂载点必须是真实存在的目录
            if (!target.IsOnline && !Directory.Exists(target.ImageRoot))
                return new DismServicingService.Result
                {
                    Success = false,
                    Error = "离线镜像根不存在: " + target.ImageRoot
                };

            return DismServicingService.Run(target.ArgPrefix + arguments, timeoutMs);
        }

        // ════════════════════════════════════════════════════════════════
        //  映像文件级（不依赖 target，作用于 WIM/VHD 文件）
        // ════════════════════════════════════════════════════════════════

        public static DismServicingService.Result GetImageInfo(string wimPath) =>
            DismServicingService.Run($"/English /get-imageinfo /imagefile:\"{wimPath}\"");

        public static DismServicingService.Result GetWimInfo(string wimPath) =>
            DismServicingService.Run($"/English /get-wiminfo /wimfile:\"{wimPath}\"");

        public static DismServicingService.Result ApplyImage(string wimPath, int index, string applyDir) =>
            DismServicingService.Run($"/English /apply-image /imagefile:\"{wimPath}\" /index:{index} /applydir:\"{applyDir}\"");

        public static DismServicingService.Result CaptureImage(string wimPath, string captureDir,
            string name = "Windows", string compress = "max") =>
            DismServicingService.Run($"/English /capture-image /imagefile:\"{wimPath}\" /capturedir:\"{captureDir}\" /name:{name} /compress:{compress}");

        /// <summary>挂载 WIM 到目录（读写）。挂载点必须已存在且为空。</summary>
        public static DismServicingService.Result MountImage(string wimPath, int index, string mountDir,
            bool readOnly = false) =>
            DismServicingService.Run($"/English /mount-image /imagefile:\"{wimPath}\" /index:{index} /mountdir:\"{mountDir}\""
                                     + (readOnly ? " /readonly" : ""));

        /// <summary>卸载挂载的 WIM。commit=true 保存改动，false 丢弃。</summary>
        public static DismServicingService.Result UnmountImage(string mountDir, bool commit) =>
            DismServicingService.Run($"/English /unmount-image /mountdir:\"{mountDir}\""
                                     + (commit ? " /commit" : " /discard"));

        /// <summary>列出已挂载的映像（用于发现泄漏的挂载 —— 挂载泄漏会让后续操作全部失败）。</summary>
        public static DismServicingService.Result GetMountedImages() =>
            DismServicingService.Run("/English /get-mountedimage");

        /// <summary>丢弃所有已挂载映像的改动并卸载（清理泄漏挂载）。</summary>
        public static DismServicingService.Result CleanupMounts() =>
            DismServicingService.Run("/English /cleanup-wim");

        // ════════════════════════════════════════════════════════════════
        //  包
        // ════════════════════════════════════════════════════════════════

        public static (DismServicingService.Result run, List<DismServicingService.PackageInfo> packages)
            GetPackages(DismTarget target) => DismServicingService.GetPackages(target.PathOrWinDir);

        public static DismServicingService.Result AddPackage(DismTarget target, string packagePath,
            string? scratchDir = null, bool ignoreCheck = false, bool allowOnline = false)
        {
            string args = $"/English /add-package /packagepath:\"{packagePath}\""
                        + (string.IsNullOrWhiteSpace(scratchDir) ? "" : $" /scratchdir:\"{scratchDir}\"")
                        + (ignoreCheck ? " /ignorecheck" : "");
            return Exec(target, args, allowOnline);
        }

        public static DismServicingService.Result RemovePackage(DismTarget target, string packageName,
            bool removePayload = false, bool allowOnline = false)
        {
            string args = $"/English /remove-package /packagename:{packageName}"
                        + (removePayload ? " /remove" : "");
            return Exec(target, args, allowOnline);
        }

        // ════════════════════════════════════════════════════════════════
        //  功能（feature）
        // ════════════════════════════════════════════════════════════════

        public static DismServicingService.Result GetFeatures(DismTarget target) =>
            Exec(target, "/English /get-features /format:table");

        public static DismServicingService.Result EnableFeature(DismTarget target, string featureName,
            string? source = null, bool all = false, bool allowOnline = false)
        {
            string args = $"/English /enable-feature /featurename:{featureName}"
                        + (all ? " /all" : "")
                        + (string.IsNullOrWhiteSpace(source) ? "" : $" /source:\"{source}\"");
            return Exec(target, args, allowOnline);
        }

        public static DismServicingService.Result DisableFeature(DismTarget target, string featureName,
            bool remove = false, bool allowOnline = false)
        {
            string args = $"/English /disable-feature /featurename:{featureName}" + (remove ? " /remove" : "");
            return Exec(target, args, allowOnline);
        }

        // ════════════════════════════════════════════════════════════════
        //  版本（edition）
        // ════════════════════════════════════════════════════════════════

        public static DismServicingService.Result GetCurrentEdition(DismTarget target) =>
            Exec(target, "/English /get-currentedition");

        public static DismServicingService.Result GetTargetEditions(DismTarget target) =>
            Exec(target, "/English /get-targeteditions");

        public static DismServicingService.Result SetEdition(DismTarget target, string edition,
            string? key = null, bool allowOnline = false)
        {
            string args = $"/English /set-edition /edition:{edition}"
                        + (string.IsNullOrWhiteSpace(key) ? "" : $" /productkey:{key}");
            return Exec(target, args, allowOnline);
        }

        // ════════════════════════════════════════════════════════════════
        //  杂项维护
        // ════════════════════════════════════════════════════════════════

        public static DismServicingService.Result GetIntl(DismTarget target) =>
            Exec(target, "/English /get-intl");

        public static DismServicingService.Result SetAllIntl(DismTarget target, string lang, bool allowOnline = false) =>
            Exec(target, $"/English /set-allintl /locale:{lang}", allowOnline);

        public static DismServicingService.Result ApplyUnattend(DismTarget target, string unattendXml, bool allowOnline = false) =>
            Exec(target, $"/English /apply-unattend:\"{unattendXml}\"", allowOnline);

        public static DismServicingService.Result AnalyzeComponentStore(DismTarget target) =>
            Exec(target, "/English /cleanup-image /analyzecomponentstore");

        public static DismServicingService.Result StartComponentCleanup(DismTarget target,
            bool resetBase = false, bool allowOnline = false) =>
            Exec(target, "/English /cleanup-image /startcomponentcleanup" + (resetBase ? " /resetbase" : ""), allowOnline);

        public static DismServicingService.Result RevertPendingActions(DismTarget target, bool allowOnline = false) =>
            Exec(target, "/English /cleanup-image /revertpendingactions", allowOnline);

        // ════════════════════════════════════════════════════════════════
        //  呈现
        // ════════════════════════════════════════════════════════════════

        /// <summary>把一个结果渲染成可读块（含退出码与"是否被闸门拒绝"的区分）。</summary>
        public static string Render(DismTarget target, string operation, DismServicingService.Result r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"目标: {target.Describe()}");
            sb.AppendLine($"操作: {operation}");
            if (r.Error.Length > 0) sb.AppendLine($"错误: {r.Error}");
            sb.AppendLine($"退出码: {r.ExitCode}  结果: {(r.Success ? "成功" : "失败")}");
            if (r.Output.Length > 0) { sb.AppendLine(); sb.AppendLine(r.Output.TrimEnd()); }
            return sb.ToString().TrimEnd();
        }

        /// <summary>列出本程序支持的离线/在线操作，供界面与文档共用。</summary>
        public static string DescribeCapabilities()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "映像文件级（离线/在线通用）",
                "  get-wiminfo      get-imageinfo     apply-image       capture-image",
                "  mount-image      unmount-image     get-mountedimage  cleanup-wim",
                "包",
                "  get-packages     add-package       remove-package",
                "功能",
                "  get-features     enable-feature    disable-feature",
                "版本（edition）",
                "  get-currentedition   get-targeteditions   set-edition",
                "语言与维护",
                "  get-intl         set-allintl       apply-unattend",
                "  analyzecomponentstore   startcomponentcleanup   revertpendingactions",
                "",
                "离线目标：dism /image:<目录> /<操作>      ← 推荐（本程序的一贯要求）",
                "在线目标：dism /<操作>                   ← 会改动正在运行的系统，写操作需显式放行",
            });
        }
    }
}
