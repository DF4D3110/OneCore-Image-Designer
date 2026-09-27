using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public sealed class UupBridgeService
    {
        private static readonly string BridgeExecutable = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "UupBridge", "UupBridge.exe");

        public async Task<UupScanSnapshot> ScanAsync(
            UupScanRequest request,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            EnsureBridge();
            var scanDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OneCoreImageDesigner", "UUP", "Scans");
            Directory.CreateDirectory(scanDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var requestPath = Path.Combine(scanDirectory, $"request-{stamp}.json");
            var snapshotPath = Path.Combine(scanDirectory, $"scan-{stamp}.json");
            await File.WriteAllTextAsync(requestPath,
                System.Text.Json.JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            try
            {
                await RunBridgeAsync(["scan", requestPath, snapshotPath], log, cancellationToken)
                    .ConfigureAwait(false);
                return await Task.Run(() =>
                {
                    var snapshot = new UupIntegrationService().LoadSnapshot(snapshotPath);
                    snapshot.SnapshotPath = snapshotPath;
                    return snapshot;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(requestPath); }
                catch (IOException) { }
            }
        }

        public async Task DownloadAsync(
            UupScanSnapshot snapshot,
            UupScanEntry selectedEntry,
            string outputDirectory,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotPath))
                throw new InvalidOperationException("当前更新列表没有对应的扫描快照。请重新扫描或从带 payload 的快照载入。");
            var payloadPath = Path.Combine(Path.GetDirectoryName(snapshot.SnapshotPath)!,
                string.IsNullOrWhiteSpace(snapshot.PayloadFile)
                    ? Path.GetFileNameWithoutExtension(snapshot.SnapshotPath) + ".data.json.gz"
                    : snapshot.PayloadFile);
            if (!File.Exists(payloadPath))
                throw new FileNotFoundException("扫描快照的更新 payload 不存在，请重新扫描。", payloadPath);

            EnsureBridge();
            await RunBridgeAsync(
                ["download", payloadPath, selectedEntry.UpdateId, outputDirectory, snapshot.Architecture],
                log, cancellationToken).ConfigureAwait(false);
        }

        private static void EnsureBridge()
        {
            if (!File.Exists(BridgeExecutable))
                throw new FileNotFoundException("仓库内 UUP 桥接程序不存在。请重新构建或安装完整应用。", BridgeExecutable);
        }

        private static Task RunBridgeAsync(
            string[] arguments,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = BridgeExecutable,
                WorkingDirectory = Path.GetDirectoryName(BridgeExecutable)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            return RunProcessAsync(psi, log, cancellationToken);
        }

        private static async Task RunProcessAsync(
            ProcessStartInfo startInfo,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Report("[stderr] " + e.Data); };
            if (!process.Start()) throw new InvalidOperationException($"无法启动进程：{startInfo.FileName}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"UUP 核心进程退出码为 {process.ExitCode}。请查看操作日志。");
        }
    }
}
