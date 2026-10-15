using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using OneCore_Image_Designer.Models.DeviceLayout;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 设备布局解析服务
    /// </summary>
    public class DeviceLayoutService
    {
        private static readonly XmlSerializer _serializerV1 = CreateSerializer("http://schemas.microsoft.com/embedded/2004/10/ImageUpdate", "DeviceLayout");
        private static readonly XmlSerializer _serializerV2 = CreateSerializer("http://schemas.microsoft.com/embedded/2004/10/ImageUpdate/v2", "DeviceLayout");
        // SpaceSplit 的 MSDeviceLayout：v2 命名空间，但根标签不同
        private static readonly XmlSerializer _serializerMS = CreateSerializer("http://schemas.microsoft.com/embedded/2004/10/ImageUpdate/v2", "MSDeviceLayout");

        private static XmlSerializer CreateSerializer(string ns, string rootTag = "DeviceLayout")
        {
            var overrides = new XmlAttributeOverrides();
            var rootAttr = new XmlRootAttribute(rootTag)
            {
                Namespace = ns
            };
            overrides.Add(typeof(DeviceLayoutDoc), new XmlAttributes { XmlRoot = rootAttr });
            return new XmlSerializer(typeof(DeviceLayoutDoc), overrides);
        }

        private static readonly string LogDir = OcidLogPaths.CrashDir;
        private static string? _currentLogFile;
        private static readonly object _logLock = new();

        /// <summary>
        /// 最后一次错误信息
        /// </summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// 最后一次日志文件路径
        /// </summary>
        public static string LastLogFile { get; private set; } = string.Empty;

        /// <summary>
        /// 是否保留临时解压目录（用于调试）
        /// </summary>
        public static bool KeepTempDir { get; set; } = false;

        /// <summary>
        /// 最后一次临时解压目录路径
        /// </summary>
        public static string LastTempDir { get; private set; } = string.Empty;

        private static void InitLog()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentLogFile = Path.Combine(LogDir, $"devicelayout_{timestamp}.log");
                LastLogFile = _currentLogFile;
                File.WriteAllText(_currentLogFile, $"=== DeviceLayout 解析日志 ===\n时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");
            }
            catch { }
        }

        private static void Log(string message)
        {
            try
            {
                if (_currentLogFile != null)
                {
                    lock (_logLock)
                    {
                        File.AppendAllText(_currentLogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[DeviceLayout] {message}");
            }
            catch { }
        }

        /// <summary>
        /// 从CAB文件中提取DeviceLayout.xml并解析
        /// </summary>
        public static DeviceLayoutDoc? ParseFromCab(string cabPath)
        {
            LastError = string.Empty;
            InitLog();
            Log($"开始解析CAB: {cabPath}");

            try
            {
                if (!File.Exists(cabPath))
                {
                    LastError = $"CAB文件不存在: {cabPath}";
                    Log($"错误: {LastError}");
                    return null;
                }

                var fileInfo = new FileInfo(cabPath);
                Log($"CAB文件大小: {fileInfo.Length} 字节");

                var tempDir = Path.Combine(Path.GetTempPath(), $"DL_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                LastTempDir = tempDir;
                Log($"临时解压目录: {tempDir}");

                try
                {
                    // 使用expand.exe解压CAB
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "expand.exe",
                        Arguments = $"-F:* \"{cabPath}\" \"{tempDir}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    Log($"执行命令: expand.exe -F:* \"{cabPath}\" \"{tempDir}\"");

                    using var process = System.Diagnostics.Process.Start(startInfo);
                    var output = process?.StandardOutput.ReadToEnd();
                    var error = process?.StandardError.ReadToEnd();
                    process?.WaitForExit();
                    var exitCode = process?.ExitCode ?? -1;

                    Log($"expand.exe 退出码: {exitCode}");
                    if (!string.IsNullOrEmpty(output))
                        Log($"expand输出:\n{output}");
                    if (!string.IsNullOrEmpty(error))
                        Log($"expand错误:\n{error}");

                    // 列出解压后的所有文件
                    var allFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
                    Log($"解压后文件总数: {allFiles.Length}");
                    foreach (var f in allFiles)
                    {
                        var relPath = f.Replace(tempDir, "");
                        var fi = new FileInfo(f);
                        Log($"  文件: {relPath} ({fi.Length} 字节)");
                    }

                    // 查找 DeviceLayout.xml 或 SpaceSplit 的 DeviceLayoutMS.xml
                    var dlFiles = Directory.GetFiles(tempDir, "DeviceLayout.xml", SearchOption.AllDirectories);
                    if (dlFiles.Length == 0)
                        dlFiles = Directory.GetFiles(tempDir, "DeviceLayoutMS.xml", SearchOption.AllDirectories);
                    Log($"找到布局 XML 数量: {dlFiles.Length}");

                    if (dlFiles.Length == 0)
                    {
                        LastError = $"CAB 中未找到 DeviceLayout.xml / DeviceLayoutMS.xml。解压了 {allFiles.Length} 个文件";
                        Log($"错误: {LastError}");
                        return null;
                    }

                    var dlFile = dlFiles[0];
                    Log($"使用布局文件: {Path.GetFileName(dlFile)}");

                    var doc = ParseFromXml(dlFile);
                    if (doc == null)
                    {
                        LastError = $"DeviceLayout.xml解析失败: {LastError}";
                        Log($"错误: {LastError}");
                        return null;
                    }

                    Log($"解析成功! Stores={doc.AllStores.Count}, Partitions={doc.AllPartitions.Count}");
                    foreach (var store in doc.AllStores)
                    {
                        Log($"  Store: {store.StoreType}, Partitions={store.Partitions.Count}, SizeInSectors={store.SizeInSectors}");
                        foreach (var p in store.Partitions)
                        {
                            Log($"    Partition: {p.Name}, Type={p.Type}, FileSystem={p.FileSystem}, TotalSectors={p.TotalSectors}, UseAllSpace={p.UseAllSpace}");
                        }
                    }

                    return doc;
                }
                finally
                {
                    if (!KeepTempDir)
                    {
                        try { Directory.Delete(tempDir, true); Log($"已清理临时目录: {tempDir}"); }
                        catch (Exception ex) { Log($"清理临时目录失败: {ex.Message}"); }
                    }
                    else
                    {
                        Log($"保留临时目录: {tempDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"从CAB解析DeviceLayout异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                Log($"异常: {LastError}");
                if (ex.InnerException != null)
                    Log($"内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从XML文件解析DeviceLayout (支持v1和v2命名空间)
        /// </summary>
        public static DeviceLayoutDoc? ParseFromXml(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath))
                {
                    LastError = $"XML文件不存在: {xmlPath}";
                    Log($"错误: {LastError}");
                    return null;
                }

                // 先读取XML内容检测命名空间与根标签
                var xmlContent = File.ReadAllText(xmlPath);
                var isMS = xmlContent.Contains("<MSDeviceLayout");
                var isV2 = xmlContent.Contains("http://schemas.microsoft.com/embedded/2004/10/ImageUpdate/v2");
                Log($"DeviceLayout.xml: 根标签={(isMS ? "MSDeviceLayout" : "DeviceLayout")}, 命名空间={(isV2 ? "v2" : "v1")}");

                using var stream = new FileStream(xmlPath, FileMode.Open, FileAccess.Read);
                var serializer = isMS ? _serializerMS : (isV2 ? _serializerV2 : _serializerV1);
                Log($"开始反序列化...");
                var doc = (DeviceLayoutDoc?)serializer.Deserialize(stream);

                if (doc == null)
                {
                    LastError = "反序列化返回null";
                    Log($"错误: {LastError}");
                    return null;
                }

                doc.RootTag = isMS ? "MSDeviceLayout" : "DeviceLayout";
                // 字节制检测：SpaceSplit（MSDeviceLayout）用 TotalBytes/SizeInBytes 而非扇区数
                doc.UsesBytes = isMS || xmlContent.Contains("<TotalBytes") || xmlContent.Contains("<SizeInBytes");

                Log($"反序列化成功: Partitions={doc.Partitions.Count}, Stores={doc.Stores.Count}, StoragePools={doc.StoragePools.Count}, UsesBytes={doc.UsesBytes}");

                // 把根节点 SectorSize 向下传播到所有 Store/Partition（容量换算需要它）；
                // 再给每个分区打「存储池内 / 池外·盘/LUN」归属标记，供分区表界面区分显示，
                // 避免把池内成员盘与池外物理盘（Default/LUN/容器分区）混为一谈。
                doc.ApplySectorSize();
                doc.DecorateProvenance();

                Log($"AllStores={doc.AllStores.Count}, AllPartitions={doc.AllPartitions.Count}");
                Log($"SectorSize={doc.SectorSize}, ChunkSize={doc.ChunkSize}");
                return doc;
            }
            catch (Exception ex)
            {
                LastError = $"解析DeviceLayout.xml异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                if (ex.InnerException != null)
                    LastError += $"\n内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                Log($"异常: {LastError}");
                return null;
            }
        }

        /// <summary>
        /// 从目录中搜索DeviceLayout相关的CAB文件
        /// </summary>
        public static List<string> SearchDeviceLayoutCabs(string rootDirectory)
        {
            var result = new List<string>();
            try
            {
                var allCabs = Directory.GetFiles(rootDirectory, "*.cab", SearchOption.AllDirectories);
                foreach (var cab in allCabs)
                {
                    var name = Path.GetFileName(cab);
                    if (name.Contains("DeviceLayout", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(cab);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"搜索DeviceLayout CAB失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 从DeviceLayout CAB文件名解析SOC/SV/Device信息
        /// </summary>
        public static DeviceLayoutInfo? ParseInfoFromCabName(string cabPath)
        {
            var name = Path.GetFileNameWithoutExtension(cabPath);
            var info = new DeviceLayoutInfo();

            // 解析SOC: 查找 DEVICELAYOUT_XXX 模式
            var socMatch = System.Text.RegularExpressions.Regex.Match(name, @"DEVICELAYOUT_([A-Z0-9_]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (socMatch.Success)
            {
                info.SOC = socMatch.Groups[1].Value;
            }

            // 解析SV: 从芯片型号推断
            if (name.Contains("SDM845", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("QC845", StringComparison.OrdinalIgnoreCase))
            {
                info.SV = "Qualcomm";
            }
            else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                info.SV = "Intel";
            }

            // 解析Device: 从ANDXXX等模式推断
            var devMatch = System.Text.RegularExpressions.Regex.Match(name, @"(AND\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (devMatch.Success)
            {
                info.Device = $"Andromeda{devMatch.Groups[1].Value.Replace("AND", "")}";
            }

            return info;
        }

        /// <summary>
        /// 把内存布局序列化为 DeviceLayout.xml（与解析对称，复用同一 XmlSerializer 工厂）。
        /// v2 命名空间输出与 20274/QC8998 官方样本一致。
        /// </summary>
        public static void Serialize(DeviceLayoutDoc doc, string xmlPath, bool isV2 = true)
        {
            var serializer = doc.RootTag == "MSDeviceLayout" ? _serializerMS
                : isV2 ? _serializerV2 : _serializerV1;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(xmlPath))!);
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false),  // 官方样本无 BOM（mum/xml 都是）
            };
            using var writer = System.Xml.XmlWriter.Create(xmlPath, settings);
            serializer.Serialize(writer, doc);
        }
    }

    /// <summary>
    /// 设备布局信息（SOC/SV/Device）
    /// </summary>
    public class DeviceLayoutInfo
    {
        public string SOC { get; set; } = string.Empty;
        public string SV { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string CabPath { get; set; } = string.Empty;
        public bool IsParsed => !string.IsNullOrEmpty(SOC);
    }
}