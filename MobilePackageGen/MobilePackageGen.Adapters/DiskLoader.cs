using DiscUtils;
using MobilePackageGen.Adapters;

namespace MobilePackageGen
{
    public static class DiskLoader
    {
        public static IEnumerable<IDisk> LoadDisks(string[] inputArgs)
        {
            Logging.Log("Getting Disks...");

            List<IDisk> disks = [];

            foreach (string inputArg in inputArgs)
            {
                if (Directory.Exists(inputArg))
                {
                    if (Directory.Exists(Path.Combine(inputArg, @"Windows\Servicing\Packages")) || Directory.Exists(Path.Combine(inputArg, @"Windows\Packages\DsmFiles")))
                    {
                        disks.Add(new Adapters.RealFileSystem.Disk(inputArg));
                    }
                    else
                    {
                        string[] files = Directory.EnumerateFiles(inputArg).ToArray();

                        foreach (string file in files)
                        {
                            IDisk? disk = LoadFile(file);
                            if (disk != null)
                            {
                                disks.Add(disk);
                            }
                        }
                    }
                }
                else if (File.Exists(inputArg))
                {
                    IDisk? disk = LoadFile(inputArg);
                    if (disk != null)
                    {
                        disks.Add(disk);
                    }
                }
            }

            if (disks.Count == 0)
            {
                return disks;
            }

            Logging.Log("Getting Update OS Disks...");

            disks.AddRange(DiskCommon.GetUpdateOSDisks(disks));

            // Aggregate all storage pool partitions across every disk into a single
            // multi-disk pool view (17705-style storage pools are split across several
            // VHD files and their slabs live on different physical disks).
            List<Stream> poolPartitionStreams = [];

            foreach (IDisk disk in disks)
            {
                foreach (IPartition partition in disk.Partitions)
                {
                    if (partition.Type == new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D") && partition.Stream != null)
                    {
                        poolPartitionStreams.Add(partition.Stream);
                    }
                }
            }

            if (poolPartitionStreams.Count > 0)
            {
                Logging.Log($"Found {poolPartitionStreams.Count} storage pool partition(s) across disks, building unified pool view...");

                disks.Add(new Adapters.StorageSpace.Disk(poolPartitionStreams));
            }

            DiskCommon.PrintDiskInfo(disks);

            return disks;

        }
        private static IDisk? LoadFile(string file)
        {
            string extension = Path.GetExtension(file);
            switch (extension.ToLowerInvariant())
            {
                case ".xml":
                    {
                        return null;
                    }
                case ".wim":
                    {
                        return new Adapters.Wim.Disk(file);
                    }
                case ".ffu":
                    {
                        return new Adapters.FullFlashUpdate.Disk(file);
                    }
                case ".vhd":
                    {
                        return new Adapters.Vhdx.Disk(file);
                    }
                case ".vhdx":
                    {
                        return new Adapters.Vhdx.Disk(file);
                    }
                case ".img":
                    {
                        return TryLoadFileSystemImage(file) ?? new Adapters.RawDisk.Disk(File.OpenRead(file));
                    }
                case ".bin":
                    {
                        return TryLoadFileSystemImage(file) ?? new Adapters.RawDisk.Disk(File.OpenRead(file));
                    }
                case ".ntfs":
                case ".fat":
                case ".vfat":
                    {
                        return new Adapters.FileSystemImage.Disk(File.OpenRead(file), Path.GetFileName(file));
                    }
                default:
                    {
                        return null;
                    }
            }
        }

        /// <summary>
        /// Detects whether a raw image is actually a bare NTFS/FAT file system volume
        /// (no partition table) and wraps it in a FileSystemImage disk. Returns null
        /// when the image is not a directly detectable file system (caller falls back
        /// to partition-table parsing, e.g. GPT/MBR).
        /// </summary>
        private static IDisk? TryLoadFileSystemImage(string file)
        {
            try
            {
                using (FileStream probe = File.OpenRead(file))
                {
                    probe.Seek(0, SeekOrigin.Begin);

                    // Cheap magic-number check first: NTFS OEM id at offset 3.
                    byte[] header = new byte[11];
                    probe.Read(header, 0, header.Length);
                    if (header.Length >= 11 &&
                        header[3] == 0x4E && header[4] == 0x54 && header[5] == 0x46 && header[6] == 0x53 && // "NTFS"
                        header[7] == 0x20 && header[8] == 0x20 && header[9] == 0x20 && header[10] == 0x20)
                    {
                        return new Adapters.FileSystemImage.Disk(File.OpenRead(file), Path.GetFileName(file));
                    }

                    // DiscUtils detection (covers FAT12/16/32 and other NTFS layouts).
                    probe.Seek(0, SeekOrigin.Begin);
                    if (DiscUtils.Ntfs.NtfsFileSystem.Detect(probe))
                    {
                        return new Adapters.FileSystemImage.Disk(File.OpenRead(file), Path.GetFileName(file));
                    }

                    probe.Seek(0, SeekOrigin.Begin);
                    if (DiscUtils.Fat.FatFileSystem.Detect(probe))
                    {
                        return new Adapters.FileSystemImage.Disk(File.OpenRead(file), Path.GetFileName(file));
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Log($"Error: File system image detection failed for {file}: {ex.Message}", LoggingLevel.Error);
            }

            return null;
        }
    }
}
