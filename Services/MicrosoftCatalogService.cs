using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services;

public sealed record MicrosoftCatalogSearchOptions(
    string Search = "",
    string Architecture = "All",
    string? OperatingSystem = null,
    string? Version = null,
    string? UpdateType = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    bool IncludePreview = false,
    string? ProductFamily = null,
    string? KbNumber = null,
    string? Language = null);

public sealed record MicrosoftCatalogFile(
    string Name,
    string Url,
    long? Size = null,
    string? Sha256 = null)
{
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string SizeDisplay => Size is null ? "未知" : FormatBytes(Size.Value);
    private static string FormatBytes(long value) => value >= 1024 * 1024
        ? $"{value / 1024d / 1024d:F1} MB"
        : $"{value / 1024d:F0} KB";
}

public sealed record MicrosoftCatalogUpdate(
    string Guid,
    string Title,
    string Products,
    string Classification,
    DateTime? LastUpdated,
    string Version,
    string Size,
    IReadOnlyList<MicrosoftCatalogFile> Files,
    string Source = "Microsoft Update Catalog",
    string? KbNumber = null,
    string? Platform = null)
{
    public string DateDisplay => LastUpdated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知";
    public string FileSummary => Files.Count == 0 ? "未读取文件" : $"{Files.Count} 个文件";
    public string KbDisplay => string.IsNullOrWhiteSpace(KbNumber) ? "—" : KbNumber;
    public string PlatformDisplay => string.IsNullOrWhiteSpace(Platform) ? Products : Platform;
}

public sealed record CatalogDownloadItem(
    MicrosoftCatalogFile File,
    string Destination,
    bool UsedAria2,
    long BytesDownloaded,
    string? Sha256,
    string Status);

