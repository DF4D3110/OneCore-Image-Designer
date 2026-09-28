using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// ADK 工具包下载服务。
    /// 从 GitHub Release（tag: adktool）拉取各 Windows build 号对应的 ADK 工具 zip，
    /// 用 GitHub 资产自带的 sha256 digest 校验后，解压到程序目录 addonkit\adk\&lt;build&gt;\。
    /// zip 顶层即版本号目录（&lt;build&gt;\Windows Kits\10\tools\bin\i386\…，含 imageapp.exe / imggen.cmd）。
    /// </summary>
    public sealed class AdkToolService
    {
        private const string RepoOwner = "DF4D3110";
        private const string RepoName = "OneCore-Image-Designer";
        private const string ReleaseTag = "adktool";

        private static readonly HttpClient Http = CreateClient();

        /// <summary>内置 addonkit（微软 iot-adk-addonkit 17763-v7）根目录：程序目录\addonkit\。</summary>
        public static string AddonKitRoot => Path.Combine(AppContext.BaseDirectory, "addonkit");

        /// <summary>下载的 ADK 工具根目录：程序目录\addonkit\adk\。</summary>
        public static string AdkRoot => Path.Combine(AddonKitRoot, "adk");

        private static HttpClient CreateClient()
        {
            var c = new HttpClient();
            c.DefaultRequestHeaders.UserAgent.ParseAdd("OneCore-Image-Designer");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.Timeout = TimeSpan.FromMinutes(30);
            return c;
        }

        /// <summary>拉取 Release 资产清单，解析为（build 号、文件名、大小、下载地址、sha256）。</summary>
        public async Task<IReadOnlyList<AdkToolReleaseAsset>> FetchAssetsAsync(CancellationToken ct = default)
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{ReleaseTag}";
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GithubRelease>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false);
            if (release?.Assets is null) return Array.Empty<AdkToolReleaseAsset>();

            var list = new List<AdkToolReleaseAsset>();
            foreach (var a in release.Assets)
            {
                if (!string.Equals(Path.GetExtension(a.Name), ".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(a.Name), out var build)) continue;
                list.Add(new AdkToolReleaseAsset(build, a.Name, a.Size,
                    a.BrowserDownloadUrl, ParseDigest(a.Digest)));
            }
            return list.OrderBy(x => x.Build).ToList();
        }

        /// <summary>从 digest 字符串（"sha256:xxxx…"）提取小写十六进制；缺失或格式异常返回 null（下载后仅记录实际哈希）。</summary>
        private static string? ParseDigest(string? digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;
            var idx = digest.IndexOf(':');
            var hex = (idx >= 0 ? digest[(idx + 1)..] : digest).Trim().ToLowerInvariant();
            return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
        }

        /// <summary>已安装的 ADK 版本（目录下存在 imageapp.exe 才算有效）。</summary>
        public static IReadOnlyList<int> GetInstalledBuilds()
        {
            if (!Directory.Exists(AdkRoot)) return Array.Empty<int>();
            var builds = new List<int>();
            foreach (var dir in Directory.EnumerateDirectories(AdkRoot))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var build)) continue;
                if (FindToolPath(build, "imageapp.exe") != null) builds.Add(build);
            }
            builds.Sort();
            return builds;
        }

        /// <summary>
        /// 在已安装的指定版本中查找可执行工具（如 imageapp.exe / imggen.cmd / PkgGen.exe）。
        /// zip 解压后根目录带版本号前缀，统一递归搜索取第一个命中。
        /// </summary>
        public static string? FindToolPath(int build, string toolFileName)
        {
            var root = Path.Combine(AdkRoot, build.ToString());
            if (!Directory.Exists(root)) return null;
            try
            {
                return Directory.EnumerateFiles(root, toolFileName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>枚举工作区 Source-&lt;arch&gt;\Products 下的产品名。</summary>
        public Task<IReadOnlyList<string>> GetProductsAsync(string workspaceRoot) =>
            Task.FromResult(EnumerateWorkspaceSubDirs(workspaceRoot, "Products"));

        /// <summary>枚举工作区 Source-&lt;arch&gt;\BSP 下的 BSP 名。</summary>
        public Task<IReadOnlyList<string>> GetBSPsAsync(string workspaceRoot) =>
            Task.FromResult(EnumerateWorkspaceSubDirs(workspaceRoot, "BSP"));

        private static IReadOnlyList<string> EnumerateWorkspaceSubDirs(string workspaceRoot, string leaf)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot)) return list;
            foreach (var src in Directory.EnumerateDirectories(workspaceRoot, "Source-*"))
            {
                var dir = Path.Combine(src, leaf);
                if (!Directory.Exists(dir)) continue;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                        list.Add(name);
                }
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>
        /// 下载指定版本 zip → SHA256 校验 → 解压到 addonkit\adk\&lt;build&gt;\。
        /// 校验失败删除临时文件并抛 InvalidDataException。返回安装目录。
        /// </summary>
        public async Task<string> DownloadAndInstallAsync(
            AdkToolReleaseAsset asset,
            IProgress<double>? progress,
            IProgress<string>? log,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(AdkRoot);
            var targetDir = Path.Combine(AdkRoot, asset.Build.ToString());
            var zipPath = Path.Combine(AdkRoot, $"{asset.Build}.download.zip");

            log?.Report($"开始下载 {asset.FileName}（{asset.SizeDisplay}）");
            try
            {
                using (var resp = await Http.GetAsync(asset.DownloadUrl,
                           HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? asset.Size;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, 81920, true);
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if (total > 0) progress?.Report(Math.Min(100.0, done * 100.0 / total));
                    }
                }

                log?.Report("下载完成，正在校验 SHA256…");
                string hash;
                using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144))
                    hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (asset.Sha256 != null && hash != asset.Sha256)
                    throw new InvalidDataException($"SHA256 校验失败：期望 {asset.Sha256}，实际 {hash}。文件已删除，请重试。");
                if (asset.Sha256 == null)
                    log?.Report($"Release 未提供 digest，记录实际 SHA256：{hash}");

                log?.Report("校验通过，正在解压…");
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                // zip 顶层为 <build>\ 目录 → 解压到 AdkRoot 恰好得到 AdkRoot\<build>\
                ZipFile.ExtractToDirectory(zipPath, AdkRoot, true);
                if (!Directory.Exists(targetDir))
                    throw new InvalidDataException($"解压后未找到预期目录：{targetDir}");

                progress?.Report(100);
                log?.Report($"安装完成：{targetDir}");
                return targetDir;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (IOException) { }
            }
        }

        private sealed class GithubRelease
        {
            [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
        }

        private sealed class GithubAsset
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("size")] public long Size { get; set; }
            [JsonPropertyName("digest")] public string? Digest { get; set; }
            [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        }
    }

    /// <summary>Release 中的一个 ADK 工具包资产。</summary>
    public sealed record AdkToolReleaseAsset(
        int Build,
        string FileName,
        long Size,
        string DownloadUrl,
        string? Sha256)
    {
        public string SizeDisplay => Size >= 1024 * 1024
            ? $"{Size / (1024.0 * 1024.0):F1} MB"
            : $"{Size / 1024.0:F0} KB";
    }
}
