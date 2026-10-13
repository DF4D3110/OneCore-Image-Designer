using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// explodeSxS 的**纯 C# 编排**（不依赖 cbsx.exe / sxsfounder.exe）。
    ///
    /// 【与旧路线的关系】
    ///   旧路线（已删除）：`ExplodeSxSService` 拼 PowerShell 序列，把 cbsx.exe / sxsfounder.exe
    ///   拷到工作盘再运行 —— 受 GPL 二进制与来源问题牵制，且依赖工作盘（会格式化 X:）。
    ///   本服务：全部走已实现的 C# 服务，无外部 exe，也不需要工作盘。
    ///
    /// 对照 cbss.cmd 的步骤与替代实现：
    ///   prepare.cmd      format X: + 建目录 + 拷 exe + xcopy servicing stack
    ///                    → 不再需要；镜像在挂载的 VHD 上，服务栈直接从镜像内加载
    ///   sxsfounder       X:\desktopimaging\sxsfounder.exe X:\image amd64
    ///                    → <see cref="SxSFounderService.Create"/>
    ///   cbsx /b:skuassembly.txt
    ///                    → <see cref="SkuAssemblyService.Parse"/> + <see cref="CbsxService.ApplyPlan"/>
    ///   dism /add-package 语言包
    ///                    → <see cref="DismServicingService.AddPackage"/>
    ///   dism /apply-unattend
    ///                    → <see cref="DismServicingService.ApplyUnattend"/>
    ///   dism /get-currentedition /get-targeteditions
    ///                    → <see cref="DismServicingService.GetCurrentEdition"/> / GetTargetEditions
    ///   dism /set-allintl
    ///                    → <see cref="DismServicingService.SetAllIntl"/>
    ///   dism /cleanup-image
    ///                    → <see cref="DismServicingService.AnalyzeComponentStore"/> / StartComponentCleanup
    ///   finishimg.cmd    State.ini + REG LOAD/IMPORT imagestate.reg + UNLOAD
    ///                    → <see cref="FinishImageStep"/>
    ///   captureimg.cmd   dism /capture-image
    ///                    → <see cref="DismServicingService.CaptureImage"/>
    ///
    /// 【强制约束】目标必须是挂载的 VHD/VHDX（CbsSafety 门禁），且每一步都要能干净收尾。
    /// </summary>
    public static class ExplodeSxSNativeService
    {
        public sealed class Config
        {
            /// <summary>离线镜像的 Windows 目录，例：G:\Windows（必须位于挂载的 VHD/VHDX 上）。</summary>
            public string WinDir = "";
            /// <summary>镜像根（默认由 WinDir 推导）。</summary>
            public string ImageRoot = "";
            /// <summary>架构：x86 / arm / amd64 / arm64。</summary>
            public string Arch = "amd64";
            /// <summary>skuassembly.txt 路径（可空 = 跳过 SKU 步骤）。</summary>
            public string SkuAssemblyFile = "";
            /// <summary>语言包 mum/cab（可空 = 跳过）。</summary>
            public string LanguagePackMum = "";
            /// <summary>edition 用的 unattend xml（可空 = 跳过）。</summary>
            public string UnattendXml = "";
            /// <summary>语言，例 en-US。</summary>
            public string Lang = "en-US";
            /// <summary>DISM 暂存目录（可空）。</summary>
            public string ScratchDir = "";
            /// <summary>日志目录（可空 = 不写文件）。</summary>
            public string LogDir = "";
            /// <summary>末尾是否 capture 成 wim。</summary>
            public bool CaptureWim;
            public string WimPath = "";
            public string WimName = "Windows";
            /// <summary>只预检不执行。</summary>
            public bool DryRun;

            /// <summary>
            /// 跳过铺底（WCP）步骤。**目标镜像已经存在时必须跳过**：
            /// 铺底是为"从零造一个镜像骨架"设计的，对已有镜像既无必要，
            /// 而且实测在已有镜像上会以 CreateNewOfflineStore 0x8007051A（权限不足）失败。
            /// </summary>
            public bool SkipFounder;

            /// <summary>
            /// servicing stack 来源目录。**非空时会执行"把服务栈装进镜像"这一步** ——
            /// 这是"从头组装"的关键：铺底只造空骨架（没有 WinSxS / 组件存储 / 服务栈），
            /// CBS 在其上起不来，必须先把服务栈与同版本 mum 放进去。
            /// </summary>
            public List<string> StackSourceDirs = new();

            /// <summary>
            /// 最小系统文件集的来源 Windows 目录（例 C:\rc_base\Windows）。
            /// 非空时会在装完服务栈后补入 CBS 启动所需的几个系统 DLL。
            /// </summary>
            public string CoreFileSourceWinDir = "";
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public bool RebootRequired;
            public readonly List<string> Log = new();
            public readonly List<(string Step, bool Ok, string Detail)> Steps = new();
        }

        public static string DeriveImageRoot(string winDir)
        {
            string full = Path.GetFullPath(winDir).TrimEnd('\\');
            if (Path.GetFileName(full).Equals("Windows", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(full)?.FullName ?? full;
            return full;
        }

        /// <summary>预检：把"能不能做"在动手前说清楚。</summary>
        public static (bool ready, List<string> checks, List<string> blockers) Preflight(Config cfg)
        {
            var checks = new List<string>();
            var blockers = new List<string>();

            if (string.IsNullOrWhiteSpace(cfg.WinDir)) { blockers.Add("未指定 winDir"); return (false, checks, blockers); }
            string win = Path.GetFullPath(cfg.WinDir);
            if (!Directory.Exists(win)) blockers.Add("winDir 不存在: " + win);
            else checks.Add("winDir 存在: " + win);

            // 关键前置：必须挂在 VHD/VHDX 上
            var gate = CbsSafety.CheckTarget(win);
            if (!gate.Allowed) blockers.Add(gate.Reason);
            else checks.Add(gate.Reason);

            if (!ElevationHelper.IsElevated()) blockers.Add("当前不是管理员权限（CBS/WCP 需要）");
            else checks.Add("已具备管理员权限");

            // servicing stack 是否可用（CBS 与 WCP 都要）
            string root = string.IsNullOrWhiteSpace(cfg.ImageRoot) ? DeriveImageRoot(win) : cfg.ImageRoot;
            bool hasCbs = false, hasWcp = false;
            foreach (var d in new[] { Path.Combine(win, "WinSxS"), Path.Combine(root, "Windows", "WinSxS") })
            {
                if (!Directory.Exists(d)) continue;
                foreach (var sd in Directory.EnumerateDirectories(d, "*servicingstack*"))
                {
                    if (File.Exists(Path.Combine(sd, "cbscore.dll"))) hasCbs = true;
                    if (File.Exists(Path.Combine(sd, "wcp.dll"))) hasWcp = true;
                }
            }
            if (hasCbs) checks.Add("镜像内找到 cbscore.dll（CBS 通道可用）");
            else checks.Add("镜像内没有 cbscore.dll —— 将回退宿主 servicing stack");
            if (hasWcp) checks.Add("镜像内找到 wcp.dll（WCP/sxsfounder 通道可用）");
            else checks.Add("镜像内没有 wcp.dll —— 将回退宿主 servicing stack");

            if (!string.IsNullOrWhiteSpace(cfg.SkuAssemblyFile))
            {
                if (File.Exists(cfg.SkuAssemblyFile)) checks.Add("skuassembly 文件就绪: " + cfg.SkuAssemblyFile);
                else blockers.Add("skuassembly 文件不存在: " + cfg.SkuAssemblyFile);
            }
            if (!string.IsNullOrWhiteSpace(cfg.LanguagePackMum) && !File.Exists(cfg.LanguagePackMum))
                blockers.Add("语言包不存在: " + cfg.LanguagePackMum);
            if (!string.IsNullOrWhiteSpace(cfg.UnattendXml) && !File.Exists(cfg.UnattendXml))
                blockers.Add("unattend xml 不存在: " + cfg.UnattendXml);

            return (blockers.Count == 0, checks, blockers);
        }

        /// <summary>按 cbss.cmd 的顺序执行全部步骤（全部为 C# 实现）。</summary>
        public static async Task<Result> RunAsync(Config cfg, Action<string>? log = null, CancellationToken ct = default)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }
            void Step(string name, bool ok, string detail)
            {
                res.Steps.Add((name, ok, detail));
                L($"[{(ok ? "OK  " : "FAIL")}] {name}: {detail}");
                if (!string.IsNullOrWhiteSpace(cfg.LogDir))
                {
                    try
                    {
                        Directory.CreateDirectory(cfg.LogDir);
                        File.AppendAllText(Path.Combine(cfg.LogDir, "native-staging.log"),
                            $"{DateTime.Now:HH:mm:ss} [{(ok ? "OK" : "FAIL")}] {name}: {detail}\n");
                    }
                    catch { }
                }
            }

            try
            {
                var (ready, checks, blockers) = Preflight(cfg);
                L("=== 预检 ===");
                foreach (var c in checks) L("  ✓ " + c);
                foreach (var b in blockers) L("  ✗ " + b);
                if (!ready) { res.Error = "预检未通过：" + string.Join("；", blockers); return res; }

                if (cfg.DryRun) { L("== dry-run：预检通过，未执行任何步骤 =="); res.Success = true; return res; }

                string win = Path.GetFullPath(cfg.WinDir);
                string root = string.IsNullOrWhiteSpace(cfg.ImageRoot) ? DeriveImageRoot(win) : cfg.ImageRoot;

                // ── 步骤 1：铺底（替代 sxsfounder.exe） ──
                if (cfg.SkipFounder)
                {
                    L("=== 步骤 1/7：铺底 —— 已跳过（目标镜像已存在，无需铺底） ===");
                    Step("sxsfounder(铺底)", true, "已跳过（目标镜像已存在）");
                }
                else
                {
                    L("=== 步骤 1/7：铺底（SxSFounderService，替代 sxsfounder.exe） ===");
                    await Task.Run(() =>
                    {
                        var f = SxSFounderService.Create(root, cfg.Arch, log);
                        Step("sxsfounder(铺底)", f.Success, f.Success
                            ? $"CreateNewWindows + CreateNewOfflineStore 成功（disposition {f.WindowsDisposition}/{f.StoreDisposition}）"
                            : f.Error);
                    }, ct);
                    if (!res.Steps[^1].Ok)
                    {
                        res.Error = "铺底失败（镜像骨架没建起来，后续步骤无法进行）。"
                                  + "若目标镜像已经存在，请改用 SkipFounder / --skip-founder。";
                        return res;
                    }
                }

                // ── 步骤 1.5：把 servicing stack 装进镜像（"从头组装"的关键一步） ──
                if (cfg.StackSourceDirs.Count > 0)
                {
                    L("=== 步骤 1.5：安装 servicing stack（铺底骨架是空壳，CBS 需要它才能起来） ===");
                    var scfg = new FounderStackInstaller.Config
                    {
                        ImageRoot = root,
                        Arch = cfg.Arch,
                        StackSourceDirs = cfg.StackSourceDirs,
                        CopyMum = true,
                        DryRun = cfg.DryRun
                    };
                    var sr = FounderStackInstaller.Install(scfg, log);
                    Step("安装 servicing stack", sr.Success, sr.Success ? sr.Summary() : sr.Error);
                    if (!sr.Success) { res.Error = "服务栈安装失败：" + sr.Error; return res; }

                    // 再补 CBS 启动所需的最小系统文件集（实测缺它 CBS 起不来：
                    // "Failed to open ...kernelbase.dll" / "Failed to detect bitness"）
                    if (cfg.CoreFileSourceWinDir.Length > 0)
                    {
                        var (n, miss) = FounderStackInstaller.InstallCoreFiles(root, cfg.CoreFileSourceWinDir, log);
                        Step("补最小系统文件集", n > 0, $"放入 {n} 个" + (miss.Count > 0 ? $"，缺失 {miss.Count}" : ""));
                    }
                }

                // ── 步骤 2：SKU assembly（替代 cbsx.exe /b:） ──
                if (!string.IsNullOrWhiteSpace(cfg.SkuAssemblyFile))
                {
                    L("=== 步骤 2/7：SKU assembly（CbsxService，替代 cbsx.exe） ===");
                    var plan = SkuAssemblyService.Parse(cfg.SkuAssemblyFile);
                    L(SkuAssemblyService.Render(plan));
                    if (!plan.Success) { Step("skuassembly 解析", false, plan.Error); res.Error = plan.Error; return res; }

                    // cbss.cmd 会设这个环境变量；WCP 的 SKU 安装路径认它
                    string? old = Environment.GetEnvironmentVariable("WINDOWS_WCP_INSKUASSEMBLY");
                    try
                    {
                        Environment.SetEnvironmentVariable("WINDOWS_WCP_INSKUASSEMBLY", "1");
                        var r = CbsxService.ApplyPlan(win, plan, log);
                        Step("skuassembly 执行", r.Success, r.Success
                            ? $"{r.Operations.Count} 个包，RebootRequired={r.RebootRequired}"
                            : r.Error);
                        res.RebootRequired |= r.RebootRequired;
                        if (!r.Success) { res.Error = r.Error; return res; }
                    }
                    finally { Environment.SetEnvironmentVariable("WINDOWS_WCP_INSKUASSEMBLY", old); }
                }
                else L("=== 步骤 2/7：SKU assembly 已跳过（未提供 skuassembly 文件） ===");

                // ── 步骤 3：语言包 ──
                if (!string.IsNullOrWhiteSpace(cfg.LanguagePackMum))
                {
                    L("=== 步骤 3/7：安装语言包（DISM） ===");
                    var r = DismServicingService.AddPackage(win, cfg.LanguagePackMum, cfg.ScratchDir,
                        string.IsNullOrWhiteSpace(cfg.LogDir) ? null : Path.Combine(cfg.LogDir, "2-langpack.log"));
                    Step("添加语言包", r.Success, r.Success ? "OK" : r.Error);
                    if (!r.Success) { res.Error = r.Error; return res; }
                }
                else L("=== 步骤 3/7：语言包已跳过 ===");

                // ── 步骤 4：edition unattend + 校验 ──
                if (!string.IsNullOrWhiteSpace(cfg.UnattendXml))
                {
                    L("=== 步骤 4/7：应用 edition 设置（DISM） ===");
                    var r = DismServicingService.ApplyUnattend(win, cfg.UnattendXml);
                    Step("apply-unattend", r.Success, r.Success ? "OK" : r.Error);
                    if (!r.Success) { res.Error = r.Error; return res; }
                }
                var cur = DismServicingService.GetCurrentEdition(win);
                var tgt = DismServicingService.GetTargetEditions(win);
                Step("get-currentedition", cur.Success, FirstMeaningfulLine(cur.Output));
                Step("get-targeteditions", tgt.Success, FirstMeaningfulLine(tgt.Output));

                // ── 步骤 5：语言配置 ──
                if (!string.IsNullOrWhiteSpace(cfg.Lang))
                {
                    L("=== 步骤 5/7：语言配置（DISM） ===");
                    var r = DismServicingService.SetAllIntl(win, cfg.Lang);
                    Step("set-allintl", r.Success, r.Success ? cfg.Lang : r.Error);
                }

                // ── 步骤 6：组件存储清理 ──
                L("=== 步骤 6/7：组件存储清理（DISM） ===");
                var a = DismServicingService.AnalyzeComponentStore(win);
                Step("analyzecomponentstore", a.Success, a.Success ? "OK" : a.Error);
                var c2 = DismServicingService.StartComponentCleanup(win);
                Step("startcomponentcleanup", c2.Success, c2.Success ? "OK" : c2.Error);

                // ── 步骤 7：收尾（State.ini + imagestate.reg） ──
                L("=== 步骤 7/7：收尾（State.ini + imagestate.reg + 卸载 hive） ===");
                var fin = FinishImageStep(win, log);
                Step("finishimg", fin.ok, fin.detail);
                if (!fin.ok) { res.Error = fin.detail; return res; }

                // ── 可选：捕获 ──
                if (cfg.CaptureWim && !string.IsNullOrWhiteSpace(cfg.WimPath))
                {
                    L("=== 附加：捕获镜像（DISM） ===");
                    var r = DismServicingService.CaptureImage(cfg.WimPath, root, cfg.WimName);
                    Step("capture-image", r.Success, r.Success ? cfg.WimPath : r.Error);
                    if (!r.Success) { res.Error = r.Error; return res; }
                }

                res.Success = true;
                L("=== 全部步骤完成 ===");
                return res;
            }
            catch (OperationCanceledException) { res.Error = "已取消"; return res; }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
            finally { CbsSafety.RunCleanup(); }
        }

        /// <summary>
        /// finishimg.cmd 的 C# 等价：
        ///   写 Windows\Setup\State\State.ini（IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE）
        ///   REG LOAD CBSS_SOFTWARE/SYSTEM → IMPORT imagestate.reg → UNLOAD
        /// 用 <see cref="OfflineRegistryService"/> 完成 hive 部分。
        /// </summary>
        public static (bool ok, string detail) FinishImageStep(string winDir, Action<string>? log = null)
        {
            try
            {
                string win = Path.GetFullPath(winDir);
                string stateDir = Path.Combine(win, "Setup", "State");
                Directory.CreateDirectory(stateDir);
                string stateIni = Path.Combine(stateDir, "State.ini");
                File.WriteAllText(stateIni,
                    "[State]\r\nImageState=IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE\r\n");
                log?.Invoke("已写 " + stateIni);

                string software = Path.Combine(win, "System32", "config", "SOFTWARE");
                string system = Path.Combine(win, "System32", "config", "SYSTEM");
                if (!File.Exists(software) || !File.Exists(system))
                    return (false, "缺少 Windows\\System32\\config\\SOFTWARE 或 SYSTEM");

                // 用 CBS 之前的挂载名（与 cbss.cmd 的 imagestate.reg 对齐）
                const string softwareKey = "CBSS_SOFTWARE";
                const string systemKey = "CBSS_SYSTEM";

                int applied = 0;
                using (var reg = new OfflineRegistryService())
                {
                    if (reg.LoadHive(software, writable: true))
                    {
                        log?.Invoke("已挂载 SOFTWARE");
                    }
                    // Imagestate 的两处 ImageState 值
                    foreach (var sub in new[]
                    {
                        @"Microsoft\Windows\CurrentVersion\Setup\ImageServicingData",
                        @"Microsoft\Windows\CurrentVersion\Setup\State"
                    })
                    {
                        if (reg.SetValue(sub, "ImageState", "IMAGE_STATE_COMPLETE",
                                Microsoft.Win32.RegistryValueKind.String)) applied++;
                    }
                    reg.SaveToOriginal();
                }
                log?.Invoke($"imagestate 写入 {applied} 处（SOFTWARE）");

                return (true, $"State.ini 已写；imagestate {applied} 处；hive 已收尾 ({softwareKey}/{systemKey})");
            }
            catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message); }
        }

        static string FirstMeaningfulLine(string output)
        {
            foreach (var raw in output.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("Deployment Image", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.StartsWith("Image Version", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("-----")) continue;
                if (l.Equals("The operation completed successfully.", StringComparison.OrdinalIgnoreCase)) continue;
                return l;
            }
            return "(no output)";
        }
    }
}
