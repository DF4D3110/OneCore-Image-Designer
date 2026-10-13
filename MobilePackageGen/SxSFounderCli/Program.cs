using System;
using System.IO;
using OneCore_Image_Designer.Services;

namespace SxSFounderCli;

/// <summary>
/// sxsfounder.exe 的 C# 替代品。
///
///   SxSFounderCli &lt;offline image path&gt; &lt;architecture&gt;   （与 sxsfounder 完全兼容）
///   SxSFounderCli &lt;image&gt; &lt;arch&gt; [/dry] [/elevate] [/log:&lt;path&gt;]
///
/// 架构：amd64 / x86 / arm / arm64（与 sxsfounder 相同）。
///
/// 安全约束（与 CbsxService 一致）：
///   · 目标必须是【挂载的 VHD/VHDX】—— 铺底会挂载并改写目标镜像的注册表 hive，
///     对物理盘/宿主系统盘操作会破坏系统；
///   · 结束时必须能干净收尾（DismountRegistryHives 在 finally 里执行）。
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("SxSFounderCli 1.0  ——  WCP 铺底（sxsfounder.exe 的 C# 替代品）");
        Console.WriteLine();

        if (args.Length == 0 || args[0] is "/?" or "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        string imageRoot = args[0];
        string arch = args.Length > 1 && !args[1].StartsWith("/") ? args[1] : "amd64";
        bool dry = false, wantElevate = false;
        string? logPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("/dry", StringComparison.OrdinalIgnoreCase)) dry = true;
            else if (a.Equals("/elevate", StringComparison.OrdinalIgnoreCase)) wantElevate = true;
            else if (a.StartsWith("/log:", StringComparison.OrdinalIgnoreCase)) logPath = a[5..];
        }

        // 架构校验（早失败，避免走到一半才发现）
        try { Console.WriteLine("架构: " + arch + " -> 0x" + SxSFounderService.ArchitectureValue(arch).ToString("X8")); }
        catch (Exception ex) { Console.Error.WriteLine("参数错误: " + ex.Message); return 2; }

        if (!Directory.Exists(imageRoot))
        {
            try { Directory.CreateDirectory(imageRoot); Console.WriteLine("已创建目录: " + imageRoot); }
            catch (Exception ex) { Console.Error.WriteLine("镜像目录不可用: " + ex.Message); return 2; }
        }

        // 权限
        Console.WriteLine("管理员权限: " + ElevationHelper.IsElevated());
        if (!ElevationHelper.IsElevated())
        {
            Console.WriteLine("当前不是管理员 —— WCP 铺底通常需要。");
            Console.WriteLine(ElevationHelper.Describe());
            if (wantElevate)
            {
                string self = "\"" + imageRoot + "\" " + arch;
                if (ElevationHelper.EnsureElevatedOrRelaunch(self, s => Console.WriteLine("  " + s)))
                    return 0;   // 已派生子进程，本进程退出
            }
        }

        var log = new System.Collections.Generic.List<string>();
        void Log(string s) { lock (log) log.Add(s); Console.WriteLine("  " + s); }

        if (dry)
        {
            var check = CbsSafety.CheckTarget(imageRoot);
            Console.WriteLine();
            Console.WriteLine("== dry-run（只做门禁与前置检查，不铺底） ==");
            Console.WriteLine("  门禁: " + check.Reason);
            Console.WriteLine(check.Allowed
                ? "  ==> 通过（可执行铺底）"
                : "  ==> 不通过（拒绝执行）");
            return check.Allowed ? 0 : 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = SxSFounderService.Create(imageRoot, arch, Log);
        sw.Stop();

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try { File.WriteAllLines(logPath, log); Console.WriteLine("日志已写: " + logPath); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(SxSFounderService.Render(r));
        Console.WriteLine($"  耗时: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine(r.Success ? "==> OK" : "==> 失败");
        return r.Success ? 0 : 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"
用法:
  SxSFounderCli <offline image path> <architecture>
  SxSFounderCli <image> <arch> [/dry] [/elevate] [/log:<path>]

架构: amd64 | x86 | arm | arm64

选项:
  /dry          只做门禁与前置检查，不铺底
  /elevate      权限不足时经 NSudo 提权重启自己
  /log:<path>   把日志写到文件
  /?            本帮助

强制约束:
  <offline image path> 必须位于【挂载的 VHD/VHDX】上。
  铺底会挂载并改写目标镜像的注册表 hive；对物理盘/宿主系统盘操作会破坏系统，
  因此本工具会直接拒绝（BusType 必须为 14 Virtual 或 15 FileBackedVirtual）。
");
    }
}
