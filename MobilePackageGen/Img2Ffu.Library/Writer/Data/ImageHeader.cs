
using System.Text;

namespace Img2Ffu.Writer.Data
{
    internal class ImageHeader
    {
        public uint ManifestLength
        {
            get; set;
        }

        public Span<byte> GetResultingBuffer(uint BlockSize, bool HasDeviceTargetInfo, uint DeviceTargetInfosCount)
        {
            using MemoryStream ImageHeaderStream = new();
            using BinaryWriter binaryWriter = new(ImageHeaderStream);

            binaryWriter.Write(HasDeviceTargetInfo ? 28u : 24u);
            binaryWriter.Write(Encoding.ASCII.GetBytes("ImageFlash  ")); // Signature
            binaryWriter.Write(ManifestLength);
            binaryWriter.Write(BlockSize / 1024);

            if (HasDeviceTargetInfo)
            {
                binaryWriter.Write(DeviceTargetInfosCount);
            }

            Memory<byte> ImageHeaderBuffer = new byte[ImageHeaderStream.Length];
            Span<byte> span = ImageHeaderBuffer.Span;
            _ = ImageHeaderStream.Seek(0, SeekOrigin.Begin);
            ImageHeaderStream.ReadExactly(span);

            return span;
        }
    }
}
