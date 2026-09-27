
using System.Runtime.InteropServices;

namespace Img2Ffu.Streams
{
    public partial class DeviceStream
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_GEOMETRY
        {
            internal long Cylinders;
            internal MEDIA_TYPE MediaType;
            internal uint TracksPerCylinder;
            internal uint SectorsPerTrack;
            internal uint BytesPerSector;
        }
    }
}