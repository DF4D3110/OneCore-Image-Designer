using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneCore_Image_Designer.Services;

namespace CbsxCli;

/// <summary>
/// cbsx.exe 的 C# 替代品。命令行语义与 cbsexploder 兼容，另加 /dry 试运行。
///
///   CbsxCli /ep /o:&lt;winDir&gt; [/log:&lt;path&gt;]
///   CbsxCli /ip|/sp|/up /m:&lt;mum&gt; /o:&lt;winDir&gt; [/dry] [/log:&lt;path&gt;]
///   CbsxCli /b:&lt;batch&gt; /o:&lt;winDir&gt; [/dry] [/log:&lt;path&gt;]
///
/// 目标必须是挂载的 VHD/VHDX —— 这是硬门禁，用来保护宿主系统。
/// </summary>
internal static class Program
{
    sealed class Options
    {
        public bool Enumerate;
        public bool Dry;
        public bool Finish;
        public int WaitForPid;
        public bool PostInstall;
        public string Lang = "en-US";
        public string Scratch = "";
        public string SkuFile = "";
        public string ResultFile = "";
        public bool Help;
        public SkuAssemblyService.PackageIntent Intent = SkuAssemblyService.PackageIntent.Unknown;
        public string? Manifest;
        public string? Batch;
        public string? WinDir;
        public string? LogPath;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ── 提权钩子：装包类操作默认在 TrustedInstaller 下跑 ──
        //
        // 【为什么】离线 CBS 的权限是分层的，实测逐级才通：
        //   管理员 + SeBackup/Restore/TakeOwnership/Security + takeown/icacls
        //        → 卡在驱动组件的 q-unstage
        //   LocalSystem（schtasks /ru SYSTEM 或 NSudo SYSTEM）
        //        → 能过 StageFile / Doqe install，但 Commit 写离线 SOFTWARE hive 的
        //          `\Classes\Unknown\shell` 键时 STATUS_ACCESS_DENIED
        //   TrustedInstaller（NSudo TI 模式）
        //        → 才拥有那些键
        // 而且 Execute 一失败，镜像就被标记 CBS_E_IMAGE_UNSERVICEABLE(0x800F0830)，
        // 必须重新 apply 才能再试 —— 所以不该"先失败再补救"，而是一开始就用对的身份。
        //
        // 只读 / 辅助模式不重启：/ep 枚举、/dry 试运行、/? 帮助、
        // /finish 收尾（它靠 /waitfor:<pid> 等父进程退出，重启会改 PID 语义）、
        // /postinstall（走 DISM，DISM 自己会委托 TrustedInstaller 服务）、/no-ti 显式关掉。
        bool readOnlyOrHelper = args.Any(a =>
            a.StartsWith("/ep", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/dry", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/?", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/help", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/finish", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/postinstall", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("/no-ti", StringComparison.OrdinalIgnoreCase));
        if (!readOnlyOrHelper && ElevationHelper.EnsureTrustedInstallerOrRelaunch(Console.WriteLine))
            return 0;

        Console.WriteLine("CbsxCli 1.0  ——  CBS COM 客户端（cbsx.exe 的 C# 替代品）");
        Console.WriteLine();

        var o = Parse(args, out string? parseError);
        if (parseError != null) { Console.Error.WriteLine("参数错误: " + parseError); PrintUsage(); return 2; }
        if (o.Help) { PrintUsage(); return 0; }

        if (string.IsNullOrWhiteSpace(o.WinDir)) { Console.Error.WriteLine("缺少 /o:<winDir>"); PrintUsage(); return 2; }

            // /postinstall：CBS 装包【之后】的全部步骤，整体在独立进程里跑。
            // 【为什么】CBS 服务在会话结束后仍独占目标镜像的 hive，而 apply-unattend
            //   需要独占 → 同进程内实测 dism 退出码 5 (ERROR_ACCESS_DENIED)；
            //   换到【父进程退出之后】的独立进程里跑同一条命令 → exit 0 成功。
            if (o.PostInstall)
            {
                Console.WriteLine("=== CbsxCli /postinstall（后置阶段，独立进程） ===");
                var po = new OneCore_Image_Designer.Services.PostInstallService.Options
                {
                    WinDir = o.WinDir!,
                    Lang = o.Lang,
                    ScratchDir = o.Scratch,
                    SkuAssemblyFile = o.SkuFile,
                    ResultFile = o.ResultFile,
                };
                // 从 skuassembly 推导 edition
                try
                {
                    if (!string.IsNullOrWhiteSpace(o.SkuFile) && System.IO.File.Exists(o.SkuFile))
                    {
                        var mm = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(o.SkuFile),
                            @"microsoft-windows-editions-([a-z0-9]+)-package",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mm.Success) po.Edition = char.ToUpperInvariant(mm.Groups[1].Value[0]) + mm.Groups[1].Value.Substring(1);
                    }
                }
                catch { }
                var (pok, psum) = OneCore_Image_Designer.Services.PostInstallService.Run(po, m => Console.WriteLine("  " + m));
                Console.WriteLine();
                Console.WriteLine(pok ? "  OK  后置阶段完成" : "  FAIL  后置阶段有失败项");
                return pok ? 0 : 1;
            }

            // /finish：以【独立进程】执行收尾（写 State.ini + imagestate）。
            // 必须独立成进程 —— CBS 服务会持有目标镜像的 hive 直到调用方进程退出，
            // 同一进程内收尾必然失败（详见 CBS_TECHNICAL_NOTES.md 第 11 节）。
            if (o.Finish)
            {
                // 如果指定了 /waitfor:<pid>，先等那个进程退出再收尾。
                // 【为什么必须等】持有目标镜像 hive 的是跑过 CBS 的那个进程；
                // 它不退出，换任何进程来收尾都拿不到 hive（实测 9→9 全失败）。
                if (o.WaitForPid > 0)
                {
                    Console.WriteLine($"  等待进程 {o.WaitForPid} 退出后再收尾…");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromMinutes(30))
                    {
                        try { System.Diagnostics.Process.GetProcessById(o.WaitForPid); }
                        catch { Console.WriteLine("  该进程已退出，开始收尾"); break; }
                        System.Threading.Thread.Sleep(1000);
                    }
                }
                Console.WriteLine("=== CbsxCli /finish（独立进程收尾） ===");
                var (ok, detail) = OneCore_Image_Designer.Services.FinishImageService
                    .Finish(o.WinDir!, m => Console.WriteLine("  " + m));
                Console.WriteLine();
                Console.WriteLine("  " + (ok ? "OK  " : "FAIL") + " " + detail);
                return ok ? 0 : 1;
            }
        if (!Directory.Exists(o.WinDir)) { Console.Error.WriteLine("winDir 不存在: " + o.WinDir); return 2; }

