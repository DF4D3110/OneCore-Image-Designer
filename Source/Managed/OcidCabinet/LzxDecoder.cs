using System;
using System.IO;

namespace OcidCabinet
{
    /// <summary>
    /// Self-contained LZX decoder for cabinet folders that use <c>TYPE_LZX</c> compression.
    /// </summary>
    /// <remarks>
    /// The decoder is stateful: one instance decodes every CFDATA frame of a folder in order,
    /// because the LZX bit stream, the sliding window and the delta-coded Huffman tables all
    /// continue across frame boundaries.
    /// </remarks>
    public sealed class LzxDecoder
    {
        private const int NumChars = 256;
        private const int MinMatch = 2;
        private const int NumPrimaryLengths = 7;
        private const int NumSecondaryLengths = 249;
        private const int PretreeNumElements = 20;
        private const int AlignedNumElements = 8;

        private const int BlockVerbatim = 1;
        private const int BlockAligned = 2;
        private const int BlockUncompressed = 3;

        private static readonly byte[] ExtraBits = CreateExtraBits();
        private static readonly int[] PositionBase = CreatePositionBase();

        private readonly int windowSize;
        private readonly byte[] window;
        private readonly int mainElementCount;
        private readonly byte[] mainLengths;
        private readonly byte[] lengthLengths = new byte[NumSecondaryLengths + 1];
        private readonly byte[] alignedLengths = new byte[AlignedNumElements];
        private readonly byte[] pretreeLengths = new byte[PretreeNumElements];

        private CanonicalHuffman mainTree;
        private CanonicalHuffman lengthTree;
        private CanonicalHuffman alignedTree;
        private CanonicalHuffman pretree;

        private int r0 = 1;
        private int r1 = 1;
        private int r2 = 1;
        private int windowPosition;
        private bool headerRead;
        private int blockType = -1;
        private int blockLength;
        private int blockRemaining;
        private int intelFileSize;
        private int intelCurrentPosition;
        private bool intelStarted;
        private int framesRead;

        /// <param name="windowBits">
        /// Window size as log2, taken from the folder's compression option (15 through 21).
        /// </param>
        public LzxDecoder(int windowBits)
        {
            if (windowBits is < 15 or > 21)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(windowBits),
                    windowBits,
                    "LZX window size must be between 15 and 21 bits.");
            }

            windowSize = 1 << windowBits;
            window = new byte[windowSize];

            // Matches may reach before the data decoded so far; the format leaves those
            // bytes undefined and the reference decoders expose 0xDC there.
            Array.Fill(window, (byte)0xDC);

            int positionSlots = windowBits switch
            {
                20 => 42,
                21 => 50,
                _ => windowBits << 1,
            };

