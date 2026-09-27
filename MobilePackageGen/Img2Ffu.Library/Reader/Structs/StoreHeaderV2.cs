
using System.Runtime.InteropServices;

namespace Img2Ffu.Reader.Structs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct StoreHeaderV2
    {
        public ushort NumberOfStores;
        public ushort StoreIndex;
        public ulong StorePayloadSize;
        public ushort DevicePathLength;

        public override readonly string ToString()
        {
            return $"{{NumberOfStores: {NumberOfStores}, StoreIndex: {StoreIndex}, StorePayloadSize: {StorePayloadSize}, DevicePathLength: {DevicePathLength}}}";
        }
    }
}