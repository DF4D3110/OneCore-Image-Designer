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
            public string Edition = "Core";      // SKU 名，对应 servicing\Editions\<Edition>Edition.xml
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

            // ── 架构判定：铺底与 CBS 都受架构影响（已实证 x64 铺 x86 镜像会失败）──
            var (imgArch, archEvidence) = ArchRelay.DetectImageArchitecture(win);
            if (ArchRelay.NeedsRelay(imgArch, out string archReason))
            {
                string? helper = ArchRelay.FindHelper(imgArch);
                checks.Add(archReason + (helper != null
                        ? $"；装包将改走 DISM 通道（子程序可用于其它操作：{helper}）"
                        : "；装包将改走 DISM 通道（架构不符时的既定路径，不是错误）"));
            }

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

            // ── 选定【工具侧】服务栈（必须在铺底之前 —— 铺底要加载 wcp.dll）──
            // 依据 explodeSxS：服务栈是工具的依赖，由调用方显式指定用哪一套；
            // 镜像里那份不参与选择。这样就不会出现"镜像栈上下文不全 ⇒
            // CbsCoreInitialize 0x8007007F，且依赖已进进程换不回来"的死结。
            try
            {
                if (cfg.StackSourceDirs != null && cfg.StackSourceDirs.Count > 0)
                {
                    var (sd, sv) = FounderStackInstaller.FindStack(cfg.StackSourceDirs, cfg.Arch);
                    if (sd != null)
                    {
                        CbsxService.PreferredStackDir = sd;
                        L($"工具侧服务栈: {System.IO.Path.GetFileName(sd)}" + (sv != null ? $"（版本 {sv}）" : ""));
                    }
                    else L("未找到可用的服务栈 —— CBS 将回退到镜像/宿主栈");
                }
            }
            catch (Exception ex) { L("选定服务栈异常: " + ex.Message); }
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

            // explodeSxS 的每条 dism 都带 /scratchdir；为空时在目标卷（VHD）上推导一个
            if (string.IsNullOrWhiteSpace(cfg.ScratchDir))
            {
                try
                {
                    cfg.ScratchDir = Path.Combine(root, "OCID_scratch");
                    Directory.CreateDirectory(cfg.ScratchDir);
                    L($"暂存目录: {cfg.ScratchDir}");
                }
                catch { }
            }

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
                    else
                    {
                        // 没有完整系统镜像可做来源（例如 arm32 只有 Repair Content）时，
                        // 从【还原出的存储】里按文件名取核心文件。
                        var (n2, miss2, _) = FounderStackInstaller.InstallCoreFilesFromStore(root, cfg.StackSourceDirs, log, cfg.Arch);
                        Step("补最小系统文件集（取自存储）", n2 > 0, $"放入 {n2} 个" + (miss2.Count > 0 ? $"，缺失 {miss2.Count}" : ""));
                    }
                }

                // ── 步骤 1.6：写入版本信息（必须在装包【之前】） ──
                // DISM/SSShim 靠 SOFTWARE\...\CurrentVersion 与 COMPONENTS 的
                // StoreFormatVersion/StoreArchitecture 判 OS 版本；裸骨架没有这些，
                // DISM 就报 Error 50 并当作 "Vista RTM or earlier"。
                // 实测：真实 arm32 镜像有这些值，DISM 完全可用且装包成功。
                // 时机：装包在收尾之前发生，所以不能放到 FinishImageStep 里。
                {
                    L("=== 步骤 1.6：写入镜像版本信息（DISM 判版本所需） ===");
                    var (vn, vd) = FounderStackInstaller.SeedImageVersionInfo(win, cfg.Arch, 0, L);
                    Step("写入版本信息", vn > 0, vn > 0 ? $"写入 {vn} 个值" : "未写入（" + vd + "）");
                }

                // ── 步骤 1.7：补齐"能被 DISM 服务"的最小结构（必须在装包之前） ──
                // 判据：dism /image:<路径> /? 必须打印 "Image Version:"，
                //       否则报 Error 50 "Vista RTM or earlier" —— 那就算做失败了。
                {
                    L("=== 步骤 1.7：补齐 DISM 可服务结构（System32\\Dism 等） ===");
                    if (cfg.StackSourceDirs != null && cfg.StackSourceDirs.Count > 0)
                    {
                        var (dn, dmiss) = FounderStackInstaller.SeedDismServicingStructure(
                            root, cfg.StackSourceDirs, cfg.Arch, L);
                        Step("补齐 DISM 可服务结构", dn > 0,
                             $"放入 {dn} 个文件" + (dmiss.Count > 0 ? $"，缺失 {dmiss.Count}" : ""));
                    }
                    else L("  （没有存储来源，跳过）");
                }

                // ── 步骤 2：SKU assembly（替代 cbsx.exe /b:） ──
                if (!string.IsNullOrWhiteSpace(cfg.SkuAssemblyFile))
                {
                    L("=== 步骤 2/7：SKU assembly（CbsxService，替代 cbsx.exe） ===");
                    var plan = SkuAssemblyService.Parse(cfg.SkuAssemblyFile);
                    L(SkuAssemblyService.Render(plan));
                    if (!plan.Success) { Step("skuassembly 解析", false, plan.Error); res.Error = plan.Error; return res; }

                    // cbss.cmd 会设这个环境变量；WCP 的 SKU 安装路径认它
                    // ── 架构分流：镜像架构与进程不符时，原生 CBS 通道起不来，改走 DISM ──
                    var (skuArch, _) = ArchRelay.DetectImageArchitecture(win);
                    bool useDism = skuArch != ArchRelay.Arch.Unknown
                                && skuArch != ArchRelay.CurrentProcessArch();
                    if (useDism)
                    {
                        L($"  架构不符（镜像 {ArchRelay.ToToken(skuArch)} / 进程 "
                        + $"{ArchRelay.ToToken(ArchRelay.CurrentProcessArch())}）→ 改用 DISM 通道逐包安装");
                        int ok = 0, fail = 0;
                        foreach (var op in plan.Operations)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (op.ResolvedPath == null) { fail++; continue; }
                            var dr = DismServicingService.AddPackage(win, op.ResolvedPath, cfg.ScratchDir);
                            if (dr.Success) ok++;
                            else { fail++; L($"    ✗ {op.IdentityString ?? op.Name}: {dr.Error}"); }
                            if (cfg.DryRun && ok >= 1) { L("    (试运行：只试一个包即停)"); break; }
                        }
                        Step("skuassembly 执行(DISM)", fail == 0, $"成功 {ok}，失败 {fail}");
                        if (fail > 0) { res.Error = $"{fail} 个包经 DISM 安装失败"; return res; }
                        goto afterSku;
                    }

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

            afterSku:;

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
                // ── 步骤 4：SKU 配置（照 explodeSxS 的两步）──
                //   ① copy <win>\Windows\servicing\Editions\<Edition>Edition.xml  → <win>\Windows\<Edition>.xml
                //   ② dism /image:<win> /apply-unattend:<那个 xml>
                // 那个 Edition XML 是 **SKU 定义**（由 editions 包装进 servicing\Editions\），
                // 没有它就无法把镜像变成目标版本 —— exploreSxS 的 _edition 参数干的就是这件事。
                {
                    string unattend = cfg.UnattendXml;
                    if (string.IsNullOrWhiteSpace(unattend) && !string.IsNullOrWhiteSpace(cfg.Edition))
                    {
                        L("=== 步骤 4/7：SKU 配置（从 servicing\\Editions 取 Edition XML） ===");
                        string srcXml = Path.Combine(win, "Windows", "servicing", "Editions", cfg.Edition + "Edition.xml");
                        if (!File.Exists(srcXml))
                            srcXml = Path.Combine(win, "servicing", "Editions", cfg.Edition + "Edition.xml");

                        // 实测：装完 editions 包后 servicing\Editions 下会出现
                        //   <Edition>Edition.xml（如 ServerStandardEdition.xml），
                        // 另有 EditionMatrix.xml / UpgradeMatrix.xml 两个【不是 SKU 定义】。
                        // cfg.Edition 默认写死会找不到，所以这里按实际存在的文件自动推导。
                        // ★ 从 skuassembly 里的 editions 包名推导 edition token —— 这是【确定】的来源，
                        //   不要用"名字最短"这类启发式（实测会选中 ProfessionalEdition.xml 而实际装的是
                        //   ServerStandard）。包名形如
                        //   microsoft-windows-editions-serverstandard-package~31bf3856ad364e35~x86~~6.2.8400.0.mum
                        string skuToken = "";
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(cfg.SkuAssemblyFile) && File.Exists(cfg.SkuAssemblyFile))
                            {
                                foreach (var line in File.ReadAllLines(cfg.SkuAssemblyFile))
                                {
                                    var mm = System.Text.RegularExpressions.Regex.Match(line,
                                        @"microsoft-windows-editions-([a-z0-9]+)-package", 
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (mm.Success) { skuToken = mm.Groups[1].Value; break; }
                                }
                            }
                        }
                        catch { }
                        if (skuToken.Length > 0)
                        {
                            string bySku = Path.Combine(win, "servicing", "Editions",
                                char.ToUpperInvariant(skuToken[0]) + skuToken.Substring(1) + "Edition.xml");
                            if (File.Exists(bySku)) { srcXml = bySku; L($"  按 SKU 包名推导 Edition XML: {Path.GetFileName(bySku)}"); }
                        }

                        if (!File.Exists(srcXml))
                        {
                            foreach (var edDir in new[]
                            {
                                Path.Combine(win, "Windows", "servicing", "Editions"),
                                Path.Combine(win, "servicing", "Editions")
                            })
                            {
                                if (!Directory.Exists(edDir)) { L($"    (目录不存在: {edDir})"); continue; }
                                var all = Directory.EnumerateFiles(edDir, "*.xml").ToList();
                                L($"    {edDir} 下有 {all.Count} 个 xml: " + string.Join(", ", all.Take(8).Select(Path.GetFileName)));
                                var cands = Directory.EnumerateFiles(edDir, "*Edition.xml")
                                    .Where(f => !Path.GetFileName(f).Equals("EditionMatrix.xml", StringComparison.OrdinalIgnoreCase)
                                             && !Path.GetFileName(f).Equals("UpgradeMatrix.xml", StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                                // 优先匹配 cfg.Edition 前缀，否则取唯一的一个
                                var pick = cands.FirstOrDefault(f => Path.GetFileName(f)
                                                .StartsWith(cfg.Edition, StringComparison.OrdinalIgnoreCase));
                                pick ??= cands.Count >= 1
                                    ? cands.OrderBy(f => Path.GetFileName(f).Length).First()   // 多个候选时取名字最短的（最可能是主 SKU）
                                    : null;
                                if (pick != null)
                                {
                                    srcXml = pick;
                                    L($"  按镜像实际内容选定 SKU 定义: {Path.GetFileName(pick)}");
                                    break;
                                }
                            }
                        }
                        if (File.Exists(srcXml))
                        {
                            // 目标路径照 explodeSxS: <镜像>\Windows\<Edition>.xml
                            // ⚠ 不要拼 "Windows" —— win 本身已是 Windows 目录（早前拼成
                            //   G:\Windows\Windows\Core.xml 就是这么来的）。
                            // 文件名用【推导出的】Edition 名，不要用 cfg.Edition（它可能是过时的默认值）。
                            string edName = Path.GetFileName(srcXml);
                            if (edName.EndsWith("Edition.xml", StringComparison.OrdinalIgnoreCase))
                                edName = edName.Substring(0, edName.Length - "Edition.xml".Length);
                            string dstXml = Path.Combine(win, edName + ".xml");
                            try
                            {
                                File.Copy(srcXml, dstXml, true);
                                unattend = dstXml;
                                L($"  已取 Edition XML: {Path.GetFileName(srcXml)} -> {Path.GetFileName(dstXml)}");
                            }
                            catch (Exception ex) { L("  复制 Edition XML 失败: " + ex.Message); }
                        }
                        else L($"  找不到 servicing\\Editions\\{cfg.Edition}Edition.xml（editions 包可能还没装）");
                    }
                    if (!string.IsNullOrWhiteSpace(unattend))
                    {
                        if (string.IsNullOrWhiteSpace(cfg.UnattendXml))
                            L("=== 步骤 4/7：应用 edition 设置（DISM） ===");
                        var r = DismServicingService.ApplyUnattend(win, unattend, cfg.ScratchDir);
                        Step("apply-unattend", r.Success, r.Success ? "OK" : r.Error);
                    }
                    else L("=== 步骤 4/7：SKU 配置已跳过（既没给 unattend，也取不到 Edition XML） ===");
                }
                var cur = DismServicingService.GetCurrentEdition(win);
                var tgt = DismServicingService.GetTargetEditions(win);
                Step("get-currentedition", cur.Success, FirstMeaningfulLine(cur.Output));
                Step("get-targeteditions", tgt.Success, FirstMeaningfulLine(tgt.Output));

                // ── 步骤 5：语言配置 ──
                if (!string.IsNullOrWhiteSpace(cfg.Lang))
                {
                    L("=== 步骤 5/7：语言配置（DISM） ===");
                    var r = DismServicingService.SetAllIntl(win, cfg.Lang, cfg.ScratchDir);
                    Step("set-allintl", r.Success, r.Success ? cfg.Lang : r.Error);
                }

                // ── 步骤 6：组件存储清理 ──
                L("=== 步骤 6/7：组件存储清理（DISM） ===");
                var a = DismServicingService.AnalyzeComponentStore(win, cfg.ScratchDir);
                Step("analyzecomponentstore", a.Success, a.Success ? "OK" : a.Error);
                var c2 = DismServicingService.StartComponentCleanup(win, cfg.ScratchDir);
                Step("startcomponentcleanup", c2.Success, c2.Success ? "OK" : c2.Error);

                // ── 步骤 7：收尾（State.ini + imagestate.reg） ──
                L("=== 步骤 7/7：收尾（State.ini + imagestate.reg + 卸载 hive） ===");
                // 优先用【独立进程】执行收尾 —— CBS 服务会持有目标镜像的 hive 直到
                // 调用方进程退出，同一进程内收尾必然失败（见 CBS_TECHNICAL_NOTES 第 11 节）。
                var fin = RunFinishOutOfProcess(win, log) ?? FinishImageService.Finish(win, log);   // 注意：要传【Windows 目录】而不是镜像根 —— 收尾找的是 <win>\System32\config\SOFTWARE
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
        
                /// <summary>
        /// 镜像收尾（转调 <see cref="FinishImageService.Finish"/>）。
        /// 保留此入口是因为调用方多；真正的实现已抽到 FinishImageService，
        /// 以便 CbsxCli 能链接并以【独立进程】执行收尾。
        /// </summary>
        /// <summary>
        /// 以【独立进程】执行收尾。成功返回结果；找不到 CbsxCli 时返回 null（调用方回退同进程）。
        ///
        /// 【为什么必须分进程】实测：CBS 服务会持有目标镜像的 hive 直到调用方进程退出。
        /// 同一进程内释放 → 9 → 9（重试 6 次约 42 秒全失败）；新进程 → 9 → 0，收尾完整成功。
        /// </summary>
        internal static (bool ok, string detail)? RunFinishOutOfProcess(string winDir, Action<string>? log)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string? cli = null;
                foreach (var cand in new[]
                {
                    Path.Combine(baseDir, "CbsxCli.exe"),
                    Path.Combine(baseDir, "tools", "CbsxCli.exe"),
                    Path.Combine(baseDir, "..", "CbsxCli", "CbsxCli.exe"),
                })
                {
                    if (File.Exists(cand)) { cli = Path.GetFullPath(cand); break; }
                }
                if (cli == null)
                {
                    log?.Invoke("  （未找到 CbsxCli.exe，收尾回退到同进程执行）");
                    return null;
                }

                log?.Invoke("  以独立进程执行收尾: " + cli);
                var psi = new System.Diagnostics.ProcessStartInfo(cli)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("/finish");
                psi.ArgumentList.Add("/o:" + Path.GetFullPath(winDir));
                // 让子进程等本进程退出后再收尾 —— 本进程持有 hive，不退出子进程也拿不到
                psi.ArgumentList.Add("/waitfor:" + Environment.ProcessId);

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;

                // ⚠ 【不等它】。子进程会等本进程退出才开始收尾（本进程持有目标镜像的
                // hive，不退出任何进程都拿不到），所以在这里 WaitForExit 必然死锁。
                // 正确做法：把它留在后台，本进程退出后它自然完成收尾。
                log?.Invoke($"  收尾已交给独立进程（PID {p.Id}）—— 它会在本程序退出后自动完成");
                return (true, $"收尾已安排由独立进程在退出后完成（PID {p.Id}）。"
                            + "若需立即确认，请关闭本程序后查看 State.ini 与 imagestate。");
            }
            catch (Exception ex)
            {
                log?.Invoke("  独立进程收尾异常，回退同进程: " + ex.Message);
                return null;
            }
        }

        public static (bool ok, string detail) FinishImageStep(string winDir, Action<string>? log = null)
            => FinishImageService.Finish(winDir, log);

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
