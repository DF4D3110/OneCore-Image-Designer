using StorageSpace.Data;
using StorageSpace.Data.Subtypes;

namespace StorageSpace
{
    public class Pool
    {
        private readonly Stream Stream;
        private readonly long OriginalSeekPosition;

        private bool IsAncientFormat = false;

        public SPACEDB SPACEDB
        {
            get; private set;
        }

        public SDBC SDBC
        {
            get; private set;
        }

        public List<SDBB> SDBBs
        {
            get; private set;
        }

        public Dictionary<uint, byte[]> BlockEntries
        {
            get; private set;
        }

        public List<Block> Blocks
        {
            get; private set;
        }

        public List<StoragePool> SDBBStorageInformation
        {
            get; private set;
        }

        public List<PhysicalDisk> SDBBPhysicalDisks
        {
            get; private set;
        }

        public List<Volume> SDBBVolumes
        {
            get; private set;
        }

        public List<SlabAllocation> SDBBSlabAllocation
        {
            get; private set;
        }

        public Pool(Stream Stream)
        {
            this.Stream = Stream;
            OriginalSeekPosition = Stream.Position;

            ParsePool();
        }

        public Pool(Stream Stream, Dictionary<long, Stream> physicalDiskStreams)
        {
            this.Stream = Stream;
            OriginalSeekPosition = Stream.Position;
            this.PhysicalDiskStreams = physicalDiskStreams;

            ParsePool();
        }

        private void ParsePool()
        {
            using BinaryReader reader = new(Stream);

            Stream.Seek(0x18, SeekOrigin.Current);
            IsAncientFormat = reader.ReadUInt64() != 0;
            Stream.Seek(OriginalSeekPosition, SeekOrigin.Begin);

            SPACEDB = SPACEDB.Parse(Stream);

            int SDBCStartPosition = 0x1000;

            Stream.Seek(OriginalSeekPosition + SDBCStartPosition, SeekOrigin.Begin);

            SDBC = SDBC.Parse(Stream);

            if (SDBC.StorageGUID != SPACEDB.StorageGUID)
            {
            }

            int SDBBChainStartPosition = SDBCStartPosition + SDBC.SDBCLength;

            Stream.Seek(OriginalSeekPosition + SDBBChainStartPosition, SeekOrigin.Begin);

            SDBBStorageInformation = [];
            SDBBPhysicalDisks = [];
            SDBBVolumes = [];
            SDBBSlabAllocation = [];

            BlockEntries = [];

            SDBBs = [];
            Blocks = [];

            for (uint j = 8; j < SDBC.SDBBLength + (IsAncientFormat ? 8 : 0); j++)
            {
                SDBB SDBB = SDBB.Parse(Stream);
                SDBBs.Add(SDBB);

                if (SDBB.ParentSDBBIndex == 0)
                {
                    continue;
                }

                if (BlockEntries.TryGetValue(SDBB.ParentSDBBIndex, out byte[]? value))
                {
                    BlockEntries[SDBB.ParentSDBBIndex] = [.. value, .. SDBB.Data];
                }
                else
                {
                    BlockEntries[SDBB.ParentSDBBIndex] = SDBB.Data;
                }
            }

            for (uint j = 8; j < SDBC.SDBBLength + (IsAncientFormat ? 8 : 0); j++)
            {
                if (!BlockEntries.ContainsKey(j))
                {
                    continue;
                }

                byte[] BlockBuffer = BlockEntries[j];
                using MemoryStream BlockStream = new(BlockBuffer);

                Block Block = Block.Parse(BlockStream);
                Blocks.Add(Block);

                using MemoryStream dataStream = new(Block.Data);

                switch (Block.Type)
                {
                    case BlockType.StoragePool:
                        SDBBStorageInformation.Add(StoragePool.Parse(dataStream));
                        break;
                    case BlockType.PhysicalDisk:
                        SDBBPhysicalDisks.Add(PhysicalDisk.Parse(dataStream));
                        break;
                    case BlockType.Volume:
                        SDBBVolumes.Add(Volume.Parse(dataStream));
                        break;
                    case BlockType.SlabAllocation:
                        SDBBSlabAllocation.Add(SlabAllocation.Parse(dataStream));
                        break;
                    default:
                        throw new Exception($"Unknown Entry Type! {Block.Type}");
                }
            }

            Stream.Seek(OriginalSeekPosition, SeekOrigin.Begin);
        }

        public Dictionary<long, Stream> PhysicalDiskStreams
        {
            get; private set;
        } = [];

        public void SetPhysicalDiskStreams(Dictionary<long, Stream> physicalDiskStreams)
        {
            PhysicalDiskStreams = physicalDiskStreams;
        }

        public static Pool OpenPool(IEnumerable<Stream> poolPartitionStreams)
        {
            List<Stream> streams = poolPartitionStreams.ToList();

            if (streams.Count == 0)
            {
                throw new ArgumentException("No storage pool partition streams provided.", nameof(poolPartitionStreams));
            }

            Stream primary = streams[0];
            primary.Position = 0;

            Pool pool = new(primary);

            Dictionary<long, Stream> diskStreams = [];

            foreach (Stream stream in streams)
            {
                try
                {
                    stream.Position = 0;
                    Data.SPACEDB spacedb = Data.SPACEDB.Parse(stream);

                    Data.Subtypes.PhysicalDisk? physicalDisk = pool.SDBBPhysicalDisks.FirstOrDefault(pd => pd.SPACEDBGUID == spacedb.SPACEDBGUID);

                    if (physicalDisk != null)
                    {
                        diskStreams[physicalDisk.PhysicalDiskNumber] = stream;
                    }

                    stream.Position = 0;
                }
                catch
                {
                    // Ignore streams that do not contain valid pool metadata.
                }
            }

            pool.SetPhysicalDiskStreams(diskStreams);

            return pool;
        }

        public Dictionary<long, string> GetDisks()
        {
            Dictionary<long, string> disks = [];

            foreach (Volume SDBBVolume in SDBBVolumes)
            {
                disks.Add(SDBBVolume.VolumeNumber, string.IsNullOrEmpty(SDBBVolume.Name) ? SDBBVolume.VolumeGUID.ToString() : SDBBVolume.Name);
            }

            return disks;
        }

        public Space OpenDisk(long storeIndex)
        {
            return new Space(Stream, storeIndex, this, OriginalSeekPosition, IsAncientFormat, PhysicalDiskStreams);
        }
    }
}