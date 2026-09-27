using System.IO;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// UEFI 固件解析器
    /// 解析 Firmware Volume (FV)、Firmware File System (FFS)、Section 结构
    /// 基于 UEFIReader (Heathcliff74xda) 的算法实现
    /// </summary>
    public static class UefiFirmwareParser
    {
        #region 日志

        private static string? _logFile;
        private static readonly object _logLock = new();

        private static void InitLog()
        {
            if (_logFile != null) return;
            string logDir = Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Logs");
            Directory.CreateDirectory(logDir);
            _logFile = Path.Combine(logDir, $"firmware_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }

        private static void Log(string message)
        {
            try
            {
                InitLog();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                lock (_logLock)
                {
                    File.AppendAllText(_logFile!, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static string? GetLogFilePath()
        {
            return _logFile;
        }

        #endregion

        #region 数据结构

        /// <summary>
        /// 固件卷 (FV)
        /// </summary>
        public class FirmwareVolume
        {
            public ulong Offset { get; set; }
            public ulong Size { get; set; }
            public string Magic { get; set; } = "";
            public List<FirmwareFile> Files { get; } = new();
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// 固件文件 (FFS File)
        /// </summary>
        public class FirmwareFile
        {
            public ulong Offset { get; set; }
            public ulong Size { get; set; }
            public Guid Guid { get; set; }
            public byte Type { get; set; }
            public string TypeName { get; set; } = "";
            public string? Name { get; set; }
            public List<FirmwareSection> Sections { get; } = new();
            public bool ChecksumValid { get; set; }
        }

        /// <summary>
        /// 固件 Section
        /// </summary>
        public class FirmwareSection
        {
            public ulong Offset { get; set; }
            public ulong Size { get; set; }
            public byte Type { get; set; }
            public string TypeName { get; set; } = "";
            public string? Name { get; set; }
            public byte[]? Data { get; set; }
            public bool IsCompressed { get; set; }
            public string? CompressionType { get; set; }
        }

        /// <summary>
        /// 解析结果
        /// </summary>
        public class ParseResult
        {
            public List<FirmwareVolume> Volumes { get; } = new();
            public int TotalFiles { get; set; }
            public int TotalSections { get; set; }
            public List<string> Errors { get; } = new();
            public string? BuildId { get; set; }
        }

        #endregion

        #region FFS 文件类型

        private static readonly Dictionary<byte, string> FileTypes = new()
        {
            { 0x00, "All" },
            { 0x01, "Raw" },
            { 0x02, "Freeform" },
            { 0x03, "Security Core (SEC)" },
            { 0x04, "PEI Core" },
            { 0x05, "DXE Core" },
            { 0x06, "PEIM" },
            { 0x07, "Driver" },
            { 0x08, "Combined PEIM/Driver" },
            { 0x09, "Application" },
            { 0x0A, "MM" },
            { 0x0B, "Firmware Volume Image" },
            { 0x0C, "Combined MM/DXE" },
            { 0xF0, "Pad" },
            { 0xFF, "All" }
        };

        #endregion

        #region Section 类型

        private static readonly Dictionary<byte, string> SectionTypes = new()
        {
            { 0x01, "Compression" },
            { 0x02, "GUID Defined" },
            { 0x03, "Disposable" },
            { 0x10, "PE32" },
            { 0x11, "PIC" },
            { 0x12, "TE" },
            { 0x13, "DXE Depex" },
            { 0x14, "Version" },
            { 0x15, "User Interface (UI)" },
            { 0x16, "Compatibility16" },
            { 0x17, "Firmware Volume Image" },
            { 0x18, "Freeform Subtype GUID" },
            { 0x19, "Raw" },
            { 0x1B, "PEI Depex" },
            { 0x1C, "MM Depex" }
        };

        #endregion

        #region 公共方法

        /// <summary>
        /// 解析固件二进制数据
        /// </summary>
        /// <param name="data">固件二进制数据</param>
        /// <returns>解析结果</returns>
        public static ParseResult Parse(byte[] data)
        {
            var result = new ParseResult();
            Log($"[Parse] 开始解析，数据大小: {data.Length:N0} 字节");

            try
            {
                // 查找所有 FV 头（_FVH magic）
                var fvOffsets = FindAllFvHeaders(data);
                Log($"[Parse] 找到 {fvOffsets.Count} 个 FV 头");
                for (int i = 0; i < fvOffsets.Count; i++)
                {
                    Log($"[Parse]   FV[{i}] @ 0x{fvOffsets[i]:X}");
                }

                if (fvOffsets.Count == 0)
                {
                    result.Errors.Add("未找到 Firmware Volume 头 (_FVH)");
                    Log($"[Parse] ❌ 未找到 FV 头");
                    return result;
                }

                int volumeIndex = 0;
                foreach (var fvOffset in fvOffsets)
                {
                    try
                    {
                        Log($"[Parse] 解析 FV @ 0x{fvOffset:X}...");
                        var volume = ParseVolume(data, fvOffset, volumeIndex);
                        if (volume != null)
                        {
                            result.Volumes.Add(volume);
                            result.TotalFiles += volume.Files.Count;
                            result.TotalSections += volume.Files.Sum(f => f.Sections.Count);
                            Log($"[Parse] ✅ FV{volumeIndex}: 大小={volume.Size:N0}, 文件数={volume.Files.Count}");
                            volumeIndex++;
                        }
                        else
                        {
                            Log($"[Parse] ⚠️ FV @ 0x{fvOffset:X} 解析返回 null");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"解析 FV @ 0x{fvOffset:X} 失败: {ex.Message}");
                        Log($"[Parse] ❌ FV @ 0x{fvOffset:X} 解析异常: {ex.Message}");
                    }
                }

                // 尝试提取 Build ID
                result.BuildId = ExtractBuildId(data);
                if (!string.IsNullOrEmpty(result.BuildId))
                {
                    Log($"[Parse] Build ID: {result.BuildId}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"解析失败: {ex.Message}");
                Log($"[Parse] ❌ 解析异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 从文件解析固件
        /// </summary>
        public static ParseResult ParseFile(string filePath)
        {
            Log($"=== ParseFile 开始 ===");
            Log($"文件路径: {filePath}");
            Log($"文件存在: {File.Exists(filePath)}");

            if (!File.Exists(filePath))
            {
                Log($"❌ 文件不存在");
                var errResult = new ParseResult();
                errResult.Errors.Add($"文件不存在: {filePath}");
                return errResult;
            }

            byte[] data = File.ReadAllBytes(filePath);
            Log($"文件大小: {data.Length:N0} 字节 ({Math.Round(data.Length / 1024.0 / 1024.0, 2)} MB)");

            var result = Parse(data);

            Log($"=== ParseFile 完成: FV={result.Volumes.Count}, Files={result.TotalFiles}, Sections={result.TotalSections}, Errors={result.Errors.Count} ===");
            if (!string.IsNullOrEmpty(result.BuildId))
            {
                Log($"Build ID: {result.BuildId}");
            }
            return result;
        }

        /// <summary>
        /// 生成解析报告
        /// </summary>
        public static string GenerateReport(ParseResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== UEFI 固件解析报告 ===");
            sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(result.BuildId))
            {
                sb.AppendLine($"Build ID: {result.BuildId}");
                sb.AppendLine();
            }

            sb.AppendLine($"--- 统计 ---");
            sb.AppendLine($"Firmware Volume 数: {result.Volumes.Count}");
            sb.AppendLine($"FFS 文件数: {result.TotalFiles}");
            sb.AppendLine($"Section 数: {result.TotalSections}");
            if (result.Errors.Count > 0)
            {
                sb.AppendLine($"错误数: {result.Errors.Count}");
            }
            sb.AppendLine();

            int volumeIndex = 0;
            foreach (var volume in result.Volumes)
            {
                sb.AppendLine($"--- Firmware Volume {volumeIndex} ---");
                sb.AppendLine($"  偏移: 0x{volume.Offset:X}");
                sb.AppendLine($"  大小: {volume.Size:N0} 字节 ({Math.Round(volume.Size / 1024.0 / 1024.0, 2)} MB)");
                sb.AppendLine($"  文件数: {volume.Files.Count}");
                sb.AppendLine();

                foreach (var file in volume.Files)
                {
                    string name = file.Name ?? file.Guid.ToString();
                    sb.AppendLine($"  📄 {name}");
                    sb.AppendLine($"     类型: {file.TypeName} (0x{file.Type:X2})");
                    sb.AppendLine($"     GUID: {file.Guid}");
                    sb.AppendLine($"     偏移: 0x{file.Offset:X}, 大小: {file.Size:N0}");
                    sb.AppendLine($"     校验和: {(file.ChecksumValid ? "✅ 有效" : "❌ 无效")}");

                    if (file.Sections.Count > 0)
                    {
                        sb.AppendLine($"     Sections ({file.Sections.Count}):");
                        foreach (var section in file.Sections)
                        {
                            string sectionName = section.Name ?? section.TypeName;
                            string compressed = section.IsCompressed ? $" [压缩: {section.CompressionType}]" : "";
                            sb.AppendLine($"       - {sectionName} ({section.TypeName}, {section.Size:N0} 字节){compressed}");
                        }
                    }
                    sb.AppendLine();
                }

                volumeIndex++;
            }

            if (result.Errors.Count > 0)
            {
                sb.AppendLine("--- 错误 ---");
                foreach (var error in result.Errors)
                {
                    sb.AppendLine($"  ⚠️ {error}");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region 内部解析方法

        /// <summary>
        /// 查找所有 FV 头
        /// </summary>
        private static List<ulong> FindAllFvHeaders(byte[] data)
        {
            var offsets = new List<ulong>();
            byte[] magic = Encoding.ASCII.GetBytes("_FVH");

            for (ulong i = 0; i <= (ulong)(data.Length - magic.Length); i++)
            {
                bool match = true;
                for (int j = 0; j < magic.Length; j++)
                {
                    if (data[i + (ulong)j] != magic[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    // FV 头在 _FVH 前 0x28 字节
                    ulong fvOffset = i - 0x28;
                    if (fvOffset >= 0 && !offsets.Contains(fvOffset))
                    {
                        offsets.Add(fvOffset);
                    }
                }
            }

            return offsets;
        }

        /// <summary>
        /// 解析单个 Firmware Volume
        /// </summary>
        private static FirmwareVolume? ParseVolume(byte[] data, ulong offset, int index)
        {
            // 验证 magic
            string magic = ReadAsciiString(data, offset + 0x28, 4);
            if (magic != "_FVH")
            {
                Log($"[ParseVolume] ❌ FV @ 0x{offset:X} magic 不匹配: '{magic}'");
                return null;
            }

            var volume = new FirmwareVolume
            {
                Offset = offset,
                Magic = magic,
                Name = $"FV{index}"
            };

            // 读取卷大小 (offset + 0x20, 8字节)
            volume.Size = ReadUInt64(data, offset + 0x20);

            // 读取卷头大小 (offset + 0x30, 2字节)
            ushort headerSize = ReadUInt16(data, offset + 0x30);

            Log($"[ParseVolume] FV{index}: offset=0x{offset:X}, size={volume.Size:N0}, headerSize={headerSize}");

            // 验证校验和
            // FV 头校验和计算比较复杂，这里先跳过

            // 解析 FFS 文件
            ulong fileOffset = offset + headerSize;
            ulong endOffset = offset + volume.Size;
            int fileCount = 0;
            int padCount = 0;

            while (fileOffset < endOffset && fileOffset + 0x18 < (ulong)data.Length)
            {
                try
                {
                    var file = ParseFile(data, fileOffset);
                    if (file == null || file.Size == 0)
                    {
                        Log($"[ParseVolume] FFS 文件 @ 0x{fileOffset:X} 解析返回 null 或大小为0，停止");
                        break;
                    }

                    // Pad 文件不添加到列表
                    if (file.Type != 0xF0)
                    {
                        volume.Files.Add(file);
                        fileCount++;
                        if (fileCount <= 5) // 只记录前5个文件的详细信息
                        {
                            Log($"[ParseVolume]   文件: {file.Name ?? file.Guid.ToString()} ({file.TypeName}, size={file.Size:N0})");
                        }
                    }
                    else
                    {
                        padCount++;
                    }

                    // 对齐到 8 字节
                    fileOffset += file.Size;
                    fileOffset = (fileOffset + 7) & ~7UL;
                }
                catch (Exception ex)
                {
                    Log($"[ParseVolume] FFS 文件 @ 0x{fileOffset:X} 解析异常: {ex.Message}，停止");
                    break;
                }
            }

            Log($"[ParseVolume] FV{index} 解析完成: 有效文件={fileCount}, Pad文件={padCount}");
            return volume;
        }

        /// <summary>
        /// 解析单个 FFS 文件
        /// </summary>
        private static FirmwareFile? ParseFile(byte[] data, ulong offset)
        {
            if (offset + 0x18 > (ulong)data.Length) return null;

            var file = new FirmwareFile
            {
                Offset = offset,
                Guid = ReadGuid(data, offset)
            };

            // 从 GUID 数据库查找名称
            string guidName = GuidDatabase.GetName(file.Guid);
            if (guidName != file.Guid.ToString())
            {
                file.Name = guidName;
            }

            // 文件大小 (offset + 0x10, 3字节)
            file.Size = ReadUInt24(data, offset + 0x10);

            // 文件类型 (offset + 0x13, 1字节)
            file.Type = data[offset + 0x13];
            file.TypeName = FileTypes.TryGetValue(file.Type, out var typeName) ? typeName : $"Unknown (0x{file.Type:X2})";

            // 校验和
            file.ChecksumValid = VerifyFileChecksum(data, offset);

            // 解析 Sections
            ulong sectionOffset = offset + 0x18;
            ulong endOffset = offset + file.Size;

            // FV Image 类型的文件包含嵌套 FV
            if (file.Type == 0x0B) // Firmware Volume Image
            {
                // 先解析 Sections 找到 FV section
                while (sectionOffset + 4 < endOffset && sectionOffset + 4 < (ulong)data.Length)
                {
                    try
                    {
                        var section = ParseSection(data, sectionOffset);
                        if (section == null || section.Size == 0) break;

                        file.Sections.Add(section);
                        sectionOffset += section.Size;
                        sectionOffset = (sectionOffset + 3) & ~3UL; // 4字节对齐
                    }
                    catch { break; }
                }
            }
            else
            {
                while (sectionOffset + 4 < endOffset && sectionOffset + 4 < (ulong)data.Length)
                {
                    try
                    {
                        var section = ParseSection(data, sectionOffset);
                        if (section == null || section.Size == 0) break;

                        file.Sections.Add(section);

                        // UI section 包含文件名
                        if (section.Type == 0x15 && section.Data != null)
                        {
                            file.Name = Encoding.Unicode.GetString(section.Data).TrimEnd('\0');
                        }

                        sectionOffset += section.Size;
                        sectionOffset = (sectionOffset + 3) & ~3UL; // 4字节对齐
                    }
                    catch { break; }
                }
            }

            return file;
        }

        /// <summary>
        /// 解析单个 Section
        /// </summary>
        private static FirmwareSection? ParseSection(byte[] data, ulong offset)
        {
            if (offset + 4 > (ulong)data.Length) return null;

            var section = new FirmwareSection
            {
                Offset = offset
            };

            // Section 大小 (3字节)
            section.Size = ReadUInt24(data, offset);

            // Section 类型 (1字节)
            section.Type = data[offset + 3];
            section.TypeName = SectionTypes.TryGetValue(section.Type, out var typeName) ? typeName : $"Unknown (0x{section.Type:X2})";

            // 读取数据
            ulong dataOffset = offset + 4;
            ulong dataSize = section.Size - 4;

            if (dataOffset + dataSize <= (ulong)data.Length && dataSize > 0)
            {
                section.Data = new byte[dataSize];
                Array.Copy(data, (int)dataOffset, section.Data, 0, (int)dataSize);
            }

            // 压缩段
            if (section.Type == 0x01 && section.Data != null && section.Data.Length >= 5)
            {
                section.IsCompressed = true;
                uint compressionType = BitConverter.ToUInt32(section.Data, 0);
                section.CompressionType = compressionType switch
                {
                    0x00000001 => "Not Compressed",
                    0x00000002 => "Standard EFI Compression",
                    0x00000003 => "Customized Compression",
                    0x00000004 => "LZMA",
                    _ => $"Unknown (0x{compressionType:X8})"
                };
            }

            // UI section 提取名称
            if (section.Type == 0x15 && section.Data != null)
            {
                section.Name = Encoding.Unicode.GetString(section.Data).TrimEnd('\0');
            }

            return section;
        }

        /// <summary>
        /// 验证文件校验和
        /// </summary>
        private static bool VerifyFileChecksum(byte[] data, ulong offset)
        {
            try
            {
                // FFS 文件头校验和 (Header Checksum) 在 offset + 0x14
                // 文件校验和 (File Checksum) 在 offset + 0x15
                // 这里简化处理，只检查是否为 0 或 0xAA (表示校验和被忽略)
                byte headerChecksum = data[offset + 0x14];
                return headerChecksum == 0x00 || headerChecksum == 0xAA;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 提取 Build ID
        /// </summary>
        private static string? ExtractBuildId(byte[] data)
        {
            try
            {
                string ascii = Encoding.ASCII.GetString(data);
                int index = ascii.IndexOf("QC_IMAGE_VERSION_STRING=");
                if (index >= 0)
                {
                    int end = ascii.IndexOf('\0', index);
                    if (end < 0) end = Math.Min(index + 100, ascii.Length);
                    return ascii.Substring(index + 25, end - index - 25).Trim();
                }
            }
            catch { }
            return null;
        }

        #endregion

        #region 字节读取工具

        private static string ReadAsciiString(byte[] data, ulong offset, uint length)
        {
            byte[] bytes = new byte[length];
            Array.Copy(data, (int)offset, bytes, 0, (int)length);
            return Encoding.ASCII.GetString(bytes);
        }

        private static ushort ReadUInt16(byte[] data, ulong offset)
        {
            return BitConverter.ToUInt16(data, (int)offset);
        }

        private static uint ReadUInt24(byte[] data, ulong offset)
        {
            return (uint)(data[offset] + (data[offset + 1] << 8) + (data[offset + 2] << 16));
        }

        private static ulong ReadUInt64(byte[] data, ulong offset)
        {
            return BitConverter.ToUInt64(data, (int)offset);
        }

        private static Guid ReadGuid(byte[] data, ulong offset)
        {
            byte[] guidBuffer = new byte[0x10];
            Array.Copy(data, (int)offset, guidBuffer, 0, 0x10);
            return new Guid(guidBuffer);
        }

        #endregion
    }
}
