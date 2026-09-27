
using System.Runtime.InteropServices;

namespace Img2Ffu.Reader.Structs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct ImageHeader
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string Signature;      // "ImageFlash  "
        public uint ManifestLength;
        public uint ChunkSize;
    }
}