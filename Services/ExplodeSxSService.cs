using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// explodeSxS 的 C# 编排（原 ThirdParty\SxS\explodeSxS\*.cmd 脚本序列的等价实现）。
    ///
    /// 桌面镜像 staging 流程：sxsfounder 铺底 → cbsx 装 SKU 组件 → DISM 加语言包/应用
    /// 版本 unattend/设语言/清理组件存储 → finishimg 写 ImageState → 可选 capture 成 install.wim。
    ///
    /// 安全边界（相对原脚本的改进）：
    /// ① 默认【不格式化】工作盘，只做目录准备；格式化必须显式 FormatWorkDrive=true 且工作盘非系统盘；
    /// ② 全程提权 PowerShell + 日志文件回传（复用 UpdateAppRunnerService 同款），每步落独立日志；
    /// ③ 工具（cbsx/sxsfounder）按 仓库 bin → D:\sxs zip 自动解包 顺序发现，缺失即中止并说明。
    /// </summary>
    public static class ExplodeSxSService
    {
        public sealed class Config
        {
            public string WorkDrive = "X:\\";        // 暂存盘（RamDisk/快盘）
            public string Edition = "Core";
            public string Arch = "amd64";
            public string Lang = "en-US";
            public string ServicingStackMum = "";     // _ss：servicing stack 目录/mum
            public string LanguagePackMum = "";       // _lp
            public bool FormatWorkDrive;              // 默认 false：不格式化
            public bool CaptureWim;                   // 末尾是否 capture 成 install.wim
            public string WimName = "Windows 11 Home";
        }

        public sealed class PreflightResult
        {
            public bool Ready;
            public readonly List<string> Checks = new();
            public readonly List<string> Blockers = new();
        }

        static string RepoSxsRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThirdParty", "SxS", "explodeSxS");

        /// <summary>发现 cbsx.exe / sxsfounder.exe：仓库 bin 优先，其次 D:\sxs 的 zip 解到临时目录。</summary>
        public static (string? dir, string note) ResolveToolDir()
        {
            string binDir = Path.Combine(RepoSxsRoot, "bin");
            if (File.Exists(Path.Combine(binDir, "cbsx.exe")) && File.Exists(Path.Combine(binDir, "sxsfounder.exe")))
                return (binDir, "仓库内置 bin");

            // D:\sxs\explodeSxS-principalis.zip 解包兜底
            const string zip = @"D:\sxs\explodeSxS-principalis.zip";
            if (File.Exists(zip))
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "icd_explode_bin");
                    if (!File.Exists(Path.Combine(tmp, "cbsx.exe")))
                    {
                        Directory.CreateDirectory(tmp);
                        using var z = ZipFile.OpenRead(zip);
                        foreach (var e in z.Entries)
                        {
                            string nm = Path.GetFileName(e.FullName);
                            if (nm is "cbsx.exe" or "sxsfounder.exe")
                                e.ExtractToFile(Path.Combine(tmp, nm), true);
                        }
                    }
                    if (File.Exists(Path.Combine(tmp, "cbsx.exe")) && File.Exists(Path.Combine(tmp, "sxsfounder.exe")))
                        return (tmp, "D:\\sxs zip 解包");
                }
                catch { /* 落到下面的失败返回 */ }
            }
            return (null, "未找到 cbsx.exe/sxsfounder.exe（仓库 bin 缺且 D:\\sxs 无 zip）");
        }

        public static PreflightResult Preflight(Config cfg)
        {
            var r = new PreflightResult();
            string drive = (cfg.WorkDrive ?? "").TrimEnd('\\');
            if (drive.Length != 2 || drive[1] != ':' || !char.IsLetter(drive[0]))
            {
                r.Blockers.Add("工作盘路径无效（需形如 X:）");
            }
            else
            {
                char letter = char.ToUpperInvariant(drive[0]);
                if (letter == Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', ':').ToUpperInvariant()[0])
                    r.Blockers.Add("工作盘不能是系统盘");
                else if (cfg.FormatWorkDrive)
                    r.Checks.Add($"⚠ 将【格式化】{letter}: 盘（已显式确认）");
                else
                    r.Checks.Add($"{letter}: 盘仅建目录，不格式化");
            }

            var (dir, note) = ResolveToolDir();
            if (dir != null) r.Checks.Add($"CBS 工具就绪（{note}）");
            else r.Blockers.Add(note);

            if (File.Exists(cfg.ServicingStackMum)) r.Checks.Add("servicing stack 存在");
            else r.Blockers.Add("servicing stack mum 缺失或路径错");

            if (File.Exists(cfg.LanguagePackMum)) r.Checks.Add("语言包存在");
            else r.Blockers.Add("语言包 mum 缺失或路径错");

            string files = Path.Combine(RepoSxsRoot, "files");
            if (File.Exists(Path.Combine(files, "skuassembly.txt"))) r.Checks.Add("skuassembly.txt 就绪");
            else r.Blockers.Add("缺 files\\skuassembly.txt");
            if (File.Exists(Path.Combine(files, "imagestate.reg"))) r.Checks.Add("imagestate.reg 就绪");
            else r.Blockers.Add("缺 files\\imagestate.reg");

            r.Ready = r.Blockers.Count == 0;
            return r;
        }

        /// <summary>
        /// 提权执行完整 staging 流程。log 回调在 UI 线程外触发（调用方自行 marshal）。
        /// 返回退出码；未能在超时内结束返回 null。
        /// </summary>
        public static async Task<int?> RunAsync(Config cfg, Action<string> log, CancellationToken ct, int timeoutMs = 60 * 60 * 1000)
        {
            var pf = Preflight(cfg);
            if (!pf.Ready)
            {
                foreach (var b in pf.Blockers) log("❌ " + b);
                return null;
            }
            var (toolDir, _) = ResolveToolDir();

            string w = (cfg.WorkDrive ?? "X:\\").TrimEnd('\\') + "\\";
            string files = Path.Combine(RepoSxsRoot, "files");
            string ssDir = Path.GetDirectoryName(cfg.ServicingStackMum) ?? cfg.ServicingStackMum;
            string lp = cfg.LanguagePackMum;
            string fmt = cfg.FormatWorkDrive
                ? $"format {w.TrimEnd('\\')} /q /y | Out-Null; if ($LASTEXITCODE -ne 0) {{ throw '格式化失败' }}"
                : $"if (-not (Test-Path '{w}')) {{ throw '工作盘不存在，且未勾选格式化' }}";

            // 生成 PowerShell 脚本（等价 cbss.cmd 序列，每步独立日志）
            var ps = new StringBuilder();
            ps.AppendLine("$ErrorActionPreference='Stop'");
            ps.AppendLine($"$w='{w}'; $tool='{toolDir}'; $files='{files}'");
            ps.AppendLine(fmt);
            ps.AppendLine("& { foreach($d in 'logs','temp','image','desktopimaging'){ New-Item -ItemType Directory -Force -Path ($w+$d) | Out-Null } }");
            ps.AppendLine("Copy-Item \"$tool\\sxsfounder.exe\" $w'desktopimaging\\' -Force");
            ps.AppendLine("Copy-Item \"$tool\\cbsx.exe\" $w'desktopimaging\\' -Force");
            ps.AppendLine($"Copy-Item '{ssDir}' $w'desktopimaging\\' -Recurse -Force");
            ps.AppendLine($"& $w'desktopimaging\\sxsfounder.exe' $w'image' '{cfg.Arch}' *>$w'logs\\0-sxsfounder.log'; if($LASTEXITCODE -ne 0){{throw 'sxsfounder 失败'}}");
            // SKU 组件
            ps.AppendLine("$env:WINDOWS_WCP_INSKUASSEMBLY='1'");
            ps.AppendLine($"& $w'desktopimaging\\cbsx.exe' /o:$w'image\\Windows' /log:$w'logs\\1-skuassembly.log' /b:\"$files\\skuassembly.txt\" /s:$w'temp' *>&1; if($LASTEXITCODE -ne 0){{throw 'cbsx skuassembly 失败'}}");
            ps.AppendLine("Remove-Item Env:WINDOWS_WCP_INSKUASSEMBLY -ErrorAction SilentlyContinue");
            // 语言包
            ps.AppendLine($"& dism /logpath:$w'logs\\2-langpack.log' /scratchdir:$w'temp' /image:$w'image' /add-package /packagepath:'{lp}'; if($LASTEXITCODE -ne 0){{throw '加语言包失败'}}");
            // 版本 unattend
            ps.AppendLine($"Copy-Item $w'image\\Windows\\servicing\\Editions\\{cfg.Edition}Edition.xml' $w'{cfg.Edition}.xml' -Force");
            ps.AppendLine($"& dism /logpath:$w'logs\\3-edition.log' /scratchdir:$w'temp' /image:$w'image' /apply-unattend:$w'{cfg.Edition}.xml'; if($LASTEXITCODE -ne 0){{throw 'apply-unattend 失败'}}");
            ps.AppendLine($"& dism /logpath:$w'logs\\4-cur.log' /scratchdir:$w'temp' /image:$w'image' /get-currentedition /english *>&1");
            ps.AppendLine($"& dism /logpath:$w'logs\\4-tgt.log' /scratchdir:$w'temp' /image:$w'image' /get-targeteditions /english *>&1");
            ps.AppendLine($"& dism /logpath:$w'logs\\5-intl.log' /scratchdir:$w'temp' /image:$w'image' /set-allintl:{cfg.Lang} /english; if($LASTEXITCODE -ne 0){{throw 'set-allintl 失败'}}");
            // 组件存储清理
            ps.AppendLine($"& dism /logpath:$w'logs\\6-analyze.log' /scratchdir:$w'temp' /image:$w'image' /cleanup-image /analyzecomponentstore /english *>&1");
            ps.AppendLine($"& dism /logpath:$w'logs\\6-cleanup.log' /scratchdir:$w'temp' /image:$w'image' /cleanup-image /startcomponentcleanup /english *>&1");
            // finishimg：写 ImageState
            ps.AppendLine("New-Item -ItemType Directory -Force -Path $w'image\\Windows\\Setup\\State' | Out-Null");
            ps.AppendLine("Set-Content -Path $w'image\\Windows\\Setup\\State\\State.ini' -Value '[State]','ImageState=IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE' -Encoding ascii");
            ps.AppendLine("& reg load HKLM\\CBSS_SOFTWARE $w'image\\Windows\\System32\\config\\SOFTWARE; if($LASTEXITCODE -ne 0){throw 'reg load SOFTWARE 失败'}");
            ps.AppendLine("& reg load HKLM\\CBSS_SYSTEM $w'image\\Windows\\System32\\config\\SYSTEM; if($LASTEXITCODE -ne 0){throw 'reg load SYSTEM 失败'}");
            ps.AppendLine("& reg import \"$files\\imagestate.reg\"");
            ps.AppendLine("& reg unload HKLM\\CBSS_SOFTWARE");
            ps.AppendLine("& reg unload HKLM\\CBSS_SYSTEM");
            // 可选 capture
            if (cfg.CaptureWim)
                ps.AppendLine($"New-Item -ItemType Directory -Force -Path $w'iso\\sources' | Out-Null; & dism /capture-image /imagefile:$w'iso\\sources\\install.wim' /capturedir:$w'image' /name:'{cfg.WimName.Replace("'", "''")}' /compress:max; if($LASTEXITCODE -ne 0){{throw 'capture 失败'}}");
            ps.AppendLine("Write-Output '=== EXPLODE-SXS-DONE ==='");

            string scriptFile = Path.Combine(Path.GetTempPath(), "icd_explode_" + Guid.NewGuid().ToString("N")[..8] + ".ps1");
            File.WriteAllText(scriptFile, ps.ToString(), new UTF8Encoding(true));   // BOM：PS5.1 正确读中文

            string logPath = Path.Combine(Path.GetTempPath(), "icd_explode_run.log");
            log("explodeSxS 提权执行中（脚本: " + scriptFile + "）…");

            int? code = await RunElevatedPowerShellAsync(scriptFile, logPath, log, ct, timeoutMs);
            try { File.Delete(scriptFile); } catch { }
            return code;
        }

        // 提权 PowerShell + 日志回传（与 UpdateAppRunnerService 同套路，但跑脚本文件）
        static async Task<int?> RunElevatedPowerShellAsync(string scriptFile, string logPath,
            Action<string> onLine, CancellationToken ct, int timeoutMs)
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\" *>&1 | Tee-Object -FilePath '{logPath}' -Encoding utf8",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process? proc;
            try { proc = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { onLine("[runner] UAC 被拒绝，未执行。"); return null; }
            if (proc == null) { onLine("[runner] 无法启动提权进程。"); return null; }

            var sw = Stopwatch.StartNew();
            long lastLen = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (File.Exists(logPath))
                    {
                        var fi = new FileInfo(logPath);
                        if (fi.Length > lastLen)
                        {
                            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fs.Seek(lastLen, SeekOrigin.Begin);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            string? line;
                            while ((line = sr.ReadLine()) != null) onLine(line);
                            lastLen = fs.Position;
                        }
                    }
                }
                catch { }
                if (proc.HasExited) break;
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
            }
            if (!proc.HasExited) { try { proc.WaitForExit(8000); } catch { } }
            try
            {
                if (File.Exists(logPath))
                {
                    var fi = new FileInfo(logPath);
                    if (fi.Length > lastLen)
                    {
                        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(lastLen, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        string? line;
                        while ((line = sr.ReadLine()) != null) onLine(line);
                    }
                }
            }
            catch { }
            if (!proc.HasExited) { onLine("[runner] 超时：提权进程可能仍在运行。"); return null; }
            onLine($"[runner] explodeSxS 退出，代码 {proc.ExitCode}。");
            return proc.ExitCode;
        }
    }
}
