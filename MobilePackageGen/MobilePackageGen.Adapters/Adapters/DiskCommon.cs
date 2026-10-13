using DiscUtils;
using DiscUtils.Streams;
using DiscUtils.Wim;
using StorageSpace;

namespace MobilePackageGen.Adapters
{
    public static class DiskCommon
    {
        public static List<IDisk> GetUpdateOSDisks(List<IDisk> disks)
        {
            List<IDisk> updateOSDisks = [];

            foreach (IDisk disk in disks)
            {
                foreach (IPartition partition in disk.Partitions)
                {
                    IDisk? updateOSDisk = GetUpdateOSDisk(partition);
                    if (updateOSDisk != null)
                    {
                        updateOSDisks.Add(updateOSDisk);
                    }
                }
            }

            return updateOSDisks;
        }

        public static IDisk? GetUpdateOSDisk(IPartition partition)
        {
            if (partition.FileSystem != null)
            {
                IFileSystem fileSystem = partition.FileSystem;
                try
                {
                    // Handle UpdateOS as well if found
                    if (fileSystem.FileExists("PROGRAMS\\UpdateOS\\UpdateOS.wim"))
                    {
                        Logging.Log();

                        Logging.Log($"UpdateOS.wim {fileSystem.GetFileLength("PROGRAMS\\UpdateOS\\UpdateOS.wim")} WindowsImageFile");

                        List<IPartition> partitions = [];

                        Stream wimStream = fileSystem.OpenFile("PROGRAMS\\UpdateOS\\UpdateOS.wim", FileMode.Open, FileAccess.Read);
                        WimFile wimFile = new(wimStream);

                        for (int i = 0; i < wimFile.ImageCount; i++)
                        {
                            IFileSystem wimFileSystem = wimFile.GetImage(i);

                            Logging.Log($"- {i} WindowsImageIndex");

                            IPartition wimPartition = new FileSystemPartition(wimStream, wimFileSystem, $"{partition.Name.Replace("\0", "-")}-UpdateOS-{i}", Guid.Empty, Guid.Empty);
                            partitions.Add(wimPartition);
                        }

                        Logging.Log();

                        Wim.Disk updateOSDisk = new(partitions);
                        return updateOSDisk;
                    }

                    // Handle UpdateOS as well if found
                    if (fileSystem.FileExists("UpdateOS.wim"))
                    {
                        Logging.Log();

                        Logging.Log($"UpdateOS.wim {fileSystem.GetFileLength("UpdateOS.wim")} WindowsImageFile");

                        List<IPartition> partitions = [];

                        Stream wimStream = fileSystem.OpenFile("UpdateOS.wim", FileMode.Open, FileAccess.Read);
                        WimFile wimFile = new(wimStream);

                        for (int i = 0; i < wimFile.ImageCount; i++)
                        {
                            IFileSystem wimFileSystem = wimFile.GetImage(i);

                            Logging.Log($"- {i} WindowsImageIndex");

                            IPartition wimPartition = new FileSystemPartition(wimStream, wimFileSystem, $"{partition.Name.Replace("\0", "-")}-UpdateOS-{i}", Guid.Empty, Guid.Empty);
                            partitions.Add(wimPartition);
                        }

                        Logging.Log();

                        Wim.Disk updateOSDisk = new(partitions);
                        return updateOSDisk;
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log($"Error: Looking up UpdateOS on file system failed! {ex.Message}", LoggingLevel.Error);
                }
            }

            return null;
        }

        public static List<(GPT.GPT.Partition, Stream)> GetPartitions(VirtualDisk virtualDisk)
        {
            List<(GPT.GPT.Partition, Stream)> partitions = [];

            int sectorSize = virtualDisk.Geometry!.Value.BytesPerSector;

            IEnumerable<GPT.GPT.Partition> partitionTable = GetGPTPartitions(virtualDisk.Content, (uint)sectorSize);

            if (partitionTable == null)
            {
                partitionTable = GetMBRPartitions(virtualDisk.Content, (uint)sectorSize);
            }

            if (partitionTable != null)
            {
                foreach (GPT.GPT.Partition partitionInfo in partitionTable)
                {
                    Stream partitionStream = Open(partitionInfo, (uint)sectorSize, virtualDisk.Content);

                    partitions.Add((partitionInfo, partitionStream));
                }
            }

            return partitions;
        }

        internal static SparseStream Open(GPT.GPT.Partition entry, uint SectorSize, Stream _diskData)
        {
            ulong start = entry.FirstSector * SectorSize;
            ulong end = (entry.LastSector + 1) * SectorSize;

            if ((long)end >= _diskData.Length)
            {
                end = (ulong)_diskData.Length;
            }

            return new SubStream(_diskData, (long)start, (long)(end - start));
        }

        internal static IEnumerable<GPT.GPT.Partition> GetGPTPartitions(Stream diskStream, uint sectorSize)
        {
            diskStream.Seek(0, SeekOrigin.Begin);

            try
            {
                byte[] buffer = new byte[sectorSize * 2];
                diskStream.Read(buffer, 0, buffer.Length);
                diskStream.Seek(0, SeekOrigin.Begin);

                uint GPTBufferSize = MobilePackageGen.GPT.GPT.GetGPTSize(buffer, sectorSize);

                buffer = new byte[GPTBufferSize];
                diskStream.Read(buffer, 0, buffer.Length);
                diskStream.Seek(0, SeekOrigin.Begin);

                GPT.GPT GPT = new(buffer, sectorSize);

                return GPT.Partitions;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] GPT signature not found on disk ({ex.Message}), falling back to MBR");
                diskStream.Seek(0, SeekOrigin.Begin);
                return null;
            }
        }

