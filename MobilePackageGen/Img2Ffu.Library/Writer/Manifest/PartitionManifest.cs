
namespace Img2Ffu.Writer.Manifest
{
    internal class PartitionManifest
    {
        public bool? RequiredToFlash;
        public uint UsedSectors;
        public Guid? Type;
        public uint TotalSectors;
        public string? Primary;
        public required string Name;
        public string? FileSystem;
        public uint? ByteAlignment;
        public uint? ClusterSize;
        public bool? UseAllSpace;
    }
}