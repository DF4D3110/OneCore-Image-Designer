
using System.Runtime.InteropServices;

namespace Img2Ffu.Reader.Structs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct SecurityHeader
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string Signature;       // "SignedImage "
        public uint ChunkSizeInKB;
        public uint AlgorithmId;
        public uint CatalogSize;
        public uint HashTableSize;
    }
}