        /// <summary>
        /// MBR 分区表解析入口。
        /// 优先使用 <see cref="MobilePackageGen.MBR.MBRParser"/>（完整实现：15 种分区类型映射 +
        /// 扩展分区 EBR / 逻辑分区解析），失败或返回空时回退到本类内的简易实现（仅 4 个主分区）。
        /// </summary>
        private static IEnumerable<GPT.GPT.Partition> GetMBRPartitions(Stream diskStream, uint sectorSize)
        {
            try
            {
                List<GPT.GPT.Partition> viaParser = MobilePackageGen.MBR.MBRParser.GetMBRPartitions(diskStream, sectorSize);
                if (viaParser != null && viaParser.Count > 0)
                {
                    return viaParser;
                }
            }
            catch (Exception ex)
            {
                Logging.Log($"MBR: full parser failed ({ex.Message}); falling back to simple parser", LoggingLevel.Warning);
            }

            return GetMBRPartitionsSimple(diskStream, sectorSize);
        }

        /// <summary>简易 MBR 回退实现：仅解析 4 个主分区（跳过扩展分区），无 EBR 支持。</summary>
        private static IEnumerable<GPT.GPT.Partition> GetMBRPartitionsSimple(Stream diskStream, uint sectorSize)
        {
            diskStream.Seek(0, SeekOrigin.Begin);

            byte[] mbr = new byte[Math.Min(sectorSize, 512)];
            diskStream.Read(mbr, 0, mbr.Length);
            diskStream.Seek(0, SeekOrigin.Begin);

            if (mbr.Length < 512 || mbr[510] != 0x55 || mbr[511] != 0xAA)
            {
                return null;
            }

            List<GPT.GPT.Partition> partitions = [];

            ulong totalSectors = (ulong)(diskStream.Length / sectorSize);
            Console.WriteLine($"[MBR-DEBUG] sectorSize={sectorSize} diskLen={diskStream.Length} totalSectors={totalSectors}");

            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                byte type = mbr[offset + 4];

                // Skip empty entries and extended partition tables
                if (type == 0x00 || type == 0x05 || type == 0x0F)
                {
                    continue;
                }

                uint startLBA = BitConverter.ToUInt32(mbr, offset + 8);
                uint sectorCount = BitConverter.ToUInt32(mbr, offset + 12);
                Console.WriteLine($"[MBR-DEBUG] P{i + 1}: type=0x{type:X2} lba={startLBA}(0x{startLBA:X}) cnt={sectorCount} end={(ulong)startLBA + sectorCount}");

                // Skip entries that are empty or extend beyond the disk
                if (sectorCount == 0 || (ulong)startLBA + sectorCount > totalSectors)
                {
                    continue;
                }

                GPT.GPT.Partition partition = new()
                {
                    Name = "Partition" + (i + 1),
                    PartitionTypeGuid = MbrTypeToGuid(type),
                    PartitionGuid = new Guid($"{type:X2}000000-0000-0000-0000-{startLBA:X12}")
                };

                partition.FirstSector = startLBA;
                partition.LastSector = (ulong)startLBA + sectorCount - 1;

                partitions.Add(partition);
            }

