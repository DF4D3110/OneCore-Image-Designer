
namespace Img2Ffu.Writer.Data
{
    public class WriteDescriptor
    {
        public required BlockDataEntry BlockDataEntry
        {
            get; set;
        }
        public required DiskLocation[] DiskLocations
        {
            get; set;
        }

        public Span<byte> GetResultingBuffer(FlashUpdateVersion storeHeaderVersion, uint CompressedDataBlockSize = 0)
        {
            using MemoryStream WriteDescriptorStream = new();
            using BinaryWriter binaryWriter = new(WriteDescriptorStream);

            BlockDataEntry.LocationCount = (uint)DiskLocations.Length;

            binaryWriter.Write(BlockDataEntry.LocationCount);
            binaryWriter.Write(BlockDataEntry.BlockCount);

            switch (storeHeaderVersion)
            {
                case FlashUpdateVersion.V1_COMPRESSED:
                    binaryWriter.Write(CompressedDataBlockSize);
                    break;
            }

            foreach (DiskLocation DiskLocation in DiskLocations)
            {
                binaryWriter.Write(DiskLocation.DiskAccessMethod);
                binaryWriter.Write(DiskLocation.BlockIndex);
            }

            Memory<byte> WriteDescriptorBuffer = new byte[WriteDescriptorStream.Length];
            Span<byte> span = WriteDescriptorBuffer.Span;
            _ = WriteDescriptorStream.Seek(0, SeekOrigin.Begin);
            WriteDescriptorStream.ReadExactly(span);

            return span;
        }
    }
}
