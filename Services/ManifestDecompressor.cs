using System.Diagnostics;
using System.IO;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// manifest 解压服务：把 WinSxS 中 DCM 压缩存储的 manifest 转成明文 XML。
    ///
    /// 【为什么需要】WinSxS 与 CAB 内的 manifest 大多是 DCM 压缩的，直接
    /// <c>XmlDocument.Load</c> 会抛异常。若调用方用空 catch 吞掉（项目内多处如此），
    /// 结果就是"解析出 0 个组件"这类静默失败 —— 实测对真实的
    /// C:\Windows\WinSxS\Manifests（20948 个文件）直接解析，得到 0 个组件。
    ///
    /// 本服务把 wcpex 的释放与调用集中管理，供 ComponentStoreVerifier、
    /// ComponentExtractor 等复用，避免各处重复实现且写法不一（例如 Process 未 Dispose）。
    /// </summary>
    public static class ManifestDecompressor
    {
        /// <summary>DCM 压缩文件头："DCM\x01PA30"</summary>
        private static readonly byte[] DcmHeader = { 0x44, 0x43, 0x4D, 0x01, 0x50, 0x41, 0x33, 0x30 };

        private static string? _wcpexDir;
        private static bool _wcpexTried;

        /// <summary>
        /// 判断文件是否为 DCM 压缩格式。读取失败时返回 false（视为非压缩）。
        /// </summary>
        public static bool IsDcmCompressed(string filePath)
        {
            try
            {
                using FileStream fs = File.OpenRead(filePath);
                if (fs.Length < DcmHeader.Length) return false;

                byte[] header = new byte[DcmHeader.Length];
                int read = fs.Read(header, 0, header.Length);
                if (read < DcmHeader.Length) return false;

                for (int i = 0; i < DcmHeader.Length; i++)
                    if (header[i] != DcmHeader[i]) return false;

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 取得 manifest 的明文 XML 文本。
        /// </summary>
        /// <returns>
        /// 明文 XML 字符串；无法取得时返回 null（压缩格式但 wcpex 不可用、
        /// 解压失败、或内容不是 XML）。调用方应记录失败原因而非静默忽略。
        /// </returns>
        public static string? ReadXmlText(string manifestPath, out string? failureReason)
        {
            failureReason = null;

            if (!File.Exists(manifestPath))
            {
                failureReason = "文件不存在";
                return null;
            }

            // 先尝试直接当明文读取（部分 manifest 本来就是明文 XML）
            string? direct = TryReadPlainText(manifestPath);
            if (direct is not null) return direct;

            if (!IsDcmCompressed(manifestPath))
            {
                failureReason = "既不是明文 XML，也不是 DCM 压缩格式";
                return null;
            }

            string? wcpexDir = EnsureWcpexAvailable(out string? wcpexError);
            if (wcpexDir is null)
            {
                failureReason = $"DCM 压缩但 wcpex 不可用：{wcpexError}";
                return null;
            }

            string convertedPath = Path.Combine(
                Path.GetTempPath(),
                $"wpmt_dec_{Guid.NewGuid():N}.manifest");

            try
            {
                if (!RunWcpex(Path.Combine(wcpexDir, "wcpex.exe"), wcpexDir, manifestPath, convertedPath,
                        out string? runError))
                {
                    failureReason = $"wcpex 解压失败：{runError}";
                    return null;
                }

                string? text = TryReadPlainText(convertedPath);
                if (text is null)
                {
                    failureReason = "wcpex 输出不含 assemblyIdentity，可能不是 manifest";
                    return null;
                }
                return text;
            }
            finally
            {
                try { if (File.Exists(convertedPath)) File.Delete(convertedPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// 调用 wcpex 解压。必须用 using 释放 Process —— 原实现在
        /// ComponentExtractor 中直接 <c>Process.Start</c> 后既不 using 也不 Dispose，
        /// 每个 manifest 泄漏一个进程句柄，批量处理上千个文件时会耗尽句柄。
        /// </summary>
        private static bool RunWcpex(string wcpexExe, string workDir, string input, string output, out string? error)
        {
            error = null;

            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = wcpexExe,
                    Arguments = $"\"{input}\" \"{output}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workDir
                };

                using Process? proc = Process.Start(psi);
                if (proc is null)
                {
                    error = "无法启动 wcpex 进程";
                    return false;
                }

                // 必须读走输出流：wcpex 输出较多时，不读会让子进程写满管道缓冲区而死锁
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                    error = "wcpex 超时（30 秒）已终止";
                    return false;
                }

                string stderr = stderrTask.GetAwaiter().GetResult();
                stdoutTask.GetAwaiter().GetResult();

                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    error = $"wcpex 未产出有效文件{(stderr.Length > 0 ? $"；stderr: {Truncate(stderr)}" : "")}";
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>读取文件并确认是含 assemblyIdentity 的 XML；否则返回 null。</summary>
        private static string? TryReadPlainText(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                return text.Contains("<assemblyIdentity", StringComparison.OrdinalIgnoreCase) ? text : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// 释放嵌入资源中的 wcpex.exe 与 wcp.dll 到临时目录（进程内只做一次）。
        /// 也可通过环境变量 ONECORE_WCPEX_DIR 指定外部目录。
        /// </summary>
        private static string? EnsureWcpexAvailable(out string? error)
        {
            error = null;

            if (_wcpexDir is not null && Directory.Exists(_wcpexDir))
                return _wcpexDir;

            if (_wcpexTried && _wcpexDir is null)
            {
                error = "先前释放 wcpex 失败";
                return null;
            }

            _wcpexTried = true;

            try
            {
                // 允许用外部目录覆盖（便于调试或资源缺失时兜底）
                string? external = Environment.GetEnvironmentVariable("ONECORE_WCPEX_DIR");
                if (!string.IsNullOrWhiteSpace(external) && File.Exists(Path.Combine(external, "wcpex.exe")))
                {
                    _wcpexDir = external;
                    return _wcpexDir;
                }

                string tempDir = Path.Combine(Path.GetTempPath(), $"OneCoreImageDesigner_wcpex_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string[] resourceNames = assembly.GetManifestResourceNames();

                string? wcpexResource = resourceNames.FirstOrDefault(
                    n => n.EndsWith("wcpex.exe", StringComparison.OrdinalIgnoreCase));
                if (wcpexResource is null)
                {
                    error = "未找到 wcpex.exe 嵌入资源";
                    TryDelete(tempDir);
                    return null;
                }

                ExtractResource(assembly, wcpexResource, Path.Combine(tempDir, "wcpex.exe"));

                // wcp.dll 是 wcpex 的运行期依赖，缺失会导致解压失败
                string? wcpResource = resourceNames.FirstOrDefault(
                    n => n.EndsWith("wcp.dll", StringComparison.OrdinalIgnoreCase));
                if (wcpResource is not null)
                    ExtractResource(assembly, wcpResource, Path.Combine(tempDir, "wcp.dll"));

                _wcpexDir = tempDir;
                return _wcpexDir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return null;
            }
        }

        private static void ExtractResource(
            System.Reflection.Assembly assembly, string resourceName, string targetPath)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return;

            using FileStream fileStream = File.Create(targetPath);
            stream.CopyTo(fileStream);
        }

        private static string Truncate(string s, int max = 200)
            => s.Length <= max ? s : s[..max] + "…";

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
