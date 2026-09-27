
using Img2Ffu.Writer.Data;
using System.Collections;
using System.Security.Cryptography;

namespace Img2Ffu.Writer.Flashing
{
    internal static class BlockPayloadsGenerator
    {
        internal static List<KeyValuePair<ByteArrayKey, BlockPayload>> GetGPTPayloads(List<KeyValuePair<ByteArrayKey, BlockPayload>> blockPayloads, Stream stream, uint BlockSize, bool IsFixedDiskLength)
        {
            List<KeyValuePair<ByteArrayKey, BlockPayload>> blockPayloadsList = [.. blockPayloads];

            byte[] EMPTY_BLOCK_HASH = SHA256.HashData(new byte[BlockSize]);
            blockPayloadsList.Insert(0, new KeyValuePair<ByteArrayKey, BlockPayload>(new ByteArrayKey(EMPTY_BLOCK_HASH), new BlockPayload(
                new WriteDescriptor()
                {
                    BlockDataEntry = new BlockDataEntry()
                    {
                        BlockCount = 1,
                        LocationCount = 2
                    },
                    DiskLocations =
                    [
                        new DiskLocation()
                        {
                            BlockIndex = 0,
                            DiskAccessMethod = 0
                        },
                        new DiskLocation()
                        {
                            BlockIndex = 0,
                            DiskAccessMethod = 2
                        }
                    ]
                },
                new MemoryStream(new byte[(int)BlockSize]),
                0
            )));

            byte[] PrimaryGPTBuffer = new byte[(int)BlockSize];
            _ = stream.Seek(0, SeekOrigin.Begin);
            _ = stream.Read(PrimaryGPTBuffer);

            MemoryStream primaryGPTStream = new(PrimaryGPTBuffer);

            KeyValuePair<ByteArrayKey, BlockPayload> primaryGPTKeyValuePair = new(new ByteArrayKey(SHA256.HashData(primaryGPTStream)), new BlockPayload(
                new WriteDescriptor()
                {
                    BlockDataEntry = new BlockDataEntry()
                    {
                        BlockCount = 1,
                        LocationCount = 1
                    },
                    DiskLocations =
                    [
                        new DiskLocation()
                        {
                            BlockIndex = 0,
                            DiskAccessMethod = 0
                        }
                    ]
                },
                primaryGPTStream,
                0
            ));
            if (IsFixedDiskLength)
            {
                ulong endGPTChunkStartLocation = (ulong)stream.Length - BlockSize;
                byte[] SecondaryGPTBuffer = new byte[(int)BlockSize];
                _ = stream.Seek((long)endGPTChunkStartLocation, SeekOrigin.Begin);
                _ = stream.Read(SecondaryGPTBuffer);

                MemoryStream secondaryGPTStream = new(SecondaryGPTBuffer);
                blockPayloadsList.Add(primaryGPTKeyValuePair);
                blockPayloadsList.Add(primaryGPTKeyValuePair);

                blockPayloadsList.Add(new KeyValuePair<ByteArrayKey, BlockPayload>(new ByteArrayKey(SHA256.HashData(secondaryGPTStream)), new BlockPayload(
                    new WriteDescriptor()
                    {
                        BlockDataEntry = new BlockDataEntry()
                        {
                            BlockCount = 1,
                            LocationCount = 1
                        },
                        DiskLocations =
                        [
                            new DiskLocation()
                            {
                                BlockIndex = 0,
                                DiskAccessMethod = 2
                            }
                        ]
                    },
                    secondaryGPTStream,
                    0
                )));
            }
            else
            {
                blockPayloadsList.Insert(1, primaryGPTKeyValuePair);
                blockPayloadsList.Add(primaryGPTKeyValuePair);

                ulong endGPTChunkStartLocation = (ulong)stream.Length - BlockSize;
                byte[] SecondaryGPTBuffer = new byte[(int)BlockSize];
                _ = stream.Seek((long)endGPTChunkStartLocation, SeekOrigin.Begin);

                try
                {
                    _ = stream.Read(SecondaryGPTBuffer);
                }
                catch (EndOfStreamException)
                {
                    SecondaryGPTBuffer = new byte[BlockSize];
                }
                catch
                {
                    throw;
                }

                MemoryStream secondaryGPTStream = new(SecondaryGPTBuffer);
                blockPayloadsList.Add(new KeyValuePair<ByteArrayKey, BlockPayload>(new ByteArrayKey(SHA256.HashData(secondaryGPTStream)), new BlockPayload(
                    new WriteDescriptor()
                    {
                        BlockDataEntry = new BlockDataEntry()
                        {
                            BlockCount = 1,
                            LocationCount = 1
                        },
                        DiskLocations =
                        [
                            new DiskLocation()
                            {
                                BlockIndex = 0,
                                DiskAccessMethod = 2
                            }
                        ]
                    },
                    secondaryGPTStream,
                    0
                )));
            }

            return blockPayloadsList;
        }

