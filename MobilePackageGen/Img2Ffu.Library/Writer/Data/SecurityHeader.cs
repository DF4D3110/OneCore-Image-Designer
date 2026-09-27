
using System.Text;

namespace Img2Ffu.Writer.Data
{
    public class SecurityHeader
    {
        private readonly uint Size = 32;
        private readonly string Signature = "SignedImage ";
        private readonly uint HashAlgorithm = 0x800C;

        public uint CatalogSize;
        public uint HashTableSize;

        public Span<byte> GetResultingBuffer(uint BlockSize)
        {
            using MemoryStream SecurityHeaderStream = new();
            using BinaryWriter binaryWriter = new(SecurityHeaderStream);

            uint ChunkSizeInKb = BlockSize / 1024;

            binaryWriter.Write(Size);
            binaryWriter.Write(Encoding.ASCII.GetBytes(Signature));
            binaryWriter.Write(ChunkSizeInKb);
            binaryWriter.Write(HashAlgorithm);
            binaryWriter.Write(CatalogSize);
            binaryWriter.Write(HashTableSize);

            Memory<byte> SecurityHeaderBuffer = new byte[SecurityHeaderStream.Length];
            Span<byte> span = SecurityHeaderBuffer.Span;
            _ = SecurityHeaderStream.Seek(0, SeekOrigin.Begin);
            SecurityHeaderStream.ReadExactly(span);

            return span;
        }
    }
}
