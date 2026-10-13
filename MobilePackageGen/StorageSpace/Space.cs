using StorageSpace.Data.Subtypes;

namespace StorageSpace
{
    public class Space : Stream
    {
        private readonly Stream Stream;
        private readonly long OriginalSeekPosition;
        private readonly long length;

        private readonly bool IsAncientFormat = false;

        private long blockSize => IsAncientFormat ? 0x10000000 : 0x100000;

        private readonly Dictionary<long, (long block, long diskNumber)> blockTable;
        private readonly Dictionary<long, Stream> physicalDiskStreams;
        private readonly List<SlabAllocation> slabAllocations = [];
        private readonly long TotalBlocks;

        private long currentPosition = 0;

        internal Space(Stream Stream, long storeIndex, Pool storageSpace, long OriginalSeekPosition, bool IsAncientFormat)
            : this(Stream, storeIndex, storageSpace, OriginalSeekPosition, IsAncientFormat, [])
        {
        }

        internal Space(Stream Stream, long storeIndex, Pool storageSpace, long OriginalSeekPosition, bool IsAncientFormat, Dictionary<long, Stream> physicalDiskStreams)
        {
            this.IsAncientFormat = IsAncientFormat;
            this.OriginalSeekPosition = OriginalSeekPosition;
            this.Stream = Stream;
            this.physicalDiskStreams = physicalDiskStreams;

            foreach (Volume volume in storageSpace.SDBBVolumes)
            {
                if (volume.VolumeNumber != storeIndex)
                {
                    continue;
                }

                TotalBlocks = volume.VolumeBlockNumber;
            }

            foreach (SlabAllocation slabAllocation in storageSpace.SDBBSlabAllocation)
            {
                if (slabAllocation.VolumeID != storeIndex)
                {
                    continue;
                }

                slabAllocations.Add(slabAllocation);
            }

            (length, blockTable) = BuildBlockTable();
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        private (long, Dictionary<long, (long block, long diskNumber)>) BuildBlockTable()
        {
            Dictionary<long, (long block, long diskNumber)> blockTable = [];

            long blockSize = IsAncientFormat ? this.blockSize : 1;

            foreach (SlabAllocation slabAllocation in slabAllocations)
            {
                long virtualDiskBlockNumber = slabAllocation.VolumeBlockNumber;
                long physicalDiskBlockNumber = slabAllocation.PhysicalDiskBlockNumber;
                long physicalDiskId = slabAllocation.PhysicalDiskID;

                blockTable.Add(virtualDiskBlockNumber, (physicalDiskBlockNumber, physicalDiskId));
            }

            return (TotalBlocks * blockSize, blockTable);
        }

        private (long block, long diskNumber) GetBlockDataIndex(long realBlockOffset)
        {
            if (blockTable.TryGetValue(realBlockOffset, out (long block, long diskNumber) value))
            {
                return value;
            }

            return (-1, -1);
        }

        private Stream GetDiskStream(long diskNumber)
        {
            if (physicalDiskStreams.TryGetValue(diskNumber, out Stream? stream))
            {
                return stream;
            }

            return Stream;
        }

        public override long Position
        {
            get => currentPosition;
            set
            {
                if (currentPosition < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                currentPosition = value;
            }
        }

        public override void Flush()
        {
        }

        private long ImageGetStoreDataBlockOffset(long physicalDiskBlockNumber) => IsAncientFormat ? ((long)physicalDiskBlockNumber + 2) * blockSize : physicalDiskBlockNumber * blockSize + 0x2000 + 0x4000000;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset + count > buffer.Length)
            {
                throw new ArgumentException("The sum of offset and count is greater than the buffer length.");
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (Position >= Length)
            {
                return count;
            }

            long readBytes = count;

            if (Position + readBytes > Length)
            {
                readBytes = (int)(Length - Position);
            }
            long overflowBlockStartByteCount = Position % blockSize;
            long overflowBlockEndByteCount = (Position + readBytes) % blockSize;
            long noOverflowBlockStartByteCount = Position - overflowBlockStartByteCount;
            long extraStartBytes = blockSize - overflowBlockStartByteCount;
            long extraEndBytes = blockSize - overflowBlockEndByteCount;
            long noOverflowBlockEndByteCount = Position + readBytes + extraEndBytes;
            long startBlockIndex = noOverflowBlockStartByteCount / blockSize;
            long endBlockIndex = noOverflowBlockEndByteCount / blockSize;
            for (long currentBlock = startBlockIndex; currentBlock < endBlockIndex; currentBlock++)
            {
                bool isFirstBlock = currentBlock == startBlockIndex;
                bool isLastBlock = currentBlock == endBlockIndex - 1;

                long bytesToRead = blockSize;
                long bufferDestination = extraStartBytes + (currentBlock - startBlockIndex - 1) * blockSize;

                if (isFirstBlock)
                {
                    bytesToRead = extraStartBytes;
                    bufferDestination = 0;
                }

                if (isLastBlock)
                {
                    bytesToRead -= extraEndBytes;
                }

                (long block, long diskNumber) virtualBlockIndex = GetBlockDataIndex(currentBlock);

                if (virtualBlockIndex.block != -1)
                {
                    long physicalDiskLocation = ImageGetStoreDataBlockOffset(virtualBlockIndex.block);

                    if (isFirstBlock)
                    {
                        physicalDiskLocation += overflowBlockStartByteCount;
                    }

                    Stream target = GetDiskStream(virtualBlockIndex.diskNumber);

                    target.Seek(OriginalSeekPosition + physicalDiskLocation, SeekOrigin.Begin);
                    target.Read(buffer, offset + (int)bufferDestination, (int)bytesToRead);
                }
                else
                {
                    Array.Fill<byte>(buffer, 0, offset + (int)bufferDestination, (int)bytesToRead);
                }
            }

            Position += readBytes;

            if (Position == Length)
            {
            }

            return (int)readBytes;
        }

        public void CopyTo(Stream DestinationStream, Action<ulong, ulong> ProgressCallBack)
        {
            long OriginalDestinationStreamPosition = DestinationStream.Position;

            ulong totalBytes = (uint)slabAllocations.Count * (ulong)blockSize * 2u;

            ProgressCallBack?.Invoke(0, totalBytes);

            ulong currentWrittenBytes = 0;

            for (int i = 0; i < slabAllocations.Count; i++)
            {
                SlabAllocation slabAllocation = slabAllocations[i];
                long virtualDiskBlockNumber = slabAllocation.VolumeBlockNumber;
                long physicalDiskBlockNumber = slabAllocation.PhysicalDiskBlockNumber;

                long virtualPosition = virtualDiskBlockNumber * blockSize;
                long physicalPosition = ImageGetStoreDataBlockOffset(physicalDiskBlockNumber);

                Stream target = GetDiskStream(slabAllocation.PhysicalDiskID);

                target.Seek(OriginalSeekPosition + physicalPosition, SeekOrigin.Begin);
                DestinationStream.Seek(OriginalDestinationStreamPosition + virtualPosition, SeekOrigin.Begin);
                ulong chunkSize = (ulong)blockSize;
                for (ulong j = 0; j < (ulong)blockSize; j += chunkSize)
                {
                    byte[] buffer = new byte[chunkSize];

                    target.Read(buffer);
                    currentWrittenBytes += chunkSize;
                    ProgressCallBack?.Invoke(currentWrittenBytes, totalBytes);

                    DestinationStream.Write(buffer);
                    currentWrittenBytes += chunkSize;
                    ProgressCallBack?.Invoke(currentWrittenBytes, totalBytes);
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    {
                        Position = offset;
                        break;
                    }
                case SeekOrigin.Current:
                    {
                        Position += offset;
                        break;
                    }
                case SeekOrigin.End:
                    {
                        Position = Length + offset;
                        break;
                    }
                default:
                    {
                        throw new ArgumentException(nameof(origin));
                    }
            }

            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
