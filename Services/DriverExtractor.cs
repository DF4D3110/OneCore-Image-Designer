using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 驱动提取与整理服务
    /// 从镜像中扫描驱动文件，解析INF，按硬件ID分类，生成可独立安装的驱动包
    /// </summary>
    public static class DriverExtractor
    {
        #region 日志

        private static readonly string LogDir = OcidLogPaths.MiscDir;
        private static readonly string LogFile = Path.Combine(LogDir, $"driver_extract_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        private static void Log(string message)
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch { }
        }

        public static string GetLogFilePath() => LogFile;

        #endregion

        #region 数据结构

        /// <summary>
        /// 驱动包信息
        /// </summary>
        public class DriverPackage
        {
            public string InfFileName { get; set; } = "";
            public string InfFilePath { get; set; } = "";
            public string Provider { get; set; } = "";
            public string DriverVersion { get; set; } = "";
            public string DriverDate { get; set; } = "";
            public string Class { get; set; } = "";
            public string ClassGuid { get; set; } = "";
            public List<string> HardwareIds { get; } = new();
            public List<string> CompatibleIds { get; } = new();
            public List<string> SourceFiles { get; } = new();
            public List<string> CatalogFiles { get; } = new();
            public string Description { get; set; } = "";
            public long TotalSize { get; set; }
        }

        /// <summary>
        /// 提取结果
        /// </summary>
        public class ExtractResult
        {
            public List<DriverPackage> Packages { get; } = new();
            public int TotalPackages { get; set; }
            public int TotalHardwareIds { get; set; }
            public long TotalSize { get; set; }
            public string OutputPath { get; set; } = "";
            public List<string> Errors { get; } = new();
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 从已提取的镜像目录中扫描并提取驱动
        /// </summary>
        /// <param name="imageRootPath">镜像根目录（包含diskX/PartitionX-Name/结构）</param>
        /// <param name="outputPath">输出目录</param>
        /// <param name="progressCallback">进度回调（当前, 总数, 描述）</param>
        /// <returns>提取结果</returns>
        public static ExtractResult ExtractFromDirectory(
            string imageRootPath,
            string outputPath,
            Action<int, int, string>? progressCallback = null)
        {
            var result = new ExtractResult
            {
                OutputPath = outputPath
            };

            try
            {
                if (!Directory.Exists(imageRootPath))
                {
                    result.Errors.Add($"镜像目录不存在: {imageRootPath}");
                    return result;
                }

                // 第一步：扫描所有INF文件
                progressCallback?.Invoke(0, 100, "正在扫描 INF 文件...");
                var infFiles = ScanInfFiles(imageRootPath);
                result.TotalPackages = infFiles.Count;

                if (infFiles.Count == 0)
                {
                    result.Errors.Add("未找到任何 INF 文件");
                    return result;
                }

                // 第二步：解析每个INF文件
                progressCallback?.Invoke(0, infFiles.Count, "正在解析 INF 文件...");
                var packages = new List<DriverPackage>();

                for (int i = 0; i < infFiles.Count; i++)
                {
                    try
                    {
                        progressCallback?.Invoke(i + 1, infFiles.Count, $"解析: {Path.GetFileName(infFiles[i])}");
                        var package = ParseInfFile(infFiles[i], imageRootPath);
                        if (package != null)
                        {
                            packages.Add(package);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"解析 {Path.GetFileName(infFiles[i])} 失败: {ex.Message}");
                    }
                }

                result.Packages.AddRange(packages);
                result.TotalHardwareIds = packages.Sum(p => p.HardwareIds.Count);
                result.TotalSize = packages.Sum(p => p.TotalSize);

                // 第三步：复制驱动文件到输出目录
                progressCallback?.Invoke(0, packages.Count, "正在复制驱动文件...");
                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                for (int i = 0; i < packages.Count; i++)
                {
                    try
                    {
                        progressCallback?.Invoke(i + 1, packages.Count, $"提取: {packages[i].InfFileName}");
                        ExtractPackage(packages[i], imageRootPath, outputPath);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"提取 {packages[i].InfFileName} 失败: {ex.Message}");
                    }
                }

                // 第四步：生成驱动列表报告和安装脚本
                progressCallback?.Invoke(0, 100, "正在生成报告...");
                GenerateDriverReport(packages, outputPath);
                GenerateInstallScript(packages, outputPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"提取失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 仅扫描并解析驱动，不复制文件（用于预览）
        /// </summary>
        public static List<DriverPackage> ScanDrivers(string imageRootPath)
        {
            var packages = new List<DriverPackage>();
            Log($"=== ScanDrivers 开始 ===");
            Log($"imageRootPath: {imageRootPath}");

            if (!Directory.Exists(imageRootPath))
            {
                Log($"路径不存在，返回空列表");
                return packages;
            }

            var infFiles = ScanInfFiles(imageRootPath);
            Log($"开始解析 {infFiles.Count} 个 INF 文件...");

            int parseSuccess = 0;
            int parseFail = 0;

            foreach (var infFile in infFiles)
            {
                try
                {
                    var package = ParseInfFile(infFile, imageRootPath);
                    if (package != null)
                    {
                        packages.Add(package);
                        parseSuccess++;
                        Log($"  ✅ 解析成功: {Path.GetFileName(infFile)} (Provider: {package.Provider}, HWIDs: {package.HardwareIds.Count})");
                    }
                    else
                    {
                        parseFail++;
                        Log($"  ⚠️ 解析返回 null: {Path.GetFileName(infFile)}");
                    }
                }
                catch (Exception ex)
                {
                    parseFail++;
                    Log($"  ❌ 解析失败: {Path.GetFileName(infFile)}: {ex.Message}");
                }
            }

            Log($"=== ScanDrivers 完成: 成功 {parseSuccess}, 失败 {parseFail}, 共 {packages.Count} 个驱动包 ===");
            return packages;
        }

        #endregion

        #region 内部方法

        /// <summary>
        /// 扫描所有INF文件
        /// </summary>
        private static List<string> ScanInfFiles(string rootPath)
        {
            var infFiles = new List<string>();
            Log($"=== ScanInfFiles 开始 ===");
            Log($"rootPath: {rootPath}");
            Log($"rootPath 存在: {Directory.Exists(rootPath)}");

            try
            {
                // 自动定位 Windows 文件夹
                string? windowsPath = LocateWindowsFolder(rootPath);
                Log($"定位到 Windows 文件夹: {(windowsPath ?? "未找到")}");

                if (windowsPath == null)
                {
                    // 没找到 Windows 文件夹，全盘搜索
                    Log("未找到 Windows 文件夹，执行全盘搜索...");
                    try
                    {
                        var allFiles = Directory.GetFiles(rootPath, "*.inf", SearchOption.AllDirectories);
                        infFiles.AddRange(allFiles);
                        Log($"全盘搜索找到 {allFiles.Length} 个 INF 文件");
                    }
                    catch (Exception ex)
                    {
                        Log($"全盘搜索失败: {ex.Message}");
                    }
                    return infFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                // 搜索常见驱动位置
                string[] searchPaths = new[]
                {
                    Path.Combine(windowsPath, "INF"),
                    Path.Combine(windowsPath, "System32", "DriverStore", "FileRepository"),
                    Path.Combine(windowsPath, "System32", "DriverStore"),
                    Path.Combine(windowsPath, "System32"),
                    Path.Combine(windowsPath, "System32", "drivers"),
                };

                foreach (var searchPath in searchPaths)
                {
                    bool exists = Directory.Exists(searchPath);
                    Log($"搜索路径: {searchPath} (存在: {exists})");
                    if (exists)
                    {
                        try
                        {
                            var found = Directory.GetFiles(searchPath, "*.inf", SearchOption.AllDirectories);
                            infFiles.AddRange(found);
                            Log($"  找到 {found.Length} 个 INF 文件");
                        }
                        catch (Exception ex)
                        {
                            Log($"  搜索失败: {ex.Message}");
                        }
                    }
                }

                // 如果没找到，在 Windows 目录下搜索
                if (infFiles.Count == 0)
                {
                    Log("常见路径未找到 INF，在整个 Windows 目录下搜索...");
                    try
                    {
                        var allInWindows = Directory.GetFiles(windowsPath, "*.inf", SearchOption.AllDirectories);
                        infFiles.AddRange(allInWindows);
                        Log($"Windows 目录搜索找到 {allInWindows.Length} 个 INF 文件");
                    }
                    catch (Exception ex)
                    {
                        Log($"Windows 目录搜索失败: {ex.Message}");
                    }
                }

                // 去重
                infFiles = infFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Log($"=== ScanInfFiles 完成，共找到 {infFiles.Count} 个 INF 文件 ===");
            }
            catch (Exception ex)
            {
                Log($"ScanInfFiles 异常: {ex.Message}");
            }

            return infFiles;
        }

        /// <summary>
        /// 自动定位 Windows 文件夹
        /// </summary>
        private static string? LocateWindowsFolder(string startPath)
        {
            try
            {
                Log($"LocateWindowsFolder: startPath={startPath}");
                if (!Directory.Exists(startPath))
                {
                    Log($"  路径不存在");
                    return null;
                }

                // 情况1：startPath 本身就是 Windows 文件夹（有 INF、System32 子目录）
                if (Directory.Exists(Path.Combine(startPath, "INF")) &&
                    Directory.Exists(Path.Combine(startPath, "System32")))
                {
                    Log($"  情况1: startPath 本身就是 Windows 文件夹");
                    return startPath;
                }

                // 情况2：startPath 下有 Windows 子文件夹
                string directWindows = Path.Combine(startPath, "Windows");
                if (Directory.Exists(directWindows) &&
                    Directory.Exists(Path.Combine(directWindows, "INF")))
                {
                    return directWindows;
                }

                // 情况3：递归查找（最多3层），找包含 INF 文件夹的 Windows 目录
                return FindWindowsFolderRecursive(startPath, 0, 3);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 递归查找 Windows 文件夹
        /// </summary>
        private static string? FindWindowsFolderRecursive(string path, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth) return null;

            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    string dirName = Path.GetFileName(dir);

                    // 找到 Windows 文件夹且包含 INF 子目录
                    if (dirName.Equals("Windows", StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(Path.Combine(dir, "INF")))
                    {
                        return dir;
                    }

                    // 递归搜索
                    var result = FindWindowsFolderRecursive(dir, currentDepth + 1, maxDepth);
                    if (result != null) return result;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 解析INF文件
        /// </summary>
        private static DriverPackage? ParseInfFile(string infPath, string rootPath)
        {
            string infFileName = Path.GetFileName(infPath);
            try
            {
                Log($"  [Parse] 开始解析: {infFileName}");

                // 尝试多种编码读取
                string content = ReadInfWithEncoding(infPath);
                Log($"  [Parse] 文件大小: {content.Length} 字符");

                var sections = ParseInfSections(content);
                Log($"  [Parse] 解析到 {sections.Count} 个段: {string.Join(", ", sections.Keys.Take(10))}");

                var package = new DriverPackage
                {
                    InfFileName = infFileName,
                    InfFilePath = infPath
                };

                // 解析 [Version] 段
                if (sections.TryGetValue("Version", out var versionSection))
                {
                    package.Provider = GetInfValue(versionSection, "Provider")?.Trim('"', '%') ?? "";
                    package.DriverVersion = GetInfValue(versionSection, "DriverVer") ?? "";
                    package.Class = GetInfValue(versionSection, "Class")?.Trim('"') ?? "";
                    package.ClassGuid = GetInfValue(versionSection, "ClassGuid")?.Trim('{', '}', '"') ?? "";

                    Log($"  [Parse] Version: Provider={package.Provider}, Class={package.Class}, DriverVer={package.DriverVersion}");

                    // 解析CatalogFile
                    foreach (var line in versionSection)
                    {
                        if (line.Key.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase))
                        {
                            string catFile = line.Value?.Trim('"') ?? "";
                            if (!string.IsNullOrEmpty(catFile))
                            {
                                package.CatalogFiles.Add(catFile);
                            }
                        }
                    }

                    // 解析 DriverVer (date, version)
                    if (!string.IsNullOrEmpty(package.DriverVersion))
                    {
                        var parts = package.DriverVersion.Split(',');
                        if (parts.Length >= 1) package.DriverDate = parts[0].Trim();
                        if (parts.Length >= 2) package.DriverVersion = parts[1].Trim();
                    }
                }
                else
                {
                    Log($"  [Parse] ⚠️ 未找到 [Version] 段");
                }

                // 解析 [Manufacturer] 段和 Models 段
                if (sections.TryGetValue("Manufacturer", out var manufacturerSection))
                {
                    Log($"  [Parse] Manufacturer 段有 {manufacturerSection.Count} 行");
                    foreach (var line in manufacturerSection)
                    {
                        string manufacturerName = line.Key.Trim('%', '"');
                        string modelsSectionName = line.Value?.Trim('%', '"') ?? "";
                        Log($"  [Parse]   Manufacturer: {manufacturerName} -> Models段: {modelsSectionName}");

                        // 解析 Models 段
                        if (!string.IsNullOrEmpty(modelsSectionName) && sections.TryGetValue(modelsSectionName, out var modelsSection))
                        {
                            Log($"  [Parse]   Models段有 {modelsSection.Count} 行");
                            foreach (var modelLine in modelsSection)
                            {
                                // 格式: 设备描述=安装段, 硬件ID1, 硬件ID2, ...
                                string description = modelLine.Key.Trim('"');
                                string[] parts = modelLine.Value?.Split(',') ?? Array.Empty<string>();
                                Log($"  [Parse]     Model: {description} -> {parts.Length} 个部分");

                                if (parts.Length >= 2)
                                {
                                    for (int i = 1; i < parts.Length; i++)
                                    {
                                        string hwId = parts[i].Trim().Trim('"');
                                        if (!string.IsNullOrEmpty(hwId))
                                        {
                                            if (IsHardwareId(hwId))
                                            {
                                                if (!package.HardwareIds.Contains(hwId, StringComparer.OrdinalIgnoreCase))
                                                {
                                                    package.HardwareIds.Add(hwId);
                                                }
                                            }
                                            else
                                            {
                                                if (!package.CompatibleIds.Contains(hwId, StringComparer.OrdinalIgnoreCase))
                                                {
                                                    package.CompatibleIds.Add(hwId);
                                                }
                                            }
                                        }
                                    }
                                }

                                if (string.IsNullOrEmpty(package.Description))
                                {
                                    package.Description = description;
                                }
                            }
                        }
                        else
                        {
                            Log($"  [Parse]   ⚠️ 未找到 Models 段: {modelsSectionName}");
                        }
                    }
                }
                else
                {
                    Log($"  [Parse] ⚠️ 未找到 [Manufacturer] 段");
                }

                // 解析 [SourceDisksFiles] 段
                if (sections.TryGetValue("SourceDisksFiles", out var sourceFilesSection))
                {
                    foreach (var line in sourceFilesSection)
                    {
                        string fileName = line.Key.Trim();
                        if (!string.IsNullOrEmpty(fileName) && !package.SourceFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        {
                            package.SourceFiles.Add(fileName);
                        }
                    }
                }

                // 也检查 [SourceDisksFiles.x86] / [SourceDisksFiles.amd64] / [SourceDisksFiles.arm64] 等
                foreach (var sectionName in sections.Keys)
                {
                    if (sectionName.StartsWith("SourceDisksFiles.", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var line in sections[sectionName])
                        {
                            string fileName = line.Key.Trim();
                            if (!string.IsNullOrEmpty(fileName) && !package.SourceFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                            {
                                package.SourceFiles.Add(fileName);
                            }
                        }
                    }
                }

                Log($"  [Parse] 结果: HWIDs={package.HardwareIds.Count}, CompatibleIDs={package.CompatibleIds.Count}, SourceFiles={package.SourceFiles.Count}");

                // 计算文件总大小
                string infDir = Path.GetDirectoryName(infPath) ?? "";
                foreach (var file in package.SourceFiles)
                {
                    string filePath = Path.Combine(infDir, file);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            package.TotalSize += new FileInfo(filePath).Length;
                        }
                        catch { }
                    }
                }

                // 只要有 [Version] 段就返回（放宽条件，不强制要求硬件ID）
                if (sections.ContainsKey("Version"))
                {
                    Log($"  [Parse] ✅ 返回驱动包（有Version段）");
                    return package;
                }

                // 或者有硬件ID也返回
                if (package.HardwareIds.Count > 0 || package.CompatibleIds.Count > 0)
                {
                    Log($"  [Parse] ✅ 返回驱动包（有硬件ID）");
                    return package;
                }

                Log($"  [Parse] ❌ 返回 null（无Version段且无硬件ID）");
                return null;
            }
            catch (Exception ex)
            {
                Log($"  [Parse] ❌ 解析异常: {infFileName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 判断是否为硬件ID（而不是兼容ID）
        /// </summary>
        private static bool IsHardwareId(string hwId)
        {
            string[] hwIdPrefixes = new[]
            {
                "PCI\\", "USB\\", "ACPI\\", "HDAUDIO\\", "BTH\\", "SD\\",
                "SCSI\\", "IDE\\", "ROOT\\", "SWD\\", "UMB\\", "HTREE\\",
                "ACPI_HAL\\", "ISAPNP\\", "PCMCIA\\", "LPTENUM\\", "USBPRINT\\",
                "STORAGE\\", "VOLUME\\", "DISPLAY\\", "MEDIA\\", "NET\\",
                "MODEM\\", "MOUSE\\", "KEYBOARD\\", "FDC\\", "HDC\\", "SENSOR\\",
                "BIOMETRIC\\", "SECUREDEVICE\\", "PRINT\\", "CDROM\\", "TAPE\\",
                "1394\\", "DOT4\\", "IMAGE\\", "CAMERA\\", "AUDIO\\", "GPS\\",
                "IRDA\\", "PARALLEL\\", "SERENUM\\", "TOASTER\\", "UMBUS\\",
                "WSD\\", "WWAN\\", "WPDBUSENUM\\", "SWPRV\\", "NFP\\",
                "UEFI\\", "QCOM\\", "QUALCOMM\\", "MSFT\\", "Microsoft\\"
            };

            foreach (var prefix in hwIdPrefixes)
            {
                if (hwId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 尝试多种编码读取 INF 文件
        /// </summary>
        private static string ReadInfWithEncoding(string path)
        {
            // 先读取前几个字节判断编码
            byte[] bytes = File.ReadAllBytes(path);

            // UTF-16 LE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes);
            }
            // UTF-16 BE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            // 尝试检测 UTF-16 LE（无BOM）：如果偶数位置有很多0字节，可能是UTF-16
            int zeroCount = 0;
            int sampleSize = Math.Min(100, bytes.Length);
            for (int i = 0; i < sampleSize; i += 2)
            {
                if (bytes[i] == 0) zeroCount++;
            }
            if (zeroCount > sampleSize / 4)
            {
                try
                {
                    return Encoding.Unicode.GetString(bytes);
                }
                catch { }
            }

            // 默认用系统编码
            try
            {
                return Encoding.Default.GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>
        /// 解析INF文件的所有段
        /// </summary>
        private static Dictionary<string, List<KeyValuePair<string, string>>> ParseInfSections(string content)
        {
            var sections = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            string currentSection = "";

            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();

                // 跳过空行和注释
                if (string.IsNullOrEmpty(line) || line.StartsWith(";")) continue;

                // 段标题
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.ContainsKey(currentSection))
                    {
                        sections[currentSection] = new List<KeyValuePair<string, string>>();
                    }
                    continue;
                }

                // 键值对
                if (!string.IsNullOrEmpty(currentSection))
                {
                    int eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = line.Substring(0, eqIndex).Trim();
                        string value = line.Substring(eqIndex + 1).Trim();
                        sections[currentSection].Add(new KeyValuePair<string, string>(key, value));
                    }
                    else
                    {
                        sections[currentSection].Add(new KeyValuePair<string, string>(line, ""));
                    }
                }
            }

            return sections;
        }

        /// <summary>
        /// 获取INF段中的值
        /// </summary>
        private static string? GetInfValue(List<KeyValuePair<string, string>> section, string key)
        {
            return section.FirstOrDefault(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        /// <summary>
        /// 提取单个驱动包
        /// </summary>
        private static void ExtractPackage(DriverPackage package, string rootPath, string outputPath)
        {
            // 创建驱动包目录：按类名/驱动名
            string driverDir = Path.Combine(outputPath,
                string.IsNullOrEmpty(package.Class) ? "Unknown" : SanitizeFileName(package.Class),
                SanitizeFileName(Path.GetFileNameWithoutExtension(package.InfFileName)));

            if (!Directory.Exists(driverDir))
            {
                Directory.CreateDirectory(driverDir);
            }

            string infDir = Path.GetDirectoryName(package.InfFilePath) ?? "";

            // 复制INF文件
            string infDest = Path.Combine(driverDir, package.InfFileName);
            File.Copy(package.InfFilePath, infDest, true);

            // 复制源文件
            foreach (var file in package.SourceFiles)
            {
                string sourcePath = Path.Combine(infDir, file);
                if (File.Exists(sourcePath))
                {
                    string destPath = Path.Combine(driverDir, file);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    File.Copy(sourcePath, destPath, true);
                }
            }

            // 复制CAT文件
            foreach (var catFile in package.CatalogFiles)
            {
                string sourcePath = Path.Combine(infDir, catFile);
                if (File.Exists(sourcePath))
                {
                    string destPath = Path.Combine(driverDir, catFile);
                    File.Copy(sourcePath, destPath, true);
                }
            }
        }

        /// <summary>
        /// 生成驱动列表报告
        /// </summary>
        private static void GenerateDriverReport(List<DriverPackage> packages, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 驱动提取报告 ===");
            sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"驱动包总数: {packages.Count}");
            sb.AppendLine($"硬件ID总数: {packages.Sum(p => p.HardwareIds.Count)}");
            sb.AppendLine($"总大小: {packages.Sum(p => p.TotalSize):N0} 字节 ({Math.Round(packages.Sum(p => p.TotalSize) / 1024.0 / 1024.0, 2)} MB)");
            sb.AppendLine();

            // 按类分组
            var grouped = packages.GroupBy(p => string.IsNullOrEmpty(p.Class) ? "Unknown" : p.Class);
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                sb.AppendLine($"--- {group.Key} ({group.Count()} 个驱动) ---");
                foreach (var package in group.OrderBy(p => p.InfFileName))
                {
                    sb.AppendLine($"  📦 {package.InfFileName}");
                    sb.AppendLine($"     提供商: {package.Provider}");
                    sb.AppendLine($"     版本: {package.DriverVersion} ({package.DriverDate})");
                    sb.AppendLine($"     描述: {package.Description}");
                    sb.AppendLine($"     硬件ID ({package.HardwareIds.Count}):");
                    foreach (var hwId in package.HardwareIds.Take(5))
                    {
                        sb.AppendLine($"       - {hwId}");
                    }
                    if (package.HardwareIds.Count > 5)
                    {
                        sb.AppendLine($"       ... 还有 {package.HardwareIds.Count - 5} 个");
                    }
                    sb.AppendLine($"     文件数: {package.SourceFiles.Count}");
                    sb.AppendLine();
                }
            }

            File.WriteAllText(Path.Combine(outputPath, "driver_report.txt"), sb.ToString(), Encoding.UTF8);

            // 生成CSV
            var csv = new StringBuilder();
            csv.AppendLine("类名,INF文件名,提供商,版本,日期,描述,硬件ID数,文件数,大小");
            foreach (var package in packages)
            {
                csv.AppendLine($"{EscapeCsv(package.Class)},{EscapeCsv(package.InfFileName)},{EscapeCsv(package.Provider)},{EscapeCsv(package.DriverVersion)},{EscapeCsv(package.DriverDate)},{EscapeCsv(package.Description)},{package.HardwareIds.Count},{package.SourceFiles.Count},{package.TotalSize}");
            }
            File.WriteAllText(Path.Combine(outputPath, "driver_list.csv"), csv.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// 生成安装脚本
        /// </summary>
        private static void GenerateInstallScript(List<DriverPackage> packages, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("echo === 驱动批量安装脚本 ===");
            sb.AppendLine("echo.");
            sb.AppendLine("echo 正在安装驱动，请稍候...");
            sb.AppendLine("echo.");
            sb.AppendLine();

            int index = 1;
            foreach (var package in packages)
            {
                string relativePath = Path.Combine(
                    string.IsNullOrEmpty(package.Class) ? "Unknown" : SanitizeFileName(package.Class),
                    SanitizeFileName(Path.GetFileNameWithoutExtension(package.InfFileName)),
                    package.InfFileName);

                sb.AppendLine($"echo [{index}/{packages.Count}] 安装: {package.InfFileName} - {package.Description}");
                sb.AppendLine($"pnputil /add-driver \"%~dp0{relativePath}\" /install");
                sb.AppendLine($"if errorlevel 1 echo   ⚠️ 安装可能失败，错误码: %errorlevel%");
                sb.AppendLine();
                index++;
            }

            sb.AppendLine("echo.");
            sb.AppendLine("echo === 驱动安装完成 ===");
            sb.AppendLine("pause");

            File.WriteAllText(Path.Combine(outputPath, "install_drivers.bat"), sb.ToString(), Encoding.GetEncoding("GB2312"));
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        #endregion
    }
}
