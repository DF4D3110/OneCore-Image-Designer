
namespace Img2Ffu.Writer
{
    internal class FlashPart(Stream Stream, ulong StartLocation)
    {
        public ulong StartLocation = StartLocation;
        public Stream Stream = Stream;
    }
}
