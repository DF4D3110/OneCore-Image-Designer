using DiscUtils;
using DiscUtils.Streams;
using MobilePackageGen.Adapters;
using StorageSpace;

namespace MobilePackageGen.Adapters.StorageSpace
{
    public class Disk : IDisk
    {
        public IEnumerable<IPartition> Partitions
        {
            get;
        }

        public Disk(IEnumerable<Stream> poolPartitionStreams)
        {
            List<IPartition> partitions = [];

            Pool pool = Pool.OpenPool(poolPartitionStreams);

            Logging.Log();

            Logging.Log("Storage Pool");

            Dictionary<long, string> disks = pool.GetDisks();

            foreach (KeyValuePair<long, string> disk in disks.OrderBy(x => x.Key))
            {
                using Space space = pool.OpenDisk(disk.Key);

                Logging.Log($"- {disk.Key}: {disk.Value} ({space.Length}B / {space.Length / 1024 / 1024}MB / {space.Length / 1024 / 1024 / 1024}GB) StorageSpace");
            }

            Logging.Log();

            foreach (KeyValuePair<long, string> disk in disks)
            {
                Space space = pool.OpenDisk(disk.Key);

                int spaceSectorSize = DiskCommon.TryDetectSectorSize(space);

                IEnumerable<GPT.GPT.Partition> msPartitionTable = DiskCommon.GetGPTPartitions(space, (uint)spaceSectorSize);

                if (msPartitionTable != null)
                {
                    foreach (GPT.GPT.Partition storageSpacePartition in msPartitionTable)
                    {
                        Stream partitionStream = DiskCommon.Open(storageSpacePartition, (uint)spaceSectorSize, space);

                        partitions.Add(new FileSystemPartition(partitionStream, storageSpacePartition.Name, storageSpacePartition.PartitionTypeGuid, storageSpacePartition.PartitionGuid));
                    }
                }
            }

            Partitions = partitions;
        }
    }
}
