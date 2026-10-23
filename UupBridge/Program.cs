using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnifiedUpdatePlatform.Services.WindowsUpdate;
using UnifiedUpdatePlatform.Services.WindowsUpdate.Downloads;
using OneCore_Image_Designer.Services;

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
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args.Length == 0) throw new ArgumentException("Expected scan or download command.");
            return args[0] switch
            {
                "scan" when args.Length == 3 => await ScanAsync(args[1], args[2]),
                "languages" when args.Length == 4 => await GetLanguagesAsync(args[1], args[2], args[3]),
                "download" when args.Length == 6 => await DownloadAsync(args[1], args[2], args[3], args[4], args[5], cancellation.Token),
                _ => throw new ArgumentException("Usage: scan <request.json> <snapshot.json> | languages <payload.gz> <updateId> <languages.json> | download <payload.gz> <updateId> <outputDir> <arch> <language>")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
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
        if (!Enum.TryParse<MachineType>(UupQueryParser.ParseArchitecture(request.Architecture), true, out var architecture))
            throw new ArgumentException($"Unknown architecture: {request.Architecture}");

        var filters = request.Filters.Length == 0
            ? new[] { FileExchangeV3UpdateFilter.ProductRelease }
            : UupQueryParser.ParseFilters(request.Filters)
                .Select(f => Enum.Parse<FileExchangeV3UpdateFilter>(f, true)).ToArray();

        var queryPlans = UupQueryPlanner.Build(
            filters.Select(filter => filter.ToString()),
            request.ReportingVersion, request.FlightRing, request.FlightingBranchName, request.CurrentBranch);
        var payloads = new Dictionary<string, UpdateData>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var failedFilters = new List<string>();
        var profileSummaries = new List<QueryProfileSummary>();
        foreach (var plan in queryPlans)
        {
            var filter = Enum.Parse<FileExchangeV3UpdateFilter>(plan.Filter, true);
            var profileName = $"{plan.Filter} | {plan.FlightRing} | {plan.CurrentBranch} | {plan.FlightingBranchName}";
            Console.WriteLine($"Querying Windows Update ({profileName}; version={plan.ReportingVersion})...");
            try
            {
                var queryCtac = new CTAC(sku, UupQueryParser.ParseVersion(plan.ReportingVersion), architecture, plan.FlightRing,
                    plan.FlightingBranchName, request.BranchReadinessLevel, plan.CurrentBranch,
                    request.ReleaseType, request.SyncCurrentVersionOnly, false, request.ContentType);
                var updates = (await FE3Handler.GetUpdates(null, queryCtac, string.Empty, filter)).ToList();
                var newCount = 0;
                var skippedCount = 0;
                foreach (var update in updates)
                {
                    var id = update.Xml?.UpdateIdentity?.UpdateID;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        skippedCount++;
                        continue;
                    }
                    var revision = update.Xml?.UpdateIdentity?.RevisionNumber ?? string.Empty;
                    var identityKey = $"{id}|{revision}";
                    var title = update.Xml?.LocalizedProperties?.Title ?? id;
                    var releaseVersion = update.Xml?.ExtendedProperties?.ReleaseVersion ?? string.Empty;
                    var build = Regex.Match(title, @"\d+\.\d+\.\d+\.\d+").Value;
                    if (build.Length == 0)
                        build = Regex.Match(releaseVersion, @"\d+\.\d+\.\d+\.\d+").Value;
                    if (!entries.TryGetValue(identityKey, out var entry))
                    {
                        entry = new ScanEntry
                        {
                            ProductLine = request.ReportingSku,
                            Architecture = request.Architecture,
                            UpdateId = id,
                            PayloadKey = identityKey,
                            Title = title,
                            Description = update.Xml?.LocalizedProperties?.Description ?? string.Empty,
                            Build = build,
                            ReleaseVersion = releaseVersion,
                            Revision = update.Xml?.UpdateIdentity?.RevisionNumber ?? string.Empty,
                            ServerDate = update.UpdateInfo?.Deployment?.LastChangeTime
                                ?? update.Xml?.ExtendedProperties?.CreationDate ?? string.Empty,
                            Filter = filter.ToString(),
                            FlightRing = plan.FlightRing,
                            FileCount = update.Xml?.Files?.File?.Length ?? 0,
                            EstimatedDownloadBytes = GetEstimatedDownloadBytes(update),
                            Profiles = new List<string>()
                        };
                        entries[identityKey] = entry;
                        newCount++;
                    }
                    if (!entry.Profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
                        entry.Profiles.Add(profileName);
                    payloads[identityKey] = update;
                }
                profileSummaries.Add(new QueryProfileSummary
                {
                    Profile = profileName,
                    ResultCount = updates.Count,
                    NewCount = newCount,
                    SkippedCount = skippedCount
                });
                Console.WriteLine($"Profile returned {updates.Count} update(s), {newCount} new; {entries.Count} distinct update(s) total.");
            }
            catch (Exception ex)
            {
                var error = $"{ex.GetType().Name}: {ex.Message}";
                failedFilters.Add(profileName);
                profileSummaries.Add(new QueryProfileSummary { Profile = profileName, Error = error });
                Console.Error.WriteLine($"Query profile {profileName} failed: {error}");
            }
        }

        if (failedFilters.Count == queryPlans.Length)
            throw new InvalidOperationException("Every UUP query profile failed; no snapshot was created.");

        var payloadPath = Path.ChangeExtension(snapshotPath, ".data.json.gz");
        var snapshotDirectory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!;
        Directory.CreateDirectory(snapshotDirectory);
        var payloadTemporaryPath = Path.Combine(snapshotDirectory, $".{Guid.NewGuid():N}.payload.tmp");
        var snapshotTemporaryPath = Path.Combine(snapshotDirectory, $".{Guid.NewGuid():N}.snapshot.tmp");
        try
        {
            await using (var file = File.Create(payloadTemporaryPath))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                await JsonSerializer.SerializeAsync(gzip, payloads, JsonOptions);

            await File.WriteAllTextAsync(snapshotTemporaryPath, JsonSerializer.Serialize(new ScanSnapshot
            {
                ScanTime = DateTimeOffset.UtcNow,
                TotalRequests = queryPlans.Length,
                FailedRequests = failedFilters.Count,
                Entries = entries.Values.OrderByDescending(e => e.ServerDate).ToList(),
                QueryProfiles = profileSummaries,
                PayloadFile = Path.GetFileName(payloadPath),
                ReportingSku = request.ReportingSku,
                Architecture = request.Architecture,
                FlightRing = string.IsNullOrWhiteSpace(request.FlightRing) ? "Retail + External" : request.FlightRing,
                CurrentBranch = request.CurrentBranch
            }, JsonOptions));
            File.Move(payloadTemporaryPath, payloadPath);
            File.Move(snapshotTemporaryPath, snapshotPath);
        }

        finally
        {
            if (File.Exists(payloadTemporaryPath)) File.Delete(payloadTemporaryPath);
            if (File.Exists(snapshotTemporaryPath)) File.Delete(snapshotTemporaryPath);
        }

        if (failedFilters.Count > 0)
            Console.Error.WriteLine($"Snapshot is partial: {failedFilters.Count}/{queryPlans.Length} query profile(s) failed ({string.Join(", ", failedFilters)}).");
        Console.WriteLine($"Snapshot saved: {snapshotPath}");
        return 0;
    }

    private static long GetEstimatedDownloadBytes(UpdateData update)
    {
        long total = 0;
        foreach (var file in update.Xml?.Files?.File ?? Array.Empty<UnifiedUpdatePlatform.Services.WindowsUpdate.CExtendedUpdateInfoXml.File>())
            if (long.TryParse(file.Size, out var size) && size > 0 && total <= long.MaxValue - size)
                total += size;
        return total;
    }

    private static async Task<int> DownloadAsync(
        string payloadPath,
        string updateId,
        string outputDirectory,
        string archText,
        string language,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<MachineType>(UupQueryParser.ParseArchitecture(archText), true, out var architecture))
            throw new ArgumentException($"Unknown architecture: {archText}");
        if (!File.Exists(payloadPath)) throw new FileNotFoundException("Scan payload file not found.", payloadPath);

        await using var file = File.OpenRead(payloadPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var payloads = await JsonSerializer.DeserializeAsync<Dictionary<string, UpdateData>>(gzip, JsonOptions)
            ?? throw new InvalidDataException("Scan payload file is empty.");
        var update = UupPayloadLocator.FindUpdate(payloads, updateId);

        Directory.CreateDirectory(outputDirectory);
        Console.WriteLine($"Downloading: {update.Xml?.LocalizedProperties?.Title ?? updateId}");
        var result = await UpdateUtils.ProcessUpdateAsync(update, outputDirectory, architecture,
            new ConsoleProgress(), Language: language, WriteMetadata: true, UseAutomaticDownloadFolder: true,
            cancellationToken: cancellationToken);
        Console.WriteLine($"Download completed: {result}");
        return 0;
    }

    private static async Task<int> GetLanguagesAsync(string payloadPath, string updateId, string outputPath)
    {
        if (!File.Exists(payloadPath)) throw new FileNotFoundException("Scan payload file not found.", payloadPath);

        await using var file = File.OpenRead(payloadPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var payloads = await JsonSerializer.DeserializeAsync<Dictionary<string, UpdateData>>(gzip, JsonOptions)
            ?? throw new InvalidDataException("Scan payload file is empty.");
        var update = UupPayloadLocator.FindUpdate(payloads, updateId);
        var languages = (await update.GetAvailableLanguagesAsync())
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(languages));
        Console.WriteLine($"Found {languages.Length} available language(s).");
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
        public string ReportingVersion { get; set; } = string.Empty;
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
        public int FailedRequests { get; set; }
        public List<ScanEntry> Entries { get; set; } = [];
        public List<QueryProfileSummary> QueryProfiles { get; set; } = [];
        public string PayloadFile { get; set; } = string.Empty;
        public string ReportingSku { get; set; } = string.Empty;
        public string Architecture { get; set; } = string.Empty;
        public string FlightRing { get; set; } = string.Empty;
        public string CurrentBranch { get; set; } = string.Empty;
    }

    private sealed class ScanEntry
    {
        public string UpdateId { get; set; } = string.Empty;
        public string PayloadKey { get; set; } = string.Empty;
        public string ProductLine { get; set; } = string.Empty;
        public string Architecture { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Build { get; set; } = string.Empty;
        public string ReleaseVersion { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public string ServerDate { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public string FlightRing { get; set; } = string.Empty;
        public List<string> Profiles { get; set; } = [];
        public int FileCount { get; set; }
        public long EstimatedDownloadBytes { get; set; }
    }

    private sealed class QueryProfileSummary
    {
        public string Profile { get; set; } = string.Empty;
        public int ResultCount { get; set; }
        public int NewCount { get; set; }
        public int SkippedCount { get; set; }
        public string Error { get; set; } = string.Empty;
    }
}
