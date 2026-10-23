using System.Diagnostics;
using System.IO;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    public sealed record UupMediaConversionRequest(
        string UupDirectory,
        string IsoPath,
        string TemporaryDirectory,
        string LanguageCode,
        string Edition);

    public sealed record UupMediaConversionPreflight(
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,
        long EstimatedRequiredBytes)
    {
        public bool IsReady => Errors.Count == 0;
    }

    public static class UupMediaConversionService
    {
        private const long MinimumWorkingSpace = 20L * 1024 * 1024 * 1024;
        private const string BundledConverterRelativePath = "UUPMediaConverter\\UUPMediaConverter.exe";

        public static UupMediaConversionPreflight Preflight(
            UupMediaConversionRequest request,
            Func<string, long>? availableFreeSpace = null,
            string? converterPathOverride = null)
        {
            try
            {
                return PreflightCore(request, availableFreeSpace, converterPathOverride);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or OverflowException)
            {
                return new UupMediaConversionPreflight(
                    new[] { $"Invalid conversion path or size: {ex.Message}" },
                    Array.Empty<string>(),
                    MinimumWorkingSpace);
            }
        }

        private static UupMediaConversionPreflight PreflightCore(
            UupMediaConversionRequest request,
            Func<string, long>? availableFreeSpace,
            string? converterPathOverride)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            long inputBytes = 0;

            var converterPath = converterPathOverride ?? GetBundledConverterPath();
            if (!File.Exists(converterPath))
                errors.Add($"The built-in UUP media converter is missing: {converterPath}");
            else
            {
                var cdImagePath = Path.Combine(Path.GetDirectoryName(converterPath)!, "CDImage", "cdimage.exe");
                if (!File.Exists(cdImagePath))
                    errors.Add($"The required CDImage utility is missing: {cdImagePath}");
            }

            if (!HasDotnet10Runtime())
                errors.Add(".NET 10 runtime was not found by 'dotnet --list-runtimes'. Install the .NET 10 Runtime to use the built-in converter.");

            if (!Directory.Exists(request.UupDirectory))
                errors.Add("The downloaded UUP input directory does not exist.");
            else
            {
                try
                {
                    inputBytes = GetDirectorySize(request.UupDirectory);
                    if (inputBytes == 0) errors.Add("The downloaded UUP input directory contains no files.");
                }
                catch (IOException ex) { errors.Add($"Unable to inspect UUP input files: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { errors.Add($"Unable to inspect UUP input files: {ex.Message}"); }
                catch (OverflowException ex) { errors.Add($"Unable to estimate UUP input size: {ex.Message}"); }
            }

            var estimate = Math.Max(MinimumWorkingSpace,
                inputBytes > long.MaxValue / 2 ? long.MaxValue : inputBytes * 2);
            if (string.IsNullOrWhiteSpace(request.LanguageCode) ||
                request.LanguageCode.Any(char.IsWhiteSpace))
                errors.Add("Enter a valid language code such as en-US.");

            string? isoParent = null;
            if (!string.Equals(Path.GetExtension(request.IsoPath), ".iso", StringComparison.OrdinalIgnoreCase))
                errors.Add("The output file must use the .iso extension.");
            else if (File.Exists(request.IsoPath))
                errors.Add("The output ISO already exists; choose a new path to prevent overwriting it.");
            else
            {
                isoParent = Path.GetDirectoryName(Path.GetFullPath(request.IsoPath));
                if (string.IsNullOrWhiteSpace(isoParent) || !Directory.Exists(isoParent))
                    errors.Add("The output ISO parent directory must already exist.");
            }

            if (!Directory.Exists(request.TemporaryDirectory))
                errors.Add("The temporary working directory must already exist.");
            else
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(request.TemporaryDirectory).Any())
                        errors.Add("Choose an empty temporary working directory.");
                }
                catch (IOException ex) { errors.Add($"Unable to inspect temporary directory: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { errors.Add($"Unable to inspect temporary directory: {ex.Message}"); }
            }

            var requiredByDrive = new Dictionary<string, (long Bytes, List<string> Uses)>(StringComparer.OrdinalIgnoreCase);
            if (isoParent != null) AddDriveRequirement(isoParent, estimate, "ISO output", requiredByDrive);
            if (Directory.Exists(request.TemporaryDirectory))
                AddDriveRequirement(request.TemporaryDirectory, estimate, "temporary workspace", requiredByDrive);
            foreach (var requirement in requiredByDrive)
            {
                try
                {
                    var freeBytes = availableFreeSpace != null
                        ? availableFreeSpace(requirement.Key)
                        : new DriveInfo(requirement.Key).AvailableFreeSpace;
                    if (freeBytes < requirement.Value.Bytes)
                        errors.Add($"{string.Join(" and ", requirement.Value.Uses)} drive has {FormatBytes(freeBytes)} free; combined estimated requirement is {FormatBytes(requirement.Value.Bytes)}.");
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    errors.Add($"Unable to verify free space for {requirement.Key}: {ex.Message}");
                }
            }
            warnings.Add("Space estimate is a conservative preflight heuristic (max of 20 GiB or twice the input size per destination), not a guarantee.");
            warnings.Add("The upstream converter does not integrate downloaded update packages into the ISO.");
            warnings.Add("The built-in UUP converter requires the .NET 10 runtime.");

            return new UupMediaConversionPreflight(errors, warnings, estimate);
        }

        public static async Task<int> RunAsync(
            UupMediaConversionRequest request,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var preflight = await Task.Run(() => Preflight(request), cancellationToken).ConfigureAwait(false);
            if (!preflight.IsReady)
                throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Errors));

            var start = CreateStartInfo(request);
            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Unable to start UUPMediaConverter.");
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var stdout = PumpAsync(process.StandardOutput, progress, null);
            var stderr = PumpAsync(process.StandardError, progress, "[stderr] ");
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }

        public static ProcessStartInfo CreateStartInfo(
            UupMediaConversionRequest request,
            string? converterPathOverride = null)
        {
            var converterPath = converterPathOverride ?? GetBundledConverterPath();
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(converterPath),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(converterPath))!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("desktop-convert");
            start.ArgumentList.Add("--uup-path");
            start.ArgumentList.Add(Path.GetFullPath(request.UupDirectory));
            start.ArgumentList.Add("--iso-path");
            start.ArgumentList.Add(Path.GetFullPath(request.IsoPath));
            start.ArgumentList.Add("--language-code");
            start.ArgumentList.Add(request.LanguageCode.Trim());
            start.ArgumentList.Add("--temp-path");
            start.ArgumentList.Add(Path.GetFullPath(request.TemporaryDirectory));
            start.ArgumentList.Add("--no-key-prompt");
            if (!string.IsNullOrWhiteSpace(request.Edition))
            {
                start.ArgumentList.Add("--edition");
                start.ArgumentList.Add(request.Edition.Trim());
            }

            return start;
        }

        public static string GetBundledConverterPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BundledConverterRelativePath);

        private static async Task PumpAsync(StreamReader reader, IProgress<string>? progress, string? prefix)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                progress?.Report((prefix ?? string.Empty) + line);
        }

        private static long GetDirectorySize(string root)
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory))
                    total = checked(total + new FileInfo(file).Length);
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
            }
            return total;
        }

        private static bool HasDotnet10Runtime()
        {
            try
            {
                var output = new StringBuilder();
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "--list-runtimes" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process == null) return false;
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (output) output.AppendLine(e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    return false;
                }
                process.WaitForExit();
                string runtimeList;
                lock (output) runtimeList = output.ToString();
                return process.ExitCode == 0 && runtimeList.Split('\n')
                    .Any(line => line.StartsWith("Microsoft.NETCore.App 10.", StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static void AddDriveRequirement(
            string directory,
            long requiredBytes,
            string label,
            IDictionary<string, (long Bytes, List<string> Uses)> requirements)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException($"Unable to determine the drive for {label}.");
            if (requirements.TryGetValue(root, out var current))
            {
                current.Uses.Add(label);
                requirements[root] = (current.Bytes > long.MaxValue - requiredBytes
                    ? long.MaxValue : current.Bytes + requiredBytes, current.Uses);
            }
            else
                requirements[root] = (requiredBytes, new List<string> { label });
        }

        private static string FormatBytes(long bytes) => $"{bytes / (1024d * 1024 * 1024):F1} GiB";
    }
}
