using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// updateapp.exe 提权执行器。
    /// 因为 updateapp 需要管理员权限（挂载/服务/提交），而 .NET 的 runas 无法直接重定向输出，
    /// 这里用「提权 PowerShell + 日志文件回传」模式：与 addonkit 命令执行服务同一套路。
    /// </summary>
    public static class UpdateAppRunnerService
    {
        /// <summary>
        /// 以管理员权限运行 updateapp 并实时回传输出行。
        /// </summary>
        /// <param name="exePath">updateapp.exe 全路径</param>
        /// <param name="arguments">完整命令行参数（命令字 + 位置参数）</param>
        /// <param name="logPath">输出日志落盘路径（同时作为回传媒介）</param>
        /// <param name="onLine">每行输出回调（UI 线程外调用，调用方自行 marshal）</param>
        /// <param name="ct">取消令牌（仅停止回传轮询，不杀提权进程）</param>
        /// <returns>进程退出码；进程未能在超时内结束时返回 null</returns>
        public static async Task<int?> RunAsync(
            string exePath,
            string arguments,
            string logPath,
            Action<string> onLine,
            CancellationToken ct,
            int timeoutMs = 30 * 60 * 1000)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (File.Exists(logPath)) File.Delete(logPath);

            // 提权 PowerShell：执行 updateapp 并把全部流写入日志
            var ps = "powershell.exe";
            var inner = $"& '{exePath}' {arguments} *>&1 | Tee-Object -FilePath '{logPath}' -Encoding utf8";
            var psi = new ProcessStartInfo
            {
                FileName = ps,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{inner.Replace("\"", "`\"")}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = false,
            };

            Process? proc = null;
            try
            {
                proc = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                onLine("[runner] UAC 被拒绝或无法提权，命令未执行。");
                return null;
            }
            if (proc == null)
            {
                onLine("[runner] 无法启动提权进程。");
                return null;
            }

            onLine($"[runner] updateapp 已提权启动（PID {proc.Id}），等待输出…");

            var sw = Stopwatch.StartNew();
            long lastLen = 0;
            var tail = new StringBuilder();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) break;

                // 增量读日志回传
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
                catch { /* 日志被占用时跳过本轮 */ }

                if (proc.HasExited) break;
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
            }

            // 收尾：等进程退出并冲刷日志尾部
            if (!proc.HasExited)
            {
                try { proc.WaitForExit(8000); } catch { }
            }
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

            if (!proc.HasExited)
            {
                onLine("[runner] 超时：回传已停止，提权进程可能仍在运行。");
                return null;
            }
            onLine($"[runner] updateapp 退出，代码 {proc.ExitCode}。");
            return proc.ExitCode;
        }
    }
}
