using DiscUtils;

namespace MobilePackageGen.Adapters.FileSystemImage
{
    /// <summary>
    /// Represents a raw file system image (NTFS / FAT) that has no partition table —
    /// the stream starts directly at the file system volume (e.g. a .ntfs or .fat dump).
    /// The whole image is exposed as a single partition; FileSystemPartition performs
    /// the NTFS/FAT detection (DiscUtils, with a 7z ArchiveBridge fallback).
    /// </summary>
    public class Disk : IDisk
    {
        public IEnumerable<IPartition> Partitions
        {
            get;
        }

        public Disk(Stream imageStream, string name)
        {
            Logging.Log($"Loading file system image: {name} ({imageStream.Length} bytes)");

            imageStream.Seek(0, SeekOrigin.Begin);

            IPartition partition = new FileSystemPartition(
                imageStream,
                name,
                new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"), // Microsoft Basic Data
                new Guid("00000000-0000-0000-0000-000000000001"));

            Partitions = [partition];
        }
    }
}
