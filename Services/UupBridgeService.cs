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
        private readonly string _bridgeExecutable;
        private readonly Func<string[], IProgress<string>?, CancellationToken, Task> _runBridge;

        public UupBridgeService(
            string? bridgeExecutable = null,
            Func<string[], IProgress<string>?, CancellationToken, Task>? runBridge = null)
        {
            _bridgeExecutable = bridgeExecutable ?? Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "UupBridge", "UupBridge.exe");
            _runBridge = runBridge ?? RunBridgeProcessAsync;
        }

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
            var stamp = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
            var requestPath = Path.Combine(scanDirectory, $"request-{stamp}.json");
            var snapshotPath = Path.Combine(scanDirectory, $"scan-{stamp}.json");
            try
            {
                await File.WriteAllTextAsync(requestPath,
                    System.Text.Json.JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
                await _runBridge(["scan", requestPath, snapshotPath], log, cancellationToken)
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
            string language,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotPath))
                throw new InvalidOperationException("当前更新列表没有对应的扫描快照。请重新扫描或从带 payload 的快照载入。");
            var payloadPath = UupPayloadLocator.ResolvePayloadPath(snapshot.SnapshotPath, snapshot.PayloadFile);
            if (!File.Exists(payloadPath))
                throw new FileNotFoundException("扫描快照的更新 payload 不存在，请重新扫描。", payloadPath);

            EnsureBridge();
            const int maximumAttempts = 3;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    log?.Report($"开始下载（尝试 {attempt}/{maximumAttempts}）...");
                    await _runBridge(
                        ["download", payloadPath, ResolvePayloadKey(selectedEntry), outputDirectory, snapshot.Architecture, language],
                        log, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    log?.Report($"下载尝试 {attempt}/{maximumAttempts} 失败：{ex.Message}");
                    if (attempt < maximumAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException($"UUP 下载在 {maximumAttempts} 次尝试后失败。请查看操作日志。", lastError);
        }

        public async Task<string[]> GetAvailableLanguagesAsync(
            UupScanSnapshot snapshot,
            UupScanEntry selectedEntry,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotPath))
                throw new InvalidOperationException("当前更新列表没有对应的扫描快照。请重新扫描或从带 payload 的快照载入。");
            var payloadPath = UupPayloadLocator.ResolvePayloadPath(snapshot.SnapshotPath, snapshot.PayloadFile);
            if (!File.Exists(payloadPath))
                throw new FileNotFoundException("扫描快照的更新 payload 不存在，请重新扫描。", payloadPath);

            EnsureBridge();
            var outputPath = Path.Combine(Path.GetTempPath(), $"ocid-uup-languages-{Guid.NewGuid():N}.json");
            try
            {
                await _runBridge(["languages", payloadPath, ResolvePayloadKey(selectedEntry), outputPath], log, cancellationToken)
                    .ConfigureAwait(false);
                var content = await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(content)
                    ?? throw new InvalidDataException("UUP bridge returned an empty language list.");
            }
            finally
            {
                try { File.Delete(outputPath); }
                catch (IOException) { }
            }
        }

        private void EnsureBridge()
        {
            if (!File.Exists(_bridgeExecutable))
                throw new FileNotFoundException("仓库内 UUP 桥接程序不存在。请重新构建或安装完整应用。", _bridgeExecutable);
        }

        private static string ResolvePayloadKey(UupScanEntry entry) =>
            string.IsNullOrWhiteSpace(entry.PayloadKey) ? entry.UpdateId : entry.PayloadKey;

        private Task RunBridgeProcessAsync(
            string[] arguments,
            IProgress<string>? log,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _bridgeExecutable,
                WorkingDirectory = Path.GetDirectoryName(_bridgeExecutable)!,
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
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                process.WaitForExit();
                throw;
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"UUP 核心进程退出码为 {process.ExitCode}。请查看操作日志。");
        }
    }
}
