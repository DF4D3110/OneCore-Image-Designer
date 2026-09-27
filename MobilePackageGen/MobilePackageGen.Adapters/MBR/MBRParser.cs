using MobilePackageGen.Helpers;

namespace MobilePackageGen.MBR
{
    public static class MBRParser
    {
        // 常见MBR分区类型映射
        private static readonly Dictionary<byte, (string Name, Guid TypeGuid)> PartitionTypeMap = new()
        {
            { 0x00, ("Empty", Guid.Empty) },
            { 0x07, ("NTFS", new Guid("EB8A6513-5E8F-4260-9C40-42A8C6F08181")) },
            { 0x0B, ("FAT32", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x0C, ("FAT32_LBA", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x0E, ("FAT16_LBA", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x11, ("Hidden_FAT16", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x12, ("EISA", Guid.Empty) },
            { 0x1B, ("Hidden_FAT32", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x1C, ("Hidden_FAT32_LBA", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x1E, ("Hidden_FAT16_LBA", new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")) },
            { 0x27, ("Windows_RE", new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")) },
            { 0x82, ("Linux_Swap", Guid.Empty) },
            { 0x83, ("Linux", Guid.Empty) },
            { 0xEE, ("GPT_Protective", Guid.Empty) },
            { 0xEF, ("EFI_System", new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")) },
        };

        public static List<GPT.GPT.Partition> GetMBRPartitions(Stream diskStream, uint sectorSize)
        {
            diskStream.Seek(0, SeekOrigin.Begin);

            try
            {
                // 读取MBR（第一个扇区）
                byte[] mbrBuffer = new byte[sectorSize];
                int read = diskStream.Read(mbrBuffer, 0, mbrBuffer.Length);
                diskStream.Seek(0, SeekOrigin.Begin);

                if (read < sectorSize)
                {
                    Logging.Log($"MBR: Could not read full sector (read {read} bytes)", LoggingLevel.Warning);
                    return null;
                }

                // 检查MBR标志
                if (mbrBuffer[0x1FE] != 0x55 || mbrBuffer[0x1FF] != 0xAA)
                {
                    Logging.Log("MBR: Invalid signature (not 0x55AA)", LoggingLevel.Warning);
                    return null;
                }

                Logging.Log("MBR: Valid MBR signature found, parsing partitions...");

                List<GPT.GPT.Partition> partitions = [];
                int partitionIndex = 0;

                // 解析4个主分区表项（偏移0x1BE开始，每个16字节）
                for (int i = 0; i < 4; i++)
                {
                    int entryOffset = 0x1BE + (i * 16);
                    byte status = mbrBuffer[entryOffset + 0x00];
                    byte partitionType = mbrBuffer[entryOffset + 0x04];
                    uint startLBA = ByteOperations.ReadUInt32(mbrBuffer, (uint)(entryOffset + 0x08));
                    uint sectorCount = ByteOperations.ReadUInt32(mbrBuffer, (uint)(entryOffset + 0x0C));

                    // 跳过空分区
                    if (partitionType == 0x00 || sectorCount == 0)
                    {
                        continue;
                    }

                    // 处理扩展分区（0x05, 0x0F, 0x85）- 解析逻辑分区
                    if (partitionType == 0x05 || partitionType == 0x0F || partitionType == 0x85)
                    {
                        Logging.Log($"MBR: Partition {i} is extended (type 0x{partitionType:X2}), parsing logical partitions...");
                        var logicalPartitions = ParseExtendedPartitions(diskStream, sectorSize, startLBA, sectorCount, ref partitionIndex);
                        partitions.AddRange(logicalPartitions);
                        continue;
                    }

                    // 获取分区类型信息
                    if (!PartitionTypeMap.TryGetValue(partitionType, out var typeInfo))
                    {
                        typeInfo = ($"Unknown_0x{partitionType:X2}", Guid.Empty);
                    }

                    // 生成分区名称
                    string partitionName = $"MBR_Partition_{partitionIndex}_{typeInfo.Name}";
                    partitionIndex++;

                    // 生成唯一GUID（基于起始LBA和分区类型，确保可重复）
                    Guid partitionGuid = GenerateDeterministicGuid(startLBA, partitionType, i);

                    var partition = new GPT.GPT.Partition
                    {
                        Name = partitionName,
                        PartitionTypeGuid = typeInfo.TypeGuid == Guid.Empty ? new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7") : typeInfo.TypeGuid,
                        PartitionGuid = partitionGuid,
                        FirstSector = startLBA,
                        LastSector = startLBA + sectorCount - 1
                    };

                    Logging.Log($"MBR: Partition {partitionName}: type=0x{partitionType:X2} ({typeInfo.Name}), start={startLBA}, count={sectorCount}, size={sectorCount * sectorSize / 1024 / 1024}MB");

                    partitions.Add(partition);
                }

                Logging.Log($"MBR: Found {partitions.Count} partition(s)");
                return partitions;
            }
            catch (Exception ex)
            {
                Logging.Log($"MBR: Error parsing MBR: {ex.Message}", LoggingLevel.Error);
                diskStream.Seek(0, SeekOrigin.Begin);
                return null;
            }
        }

        /// <summary>
        /// 解析扩展分区中的逻辑分区
        /// </summary>
        private static List<GPT.GPT.Partition> ParseExtendedPartitions(Stream diskStream, uint sectorSize, uint extendedStartLBA, uint extendedSectorCount, ref int partitionIndex)
        {
            var logicalPartitions = new List<GPT.GPT.Partition>();
            uint currentEBR = extendedStartLBA;
            int logicalIndex = 0;
            int maxIterations = 64; // 防止无限循环

            while (logicalIndex < maxIterations)
            {
                try
                {
                    // 读取EBR（扩展引导记录）
                    diskStream.Seek((long)currentEBR * sectorSize, SeekOrigin.Begin);
                    byte[] ebrBuffer = new byte[sectorSize];
                    int read = diskStream.Read(ebrBuffer, 0, ebrBuffer.Length);

                    if (read < sectorSize || ebrBuffer[0x1FE] != 0x55 || ebrBuffer[0x1FF] != 0xAA)
                    {
                        Logging.Log($"MBR: Invalid EBR signature at LBA {currentEBR}, stopping logical partition parsing", LoggingLevel.Warning);
                        break;
                    }

                    // 第一个表项是逻辑分区
                    int entryOffset = 0x1BE;
                    byte partitionType = ebrBuffer[entryOffset + 0x04];
                    uint startLBA = ByteOperations.ReadUInt32(ebrBuffer, (uint)(entryOffset + 0x08));
                    uint sectorCount = ByteOperations.ReadUInt32(ebrBuffer, (uint)(entryOffset + 0x0C));

                    if (partitionType != 0x00 && sectorCount > 0)
                    {
                        // 逻辑分区的LBA是相对于扩展分区起始的
                        uint absoluteStartLBA = extendedStartLBA + startLBA;

                        if (!PartitionTypeMap.TryGetValue(partitionType, out var typeInfo))
                        {
                            typeInfo = ($"Unknown_0x{partitionType:X2}", Guid.Empty);
                        }

                        string partitionName = $"MBR_Logical_{partitionIndex}_{typeInfo.Name}";
                        partitionIndex++;

                        Guid partitionGuid = GenerateDeterministicGuid(absoluteStartLBA, partitionType, 0x80 + logicalIndex);

                        var partition = new GPT.GPT.Partition
                        {
                            Name = partitionName,
                            PartitionTypeGuid = typeInfo.TypeGuid == Guid.Empty ? new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7") : typeInfo.TypeGuid,
                            PartitionGuid = partitionGuid,
                            FirstSector = absoluteStartLBA,
                            LastSector = absoluteStartLBA + sectorCount - 1
                        };

                        Logging.Log($"MBR: Logical Partition {partitionName}: type=0x{partitionType:X2} ({typeInfo.Name}), start={absoluteStartLBA}, count={sectorCount}, size={sectorCount * sectorSize / 1024 / 1024}MB");

                        logicalPartitions.Add(partition);
                    }

                    // 第二个表项指向下一个EBR（如果有）
                    entryOffset = 0x1CE;
                    byte nextType = ebrBuffer[entryOffset + 0x04];
                    uint nextStartLBA = ByteOperations.ReadUInt32(ebrBuffer, (uint)(entryOffset + 0x08));
                    uint nextSectorCount = ByteOperations.ReadUInt32(ebrBuffer, (uint)(entryOffset + 0x0C));

                    if (nextType == 0x00 || nextStartLBA == 0)
                    {
                        break; // 没有更多逻辑分区
                    }

                    // 下一个EBR的LBA是相对于扩展分区起始的
                    currentEBR = extendedStartLBA + nextStartLBA;
                    logicalIndex++;
                }
                catch (Exception ex)
                {
                    Logging.Log($"MBR: Error parsing logical partition: {ex.Message}", LoggingLevel.Error);
                    break;
                }
            }

            diskStream.Seek(0, SeekOrigin.Begin);
            return logicalPartitions;
        }

        private static Guid GenerateDeterministicGuid(uint startLBA, byte partitionType, int index)
        {
            // 基于起始LBA、分区类型和索引生成确定性GUID
            byte[] guidBytes = new byte[16];
            BitConverter.GetBytes(startLBA).CopyTo(guidBytes, 0);
            guidBytes[4] = partitionType;
            guidBytes[5] = (byte)index;
            guidBytes[6] = 0x4D; // 'M' for MBR
            guidBytes[7] = 0x42; // 'B'
            guidBytes[8] = 0x52; // 'R'
            return new Guid(guidBytes);
        }
    }
}