        var lines = new List<string>();
        void Log(string s)
        {
            lock (lines) lines.Add(s);
            Console.WriteLine("  " + s);
        }

        // 可选：把完整日志写到文件
        if (!string.IsNullOrWhiteSpace(o.LogPath))
        {
            try { File.WriteAllText(o.LogPath, "CbsxCli log\n" + string.Join("\n", args) + "\n\n"); } catch { }
        }

        try
        {
            // ───── /ep 枚举 ─────
            if (o.Enumerate)
            {
                var r = CbsxService.EnumeratePackages(o.WinDir!, Log);
                DumpLog(o.LogPath, lines);
                Console.WriteLine();
                Console.WriteLine(CbsxService.RenderEnumeration(r));
                Console.WriteLine(r.Success
                    ? $"==> OK，共 {r.EnumeratedIdentities.Count} 个包"
                    : "==> 失败");
                return r.Success ? 0 : 1;
            }

            // ───── 构造计划 ─────
            SkuAssemblyService.Plan plan;
            if (!string.IsNullOrWhiteSpace(o.Batch))
            {
                Log($"批处理: {o.Batch}");
                plan = SkuAssemblyService.Parse(o.Batch!);
            }
            else
            {
                // 单包：合成一个临时批处理内容，复用同一套解析与预检
                string tmp = Path.Combine(Path.GetTempPath(), "cbsxcli_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
                string intent = o.Intent switch
                {
                    SkuAssemblyService.PackageIntent.Installed => "/ip",
                    SkuAssemblyService.PackageIntent.Staged => "/sp",
                    SkuAssemblyService.PackageIntent.Absent => "/up",
                    _ => throw new InvalidOperationException("缺少包操作（/ip | /sp | /up）")
                };
                File.WriteAllText(tmp, intent + " /m:" + o.Manifest + Environment.NewLine);
                plan = SkuAssemblyService.Parse(tmp);
                try { File.Delete(tmp); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(SkuAssemblyService.Render(plan));
            if (!plan.Success)
            {
                Console.Error.WriteLine("计划无效: " + plan.Error);
                return 1;
            }

            // ───── 执行（或试运行）─────
            var res = CbsxService.ApplyPlan(o.WinDir!, plan, Log, o.Dry);
            DumpLog(o.LogPath, lines);

            Console.WriteLine();
            Console.WriteLine("--- 逐包结果 ---");
            foreach (var op in res.Operations)
            {
                Console.WriteLine($"  {(op.Ok ? "OK  " : "FAIL")}  {op.Identity}");
                Console.WriteLine($"        当前={op.CurrentState}  适用={op.Applicability}  目标={op.TargetState}"
                                  + (op.Error != null ? $"  错误: {op.Error}" : ""));
            }

            Console.WriteLine();
            Console.WriteLine("  Success        : " + res.Success);
            Console.WriteLine("  Error          : " + (res.Error.Length == 0 ? "(none)" : res.Error));
            Console.WriteLine("  RebootRequired : " + res.RebootRequired);
            Console.WriteLine("  DryRun         : " + o.Dry);
            Console.WriteLine(res.Success ? "==> OK" : "==> 失败");
            return res.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            DumpLog(o.LogPath, lines);
            Console.Error.WriteLine("异常: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            CbsSafety.RunCleanup();
        }
    }

    static void DumpLog(string? path, List<string> lines)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, string.Join("\n", lines) + "\n"); } catch { }
    }

