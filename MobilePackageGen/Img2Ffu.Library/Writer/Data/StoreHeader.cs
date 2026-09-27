
using System.Text;

namespace Img2Ffu.Writer.Data
{
    public class StoreHeader
    {
        private uint UpdateType = 0;

        private ushort MajorVersion = 1;
        private ushort MinorVersion = 0;
        private ushort FullFlashMajorVersion = 2;
        private ushort FullFlashMinorVersion = 0;
        public required string[] PlatformIds
        {
            get; set;
        }

        public uint BlockSize
        {
            get; set;
        }
        public uint WriteDescriptorCount
        {
            get; set;
        }
        public uint WriteDescriptorLength
        {
            get; set;
        }
        public uint ValidateDescriptorCount { get; set; } = 0;
        public uint ValidateDescriptorLength { get; set; } = 0;
        public uint InitialTableIndex { get; set; } = 0;
        public uint InitialTableCount { get; set; } = 0;
        public uint FlashOnlyTableIndex { get; set; } = 0;
        public uint FlashOnlyTableCount { get; set; } = 1;

        private uint FinalTableIndex;

        public uint FinalTableCount { get; set; } = 0;
        private uint CompressionAlgorithm = 0;
        public ushort NumberOfStores
        {
            get; set;
        }
        public ushort StoreIndex
        {
            get; set;
        }
        public ulong StorePayloadSize
        {
            get; set;
        }

        private ushort DevicePathLength;

        private byte[]? DevicePathBuffer;

        public string? DevicePath
        {
            get; set;
        }

        public Memory<byte> GetResultingBuffer(FlashUpdateVersion storeHeaderVersion, FlashUpdateType storeHeaderUpdateType, CompressionAlgorithm storeHeaderCompressionAlgorithm)
        {
            switch (storeHeaderVersion)
            {
                case FlashUpdateVersion.V1:
                    MajorVersion = 1;
                    MinorVersion = 0;
                    FullFlashMajorVersion = 2;
                    FullFlashMinorVersion = 0;
                    break;
                case FlashUpdateVersion.V1_COMPRESSED:
                    MajorVersion = 1;
                    MinorVersion = 0;
                    FullFlashMajorVersion = 3;
                    FullFlashMinorVersion = 0;
                    CompressionAlgorithm = (uint)storeHeaderCompressionAlgorithm;
                    break;
                case FlashUpdateVersion.V2:
                    if (string.IsNullOrEmpty(DevicePath))
                    {
                        throw new ArgumentException(nameof(DevicePath));
                    }

                    MajorVersion = 2;
                    MinorVersion = 0;
                    FullFlashMajorVersion = 2;
                    FullFlashMinorVersion = 0;
                    UnicodeEncoding UnicodeEncoding = new();
                    DevicePathBuffer = UnicodeEncoding.GetBytes(DevicePath!.ToCharArray());
                    DevicePathLength = (ushort)DevicePath!.Length;
                    break;
            }

            switch (storeHeaderUpdateType)
            {
                case FlashUpdateType.Full:
                    UpdateType = 0;
                    break;
                case FlashUpdateType.Partial:
                    UpdateType = 1;
                    break;
            }

            byte[] PlatformIdsBuffer = new byte[192];
            int CurrentBufferSize = 0;
            foreach (string PlatformId in PlatformIds)
            {
                byte[] PlatformIdBuffer = Encoding.ASCII.GetBytes(PlatformId);
                int AddedBufferSize = PlatformIdBuffer.Length + 1;
                if (CurrentBufferSize + AddedBufferSize > PlatformIdsBuffer.Length - 1)
                {
                    break;
                }
                PlatformIdBuffer.CopyTo(PlatformIdsBuffer, CurrentBufferSize);
                CurrentBufferSize += AddedBufferSize;
            }

            FinalTableIndex = WriteDescriptorCount - FinalTableCount;

            using MemoryStream StoreHeaderStream = new();
            using BinaryWriter binaryWriter = new(StoreHeaderStream);

            binaryWriter.Write(UpdateType);
            binaryWriter.Write(MajorVersion);
            binaryWriter.Write(MinorVersion);
            binaryWriter.Write(FullFlashMajorVersion);
            binaryWriter.Write(FullFlashMinorVersion);
            binaryWriter.Write(PlatformIdsBuffer);
            binaryWriter.Write(BlockSize);
            binaryWriter.Write(WriteDescriptorCount);
            binaryWriter.Write(WriteDescriptorLength);
            binaryWriter.Write(ValidateDescriptorCount);
            binaryWriter.Write(ValidateDescriptorLength);
            binaryWriter.Write(InitialTableIndex);
            binaryWriter.Write(InitialTableCount);
            binaryWriter.Write(FlashOnlyTableIndex);
            binaryWriter.Write(FlashOnlyTableCount);
            binaryWriter.Write(FinalTableIndex);
            binaryWriter.Write(FinalTableCount);

            switch (storeHeaderVersion)
            {
                case FlashUpdateVersion.V1_COMPRESSED:
                    binaryWriter.Write(CompressionAlgorithm);
                    break;
                case FlashUpdateVersion.V2:
                    binaryWriter.Write(NumberOfStores);
                    binaryWriter.Write(StoreIndex);
                    binaryWriter.Write(StorePayloadSize);
                    binaryWriter.Write(DevicePathLength);
                    binaryWriter.Write(DevicePathBuffer!);
                    break;
            }

            Memory<byte> StoreHeaderBuffer = new byte[StoreHeaderStream.Length];
            _ = StoreHeaderStream.Seek(0, SeekOrigin.Begin);
            StoreHeaderStream.ReadExactly(StoreHeaderBuffer.Span);

            return StoreHeaderBuffer;
        }
    }
}
