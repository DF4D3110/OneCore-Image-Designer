
using System.Runtime.InteropServices;

namespace Img2Ffu.Reader.Structs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct StoreHeader
    {
        public uint UpdateType;
        public ushort MajorVersion, MinorVersion;
        public ushort FullFlashMajorVersion, FullFlashMinorVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 192)]
        public byte[] PlatformId;
        public uint BlockSize;
        public uint WriteDescriptorCount;
        public uint WriteDescriptorLength;
        public uint ValidateDescriptorCount;
        public uint ValidateDescriptorLength;
        public uint InitialTableIndex;
        public uint InitialTableCount;
        public uint FlashOnlyTableIndex;
        public uint FlashOnlyTableCount;
        public uint FinalTableIndex;
        public uint FinalTableCount;

        public override readonly string ToString()
        {
            return $"{{UpdateType: {UpdateType}, MajorVersion: {MajorVersion}, MinorVersion: {MinorVersion}}}";
        }
    }
}