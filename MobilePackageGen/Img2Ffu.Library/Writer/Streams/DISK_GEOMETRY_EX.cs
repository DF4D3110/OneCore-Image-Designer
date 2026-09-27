
using System.Runtime.InteropServices;

namespace Img2Ffu.Streams
{
    public partial class DeviceStream
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_GEOMETRY_EX
        {
            internal DISK_GEOMETRY Geometry;
            internal long DiskSize;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            internal byte[] Data;
        }
    }
}