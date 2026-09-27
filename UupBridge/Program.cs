using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnifiedUpdatePlatform.Services.WindowsUpdate;
using UnifiedUpdatePlatform.Services.WindowsUpdate.Downloads;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Expected scan or download command.");
            return args[0] switch
            {
                "scan" when args.Length == 3 => await ScanAsync(args[1], args[2]),
                "download" when args.Length == 5 => await DownloadAsync(args[1], args[2], args[3], args[4]),
                _ => throw new ArgumentException("Usage: scan <request.json> <snapshot.json> | download <payload.gz> <updateId> <outputDir> <arch>")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ScanAsync(string requestPath, string snapshotPath)
    {
        var request = JsonSerializer.Deserialize<ScanRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)
            ?? throw new InvalidDataException("Scan request is empty.");
        var skuName = request.ReportingSku switch
        {
            "ServerStandard" => nameof(OSSkuId.StandardServer),
            "ServerDatacenter" => nameof(OSSkuId.DatacenterServer),
            _ => request.ReportingSku
        };
        if (!Enum.TryParse<OSSkuId>(skuName, true, out var sku))
            throw new ArgumentException($"Unknown reporting SKU: {request.ReportingSku}");
        if (!Enum.TryParse<MachineType>(request.Architecture, true, out var architecture))
            throw new ArgumentException($"Unknown architecture: {request.Architecture}");

        var ctac = new CTAC(sku, request.ReportingVersion, architecture, request.FlightRing,
            request.FlightingBranchName, request.BranchReadinessLevel, request.CurrentBranch,
            request.ReleaseType, request.SyncCurrentVersionOnly, false, request.ContentType);
        var filters = request.Filters.Length == 0
            ? new[] { FileExchangeV3UpdateFilter.ProductRelease }
            : request.Filters.Select(f => Enum.Parse<FileExchangeV3UpdateFilter>(f, true)).Distinct().ToArray();

        var payloads = new Dictionary<string, UpdateData>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            Console.WriteLine($"Querying Windows Update ({filter})...");
            var updates = await FE3Handler.GetUpdates(null, ctac, string.Empty, filter);
            foreach (var update in updates)
            {
                var id = update.Xml?.UpdateIdentity?.UpdateID;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = update.Xml?.LocalizedProperties?.Title ?? id;
                var build = Regex.Match(title, @"\d+\.\d+\.\d+\.\d+").Value;
                entries[id] = new ScanEntry
                {
                    UpdateId = id,
                    Title = title,
                    Description = update.Xml?.LocalizedProperties?.Description ?? string.Empty,
                    Build = build,
                    ServerDate = update.Xml?.ExtendedProperties?.CreationDate ?? string.Empty,
                    Filter = filter.ToString(),
                    FlightRing = request.FlightRing
                };
                payloads[id] = update;
            }
            Console.WriteLine($"{entries.Count} unique update(s) found.");
        }

        var payloadPath = Path.ChangeExtension(snapshotPath, ".data.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!);
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(new ScanSnapshot
        {
            ScanTime = DateTimeOffset.Now,
            TotalRequests = filters.Length,
            Entries = entries.Values.OrderByDescending(e => e.ServerDate).ToList(),
            PayloadFile = Path.GetFileName(payloadPath),
            ReportingSku = request.ReportingSku,
            Architecture = request.Architecture,
            FlightRing = request.FlightRing,
            CurrentBranch = request.CurrentBranch
        }, JsonOptions));
        await using (var file = File.Create(payloadPath))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await JsonSerializer.SerializeAsync(gzip, payloads, JsonOptions);
        Console.WriteLine($"Snapshot saved: {snapshotPath}");
        return 0;
    }

    private static async Task<int> DownloadAsync(string payloadPath, string updateId, string outputDirectory, string archText)
    {
        if (!Enum.TryParse<MachineType>(archText, true, out var architecture))
            throw new ArgumentException($"Unknown architecture: {archText}");
        if (!File.Exists(payloadPath)) throw new FileNotFoundException("Scan payload file not found.", payloadPath);

        await using var file = File.OpenRead(payloadPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var payloads = await JsonSerializer.DeserializeAsync<Dictionary<string, UpdateData>>(gzip, JsonOptions)
            ?? throw new InvalidDataException("Scan payload file is empty.");
        if (!payloads.TryGetValue(updateId, out var update))
            throw new KeyNotFoundException($"Update {updateId} is not present in the scan payload.");

        Directory.CreateDirectory(outputDirectory);
        Console.WriteLine($"Downloading: {update.Xml?.LocalizedProperties?.Title ?? updateId}");
        var result = await UpdateUtils.ProcessUpdateAsync(update, outputDirectory, architecture,
            new ConsoleProgress(), WriteMetadata: true, UseAutomaticDownloadFolder: true);
        Console.WriteLine($"Download completed: {result}");
        return 0;
    }

    private sealed class ConsoleProgress : IProgress<GeneralDownloadProgress>
    {
        public void Report(GeneralDownloadProgress value)
        {
            Console.WriteLine($"Progress: {value.NumFilesDownloadedSuccessfully}/{value.NumFiles} files; " +
                              $"{value.DownloadedTotalBytes}/{value.EstimatedTotalBytes} bytes");
        }
    }

    private sealed class ScanRequest
    {
        public string ReportingSku { get; set; } = "Professional";
        public string ReportingVersion { get; set; } = "10.0.26100.1";
        public string Architecture { get; set; } = "amd64";
        public string FlightRing { get; set; } = "Retail";
        public string FlightingBranchName { get; set; } = string.Empty;
        public string BranchReadinessLevel { get; set; } = "CB";
        public string CurrentBranch { get; set; } = "ge_release";
        public string ReleaseType { get; set; } = "Production";
        public string ContentType { get; set; } = "Mainline";
        public bool SyncCurrentVersionOnly { get; set; }
        public string[] Filters { get; set; } = ["ProductRelease", "OSFlight"];
    }

    private sealed class ScanSnapshot
    {
        public DateTimeOffset ScanTime { get; set; }
        public int TotalRequests { get; set; }
        public List<ScanEntry> Entries { get; set; } = [];
        public string PayloadFile { get; set; } = string.Empty;
        public string ReportingSku { get; set; } = string.Empty;
        public string Architecture { get; set; } = string.Empty;
        public string FlightRing { get; set; } = string.Empty;
        public string CurrentBranch { get; set; } = string.Empty;
    }

    private sealed class ScanEntry
    {
        public string UpdateId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Build { get; set; } = string.Empty;
        public string ServerDate { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public string FlightRing { get; set; } = string.Empty;
    }
}
