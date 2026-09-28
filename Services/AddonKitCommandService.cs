using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 内置 addonkit（Microsoft iot-adk-addonkit 17763-v7）命令执行服务。
    /// 以提权 PowerShell 窗口运行 IoTCoreImaging 命令：先 Import-Module + Open-IoTWorkspace，
    /// 再执行目标命令；用 Start-Transcript 把输出（含 Write-Host）落盘，主程序轮询增量读取回传日志。
    /// 之所以提权：New-IoTFFUImage 等内部直接调用 imageapp/PkgGen，需管理员权限，外层会话必须提权。
    /// </summary>
    public sealed class AddonKitCommandService
    {
        /// <summary>在指定工作区中执行一条 IoTCoreImaging 命令，返回进程退出码。</summary>
        /// <param name="workspaceXml">要打开的 IoTWorkspace.xml（绝对路径）。</param>
        /// <param name="command">要执行的命令，如 "New-IoTFFUImage 'SampleA' 'Test'"。</param>
        /// <param name="log">增量日志（回调投递到创建 Progress 的线程上下文）。</param>
        public async Task<int> RunWorkspaceCommandAsync(
            string workspaceXml,
            string command,
            IProgress<string>? log,
            CancellationToken ct = default)
        {
            if (!AddonKitShellService.IsModuleAvailable)
                throw new InvalidOperationException("未找到内置 IoTCoreImaging 模块：" + AddonKitShellService.ModuleManifest);
            if (string.IsNullOrWhiteSpace(workspaceXml) || !File.Exists(workspaceXml))
                throw new FileNotFoundException("工作区 XML 不存在：" + workspaceXml);
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("命令不能为空", nameof(command));

            var toolsDir = Path.Combine(AdkToolService.AddonKitRoot, "Tools");
            var runId = Guid.NewGuid().ToString("N");
            var ps1 = Path.Combine(Path.GetTempPath(), $"icd_addonkit_{runId}.ps1");
            var transcript = Path.Combine(Path.GetTempPath(), $"icd_addonkit_{runId}.log");

            // 路径含空格/特殊字符：脚本内一律用单引号字符串，内部单引号转义为两单引号。
            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("try {");
            script.AppendLine("  Start-Transcript -Path '" + Esc(transcript) + "' -Force | Out-Null");
            script.AppendLine("  $env:PSModulePath = '" + Esc(toolsDir) + ";' + $env:PSModulePath");
            script.AppendLine("  Import-Module IoTCoreImaging.psd1 -Force");
            script.AppendLine("  Open-IoTWorkspace '" + Esc(workspaceXml) + "'");
            script.AppendLine("  " + command);
            script.AppendLine("  Stop-Transcript | Out-Null");
            script.AppendLine("  exit 0");
            script.AppendLine("} catch {");
            script.AppendLine("  Write-Host ('ERROR: ' + $_)");
            script.AppendLine("  try { Stop-Transcript | Out-Null } catch {}");
            script.AppendLine("  exit 1");
            script.AppendLine("}");
            File.WriteAllText(ps1, script.ToString(), new UTF8Encoding(true)); // BOM：命令含中文时 PS5.1 必需

            log?.Report("命令已提交（提权窗口可能弹出 UAC 提示）：" + command);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = AdkToolService.AddonKitRoot,
                UseShellExecute = true,   // Verb=runas 要求 ShellExecute
                CreateNoWindow = false,
                Verb = "runas"
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(ps1);

            try
            {
                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("无法启动提权 PowerShell 进程");
                using var reg = ct.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });

                var lineState = new LineStreamState();
                long pos = 0;
                while (!ct.IsCancellationRequested)
                {
                    pos = PumpOnce(transcript, pos, lineState, log);
                    if (process.HasExited) break;
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }

                // 进程已退出：transcript 已关闭，从游标读到 EOF 收尾
                for (int guard = 0; guard < 40; guard++)
                {
                    long next = PumpOnce(transcript, pos, lineState, log);
                    if (next == pos) break;
                    pos = next;
                }
                lineState.FlushRemainder(log);

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var code = process.ExitCode;
                log?.Report(code == 0 ? "命令完成（退出码 0）。" : $"命令失败（退出码 {code}），详见上方日志。");
                return code;
            }
            finally
            {
                TryDelete(ps1);
                TryDelete(transcript);
            }
        }

        /// <summary>读取 transcript 新增字节并按行推送，返回新游标。</summary>
        private static long PumpOnce(string path, long from, LineStreamState state, IProgress<string>? log)
        {
            try
            {
                if (!File.Exists(path)) return from;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= from) return from;
                fs.Seek(from, SeekOrigin.Begin);
                var len = (int)(fs.Length - from);
                var buf = new byte[len];
                int got = fs.Read(buf, 0, len);
                if (got > 0) state.Append(Encoding.Unicode.GetString(buf, 0, got), log); // transcript 为 UTF-16LE
                return from + got;
            }
            catch (IOException) { return from; }        // 提权进程独占写入时允许瞬时失败
            catch (UnauthorizedAccessException) { return from; }
        }

        private static string Esc(string s) => s.Replace("'", "''");

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }

        /// <summary>UTF-16 文本按行切分的小状态机（保留未完结行片段）。</summary>
        private sealed class LineStreamState
        {
            private string _carry = "";

            public void Append(string chunk, IProgress<string>? log)
            {
                if (log is null) return;
                var data = _carry + chunk;
                int lastBreak = data.LastIndexOf('\n');
                if (lastBreak < 0) { _carry = data; return; }
                _carry = data[(lastBreak + 1)..];
                foreach (var raw in data[..lastBreak].Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length > 0) log.Report(line);
                }
            }

            public void FlushRemainder(IProgress<string>? log)
            {
                if (log is null) return;
                var line = _carry.TrimEnd('\r');
                _carry = "";
                if (line.Length > 0) log.Report(line);
            }
        }
    }
}
