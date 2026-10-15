using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DiscUtils;
using DiscUtils.Streams;
using Img2Ffu.Reader;
using MobilePackageGen;
using MobilePackageGen.Adapters;
using OneCore_Image_Designer.Models.MobilePackageGen;
using XmlMum = MobilePackageGen.XmlMum;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 简单的IDisk实现，用于包装每个store的分区
    /// </summary>
    internal class SimpleDisk : IDisk
    {
        public IEnumerable<IPartition> Partitions { get; set; } = [];
    }

    /// <summary>
    /// 组件提取服务：按系统原有结构导出选中的组件，包含文件和注册表
    /// </summary>
    public static class ComponentExtractor
    {
        /// <summary>
        /// 提取模式
        /// </summary>
        public enum ExtractMode
        {
            /// <summary>
            /// 按磁盘\分区扁平组织
            /// </summary>
            FlatByDisk,

            /// <summary>
            /// 按组件分组组织
            /// </summary>
            GroupByComponent
        }

        /// <summary>
        /// 提取结果
        /// </summary>
        public class ExtractResult
        {
            public int TotalComponents { get; set; }
            public int TotalFiles { get; set; }
            public int TotalRegistryKeys { get; set; }
            public List<string> ExtractedComponents { get; } = new();
            public List<string> Dependencies { get; } = new();
            public List<string> Errors { get; } = new();
            public string OutputPath { get; set; } = "";
            // 校验结果
            public int VerifiedFiles { get; set; }
            public int VerifyFailed { get; set; }
            public string? VerifyReportPath { get; set; }
        }

        /// <summary>
        /// 提取预览结果
        /// </summary>
        public class ExtractPreviewResult
        {
            public int TotalComponents { get; set; }
            public int TotalFiles { get; set; }
            public int TotalRegistryKeys { get; set; }
            public long TotalFileSize { get; set; }
            public int TotalDependencies { get; set; }
            public List<string> ComponentSummaries { get; } = new();
            public List<string> Errors { get; } = new();
        }

        /// <summary>
        /// 加载FFU文件，解析多个store（UFS LUN），每个store作为一个独立磁盘（与FFU浏览器一致）
        /// </summary>
        private static IEnumerable<IDisk> LoadFFUDisksWithStores(string ffuPath)
        {
            var disks = new List<IDisk>();

            try
            {
                int storeCount = FullFlashUpdateReaderStream.GetStoreCount(ffuPath);
                Log($"FFU文件包含 {storeCount} 个store");

                for (int i = 0; i < storeCount; i++)
                {
                    try
                    {
                        var store = new FullFlashUpdateReaderStream(ffuPath, (ulong)i);
                        Log($"  Store {i}: Size={store.Length:N0}, SectorSize={store.SectorSize}");

                        long diskCapacity = store.Length;
                        var virtualDisk = new DiscUtils.Raw.Disk(store, Ownership.None, Geometry.FromCapacity(diskCapacity, store.SectorSize));

                        var partitions = DiskCommon.GetPartitions(virtualDisk);
                        var partitionList = new List<IPartition>();

                        foreach (var (partitionInfo, partitionStream) in partitions)
                        {
                            var fsPartition = new FileSystemPartition(
                                partitionStream,
                                partitionInfo.Name,
                                partitionInfo.PartitionTypeGuid,
                                partitionInfo.PartitionGuid);
                            partitionList.Add(fsPartition);
                        }

                        Log($"    Store {i} 解析到 {partitionList.Count} 个分区");
                        disks.Add(new SimpleDisk { Partitions = partitionList });
                    }
                    catch (Exception ex)
                    {
                        Log($"    Store {i} 解析失败: {ex.Message}");
                        disks.Add(new SimpleDisk { Partitions = [] });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"FFU多store解析失败: {ex.Message}，回退到DiskLoader");
                return DiskLoader.LoadDisks([ffuPath]);
            }

            return disks;
        }

        /// <summary>
        /// 提取预览：扫描选中的组件，统计将提取的文件数、注册表项数、总大小，不实际提取
        /// </summary>
        public static ExtractPreviewResult PreviewExtract(
            IEnumerable<PackageInfo> selectedPackages,
            IEnumerable<IDisk> disks,
            bool includeRegistry = true,
            string? ffuPath = null)
        {
            var preview = new ExtractPreviewResult();

            try
            {
                // 使用临时目录存放manifest（不实际提取文件）
                string tempDir = Path.Combine(Path.GetTempPath(), $"ComponentExtract_Preview_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                // FFU文件：使用多store解析
                var diskList = disks.ToList();
                if (!string.IsNullOrEmpty(ffuPath) &&
                    Path.GetExtension(ffuPath).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[预览] 检测到FFU文件，使用多store解析模式");
                    var ffuDisks = LoadFFUDisksWithStores(ffuPath);
                    if (ffuDisks != null && ffuDisks.Any())
                    {
                        diskList = ffuDisks.ToList();
                        Log($"[预览] 多store解析完成: {diskList.Count} 个磁盘");
                    }
                }

                foreach (var package in selectedPackages)
                {
                    try
                    {
                        Log($"[预览] 处理组件: {package.Name}");

                        // 1. 从镜像中读取manifest文件
                        string? manifestPath = ExtractManifestFromImage(package, diskList, tempDir);
                        if (manifestPath == null)
                        {
                            preview.Errors.Add($"无法从镜像中找到manifest: {package.Name}");
                            continue;
                        }

                        // 2. 解析manifest
                        var manifest = ParseManifestFile(manifestPath, package);
                        if (manifest == null)
                        {
                            preview.Errors.Add($"无法解析manifest: {package.Name}");
                            continue;
                        }

                        int fileCount = manifest.Package?.CustomInformation?.File?.Count ?? 0;
                        int regCount = manifest.RegistryKeys?.Count ?? 0;

                        // 3. 估算文件总大小（遍历manifest中的文件，从文件系统获取大小）
                        long fileSize = EstimateComponentFileSize(manifest, diskList);

                        // 4. 收集依赖
                        var deps = new List<string>();
                        int depCount = CollectDependencies(manifest, deps, manifestPath);

                        preview.TotalComponents++;
                        preview.TotalFiles += fileCount;
                        preview.TotalRegistryKeys += regCount;
                        preview.TotalFileSize += fileSize;
                        preview.TotalDependencies += depCount;

                        string summary = $"{package.Name}: {fileCount}个文件, {regCount}个注册表项, {fileSize:N0}字节";
                        preview.ComponentSummaries.Add(summary);
                        Log($"[预览]   {summary}, {depCount}个依赖");
                    }
                    catch (Exception ex)
                    {
                        preview.Errors.Add($"{package.Name}: {ex.Message}");
                        Log($"[预览]   错误: {ex.Message}");
                    }
                }

                // 清理临时目录
                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"预览失败: {ex.Message}");
                Log($"[预览] 失败: {ex.Message}");
            }

            return preview;
        }

        /// <summary>
        /// 估算组件文件总大小
        /// </summary>
        private static long EstimateComponentFileSize(XmlMum.Assembly manifest, List<IDisk> disks)
        {
            long totalSize = 0;
            var files = manifest.Package?.CustomInformation?.File;
            if (files == null || files.Count == 0) return 0;

            foreach (var file in files)
            {
                string? filePath = file.Name;
                if (string.IsNullOrEmpty(filePath)) continue;

                // 在所有磁盘的所有分区中查找文件
                foreach (var disk in disks)
                {
                    if (disk.Partitions == null) continue;
                    foreach (var partition in disk.Partitions)
                    {
                        if (partition.FileSystem == null) continue;
                        try
                        {
                            string fullPath = filePath.StartsWith("\\") ? filePath : "\\" + filePath;
                            if (partition.FileSystem.FileExists(fullPath))
                            {
                                var fi = partition.FileSystem.GetFileInfo(fullPath);
                                totalSize += fi.Length;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            return totalSize;
        }

        /// <summary>
        /// 提取选中的组件
        /// </summary>
        public static ExtractResult ExtractComponents(
            IEnumerable<PackageInfo> selectedPackages,
            IEnumerable<IDisk> disks,
            string outputPath,
            ExtractMode mode = ExtractMode.FlatByDisk,
            bool includeRegistry = true,
            string? ffuPath = null)
        {
            var result = new ExtractResult { OutputPath = outputPath };

            try
            {
                // 强制覆盖：清空输出目录
                Log($"清空输出目录: {outputPath}");
                if (Directory.Exists(outputPath))
                {
                    try { Directory.Delete(outputPath, true); } catch { }
                }
                Directory.CreateDirectory(outputPath);

                // 创建输出目录结构
                string componentDir = Path.Combine(outputPath, "component");
                string registryDir = Path.Combine(outputPath, "Registry");
                Directory.CreateDirectory(componentDir);
                if (includeRegistry) Directory.CreateDirectory(registryDir);

                // FFU文件：使用多store解析，每个UFS LUN作为独立磁盘（与FFU浏览器一致）
                var diskList = disks.ToList();
                if (!string.IsNullOrEmpty(ffuPath) &&
                    Path.GetExtension(ffuPath).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"检测到FFU文件，使用多store解析模式: {ffuPath}");
                    var ffuDisks = LoadFFUDisksWithStores(ffuPath);
                    if (ffuDisks != null && ffuDisks.Any())
                    {
                        diskList = ffuDisks.ToList();
                        Log($"多store解析完成: {diskList.Count} 个磁盘");
                        for (int i = 0; i < diskList.Count; i++)
                        {
                            Log($"  磁盘 {i}: {diskList[i].Partitions?.Count() ?? 0} 个分区");
                        }
                    }
                    else
                    {
                        Log("多store解析失败，使用传入的磁盘列表");
                    }
                }

                foreach (var package in selectedPackages)
                {
                    try
                    {
                        Log($"处理组件: {package.Name} ({package.TypeDisplay})");

                        // 1. 从镜像中读取manifest文件
                        string? manifestPath = ExtractManifestFromImage(package, diskList, outputPath);
                        if (manifestPath == null)
                        {
                            result.Errors.Add($"无法从镜像中找到manifest: {package.Name}");
                            continue;
                        }

                        // 2. 解析manifest（自动处理DCM压缩）
                        var manifest = ParseManifestFile(manifestPath, package);
                        if (manifest == null)
                        {
                            result.Errors.Add($"无法解析manifest: {package.Name}");
                            Log($"  [失败] 无法解析manifest");
                            continue;
                        }

                        int manifestFileCount = manifest.Package?.CustomInformation?.File?.Count ?? 0;
                        int manifestRegCount = manifest.RegistryKeys?.Count ?? 0;
                        Log($"  [OK] manifest解析成功: {manifestFileCount}个文件, {manifestRegCount}个注册表项");

                        // 3. 提取文件
                        Log($"  开始提取文件...");
                        int fileCount = ExtractComponentFiles(manifest, package, diskList, componentDir, mode, manifestPath);
                        result.TotalFiles += fileCount;
                        Log($"  [OK] 文件提取完成: {fileCount}个");

                        // 4. 提取注册表
                        if (includeRegistry)
                        {
                            Log($"  开始提取注册表...");
                            int regCount = ExtractComponentRegistry(manifest, package, registryDir, mode, componentDir, diskList);
                            result.TotalRegistryKeys += regCount;
                            Log($"  [OK] 注册表提取完成: {regCount}项");
                        }

                        // 5. 收集依赖
                        int depCount = CollectDependencies(manifest, result.Dependencies, manifestPath);
                        if (depCount > 0)
                            Log($"  [OK] 收集到 {depCount} 个依赖");

                        result.ExtractedComponents.Add(package.Name);
                        result.TotalComponents++;
                        Log($"  [完成] {package.Name}: {fileCount}个文件, {(includeRegistry ? "注册表已提取" : "注册表跳过")}");
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{package.Name}: {ex.Message}");
                    }
                }

                // 6. 生成依赖说明文件
                if (result.Dependencies.Count > 0)
                {
                    string depFile = Path.Combine(outputPath, "Supplementary_dependencies.txt");
                    File.WriteAllLines(depFile, result.Dependencies, Encoding.UTF8);
                }

                // 7. 生成提取报告
                GenerateExtractReport(result, outputPath);

                // 8. 提取校验：计算所有提取文件的哈希，生成校验报告
                Log("开始提取校验...");
                VerifyExtraction(result, outputPath);
                Log($"校验完成: 通过{result.VerifiedFiles}个, 失败{result.VerifyFailed}个");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"提取失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 提取校验：遍历输出目录，计算所有文件的MD5哈希，生成校验报告
        /// </summary>
        private static void VerifyExtraction(ExtractResult result, string outputPath)
        {
            try
            {
                var fileEntries = new List<(string RelativePath, long Size, string Md5)>();
                int failed = 0;

                // 遍历输出目录中的所有文件
                var allFiles = Directory.GetFiles(outputPath, "*.*", SearchOption.AllDirectories);
                Log($"  校验文件总数: {allFiles.Length}");

                foreach (var file in allFiles)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        string relativePath = Path.GetRelativePath(outputPath, file);

                        // 计算MD5
                        using var fs = File.OpenRead(file);
                        using var md5 = System.Security.Cryptography.MD5.Create();
                        byte[] hash = md5.ComputeHash(fs);
                        string hashStr = BitConverter.ToString(hash).Replace("-", "").ToLower();

                        fileEntries.Add((relativePath, fi.Length, hashStr));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log($"  [校验失败] {file}: {ex.Message}");
                    }
                }

                result.VerifiedFiles = fileEntries.Count;
                result.VerifyFailed = failed;

                // 生成校验报告HTML
                string reportPath = Path.Combine(outputPath, "verify_report.html");
                GenerateVerifyReport(fileEntries, failed, outputPath, reportPath);
                result.VerifyReportPath = reportPath;
                Log($"  校验报告已生成: {reportPath}");
            }
            catch (Exception ex)
            {
                Log($"  校验过程出错: {ex.Message}");
                result.Errors.Add($"校验失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成校验报告HTML
        /// </summary>
        private static void GenerateVerifyReport(List<(string RelativePath, long Size, string Md5)> files, int failed, string outputPath, string reportPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='zh-CN'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<title>提取校验报告</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 20px; background: #f5f5f5; }");
            sb.AppendLine("h1 { color: #2c5aa0; border-bottom: 2px solid #2c5aa0; padding-bottom: 8px; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 12px 0; background: white; font-size: 12px; }");
            sb.AppendLine("th, td { border: 1px solid #ddd; padding: 6px 8px; text-align: left; }");
            sb.AppendLine("th { background: #2c5aa0; color: white; }");
            sb.AppendLine("tr:nth-child(even) { background: #f9f9f9; }");
            sb.AppendLine(".info-box { background: #e8f4fd; border-left: 4px solid #2c5aa0; padding: 12px; margin: 12px 0; }");
            sb.AppendLine(".success { color: #2e7d32; font-weight: bold; }");
            sb.AppendLine(".failed { color: #c62828; font-weight: bold; }");
            sb.AppendLine(".hash { font-family: Consolas, monospace; font-size: 11px; }");
            sb.AppendLine(".footer { margin-top: 30px; color: #888; font-size: 12px; text-align: center; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<h1>✅ 提取校验报告</h1>");

            sb.AppendLine("<div class='info-box'>");
            sb.AppendLine($"<p><strong>输出目录:</strong> {outputPath}</p>");
            sb.AppendLine($"<p><strong>校验时间:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
            sb.AppendLine($"<p><strong>校验通过:</strong> <span class='success'>{files.Count} 个文件</span></p>");
            sb.AppendLine($"<p><strong>校验失败:</strong> <span class='{(failed > 0 ? "failed" : "success")}'>{failed} 个文件</span></p>");
            sb.AppendLine("</div>");

            sb.AppendLine("<h2>📋 文件清单</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>#</th><th>文件路径</th><th>大小</th><th>MD5哈希</th></tr>");

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                sb.AppendLine($"<tr>");
                sb.AppendLine($"<td>{i + 1}</td>");
                sb.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(f.RelativePath)}</td>");
                sb.AppendLine($"<td>{f.Size:N0} 字节</td>");
                sb.AppendLine($"<td class='hash'>{f.Md5}</td>");
                sb.AppendLine($"</tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<div class='footer'>由 OneCore Image Designer 生成 | Component Extract Verification Report</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
        }

        #region Cab提取

        /// <summary>
        /// 从镜像中提取组件的cab文件
        /// </summary>
        /// <summary>
        /// 从镜像中读取manifest文件到临时目录
        /// </summary>
        private static string? ExtractManifestFromImage(PackageInfo package, List<IDisk> disks, string tempOutputPath)
        {
            try
            {
                if (string.IsNullOrEmpty(package.SourceManifest))
                {
                    Log($"  警告: 包 {package.Name} 没有SourceManifest路径，尝试从CabFileName推断");
                    // 尝试从CabFileName推断manifest路径
                    if (!string.IsNullOrEmpty(package.CabFileName))
                    {
                        string cabName = Path.GetFileName(package.CabFileName);
                        string[] possiblePaths = new[]
                        {
                            $@"Windows\servicing\Packages\{cabName}.mum",
                            $@"Windows\servicing\Packages\{cabName}.manifest",
                        };
                        foreach (var tryPath in possiblePaths)
                        {
                            foreach (var disk in disks)
                            {
                                if (disk.Partitions == null) continue;
                                foreach (var partition in disk.Partitions)
                                {
                                    var fs = partition.FileSystem;
                                    if (fs == null) continue;
                                    try
                                    {
                                        if (fs.FileExists(tryPath))
                                        {
                                            Log($"  从推断路径找到manifest: {tryPath}");
                                            return ExtractManifestToTemp(fs, tryPath, package, tempOutputPath);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    return null;
                }

                string manifestPathInImage = package.SourceManifest.Replace('\\', System.IO.Path.DirectorySeparatorChar);
                string manifestFileName = Path.GetFileName(manifestPathInImage);

                Log($"  查找manifest: {manifestPathInImage}");

                // 先尝试精确匹配
                foreach (var disk in disks)
                {
                    if (disk.Partitions == null) continue;
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;

                        try
                        {
                            if (fs.FileExists(manifestPathInImage))
                            {
                                return ExtractManifestToTemp(fs, manifestPathInImage, package, tempOutputPath);
                            }
                        }
                        catch { }
                    }
                }

                // 精确匹配失败，尝试枚举目录后模糊匹配（忽略大小写）
                Log($"  精确匹配失败，尝试模糊匹配...");
                foreach (var disk in disks)
                {
                    if (disk.Partitions == null) continue;
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;

                        try
                        {
                            string pkgDir = @"Windows\servicing\Packages";
                            if (!fs.DirectoryExists(pkgDir)) continue;

                            var files = fs.GetFiles(pkgDir, "*.mum", SearchOption.TopDirectoryOnly);
                            foreach (var f in files)
                            {
                                string fname = Path.GetFileName(f);
                                if (string.Equals(fname, manifestFileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    Log($"  模糊匹配成功: 分区[{partition.Name}] {fname}");
                                    return ExtractManifestToTemp(fs, f, package, tempOutputPath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"    分区[{partition.Name}] 枚举失败: {ex.Message}");
                        }
                    }
                }

                Log($"  警告: 未在镜像中找到manifest: {manifestPathInImage}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"  读取manifest失败: {ex.Message}");
                return null;
            }
        }

        private static string ExtractManifestToTemp(dynamic fs, string manifestPathInImage, PackageInfo package, string tempOutputPath)
        {
            string manifestFileName = Path.GetFileName(manifestPathInImage);
            string tempManifestDir = Path.Combine(tempOutputPath, "_temp_manifests");
            Directory.CreateDirectory(tempManifestDir);
            string outputManifestPath = Path.Combine(tempManifestDir, $"{package.Name}_{manifestFileName}");

            using var srcStream = fs.OpenFile(manifestPathInImage, FileMode.Open, FileAccess.Read);
            using var destStream = File.Create(outputManifestPath);
            srcStream.CopyTo(destStream);
            destStream.Flush();

            Log($"  从镜像读取manifest: {manifestPathInImage} ({new FileInfo(outputManifestPath).Length} 字节)");
            return outputManifestPath;
        }

        #endregion

        #region Manifest解析

        /// <summary>
        /// 解析manifest文件（自动处理DCM压缩）。
        /// </summary>
        /// <remarks>
        /// 改为复用 <see cref="ManifestDecompressor"/>，修掉原实现的三个问题：
        ///   1. <c>Process.Start</c> 后既不 using 也不 Dispose —— 每个 manifest 泄漏
        ///      一个进程句柄，批量处理上千个文件时会耗尽句柄；
        ///   2. 解压产物写成 <c>{manifestPath}.converted</c>，即落在【源文件所在目录】，
        ///      既污染源目录、又在目录只读时直接失败；
        ///   3. 与 ComponentStoreVerifier 各写一份 wcpex 调用，行为容易走偏。
        /// 现统一由共享服务解压到临时目录，并正确释放进程与读走输出流（避免管道死锁）。
        /// </remarks>
        private static XmlMum.Assembly? ParseManifestFile(string manifestFilePath, PackageInfo package)
        {
            try
            {
                Log($"  解析manifest: {Path.GetFileName(manifestFilePath)} ({new FileInfo(manifestFilePath).Length} 字节)");

                string? xml = ManifestDecompressor.ReadXmlText(manifestFilePath, out string? failure);
                if (xml is null)
                {
                    // 明确记录原因，而不是像原来那样静默继续解析一个压缩文件后失败
                    Log($"  无法取得 manifest XML：{failure}");
                    return null;
                }

                var serializer = new XmlSerializer(typeof(XmlMum.Assembly));
                using var reader = new StringReader(xml);
                return serializer.Deserialize(reader) as XmlMum.Assembly;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                Log($"  解析manifest失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 文件提取

        /// <summary>
        /// 提取组件文件
        /// </summary>
        private static int ExtractComponentFiles(
            XmlMum.Assembly manifest,
            PackageInfo package,
            List<IDisk> disks,
            string baseOutputDir,
            ExtractMode mode,
            string? manifestPath = null)
        {
            int count = 0;
            int notFound = 0;
            try
            {
                var files = manifest.Package?.CustomInformation?.File;
                if (files == null || files.Count == 0)
                {
                    Log($"    manifest中没有文件列表 (Package.CustomInformation.File为空)");
                    return 0;
                }

                Log($"    manifest中声明了 {files.Count} 个文件");
                string WindowsSideBySideManifestsFolderPath = @"Windows\WinSxS\Manifests";

                foreach (var packageFile in files)
                {
                    try
                    {
                        string fileName = packageFile.Name.ToLower();
                        if (fileName.StartsWith('\\'))
                            fileName = fileName[1..];

                        if (!fileName.Contains('\\') && fileName.EndsWith(".manifest"))
                        {
                            fileName = Path.Combine(WindowsSideBySideManifestsFolderPath, fileName);
                        }

                        // 宏替换
                        string normalized = fileName.Replace("$(runtime.bootdrive)", "")
                                                            .Replace("$(runtime.systemroot)", "windows")
                                                            .Replace("$(runtime.fonts)", @"windows\fonts")
                                                            .Replace("$(runtime.inf)", @"windows\inf")
                                                            .Replace("$(runtime.system)", @"windows\system")
                                                            .Replace("$(runtime.system32)", @"windows\system32")
                                                            .Replace("$(runtime.wbem)", @"windows\system32\wbem")
                                                            .Replace("$(runtime.drivers)", @"windows\system32\drivers")
                                                            .Replace("$(runtime.programfiles)", "Program Files")
                                                            .Replace("$(runtime.programdata)", "ProgramData")
                                                            .Replace("$(runtime.startmenu)", @"ProgramData\Microsoft\Windows\Start Menu");

                        // 在所有磁盘分区中查找文件
                        var found = FindAndExtractFile(normalized, disks, baseOutputDir, mode, package.Name);

                        // 如果是.cat文件且普通查找失败，尝试在CatRoot目录中搜索包名格式的cat文件
                        if (!found && normalized.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
                        {
                            found = FindAndExtractCatFile(package, disks, baseOutputDir, mode);
                            if (found)
                                Log($"    [Cat匹配] 从CatRoot目录找到包名格式cat文件: {package.Name}");
                        }

                        if (found)
                        {
                            count++;
                        }
                        else
                        {
                            notFound++;
                            if (notFound <= 10)
                                Log($"    [未找到] {normalized}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"    [错误] 处理文件 {packageFile.Name}: {ex.Message}");
                    }
                }

                if (notFound > 10)
                    Log($"    ... 还有 {notFound - 10} 个文件未找到");
            }
            catch (Exception ex)
            {
                Log($"  提取文件失败: {ex.Message}");
            }
            Log($"    文件提取统计: 成功{count}个, 未找到{notFound}个");

            // 提取WinSxS组件存储（组件的原始副本）
            Log($"  开始提取WinSxS组件存储...");
            int winsxsCount = ExtractWinSxSComponentStore(manifest, package, disks, baseOutputDir, mode, manifestPath);
            count += winsxsCount;
            if (winsxsCount > 0)
                Log($"  [OK] WinSxS组件存储提取完成: {winsxsCount}个文件");

            return count;
        }

        /// <summary>
        /// 在镜像中查找并提取文件
        /// </summary>
        private static bool FindAndExtractFile(
            string relativePath,
            List<IDisk> disks,
            string baseOutputDir,
            ExtractMode mode,
            string componentName)
        {
            try
            {
                // 规范化路径：去掉开头的反斜杠，避免Path.Combine把它当作绝对路径
                string normalizedPath = relativePath.TrimStart('\\', '/');
                if (string.IsNullOrEmpty(normalizedPath)) return false;

                for (int diskIdx = 0; diskIdx < disks.Count; diskIdx++)
                {
                    var disk = disks[diskIdx];
                    if (disk.Partitions == null) continue;

                    var partitions = disk.Partitions.ToList();
                    for (int partIdx = 0; partIdx < partitions.Count; partIdx++)
                    {
                        var partition = partitions[partIdx];
                        var fs = partition.FileSystem;
                        if (fs == null) continue;

                        try
                        {
                            // 同时尝试带和不带开头反斜杠的路径
                            bool found = fs.FileExists(normalizedPath);
                            if (!found && relativePath != normalizedPath)
                                found = fs.FileExists(relativePath);

                            if (found)
                            {
                                // 构建输出路径: diskX\PartitionX-分区名\原始路径
                                string partitionFolder = $"Partition{partIdx + 1}-{SanitizeFileName(partition.Name)}";
                                string outputSubDir = mode == ExtractMode.GroupByComponent
                                    ? Path.Combine(baseOutputDir, SanitizeFileName(componentName), $"disk{diskIdx}", partitionFolder)
                                    : Path.Combine(baseOutputDir, $"disk{diskIdx}", partitionFolder);

                                string outputFilePath = Path.Combine(outputSubDir, normalizedPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

                                string openPath = fs.FileExists(normalizedPath) ? normalizedPath : relativePath;
                                using var srcStream = fs.OpenFile(openPath, FileMode.Open, FileAccess.Read);
                                using var destStream = File.Create(outputFilePath);
                                srcStream.CopyTo(destStream);
                                destStream.Flush();

                                Log($"      [提取] disk{diskIdx}\\{partition.Name}: {relativePath} ({new FileInfo(outputFilePath).Length} 字节)");
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"      [错误] disk{diskIdx}\\{partition.Name} 读取文件 {relativePath}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"      [错误] 查找文件 {relativePath} 失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 在CatRoot目录中搜索并提取包名格式的cat文件
        /// manifest中声明的cat文件名通常是update.cat，但实际文件名是包名格式
        /// </summary>
        private static bool FindAndExtractCatFile(
            PackageInfo package,
            List<IDisk> disks,
            string baseOutputDir,
            ExtractMode mode)
        {
            string catRootPath = @"Windows\System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";
            string packageName = package.Name;

            for (int diskIdx = 0; diskIdx < disks.Count; diskIdx++)
            {
                var disk = disks[diskIdx];
                if (disk.Partitions == null) continue;

                var partitions = disk.Partitions.ToList();
                for (int partIdx = 0; partIdx < partitions.Count; partIdx++)
                {
                    var partition = partitions[partIdx];
                    var fs = partition.FileSystem;
                    if (fs == null) continue;

                    try
                    {
                        if (!fs.DirectoryExists(catRootPath)) continue;

                        var catFiles = fs.GetFiles(catRootPath);
                        foreach (var catFile in catFiles)
                        {
                            string catFileName = Path.GetFileName(catFile);
                            // cat文件名格式: PackageName~pkt~arch~~version.cat
                            // 检查是否包含包名
                            if (catFileName.StartsWith(packageName, StringComparison.OrdinalIgnoreCase) &&
                                catFileName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
                            {
                                // 构建输出路径
                                string partitionFolder = $"Partition{partIdx + 1}-{SanitizeFileName(partition.Name)}";
                                string outputSubDir = mode == ExtractMode.GroupByComponent
                                    ? Path.Combine(baseOutputDir, SanitizeFileName(package.Name), $"disk{diskIdx}", partitionFolder)
                                    : Path.Combine(baseOutputDir, $"disk{diskIdx}", partitionFolder);

                                string outputFilePath = Path.Combine(outputSubDir, catRootPath, catFileName);
                                Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

                                using var srcStream = fs.OpenFile(catFile, FileMode.Open, FileAccess.Read);
                                using var destStream = File.Create(outputFilePath);
                                srcStream.CopyTo(destStream);
                                destStream.Flush();

                                Log($"      [Cat提取] disk{diskIdx}\\{partition.Name}: {catFileName} ({new FileInfo(outputFilePath).Length} 字节)");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"      [Cat错误] disk{diskIdx}\\{partition.Name}: {ex.Message}");
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 构建WinSxS文件夹名称前缀（不含最后的hash部分）
        /// 格式: 架构_名称_公钥令牌_版本_语言_
        /// </summary>
        private static string GetWinSxSFolderPrefix(XmlMum.AssemblyIdentity? identity)
        {
            if (identity == null) return "";
            string arch = string.IsNullOrEmpty(identity.ProcessorArchitecture) ? "neutral" : identity.ProcessorArchitecture.ToLowerInvariant();
            string name = string.IsNullOrEmpty(identity.Name) ? "" : identity.Name.ToLowerInvariant();
            string pkt = string.IsNullOrEmpty(identity.PublicKeyToken) ? "" : identity.PublicKeyToken.ToLowerInvariant();
            string version = string.IsNullOrEmpty(identity.Version) ? "" : identity.Version;
            string lang = string.IsNullOrEmpty(identity.Language) ? "none" : identity.Language.ToLowerInvariant();
            return $"{arch}_{name}_{pkt}_{version}_{lang}_";
        }

        /// <summary>
        /// 递归提取目录中的所有文件和子目录
        /// </summary>
        private static int ExtractDirectoryRecursive(
            IFileSystem fs,
            string sourceDir,
            string outputDir,
            string logPrefix)
        {
            int count = 0;
            try
            {
                // 提取子目录
                foreach (var dir in fs.GetDirectories(sourceDir))
                {
                    string dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(dirName)) continue;
                    string outputSubDir = Path.Combine(outputDir, dirName);
                    Directory.CreateDirectory(outputSubDir);
                    count += ExtractDirectoryRecursive(fs, dir, outputSubDir, logPrefix);
                }

                // 提取文件
                foreach (var file in fs.GetFiles(sourceDir))
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        string outputFilePath = Path.Combine(outputDir, fileName);
                        using var srcStream = fs.OpenFile(file, FileMode.Open, FileAccess.Read);
                        using var destStream = File.Create(outputFilePath);
                        srcStream.CopyTo(destStream);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Log($"{logPrefix} 提取文件 {file} 失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"{logPrefix} 遍历目录 {sourceDir} 失败: {ex.Message}");
            }
            return count;
        }

        /// <summary>
        /// 提取WinSxS组件存储（组件的原始副本文件夹）
        /// 匹配优先级：
        /// 1. 从镜像WinSxS\Manifests目录枚举，根据包名匹配manifest文件名
        /// 2. 从临时manifest文件名提取（如果是原始路径）
        /// 3. 从AssemblyIdentity构建前缀匹配
        /// </summary>
        private static int ExtractWinSxSComponentStore(
            XmlMum.Assembly manifest,
            PackageInfo package,
            List<IDisk> disks,
            string baseOutputDir,
            ExtractMode mode,
            string? manifestPath = null)
        {
            string winsxsPath = @"Windows\WinSxS";
            string manifestsPath = @"Windows\WinSxS\Manifests";

            // 从AssemblyIdentity构建包名关键词（去掉-package后缀，转小写）
            string? packageNameKey = null;
            if (manifest.AssemblyIdentity?.Name != null)
            {
                packageNameKey = manifest.AssemblyIdentity.Name.ToLowerInvariant();
                if (packageNameKey.EndsWith("-package"))
                    packageNameKey = packageNameKey[..^8];
                Log($"    包名关键词: {packageNameKey}");
            }

            // 方式0（最可靠）：从manifest声明的文件列表中找WinSxS格式的manifest文件名
            string? winsxsFolderFromManifest = null;
            var declaredFiles = manifest.Package?.CustomInformation?.File;
            if (declaredFiles != null)
            {
                foreach (var f in declaredFiles)
                {
                    string fileName = f.Name ?? "";
                    // WinSxS manifest文件名格式: arch_name_pkt_version_lang_hash.manifest
                    // 特征：以.manifest结尾，包含多个下划线，不包含路径分隔符或变量
                    if (fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) &&
                        fileName.Count(c => c == '_') >= 4 &&
                        !fileName.Contains('\\') && !fileName.Contains('/') &&
                        !fileName.Contains('$'))
                    {
                        // 如果有包名关键词，优先匹配包含关键词的
                        if (!string.IsNullOrEmpty(packageNameKey))
                        {
                            if (fileName.ToLowerInvariant().Contains(packageNameKey))
                            {
                                winsxsFolderFromManifest = Path.GetFileNameWithoutExtension(fileName);
                                Log($"    从manifest声明找到WinSxS manifest(匹配包名): {winsxsFolderFromManifest}");
                                break;
                            }
                        }
                        else
                        {
                            winsxsFolderFromManifest = Path.GetFileNameWithoutExtension(fileName);
                            Log($"    从manifest声明找到WinSxS manifest: {winsxsFolderFromManifest}");
                            break;
                        }
                    }
                }

                // 如果没有匹配到包名，用第一个WinSxS格式的manifest
                if (winsxsFolderFromManifest == null && !string.IsNullOrEmpty(packageNameKey))
                {
                    foreach (var f in declaredFiles)
                    {
                        string fileName = f.Name ?? "";
                        if (fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) &&
                            fileName.Count(c => c == '_') >= 4 &&
                            !fileName.Contains('\\') && !fileName.Contains('/') &&
                            !fileName.Contains('$'))
                        {
                            winsxsFolderFromManifest = Path.GetFileNameWithoutExtension(fileName);
                            Log($"    从manifest声明找到第一个WinSxS manifest: {winsxsFolderFromManifest}");
                            break;
                        }
                    }
                }
            }

            for (int diskIdx = 0; diskIdx < disks.Count; diskIdx++)
            {
                var disk = disks[diskIdx];
                if (disk.Partitions == null) continue;

                var partitions = disk.Partitions.ToList();
                for (int partIdx = 0; partIdx < partitions.Count; partIdx++)
                {
                    var partition = partitions[partIdx];
                    var fs = partition.FileSystem;
                    if (fs == null) continue;

                    try
                    {
                        if (!fs.DirectoryExists(winsxsPath)) continue;

                        // 方式0（最优先）：从manifest声明的文件列表中找到的WinSxS manifest文件名
                        string? winsxsFolderName = winsxsFolderFromManifest;

                        // 方式1：枚举WinSxS\Manifests目录，根据包名匹配manifest文件名
                        if (winsxsFolderName == null && !string.IsNullOrEmpty(packageNameKey) && fs.DirectoryExists(manifestsPath))
                        {
                            var manifestFiles = fs.GetFiles(manifestsPath);
                            Log($"    WinSxS\\Manifests目录有 {manifestFiles.Count()} 个manifest文件");

                            foreach (var mf in manifestFiles)
                            {
                                string mfName = Path.GetFileNameWithoutExtension(mf).ToLowerInvariant();
                                // 直接检查整个manifest文件名是否包含包名关键词（更可靠）
                                if (mfName.Contains(packageNameKey))
                                {
                                    winsxsFolderName = Path.GetFileNameWithoutExtension(mf);
                                    Log($"    从WinSxS\\Manifests匹配到: {winsxsFolderName}");
                                    break;
                                }
                            }
                        }

                        // 方式2：从临时manifest文件名提取（如果格式正确）
                        if (winsxsFolderName == null && !string.IsNullOrEmpty(manifestPath))
                        {
                            string manifestFileName = Path.GetFileNameWithoutExtension(manifestPath);
                            if (!string.IsNullOrEmpty(manifestFileName) &&
                                manifestFileName.Contains('_') &&
                                manifestFileName.Count(c => c == '_') >= 4 &&
                                !manifestFileName.Contains('~')) // CBS keyform格式用~，不是WinSxS格式
                            {
                                winsxsFolderName = manifestFileName;
                                Log($"    从manifest文件名提取: {winsxsFolderName}");
                            }
                        }

                        var dirs = fs.GetDirectories(winsxsPath);

                        // 优先精确匹配
                        if (!string.IsNullOrEmpty(winsxsFolderName))
                        {
                            foreach (var dir in dirs)
                            {
                                string dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                                if (dirName.Equals(winsxsFolderName, StringComparison.OrdinalIgnoreCase))
                                {
                                    Log($"    精确匹配WinSxS组件存储: {dirName}");
                                    return ExtractWinSxFolder(fs, dir, diskIdx, partIdx, partition, package, baseOutputDir, mode);
                                }
                            }
                            Log($"    精确匹配未找到，尝试前缀匹配");
                        }

                        // 回退：前缀匹配（从AssemblyIdentity构建）
                        string prefixFromIdentity = GetWinSxSFolderPrefix(manifest.AssemblyIdentity);
                        if (!string.IsNullOrEmpty(prefixFromIdentity))
                        {
                            Log($"    前缀匹配: {prefixFromIdentity}*");
                            foreach (var dir in dirs)
                            {
                                string dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                                if (string.IsNullOrEmpty(dirName)) continue;

                                if (dirName.StartsWith(prefixFromIdentity, StringComparison.OrdinalIgnoreCase))
                                {
                                    Log($"    前缀匹配找到WinSxS组件存储: {dirName}");
                                    return ExtractWinSxFolder(fs, dir, diskIdx, partIdx, partition, package, baseOutputDir, mode);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"    查找WinSxS失败 disk{diskIdx}\\{partition.Name}: {ex.Message}");
                    }
                }
            }

            // 如果从manifest声明中找到了WinSxS文件夹名，但镜像中没有对应文件夹，创建空文件夹
            if (!string.IsNullOrEmpty(winsxsFolderFromManifest))
            {
                // 优先在MainOS分区创建，其次在第一个有WinSxS目录的分区
                int mainOsDiskIdx = -1, mainOsPartIdx = -1;
                int firstDiskIdx = -1, firstPartIdx = -1;

                for (int diskIdx = 0; diskIdx < disks.Count; diskIdx++)
                {
                    var disk = disks[diskIdx];
                    if (disk.Partitions == null) continue;

                    var partitions = disk.Partitions.ToList();
                    for (int partIdx = 0; partIdx < partitions.Count; partIdx++)
                    {
                        var partition = partitions[partIdx];
                        var fs = partition.FileSystem;
                        if (fs == null) continue;

                        if (fs.DirectoryExists(@"Windows\WinSxS"))
                        {
                            if (firstDiskIdx < 0)
                            {
                                firstDiskIdx = diskIdx;
                                firstPartIdx = partIdx;
                            }
                            if (partition.Name.Equals("MainOS", StringComparison.OrdinalIgnoreCase))
                            {
                                mainOsDiskIdx = diskIdx;
                                mainOsPartIdx = partIdx;
                            }
                        }
                    }
                }

                int targetDiskIdx = mainOsDiskIdx >= 0 ? mainOsDiskIdx : firstDiskIdx;
                int targetPartIdx = mainOsPartIdx >= 0 ? mainOsPartIdx : firstPartIdx;

                if (targetDiskIdx >= 0 && targetPartIdx >= 0)
                {
                    var targetPartition = disks[targetDiskIdx].Partitions.ToList()[targetPartIdx];
                    string partitionFolder = $"Partition{targetPartIdx + 1}-{SanitizeFileName(targetPartition.Name)}";
                    string outputSubDir = mode == ExtractMode.GroupByComponent
                        ? Path.Combine(baseOutputDir, SanitizeFileName(package.Name), $"disk{targetDiskIdx}", partitionFolder, "Windows", "WinSxS", winsxsFolderFromManifest)
                        : Path.Combine(baseOutputDir, $"disk{targetDiskIdx}", partitionFolder, "Windows", "WinSxS", winsxsFolderFromManifest);

                    Directory.CreateDirectory(outputSubDir);
                    Log($"    [创建空WinSxS文件夹] disk{targetDiskIdx}\\{targetPartition.Name}: Windows\\WinSxS\\{winsxsFolderFromManifest}");
                    return 0;
                }
            }

            Log("    未找到WinSxS组件存储文件夹");
            return 0;
        }

        /// <summary>
        /// 提取单个WinSxS文件夹
        /// </summary>
        private static int ExtractWinSxFolder(
            IFileSystem fs,
            string sourceDir,
            int diskIdx,
            int partIdx,
            IPartition partition,
            PackageInfo package,
            string baseOutputDir,
            ExtractMode mode)
        {
            string dirName = Path.GetFileName(sourceDir.TrimEnd('\\', '/'));
            string partitionFolder = $"Partition{partIdx + 1}-{SanitizeFileName(partition.Name)}";
            string outputSubDir = mode == ExtractMode.GroupByComponent
                ? Path.Combine(baseOutputDir, SanitizeFileName(package.Name), $"disk{diskIdx}", partitionFolder, "Windows", "WinSxS", dirName)
                : Path.Combine(baseOutputDir, $"disk{diskIdx}", partitionFolder, "Windows", "WinSxS", dirName);
            Directory.CreateDirectory(outputSubDir);

            int count = ExtractDirectoryRecursive(fs, sourceDir, outputSubDir, "    WinSxS");
            Log($"    WinSxS组件存储提取完成: {count}个文件");
            return count;
        }

        #endregion

        #region 注册表提取

        /// <summary>
        /// 提取组件注册表项，生成reg文件
        /// </summary>
        private static int ExtractComponentRegistry(
            XmlMum.Assembly manifest,
            PackageInfo package,
            string registryDir,
            ExtractMode mode,
            string componentDir,
            List<IDisk> disks)
        {
            int count = 0;
            try
            {
                // 解析manifest中的registry元素
                var registryKeys = ParseRegistryFromManifest(manifest);
                if (registryKeys.Count == 0)
                {
                    Log($"    manifest中没有注册表项");
                    return 0;
                }

                Log($"    解析到 {registryKeys.Count} 个注册表项");

                // 生成reg文件
                StringBuilder regContent = new StringBuilder();
                regContent.AppendLine("Windows Registry Editor Version 5.00");
                regContent.AppendLine();

                int valueCount = 0;
                foreach (var key in registryKeys)
                {
                    regContent.AppendLine($"[{key.KeyPath}]");
                    foreach (var value in key.Values)
                    {
                        regContent.AppendLine(FormatRegistryValue(value));
                        valueCount++;
                    }
                    regContent.AppendLine();
                    count++;
                }

                // 保存reg文件
                string regFileName = $"{SanitizeFileName(package.Name)}.reg";
                string regFilePath = mode == ExtractMode.GroupByComponent
                    ? Path.Combine(componentDir, SanitizeFileName(package.Name), regFileName)
                    : Path.Combine(registryDir, regFileName);

                Directory.CreateDirectory(Path.GetDirectoryName(regFilePath)!);
                File.WriteAllText(regFilePath, regContent.ToString(), new UnicodeEncoding(false, true));

                Log($"    [OK] 生成reg文件: {regFileName} ({count}个项, {valueCount}个值)");
                Log($"         路径: {regFilePath}");

                // 额外：解析驱动包INF文件中的AddReg注册表项
                int infRegCount = ParseAndAppendInfRegistry(package, disks, regFilePath, count);
                if (infRegCount > 0)
                {
                    Log($"    [OK] INF AddReg注册表: 追加 {infRegCount} 个项");
                    count += infRegCount;
                }
            }
            catch (Exception ex)
            {
                Log($"    [错误] 提取注册表失败: {ex.Message}");
            }
            return count;
        }

        /// <summary>
        /// 注册表项
        /// </summary>
        private class RegistryKeyEntry
        {
            public string KeyPath { get; set; } = "";
            public List<RegistryValueEntry> Values { get; } = new();
        }

        private class RegistryValueEntry
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "REG_SZ";
            public string Value { get; set; } = "";
        }

        /// <summary>
        /// 解析驱动包INF文件中的AddReg注册表项，并追加到reg文件
        /// </summary>
        private static int ParseAndAppendInfRegistry(PackageInfo package, List<IDisk> disks, string regFilePath, int existingKeyCount)
        {
            try
            {
                // 查找INF文件：优先从manifest文件列表中找.inf文件
                string? infContent = FindAndReadInfFile(package, disks);
                if (string.IsNullOrEmpty(infContent))
                {
                    Log($"    [INF] 未找到INF文件，跳过AddReg解析");
                    return 0;
                }

                // 解析INF文件中的AddReg段
                var infKeys = ParseInfAddRegSections(infContent);
                if (infKeys.Count == 0)
                {
                    Log($"    [INF] 未找到AddReg注册表项");
                    return 0;
                }

                // 追加到reg文件
                StringBuilder regAppend = new StringBuilder();
                int valueCount = 0;
                foreach (var key in infKeys)
                {
                    regAppend.AppendLine($"[{key.KeyPath}]");
                    foreach (var value in key.Values)
                    {
                        regAppend.AppendLine(FormatRegistryValue(value));
                        valueCount++;
                    }
                    regAppend.AppendLine();
                }

                // 追加到现有reg文件
                File.AppendAllText(regFilePath, regAppend.ToString(), new UnicodeEncoding(false, true));
                Log($"    [INF] 解析到 {infKeys.Count} 个注册表项, {valueCount} 个值");

                return infKeys.Count;
            }
            catch (Exception ex)
            {
                Log($"    [INF] 解析INF注册表出错: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 查找并读取驱动包的INF文件
        /// </summary>
        private static string? FindAndReadInfFile(PackageInfo package, List<IDisk> disks)
        {
            try
            {
                // 从包名推断INF文件名（去掉版本号等后缀）
                string packageName = package.Name;
                string[] possibleInfNames = new[]
                {
                    packageName + ".inf",
                    packageName.ToLower() + ".inf",
                    // 尝试去掉版本号
                    packageName.Split('~')[0] + ".inf",
                    packageName.Split('-')[0] + ".inf"
                };

                // 在所有磁盘分区中查找INF文件
                foreach (var disk in disks)
                {
                    if (disk.Partitions == null) continue;
                    foreach (var partition in disk.Partitions)
                    {
                        if (partition.FileSystem == null) continue;

                        // 搜索Windows\INF目录和根目录
                        string[] searchPaths = new[]
                        {
                            @"\Windows\INF",
                            @"\Windows\System32\DriverStore\FileRepository",
                            @"\"
                        };

                        foreach (var searchPath in searchPaths)
                        {
                            try
                            {
                                if (!partition.FileSystem.DirectoryExists(searchPath)) continue;

                                // 查找.inf文件
                                var infFiles = partition.FileSystem.GetFiles(searchPath, "*.inf", SearchOption.AllDirectories);
                                foreach (var infFile in infFiles)
                                {
                                    string fileName = Path.GetFileName(infFile).ToLower();
                                    // 匹配可能的INF文件名
                                    foreach (var possibleName in possibleInfNames)
                                    {
                                        if (fileName.Equals(possibleName.ToLower(), StringComparison.OrdinalIgnoreCase))
                                        {
                                            // 读取INF文件内容
                                            using var stream = partition.FileSystem.OpenFile(infFile, FileMode.Open, FileAccess.Read);
                                            using var reader = new StreamReader(stream, Encoding.Default);
                                            string content = reader.ReadToEnd();
                                            Log($"    [INF] 找到INF文件: {infFile}");
                                            return content;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Log($"    [INF] 查找INF文件出错: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析INF文件中的所有AddReg段
        /// </summary>
        private static List<RegistryKeyEntry> ParseInfAddRegSections(string infContent)
        {
            var keys = new List<RegistryKeyEntry>();
            try
            {
                var lines = infContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string currentSection = "";
                bool inAddRegSection = false;

                // 首先收集所有AddReg段名
                var addRegSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                    }
                    else if (inAddRegSection || currentSection.Equals("AddReg", StringComparison.OrdinalIgnoreCase))
                    {
                        // 在DefaultInstall段中，AddReg=xxx表示xxx段是AddReg段
                        if (trimmed.StartsWith("AddReg", StringComparison.OrdinalIgnoreCase))
                        {
                            int eqPos = trimmed.IndexOf('=');
                            if (eqPos > 0)
                            {
                                string sections = trimmed.Substring(eqPos + 1).Trim();
                                foreach (var sec in sections.Split(','))
                                {
                                    string secName = sec.Trim().Trim('"');
                                    if (!string.IsNullOrEmpty(secName))
                                        addRegSectionNames.Add(secName);
                                }
                            }
                        }
                    }

                    inAddRegSection = currentSection.Equals("DefaultInstall", StringComparison.OrdinalIgnoreCase) ||
                                      currentSection.Equals("DefaultInstall.NT", StringComparison.OrdinalIgnoreCase) ||
                                      currentSection.Equals("DefaultInstall.NTamd64", StringComparison.OrdinalIgnoreCase);
                }

                // 标准AddReg段名
                addRegSectionNames.Add("AddReg");
                addRegSectionNames.Add("AddReg.NT");
                addRegSectionNames.Add("AddReg.NTamd64");
                addRegSectionNames.Add("DriverInstall_AddReg");

                // 解析所有AddReg段
                currentSection = "";
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();

                    // 跳过注释和空行
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";")) continue;

                    // 段名
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }

                    // 检查是否在AddReg段中
                    if (!addRegSectionNames.Contains(currentSection)) continue;

                    // 解析AddReg行
                    var entry = ParseInfAddRegLine(trimmed);
                    if (entry.HasValue)
                    {
                        var (keyPath, value) = entry.Value;
                        // 查找或创建注册表项
                        var existingKey = keys.FirstOrDefault(k => k.KeyPath.Equals(keyPath, StringComparison.OrdinalIgnoreCase));
                        if (existingKey == null)
                        {
                            existingKey = new RegistryKeyEntry { KeyPath = keyPath };
                            keys.Add(existingKey);
                        }
                        existingKey.Values.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"    [INF] 解析AddReg段出错: {ex.Message}");
            }

            return keys;
        }

        /// <summary>
        /// 解析单行AddReg条目
        /// 格式: root,subkey,valuename,valuetype,valuedata
        /// </summary>
        private static (string KeyPath, RegistryValueEntry Value)? ParseInfAddRegLine(string line)
        {
            try
            {
                // 简单解析：按逗号分割，但要注意引号内的逗号
                var parts = ParseInfLineParts(line);
                if (parts.Length < 2) return null;

                string root = parts[0].Trim().Trim('"');
                string subKey = parts.Length > 1 ? parts[1].Trim().Trim('"') : "";
                string valueName = parts.Length > 2 ? parts[2].Trim().Trim('"') : "";
                string valueTypeStr = parts.Length > 3 ? parts[3].Trim().Trim('"') : "";
                string valueData = parts.Length > 4 ? string.Join(",", parts.Skip(4)).Trim().Trim('"') : "";

                // 转换根键
                string rootKey = root.ToUpper() switch
                {
                    "HKCR" => "HKEY_CLASSES_ROOT",
                    "HKCU" => "HKEY_CURRENT_USER",
                    "HKLM" => "HKEY_LOCAL_MACHINE",
                    "HKU" => "HKEY_USERS",
                    "HKCC" => "HKEY_CURRENT_CONFIG",
                    "HKR" => "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services", // 相对键，假设为服务键
                    _ => root
                };

                // 构建完整键路径
                string keyPath = string.IsNullOrEmpty(subKey) ? rootKey : $"{rootKey}\\{subKey}";

                // 转换值类型
                string valueType = "REG_SZ";
                if (!string.IsNullOrEmpty(valueTypeStr))
                {
                    if (valueTypeStr.StartsWith("0x"))
                    {
                        // 十六进制类型标志
                        if (uint.TryParse(valueTypeStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint typeFlag))
                        {
                            // 低16位是基本类型
                            uint baseType = typeFlag & 0xFFFF;
                            valueType = baseType switch
                            {
                                0x0000 => "REG_SZ",
                                0x0001 => "REG_BINARY",
                                0x0002 => "REG_DWORD",
                                0x0003 => "REG_DWORD_BIG_ENDIAN",
                                0x0004 => "REG_LINK",
                                0x0005 => "REG_MULTI_SZ",
                                0x0006 => "REG_EXPAND_SZ",
                                0x0007 => "REG_RESOURCE_LIST",
                                0x0008 => "REG_FULL_RESOURCE_DESCRIPTOR",
                                0x0009 => "REG_RESOURCE_REQUIREMENTS_LIST",
                                0x000A => "REG_QWORD",
                                _ => "REG_SZ"
                            };
                        }
                    }
                    else
                    {
                        valueType = valueTypeStr.ToUpper() switch
                        {
                            "SZ" => "REG_SZ",
                            "BINARY" => "REG_BINARY",
                            "DWORD" => "REG_DWORD",
                            "MULTI_SZ" => "REG_MULTI_SZ",
                            "EXPAND_SZ" => "REG_EXPAND_SZ",
                            "QWORD" => "REG_QWORD",
                            _ => "REG_SZ"
                        };
                    }
                }

                // 处理默认值名
                if (string.IsNullOrEmpty(valueName)) valueName = "@";

                return (keyPath, new RegistryValueEntry
                {
                    Name = valueName,
                    Type = valueType,
                    Value = valueData
                });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析INF行的各部分（处理引号内的逗号）
        /// </summary>
        private static string[] ParseInfLineParts(string line)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                }
                else if (c == ',' && !inQuotes)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            parts.Add(current.ToString());

            return parts.ToArray();
        }

        /// <summary>
        /// 从manifest中解析注册表项
        /// </summary>
        private static List<RegistryKeyEntry> ParseRegistryFromManifest(XmlMum.Assembly manifest)
        {
            var keys = new List<RegistryKeyEntry>();
            try
            {
                if (manifest.RegistryKeys == null || manifest.RegistryKeys.Count == 0)
                    return keys;

                foreach (var regKey in manifest.RegistryKeys)
                {
                    if (string.IsNullOrEmpty(regKey.KeyName)) continue;

                    var entry = new RegistryKeyEntry
                    {
                        KeyPath = ConvertRegistryKeyPath(regKey.KeyName)
                    };

                    if (regKey.RegistryValues != null)
                    {
                        foreach (var regValue in regKey.RegistryValues)
                        {
                            entry.Values.Add(new RegistryValueEntry
                            {
                                Name = regValue.Name ?? "",
                                Type = ConvertRegistryValueType(regValue.ValueType),
                                Value = regValue.Value ?? ""
                            });
                        }
                    }

                    keys.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Log($"  解析注册表失败: {ex.Message}");
            }
            return keys;
        }

        /// <summary>
        /// 转换注册表项路径（manifest中的路径格式 -> reg文件格式）
        /// </summary>
        private static string ConvertRegistryKeyPath(string keyName)
        {
            // manifest中可能使用HKLM\...或其他格式，需要转换
            return keyName
                .Replace("HKLM", "HKEY_LOCAL_MACHINE")
                .Replace("HKCU", "HKEY_CURRENT_USER")
                .Replace("HKCR", "HKEY_CLASSES_ROOT")
                .Replace("HKU", "HKEY_USERS")
                .Replace("HKCC", "HKEY_CURRENT_CONFIG");
        }

        /// <summary>
        /// 转换注册表值类型
        /// </summary>
        private static string ConvertRegistryValueType(string? valueType)
        {
            if (string.IsNullOrEmpty(valueType)) return "REG_SZ";
            return valueType.ToUpper() switch
            {
                "REG_SZ" => "REG_SZ",
                "REG_DWORD" => "REG_DWORD",
                "REG_EXPAND_SZ" => "REG_EXPAND_SZ",
                "REG_MULTI_SZ" => "REG_MULTI_SZ",
                "REG_BINARY" => "REG_BINARY",
                "REG_QWORD" => "REG_QWORD",
                _ => "REG_SZ"
            };
        }

        /// <summary>
        /// 格式化注册表值为reg格式
        /// </summary>
        private static string FormatRegistryValue(RegistryValueEntry value)
        {
            string name = value.Name == "@" ? "@" : $"\"{value.Name}\"";
            return value.Type switch
            {
                "REG_SZ" => $"{name}=\"{value.Value}\"",
                "REG_DWORD" => $"{name}=dword:{value.Value}",
                "REG_EXPAND_SZ" => $"{name}=hex(2):{StringToHex(value.Value)}",
                "REG_MULTI_SZ" => $"{name}=hex(7):{StringToMultiSzHex(value.Value)}",
                "REG_BINARY" => $"{name}=hex:{value.Value}",
                "REG_QWORD" => $"{name}=hex(b):{StringToHex(value.Value)}",
                _ => $"{name}=\"{value.Value}\""
            };
        }

        private static string StringToHex(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);
            return string.Join(",", bytes.Select(b => b.ToString("x2")));
        }

        private static string StringToMultiSzHex(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0\0");
            return string.Join(",", bytes.Select(b => b.ToString("x2")));
        }

        #endregion

        #region 依赖收集

        /// <summary>
        /// <summary>
        /// 收集组件依赖：解析manifest XML中的dependency元素
        /// </summary>
        private static int CollectDependencies(XmlMum.Assembly manifest, List<string> dependencies, string? manifestPath = null)
        {
            try
            {
                int before = dependencies.Count;

                // 优先从manifest XML文件中解析dependency元素
                if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                {
                    ParseDependenciesFromManifestXml(manifestPath, dependencies);
                }

                // 如果XML解析没有找到依赖，尝试从manifest对象中获取（备用）
                if (dependencies.Count == before)
                {
                    // 从AssemblyIdentity中获取当前组件信息（用于日志）
                    string currentName = manifest.AssemblyIdentity?.Name ?? "unknown";
                    Log($"  [依赖] 组件 {currentName} 未声明显式依赖");
                }

                return dependencies.Count - before;
            }
            catch (Exception ex)
            {
                Log($"  [依赖] 收集依赖出错: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 从manifest XML文件中解析dependency元素
        /// </summary>
        private static void ParseDependenciesFromManifestXml(string manifestPath, List<string> dependencies)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(manifestPath);

                // 注册命名空间
                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v3");

                // 查找所有dependency元素
                XmlNodeList depNodes = doc.SelectNodes("//asm:dependency/asm:dependentAssembly/asm:assemblyIdentity", nsmgr);
                if (depNodes == null || depNodes.Count == 0) return;

                foreach (XmlNode node in depNodes)
                {
                    string name = node.Attributes?["name"]?.Value ?? "";
                    string version = node.Attributes?["version"]?.Value ?? "";
                    string arch = node.Attributes?["processorArchitecture"]?.Value ?? "";
                    string publicKeyToken = node.Attributes?["publicKeyToken"]?.Value ?? "";
                    string language = node.Attributes?["language"]?.Value ?? "";

                    if (string.IsNullOrEmpty(name)) continue;

                    // 格式化依赖信息
                    string depInfo = name;
                    if (!string.IsNullOrEmpty(version)) depInfo += $" (v{version})";
                    if (!string.IsNullOrEmpty(arch)) depInfo += $" [{arch}]";
                    if (!string.IsNullOrEmpty(publicKeyToken)) depInfo += $" token={publicKeyToken}";
                    if (!string.IsNullOrEmpty(language) && language != "*") depInfo += $" lang={language}";

                    if (!dependencies.Contains(depInfo))
                    {
                        dependencies.Add(depInfo);
                        Log($"  [依赖]   -> {depInfo}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  [依赖] 解析manifest XML失败: {ex.Message}");
            }
        }

        #endregion

        #region 提取报告

        /// <summary>
        /// 生成提取报告
        /// </summary>
        private static void GenerateExtractReport(ExtractResult result, string outputPath)
        {
            try
            {
                string reportPath = Path.Combine(outputPath, "extract_report.html");
                var sb = new StringBuilder();

                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html><head><meta charset='utf-8'><title>组件提取报告</title>");
                sb.AppendLine("<style>body{font-family:Segoe UI,Arial;margin:20px;}");
                sb.AppendLine("h1{color:#007acc;}table{border-collapse:collapse;width:100%;margin:10px 0;}");
                sb.AppendLine("th,td{border:1px solid #ddd;padding:8px;text-align:left;}");
                sb.AppendLine("th{background:#f5f5f5;}.error{color:red;}.success{color:green;}</style></head><body>");

                sb.AppendLine($"<h1>组件提取报告</h1>");
                sb.AppendLine($"<p>提取时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
                sb.AppendLine($"<p>输出目录: {outputPath}</p>");

                sb.AppendLine("<h2>统计</h2>");
                sb.AppendLine("<table>");
                sb.AppendLine($"<tr><td>提取组件数</td><td>{result.TotalComponents}</td></tr>");
                sb.AppendLine($"<tr><td>提取文件数</td><td>{result.TotalFiles}</td></tr>");
                sb.AppendLine($"<tr><td>注册表项数</td><td>{result.TotalRegistryKeys}</td></tr>");
                sb.AppendLine($"<tr><td>错误数</td><td class='{(result.Errors.Count > 0 ? "error" : "success")}'>{result.Errors.Count}</td></tr>");
                sb.AppendLine("</table>");

                sb.AppendLine("<h2>已提取组件</h2>");
                sb.AppendLine("<table><tr><th>序号</th><th>组件名</th></tr>");
                for (int i = 0; i < result.ExtractedComponents.Count; i++)
                    sb.AppendLine($"<tr><td>{i + 1}</td><td>{result.ExtractedComponents[i]}</td></tr>");
                sb.AppendLine("</table>");

                if (result.Errors.Count > 0)
                {
                    sb.AppendLine("<h2>错误信息</h2>");
                    sb.AppendLine("<table><tr><th>错误</th></tr>");
                    foreach (var err in result.Errors)
                        sb.AppendLine($"<tr><td class='error'>{err}</td></tr>");
                    sb.AppendLine("</table>");
                }

                sb.AppendLine("</body></html>");
                File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        #endregion

        #region 工具方法

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string? _logFilePath;

        /// <summary>可选日志转发钩子（工作线程调用；用于把提取过程实时汇入任务日志窗口）。</summary>
        public static Action<string>? LiveSink;

        private static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            System.Diagnostics.Debug.WriteLine($"[ComponentExtractor] {message}");
            try { LiveSink?.Invoke(message); } catch { }

            try
            {
                if (_logFilePath == null)
                {
                    string logDir = OcidLogPaths.MiscDir;
                    Directory.CreateDirectory(logDir);
                    _logFilePath = Path.Combine(logDir, $"component_extract_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }

        #endregion
    }
}