    static Options Parse(string[] args, out string? error)
    {
        error = null;
        var o = new Options();
        foreach (var a in args)
        {
            if (a.StartsWith("/?", StringComparison.Ordinal) || a.Equals("-h", StringComparison.OrdinalIgnoreCase))
            { o.Help = true; continue; }

            if (a.StartsWith("/ep", StringComparison.OrdinalIgnoreCase)) { o.Enumerate = true; continue; }
            if (a.StartsWith("/dry", StringComparison.OrdinalIgnoreCase)) { o.Dry = true; continue; }
            if (a.StartsWith("/finish", StringComparison.OrdinalIgnoreCase)) { o.Finish = true; continue; }
            if (a.StartsWith("/postinstall", StringComparison.OrdinalIgnoreCase)) { o.PostInstall = true; continue; }
            if (a.StartsWith("/lang:", StringComparison.OrdinalIgnoreCase)) { o.Lang = a.Substring(6); continue; }
            if (a.StartsWith("/scratch:", StringComparison.OrdinalIgnoreCase)) { o.Scratch = a.Substring(9).Trim('"'); continue; }
            if (a.StartsWith("/sku:", StringComparison.OrdinalIgnoreCase)) { o.SkuFile = a.Substring(5).Trim('"'); continue; }
            if (a.StartsWith("/result:", StringComparison.OrdinalIgnoreCase)) { o.ResultFile = a.Substring(8).Trim('"'); continue; }
            if (a.StartsWith("/waitfor:", StringComparison.OrdinalIgnoreCase))
            { int.TryParse(a.Substring("/waitfor:".Length), out o.WaitForPid); continue; }

            if (a.StartsWith("/ip", StringComparison.OrdinalIgnoreCase)) { if (!SetIntent(o, SkuAssemblyService.PackageIntent.Installed, ref error)) return o; continue; }
            if (a.StartsWith("/sp", StringComparison.OrdinalIgnoreCase)) { if (!SetIntent(o, SkuAssemblyService.PackageIntent.Staged, ref error)) return o; continue; }
            if (a.StartsWith("/up", StringComparison.OrdinalIgnoreCase)) { if (!SetIntent(o, SkuAssemblyService.PackageIntent.Absent, ref error)) return o; continue; }

            if (TryValue(a, "/o", out string v)) { o.WinDir = v; continue; }
            if (TryValue(a, "/m", out v)) { o.Manifest = v; continue; }
            if (TryValue(a, "/b", out v)) { o.Batch = v; continue; }
            if (TryValue(a, "/log", out v)) { o.LogPath = v; continue; }
        }

        if (o.Enumerate && !string.IsNullOrWhiteSpace(o.Batch)) error = "/ep 不能与 /b 同用";
        if (!string.IsNullOrWhiteSpace(o.Batch) && (o.Intent != SkuAssemblyService.PackageIntent.Unknown || o.Manifest != null))
            error = "/b 不能与 /ip /sp /up /m 同用";
        if (!o.Enumerate && !o.Finish && !o.PostInstall && string.IsNullOrWhiteSpace(o.Batch))
        {
            if (o.Intent == SkuAssemblyService.PackageIntent.Unknown) error = "缺少包操作（/ip | /sp | /up）";
            else if (string.IsNullOrWhiteSpace(o.Manifest)) error = "缺少 /m:<mum>";
        }
        return o;
    }