            // No valid MBR partition entries found - treat the whole disk as a single
            // "superfloppy" style volume (entire disk formatted as one file system).
            if (partitions.Count == 0)
            {
                GPT.GPT.Partition wholeDisk = new()
                {
                    Name = "Partition1",
                    PartitionTypeGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"),
                    PartitionGuid = new Guid("00000000-0000-0000-0000-000000000001")
                };
                wholeDisk.FirstSector = 0;
                wholeDisk.LastSector = totalSectors - 1;
                partitions.Add(wholeDisk);
            }

            return partitions;
        }

        private static Guid MbrTypeToGuid(byte type)
        {
            return type switch
            {
                0xEF => new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"), // EFI System partition
                0x27 => new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"), // Microsoft Recovery
                _ => new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")     // Microsoft Basic Data
            };
        }

        internal static int TryDetectSectorSize(Stream diskStream)
        {
            // Default is 4096
            int sectorSize = 4096;

            if (diskStream.Length > 4096 * 2)
            {
                BinaryReader reader = new(diskStream);

                diskStream.Seek(512, SeekOrigin.Begin);
                byte[] header1 = reader.ReadBytes(8);

                diskStream.Seek(4096, SeekOrigin.Begin);
                byte[] header2 = reader.ReadBytes(8);

                string header1str = System.Text.Encoding.ASCII.GetString(header1);
                string header2str = System.Text.Encoding.ASCII.GetString(header2);

                if (header1str == "EFI PART")
                {
                    sectorSize = 512;
                }
                else if (header2str == "EFI PART")
                {
                    sectorSize = 4096;
                }
                else if (diskStream.Length % 512 == 0 && diskStream.Length % 4096 != 0)
                {
                    sectorSize = 512;
                }

                diskStream.Seek(0, SeekOrigin.Begin);
            }
            else
            {
                if (diskStream.Length % 512 == 0 && diskStream.Length % 4096 != 0)
                {
                    sectorSize = 512;
                }
            }

            return sectorSize;
        }

        public static void PrintDiskInfo(IEnumerable<IDisk> disks)
        {
            Logging.Log();
            Logging.Log("Found partitions with recognized file system:");
            Logging.Log();

            foreach (IDisk disk in disks)
            {
                foreach (IPartition partition in disk.Partitions)
                {
                    if (partition.FileSystem != null)
                    {
                        Logging.Log($"{partition.Name.Replace("\0", "-")} {partition.ID} {partition.Type} {partition.Size} KnownFS");
                    }
                    else if (partition.Type == new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D"))
                    {
                        Logging.Log($"{partition.Name.Replace("\0", "-")} {partition.ID} {partition.Type} {partition.Size} StoragePool");
                    }
                }
            }

            Logging.Log();
            Logging.Log("Found partitions with unrecognized file system:");
            Logging.Log();

            foreach (IDisk disk in disks)
            {
                foreach (IPartition partition in disk.Partitions)
                {
                    if (partition.FileSystem == null && partition.Type != new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D"))
                    {
                        Logging.Log($"{partition.Name.Replace("\0", "-")} {partition.ID} {partition.Type} {partition.Size} UnknownFS");
                    }
                }
            }
        }
    }
}
