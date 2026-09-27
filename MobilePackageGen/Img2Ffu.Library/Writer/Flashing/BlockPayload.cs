
using Img2Ffu.Writer.Data;

namespace Img2Ffu.Writer.Flashing
{
    public class BlockPayload(WriteDescriptor WriteDescriptor, Stream Stream, ulong StreamLocation)
    {
        public WriteDescriptor WriteDescriptor = WriteDescriptor;
        public Stream Stream = Stream;
        public ulong FlashPartStreamLocation = StreamLocation;

        internal void ReadBlock(Span<byte> BlockBuffer)
        {
            _ = Stream.Seek((long)FlashPartStreamLocation, SeekOrigin.Begin);
            _ = Stream.Read(BlockBuffer);
        }
    }
}