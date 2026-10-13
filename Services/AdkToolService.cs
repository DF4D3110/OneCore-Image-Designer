using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
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
            // SSL 加固：显式锁定 TLS 1.2/1.3（不依赖系统默认协商），
            // SocketsHttpHandler 强制 HTTP/1.1（规避部分网络对 h2 的 ALPN 干扰），
            // 并绕过系统代理（直连更稳，代理异常时报错也更干净）。
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = null,   // 保持默认严格校验
                },
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            var c = new HttpClient(handler);
            // 说明：.NET 默认即 HTTP/1.1（不主动升级 h2），无需额外设置。
            c.DefaultRequestHeaders.UserAgent.ParseAdd("OneCore-Image-Designer");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.Timeout = TimeSpan.FromMinutes(30);
            return c;
        }

        /// <summary>把网络异常翻译成可操作的中文说明。</summary>
        public static string DescribeNetworkException(Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException hre)
                {
                    sb.Append($"HTTP: {hre.Message}");
                    if (hre.StatusCode is { } sc) sb.Append($"（状态码 {(int)sc} {sc}）");
                    if (hre.InnerException is AuthenticationException aex)
                        sb.Append($" | TLS: {aex.Message}（常见原因：系统时间偏差、根证书缺失或代理拦截）");
                    else if (hre.InnerException is SocketException sex)
                        sb.Append($" | Socket: {sex.Message}（socket 错误 {sex.SocketErrorCode}）");
                    else if (hre.InnerException != null)
                        sb.Append($" | {hre.InnerException.GetType().Name}: {hre.InnerException.Message}");
                }
                else if (e is TaskCanceledException || e is TimeoutException)
                    sb.Append($"超时: {e.Message}（连接或传输过慢，可稍后重试）");
                else
                    sb.Append($"{e.GetType().Name}: {e.Message}");
                if (e.InnerException != null && !ReferenceEquals(e, ex)) sb.Append(" <- ");
            }
            return sb.ToString();
        }

        /// <summary>拉取 Release 资产清单，解析为（build 号、文件名、大小、下载地址、sha256）。</summary>
        public async Task<IReadOnlyList<AdkToolReleaseAsset>> FetchAssetsAsync(CancellationToken ct = default)
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{ReleaseTag}";
            HttpResponseMessage resp;
            try
            {
                resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
            {
                throw new HttpRequestException("无法连接 GitHub API：" + DescribeNetworkException(ex), ex);
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"GitHub API 返回 {(int)resp.StatusCode} {resp.ReasonPhrase}（可能触发限流，稍后再试）");
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
                // 网络抖动重试：最多 3 次尝试，指数退避（1s/3s）。
                // 仅对传输层失败（HttpRequestException/超时/DNS）重试；SHA 校验失败不重试。
                HttpResponseMessage? resp = null;
                Exception? lastError = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        resp = await Http.GetAsync(asset.DownloadUrl,
                            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var code = (int)resp.StatusCode;
                            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            resp.Dispose(); resp = null;
                            lastError = new HttpRequestException(
                                $"HTTP {code} {resp?.ReasonPhrase}", null, (HttpStatusCode)code);
                            if (code is >= 500 and <= 599 && attempt < 3)
                            {
                                log?.Report($"服务器错误 {code}，第 {attempt} 次重试…");
                                await Task.Delay(attempt * 1000, ct).ConfigureAwait(false);
                                continue;
                            }
                            throw lastError;
                        }
                        break;  // 成功
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
                    {
                        resp?.Dispose(); resp = null;
                        lastError = ex;
                        if (attempt < 3)
                        {
                            log?.Report($"网络异常（第 {attempt} 次）：{DescribeNetworkException(ex)}");
                            log?.Report($"等待 {attempt * 3} 秒后重试…");
                            await Task.Delay(attempt * 3000, ct).ConfigureAwait(false);
                        }
                    }
                }
                if (resp is null)
                    throw lastError ?? new HttpRequestException("下载失败：未知错误");

                using (resp)
                {
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