        internal static List<KeyValuePair<ByteArrayKey, BlockPayload>> GetOptimizedPayloads(FlashPart[] flashParts, uint BlockSize, uint BlankSectorBufferSize, ILogging Logging)
        {
            List<KeyValuePair<ByteArrayKey, BlockPayload>> hashedBlocks = [];

            if (flashParts == null)
            {
                return hashedBlocks;
            }

            ulong CurrentBlockCount = 0;
            ulong TotalBlockCount = 0;

            foreach (FlashPart flashPart in flashParts)
            {
                TotalBlockCount += (ulong)flashPart.Stream.Length / BlockSize;
            }

            DateTime startTime = DateTime.Now;

            Logging.Log($"Total Block Count: {TotalBlockCount} - {TotalBlockCount * BlockSize / (1024 * 1024 * 1024)}GB");
            Logging.Log("Hashing resources...");

            bool blankPayloadPhase = false;
            ulong blankPayloadCount = 0;

            byte[] EMPTY_BLOCK_HASH = SHA256.HashData(new byte[BlockSize]);

            List<KeyValuePair<ByteArrayKey, BlockPayload>> blankBlocks = [];

            Memory<byte> blockBuffer = new byte[BlockSize];
            Span<byte> FFUBlockPayload = blockBuffer.Span;

            foreach (FlashPart flashPart in flashParts)
            {
                _ = flashPart.Stream.Seek(0, SeekOrigin.Begin);

                ulong streamLength = (ulong)flashPart.Stream.Length;
                ulong totalBlockCount = streamLength / BlockSize;

                for (ulong blockIndex = 0; blockIndex < totalBlockCount; blockIndex++)
                {
                    ulong streamPosition = (ulong)flashPart.Stream.Position;

                    try
                    {
                        _ = flashPart.Stream.Read(FFUBlockPayload);
                    }
                    catch (EndOfStreamException)
                    {
                        blockBuffer = new byte[BlockSize];
                        FFUBlockPayload = blockBuffer.Span;
                    }
                    catch
                    {
                        throw;
                    }

                    byte[] FFUBlockHash = SHA256.HashData(FFUBlockPayload);

                    if (!StructuralComparisons.StructuralEqualityComparer.Equals(EMPTY_BLOCK_HASH, FFUBlockHash) ||
                        blankPayloadCount < BlankSectorBufferSize)
                    {
                        ulong FFUBlockIndex = (flashPart.StartLocation / BlockSize) + blockIndex;

                        if (FFUBlockIndex > uint.MaxValue)
                        {
                            throw new NotSupportedException("The image requires more block than the FFU format can support.");
                        }

                        KeyValuePair<ByteArrayKey, BlockPayload> blockDataKeyPair = new(
                            new ByteArrayKey(FFUBlockHash),
                            new BlockPayload(
                                new WriteDescriptor()
                                {
                                    BlockDataEntry = new BlockDataEntry()
                                    {
                                        BlockCount = 1,
                                        LocationCount = 1
                                    },
                                    DiskLocations =
                                    [
                                        new DiskLocation()
                                    {
                                        BlockIndex = (uint)FFUBlockIndex,
                                        DiskAccessMethod = 0
                                    }
                                    ]
                                },
                                flashPart.Stream,
                                streamPosition
                            ));

                        if (!StructuralComparisons.StructuralEqualityComparer.Equals(EMPTY_BLOCK_HASH, FFUBlockHash))
                        {
                            hashedBlocks.Add(blockDataKeyPair);

                            if (blankPayloadPhase && blankPayloadCount < BlankSectorBufferSize)
                            {
                                foreach (KeyValuePair<ByteArrayKey, BlockPayload> blankPayload in blankBlocks)
                                {
                                    hashedBlocks.Add(blankPayload);
                                }
                            }

                            blankPayloadPhase = false;
                            blankPayloadCount = 0;
                            blankBlocks.Clear();
                        }
                        else if (blankPayloadCount < BlankSectorBufferSize)
                        {
                            blankPayloadPhase = true;
                            blankPayloadCount++;

                            blankBlocks.Add(blockDataKeyPair);
                        }
                    }
                    else if (blankPayloadCount >= BlankSectorBufferSize && blankBlocks.Count > 0)
                    {
                        foreach (KeyValuePair<ByteArrayKey, BlockPayload> blankPayload in blankBlocks)
                        {
                            hashedBlocks.Add(blankPayload);
                        }

                        blankBlocks.Clear();
                    }

                    CurrentBlockCount++;
                    LoggingHelpers.ShowProgress(CurrentBlockCount, TotalBlockCount, startTime, blankPayloadPhase, Logging);
                }
            }

            Logging.Log("");
            Logging.Log($"FFU Block Count: {hashedBlocks.Count} - {hashedBlocks.Count * BlockSize / (1024 * 1024 * 1024)}GB");

            return hashedBlocks;
        }
    }
}