            mainElementCount = NumChars + (positionSlots << 3);
            mainLengths = new byte[mainElementCount];
        }

        /// <summary>
        /// Decodes one CFDATA frame. State (window, repeated offsets, block position and the
        /// adaptive Huffman tables) is carried over to the next call.
        /// </summary>
        /// <returns>Number of bytes written, which always equals the destination length.</returns>
        /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
        public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            BitReader reader = new(source);

            if (!headerRead)
            {
                if (reader.Read(1) != 0)
                {
                    int high = (int)reader.Read(16);
                    int low = (int)reader.Read(16);
                    intelFileSize = (high << 16) | low;
                }

                headerRead = true;
            }

            int togo = destination.Length;
            int outPosition = 0;

            while (togo > 0)
            {
                if (blockRemaining == 0)
                {
                    if (blockType == BlockUncompressed)
                    {
                        if ((blockLength & 1) != 0)
                        {
                            reader.SkipRawByte(); // pad byte keeps the next header word aligned
                        }

                        reader.ResetBufferedBits();
                    }

                    ReadBlockHeader(ref reader);
                }

                windowPosition &= windowSize - 1;

                int thisRun = Math.Min(blockRemaining, togo);
                if (windowPosition + thisRun > windowSize)
                {
                    thisRun = windowSize - windowPosition;
                }

                int chunkStart = windowPosition;
                blockRemaining -= thisRun;
                togo -= thisRun;

                int remaining = thisRun;
                switch (blockType)
                {
                    case BlockVerbatim:
                        DecodeSymbols(ref reader, aligned: false, ref remaining);
                        break;

                    case BlockAligned:
                        DecodeSymbols(ref reader, aligned: true, ref remaining);
                        break;

                    case BlockUncompressed:
                        CopyUncompressed(ref reader, ref remaining);
                        break;
                }

                int produced = windowPosition - chunkStart;
                if (produced != thisRun)
                {
                    throw new InvalidDataException("LZX match crosses a frame boundary.");
                }

                window.AsSpan(chunkStart, produced).CopyTo(destination.Slice(outPosition, produced));
                outPosition += produced;
            }

            ApplyIntelTransform(destination);
            return outPosition;
        }

        private void ReadBlockHeader(ref BitReader reader)
        {
            blockType = (int)reader.Read(3);
            int high = (int)reader.Read(16);
            int low = (int)reader.Read(8);
            blockLength = blockRemaining = (high << 8) | low;

            switch (blockType)
            {
                case BlockAligned:
                    for (int i = 0; i < AlignedNumElements; i++)
                    {
                        alignedLengths[i] = (byte)reader.Read(3);
                    }

                    alignedTree = CanonicalHuffman.Build(alignedLengths)
                        ?? throw new InvalidDataException("Invalid LZX aligned offset table.");
                    goto case BlockVerbatim;

                case BlockVerbatim:
                    ReadLengths(ref reader, mainLengths, 0, NumChars);
                    ReadLengths(ref reader, mainLengths, NumChars, mainElementCount);
                    mainTree = CanonicalHuffman.Build(mainLengths.AsSpan(0, mainElementCount))
                        ?? throw new InvalidDataException("Invalid LZX main tree.");

                    if (mainLengths[0xE8] != 0)
                    {
                        intelStarted = true;
                    }

                    ReadLengths(ref reader, lengthLengths, 0, NumSecondaryLengths);
                    lengthTree = CanonicalHuffman.Build(lengthLengths)
                        ?? throw new InvalidDataException("Invalid LZX length tree.");
                    break;

                case BlockUncompressed:
                    intelStarted = true;
                    reader.AlignForUncompressedBlock();
                    r0 = reader.ReadRawInt32();
                    r1 = reader.ReadRawInt32();
                    r2 = reader.ReadRawInt32();
                    break;

                default:
                    throw new InvalidDataException($"Invalid LZX block type {blockType}.");
            }
        }

        private void DecodeSymbols(ref BitReader reader, bool aligned, ref int remaining)
        {
            while (remaining > 0)
            {
                int symbol = DecodeSymbol(ref reader, mainTree);
                if (symbol < NumChars)
                {
                    window[windowPosition++] = (byte)symbol;
                    remaining--;
                    continue;
                }

                int match = symbol - NumChars;
                int length = match & NumPrimaryLengths;
                if (length == NumPrimaryLengths)
                {
                    length += DecodeSymbol(ref reader, lengthTree);
                }

                length += MinMatch;
                int slot = match >> 3;
                int offset;

                if (slot > 2)
                {
                    offset = PositionBase[slot] - 2;
                    int extra = ExtraBits[slot];

                    if (aligned)
                    {
                        if (extra > 3)
                        {
                            offset += (int)reader.Read(extra - 3) << 3;
                            offset += DecodeSymbol(ref reader, alignedTree);
                        }
                        else if (extra == 3)
                        {
                            offset += DecodeSymbol(ref reader, alignedTree);
                        }
                        else if (extra > 0)
                        {
                            offset += (int)reader.Read(extra);
                        }
                    }
                    else
                    {
                        offset += (int)reader.Read(extra);
                    }

                    r2 = r1;
                    r1 = r0;
                    r0 = offset;
                }
                else if (slot == 0)
                {
                    offset = r0;
                }
                else if (slot == 1)
                {
                    offset = r1;
                    r1 = r0;
                    r0 = offset;
                }
                else
                {
                    offset = r2;
                    r2 = r0;
                    r0 = offset;
                }

                CopyMatch(offset, length);
                remaining -= length;
            }
        }

        private void CopyMatch(int offset, int length)
        {
            int destination = windowPosition;
            int source = destination - offset;
            if (source < 0)
            {
                source += windowSize;
            }

            for (int i = 0; i < length; i++)
            {
                window[destination] = window[source];
                if (++destination == windowSize)
                {
                    destination = 0;
                }

                if (++source == windowSize)
                {
                    source = 0;
                }
            }

            windowPosition = destination;
        }

        private void CopyUncompressed(ref BitReader reader, ref int remaining)
        {
            while (remaining > 0)
            {
                window[windowPosition++] = (byte)reader.ReadRawByte();
                remaining--;
            }
        }

        private void ReadLengths(ref BitReader reader, byte[] lengths, int first, int last)
        {
            for (int i = 0; i < PretreeNumElements; i++)
            {
                pretreeLengths[i] = (byte)reader.Read(4);
            }

            pretree = CanonicalHuffman.Build(pretreeLengths)
                ?? throw new InvalidDataException("Invalid LZX pre-tree.");

            int position = first;
            while (position < last)
            {
                int symbol = DecodeSymbol(ref reader, pretree);

                if (symbol == 17)
                {
                    int count = (int)reader.Read(4) + 4;
                    FillZeros(lengths, position, count, last);
                    position += count;
                }
                else if (symbol == 18)
                {
                    int count = (int)reader.Read(5) + 20;
                    FillZeros(lengths, position, count, last);
                    position += count;
                }
                else if (symbol == 19)
                {
                    int count = (int)reader.Read(1) + 4;
                    int delta = DecodeSymbol(ref reader, pretree);
                    int value = lengths[position] - delta;
                    if (value < 0)
                    {
                        value += 17;
                    }

                    if (position + count > last)
                    {
                        throw new InvalidDataException("LZX code length run overruns its table.");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        lengths[position++] = (byte)value;
                    }
                }
                else
                {
                    int value = lengths[position] - symbol;
                    if (value < 0)
                    {
                        value += 17;
                    }

                    lengths[position++] = (byte)value;
                }
            }
        }

        private static void FillZeros(byte[] lengths, int start, int count, int last)
        {
            if (start + count > last)
            {
                throw new InvalidDataException("LZX code length run overruns its table.");
            }

            for (int i = 0; i < count; i++)
            {
                lengths[start + i] = 0;
            }
        }

        private static int DecodeSymbol(ref BitReader reader, CanonicalHuffman table)
        {
            if (table == null || table.IsEmpty)
            {
                throw new InvalidDataException("LZX stream references an empty Huffman table.");
            }

            int code = 0;
            int first = 0;
            int index = 0;

            for (int length = 1; length <= 16; length++)
            {
                code |= reader.ReadBit();
                int count = table.LengthCounts[length];
                if (code - first < count)
                {
                    return table.Symbols[index + (code - first)];
                }

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            throw new InvalidDataException("Invalid LZX Huffman code.");
        }

        /// <summary>
        /// Applies the x86 E8 call translation that LZX may apply while compressing executables.
        /// The stream header only enables it when a non-zero file size was stored.
        /// </summary>
        private void ApplyIntelTransform(Span<byte> data)
        {
            if (intelFileSize != 0 && intelStarted && data.Length > 6 && framesRead < 32768)
            {
                int current = intelCurrentPosition;
                int limit = data.Length - 10;

                for (int i = 0; i < limit;)
                {
                    if (data[i] != 0xE8)
                    {
                        i++;
                        current++;
                        continue;
                    }

                    int absolute = data[i + 1] | (data[i + 2] << 8) | (data[i + 3] << 16) | (data[i + 4] << 24);
                    if (absolute >= -current && absolute < intelFileSize)
                    {
                        int relative = absolute >= 0 ? absolute - current : absolute + intelFileSize;
                        data[i + 1] = (byte)relative;
                        data[i + 2] = (byte)(relative >> 8);
                        data[i + 3] = (byte)(relative >> 16);
                        data[i + 4] = (byte)(relative >> 24);
                    }

                    i += 5;
                    current += 5;
                }
            }

            framesRead++;
            intelCurrentPosition += data.Length;
        }

        private static byte[] CreateExtraBits()
        {
            byte[] bits = new byte[52];
            for (int i = 0, j = 0; i <= 50; i += 2)
            {
                bits[i] = bits[i + 1] = (byte)j;
                if (i != 0 && j < 17)
                {
                    j++;
                }
            }

            return bits;
        }

        private static int[] CreatePositionBase()
        {
            int[] bases = new int[51];
            for (int i = 0, value = 0; i <= 50; i++)
            {
                bases[i] = value;
                value += 1 << ExtraBits[i];
            }

            return bases;
        }

        /// <summary>
        /// LZX bit reader: 16-bit little-endian words consumed most-significant bit first.
        /// </summary>
        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> source;
            private int position;
            private uint buffer;
            private int bitsAvailable;

            internal BitReader(ReadOnlySpan<byte> data)
            {
                source = data;
                position = 0;
                buffer = 0;
                bitsAvailable = 0;
            }

            internal int Position => position;

            internal int ReadBit()
            {
                return (int)Read(1);
            }

            internal uint Read(int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                Ensure(count);
                uint value = buffer >> (32 - count);
                buffer <<= count;
                bitsAvailable -= count;
                return value;
            }

            private void Ensure(int count)
            {
                while (bitsAvailable < count)
                {
                    int low = position < source.Length ? source[position] : 0;
                    int high = position + 1 < source.Length ? source[position + 1] : 0;
                    position += 2;

                    if (position > source.Length + 4)
                    {
                        throw new InvalidDataException("LZX stream ended unexpectedly.");
                    }

                    buffer |= (uint)(((high << 8) | low) << (16 - bitsAvailable));
                    bitsAvailable += 16;
                }
            }

            /// <summary>
            /// Word-aligns the reader for an uncompressed block, dropping the partially
            /// consumed word as required by the format's padding rules.
            /// </summary>
            internal void AlignForUncompressedBlock()
            {
                Ensure(16);
                if (bitsAvailable > 16)
                {
                    position -= 2;
                }

                ResetBufferedBits();
            }

            internal void ResetBufferedBits()
            {
                buffer = 0;
                bitsAvailable = 0;
            }

            internal void SkipRawByte()
            {
                position++;
            }

            internal int ReadRawByte()
            {
                if (position >= source.Length)
                {
                    throw new InvalidDataException("LZX stream ended inside an uncompressed block.");
                }

                return source[position++];
            }

            internal int ReadRawInt32()
            {
                return ReadRawByte() | (ReadRawByte() << 8) | (ReadRawByte() << 16) | (ReadRawByte() << 24);
            }
        }
    }
}