public sealed class MicrosoftCatalogService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex RowRegex = new(
        "<tr[^>]*>\\s*<td[^>]*>.*?</td>\\s*<td[^>]*>(?<title>.*?)</td>\\s*<td[^>]*>(?<products>.*?)</td>\\s*<td[^>]*>(?<classification>.*?)</td>\\s*<td[^>]*>(?<date>.*?)</td>\\s*<td[^>]*>(?<version>.*?)</td>\\s*<td[^>]*>(?<size>.*?)</td>\\s*<td[^>]*>.*?(?:id=\\\"(?<guid>[0-9a-f-]{20,})\\\"|value=\\\"(?<guid2>[0-9a-f-]{20,})\\\").*?</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(
        "<a[^>]+href=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<IReadOnlyList<MicrosoftCatalogUpdate>> SearchAsync(
        MicrosoftCatalogSearchOptions options,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var query = BuildQuery(options);
        if (query.Length == 0)
            throw new ArgumentException("请输入关键词、KB、版本，或选择产品/系统/架构等查询条件。", nameof(options));

        log?.Report($"正在查询 Microsoft Update Catalog：{query}");
        using var response = await Http.GetAsync(
            $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(query)}",
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var results = ParseSearchHtml(html);
        var filtered = results.Where(x => MatchesFilters(x, options)).ToList();
        log?.Report($"Catalog 返回 {results.Count} 条，筛选后 {filtered.Count} 条。");
        return filtered;
    }

    public static IReadOnlyList<MicrosoftCatalogUpdate> ParseSearchHtml(string html)
    {
        var list = new List<MicrosoftCatalogUpdate>();
        foreach (Match row in RowRegex.Matches(html))
        {
            var guid = First(row, "guid", "guid2");
            if (string.IsNullOrWhiteSpace(guid)) continue;
            var title = Clean(row.Groups["title"].Value);
            if (title.Length == 0) continue;
            var platform = Clean(row.Groups["products"].Value);
            list.Add(new MicrosoftCatalogUpdate(
                guid,
                title,
                platform,
                Clean(row.Groups["classification"].Value),
                ParseDate(Clean(row.Groups["date"].Value)),
                Clean(row.Groups["version"].Value),
                Clean(row.Groups["size"].Value),
                Array.Empty<MicrosoftCatalogFile>(),
                KbNumber: ExtractKb(title),
                Platform: platform));
        }
        return list
            .GroupBy(x => x.Guid, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    public async Task<MicrosoftCatalogUpdate> LoadFilesAsync(
        MicrosoftCatalogUpdate update,
        CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(
            $"https://www.catalog.update.microsoft.com/DownloadDialog.aspx?updateid={Uri.EscapeDataString(update.Guid)}",
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var files = new List<MicrosoftCatalogFile>();
        foreach (Match match in LinkRegex.Matches(html))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(name)) continue;
            files.Add(new MicrosoftCatalogFile(name, uri.ToString()));
        }
        return update with { Files = files.DistinctBy(x => x.Url).ToList() };
    }

    public static string BuildQuery(MicrosoftCatalogSearchOptions options)
    {
        var parts = new List<string>();
        AddPart(parts, options.Search);
        AddPart(parts, options.KbNumber);
        if (!string.IsNullOrWhiteSpace(options.ProductFamily) &&
            !string.Equals(options.ProductFamily, "全部产品", StringComparison.OrdinalIgnoreCase))
            AddPart(parts, options.ProductFamily);
        if (!string.IsNullOrWhiteSpace(options.OperatingSystem) &&
            !string.Equals(options.OperatingSystem, "全部系统", StringComparison.OrdinalIgnoreCase))
            AddPart(parts, options.OperatingSystem);
        AddPart(parts, options.Version);
        if (!string.Equals(options.Architecture, "All", StringComparison.OrdinalIgnoreCase))
            AddPart(parts, options.Architecture);
        AddPart(parts, options.UpdateType);
        AddPart(parts, options.Language);
        return string.Join(' ', parts);
    }

    private static bool MatchesFilters(MicrosoftCatalogUpdate update, MicrosoftCatalogSearchOptions options)
    {
        if (!options.IncludePreview && update.Title.Contains("Preview", StringComparison.OrdinalIgnoreCase)) return false;
        if (options.FromDate is { } from && update.LastUpdated is { } date && date < from.Date) return false;
        if (options.ToDate is { } to && update.LastUpdated is { } date2 && date2 > to.Date.AddDays(1).AddTicks(-1)) return false;
        if (!string.IsNullOrWhiteSpace(options.UpdateType) &&
            !update.Classification.Contains(options.UpdateType, StringComparison.OrdinalIgnoreCase) &&
            !update.Title.Contains(options.UpdateType, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(options.KbNumber) &&
            !string.Equals(NormalizeKb(options.KbNumber), NormalizeKb(update.KbNumber), StringComparison.OrdinalIgnoreCase) &&
            !update.Title.Contains(options.KbNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(options.Version) &&
            !update.Version.Contains(options.Version.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !update.Title.Contains(options.Version.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(options.Language) ||
               update.Title.Contains(options.Language, StringComparison.OrdinalIgnoreCase) ||
               update.Products.Contains(options.Language, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
    }

    private static string? ExtractKb(string title)
    {
        var match = Regex.Match(title, @"\bKB\d{4,8}\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string NormalizeKb(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant().Replace("KB", string.Empty, StringComparison.Ordinal);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OneCore-Image-Designer");
        return client;
    }

    private static string First(Match match, params string[] names) =>
        names.Select(name => match.Groups[name].Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string Clean(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value), "<.*?>", " ").Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();

    private static DateTime? ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
}

public interface IUpdateDownloader
{
    string EngineName { get; }
    Task<CatalogDownloadItem> DownloadAsync(
        MicrosoftCatalogFile file,
        string destinationDirectory,
        bool overwrite,
        IProgress<double>? progress,
        IProgress<string>? log,
        CancellationToken ct = default);
}

public sealed class UpdateDownloader : IUpdateDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string? _aria2Path;
    public string EngineName => _aria2Path is null ? "原生下载回退" : "aria2 高效下载";

    public UpdateDownloader(string? aria2Path = null)
    {
        _aria2Path = aria2Path ?? FindAria2();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("OneCore-Image-Designer");
    }

    public async Task<CatalogDownloadItem> DownloadAsync(
        MicrosoftCatalogFile file,
        string destinationDirectory,
        bool overwrite,
        IProgress<double>? progress,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var safeName = MakeSafeFileName(file.Name);
        var finalPath = Path.Combine(destinationDirectory, safeName);
        if (File.Exists(finalPath) && !overwrite)
            return new(file, finalPath, _aria2Path is not null, new FileInfo(finalPath).Length, null, "跳过：文件已存在");

        var tempPath = finalPath + ".ocid.download";
        if (_aria2Path is not null)
        {
            try
            {
                await DownloadWithAria2Async(file.Url, tempPath, progress, log, ct).ConfigureAwait(false);
                File.Move(tempPath, finalPath, true);
                return await VerifyAsync(file, finalPath, true, log, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Report($"aria2 不可用，回退原生下载：{ex.Message}");
                TryDelete(tempPath);
            }
        }

        await DownloadWithHttpAsync(file.Url, tempPath, progress, log, ct).ConfigureAwait(false);
        File.Move(tempPath, finalPath, true);
        return await VerifyAsync(file, finalPath, false, log, ct).ConfigureAwait(false);
    }

    private async Task DownloadWithAria2Async(string url, string tempPath, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _aria2Path!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("--allow-overwrite=true");
        psi.ArgumentList.Add("--auto-file-renaming=false");
        psi.ArgumentList.Add("--max-connection-per-server=4");
        psi.ArgumentList.Add("--split=4");
        psi.ArgumentList.Add("--file-allocation=none");
        psi.ArgumentList.Add($"--dir={Path.GetDirectoryName(tempPath)!}");
        psi.ArgumentList.Add($"--out={Path.GetFileName(tempPath)}");
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(output)) log?.Report(output.Trim());
        if (process.ExitCode != 0) throw new IOException($"aria2c 退出码 {process.ExitCode}：{error.Trim()}");
        progress?.Report(100);
    }

    private static async Task DownloadWithHttpAsync(string url, string tempPath, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report(done * 100d / total);
        }
        log?.Report($"原生下载完成：{done / 1024d / 1024d:F1} MB");
    }

    private static async Task<CatalogDownloadItem> VerifyAsync(MicrosoftCatalogFile file, string path, bool aria2, IProgress<string>? log, CancellationToken ct)
    {
        string hash;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, true))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        }
        log?.Report($"SHA-256：{hash}");
        return new(file, path, aria2, new FileInfo(path).Length, hash, "完成");
    }

    private static string? FindAria2()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "aria2c", "aria2c.exe"),
            Path.Combine(AppContext.BaseDirectory, "aria2c.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string MakeSafeFileName(string name)
    {
        var file = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars()) file = file.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(file) ? "update.bin" : file;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