    static bool SetIntent(Options o, SkuAssemblyService.PackageIntent intent, ref string? error)
    {
        if (o.Intent != SkuAssemblyService.PackageIntent.Unknown) { error = "同一命令行出现多个包操作"; return false; }
        o.Intent = intent;
        return true;
    }

    static bool TryValue(string arg, string key, out string value)
    {
        value = "";
        if (!arg.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) return false;
        value = arg[(key.Length + 1)..];
        return true;
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"
用法:
  CbsxCli /ep /o:<winDir> [/log:<path>]
  CbsxCli /postinstall /o:<winDir> [/lang:..] [/scratch:..] [/sku:..] [/waitfor:<pid>]
  CbsxCli /finish /o:<winDir> [/waitfor:<pid>]   独立进程执行收尾（等 pid 退出后再做）
  CbsxCli /ip|/sp|/up /m:<mum> /o:<winDir> [/dry] [/log:<path>]
  CbsxCli /b:<batch> /o:<winDir> [/dry] [/log:<path>]

包操作:
  /ip    Install package   (CbsInstallState::Installed)
  /sp    Stage   package   (CbsInstallState::Staged)
  /up    Uninstall package (CbsInstallState::Absent)
  /ep    Enumerate packages (read-only, overrides other modes)

参数:
  /m:<mum>      包清单
  /o:<winDir>   离线镜像的 Windows 目录（必需）
  /b:<file>     批处理文件，每行一条操作（格式同命令行）
  /log:<path>   日志文件
  /dry          试运行：只做适用性判定，不修改镜像
  /?            本帮助

强制约束:
  目标 /o: 必须是【挂载的 VHD/VHDX】支撑的卷。
  CBS 会挂载并改写目标镜像的注册表 hive；对物理盘/宿主系统盘操作会破坏系统，
  因此本工具会直接拒绝（BusType 必须为 14 Virtual 或 15 FileBackedVirtual）。
");
    }
}
