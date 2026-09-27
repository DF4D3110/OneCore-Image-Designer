
namespace Img2Ffu
{
    public partial class GPT
    {
        public class Partition
        {
            private ulong _SizeInSectors;
            private ulong _FirstSector;
            private ulong _LastSector;

            public required string Name;
            public Guid PartitionTypeGuid;
            public Guid PartitionGuid;
            internal ulong Attributes;

            internal ulong SizeInSectors
            {
                get => _SizeInSectors != 0 ? _SizeInSectors : LastSector - FirstSector + 1;
                set
                {
                    _SizeInSectors = value;
                    if (FirstSector != 0)
                    {
                        LastSector = FirstSector + _SizeInSectors - 1;
                    }
                }
            }

            internal ulong FirstSector
            {
                get => _FirstSector;
                set
                {
                    _FirstSector = value;
                    if (_SizeInSectors != 0)
                    {
                        _LastSector = FirstSector + _SizeInSectors - 1;
                    }
                }
            }

            internal ulong LastSector
            {
                get => _LastSector;
                set
                {
                    _LastSector = value;
                    _SizeInSectors = 0;
                }
            }

            public string Volume => @"\\?\Volume" + PartitionGuid.ToString("b") + @"\";

            public string FirstSectorAsString
            {
                get => "0x" + FirstSector.ToString("X16");
                set => FirstSector = Convert.ToUInt64(value, 16);
            }

            public string LastSectorAsString
            {
                get => "0x" + LastSector.ToString("X16");
                set => LastSector = Convert.ToUInt64(value, 16);
            }

            public string AttributesAsString
            {
                get => "0x" + Attributes.ToString("X16");
                set => Attributes = Convert.ToUInt64(value, 16);
            }
        }
    }
}