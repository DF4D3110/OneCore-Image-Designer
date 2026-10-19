using System;
using System.IO;

namespace OcidCabinet
{
    /// <summary>
    /// Self-contained raw DEFLATE (RFC 1951) decoder used for MSZIP cabinet folders.
    /// </summary>
    /// <remarks>
    /// MSZIP stores the two-byte <c>CK</c> marker before each CFDATA payload, and the compressor
    /// keeps using the last 32 KiB of the folder output as the match window, so a decoder
    /// instance carries its window from one block to the next. One instance must therefore be
    /// used for every block of the same cabinet folder, in order.
    /// </remarks>
    public sealed class DeflateDecoder
    {
        private const int WindowSize = 1 << 15;
        private const int WindowMask = WindowSize - 1;

        private static readonly int[] LengthBase =
        [
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59,
            67, 83, 99, 115, 131, 163, 195, 227, 258,
        ];

        private static readonly int[] LengthExtra =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
            4, 4, 4, 4, 5, 5, 5, 5, 0,
        ];

        private static readonly int[] DistanceBase =
        [
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
        ];

        private static readonly int[] DistanceExtra =
        [
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
        ];

        private static readonly int[] CodeLengthOrder =
        [
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
        ];

        private static readonly CanonicalHuffman FixedLiterals = BuildFixedLiterals();
        private static readonly CanonicalHuffman FixedDistances = BuildFixedDistances();

        private readonly byte[] window = new byte[WindowSize];
        private int windowPosition;
        private long totalProduced;

        /// <summary>
        /// Decodes one cabinet data block into <paramref name="destination"/>.
        /// </summary>
        /// <returns>Number of bytes written (always equal to the destination length on success).</returns>
        /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
        public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            BitReader reader = new(source);
            int outPosition = 0;

            while (true)
            {
                bool isFinalBlock = reader.ReadBits(1) != 0;
                int blockType = reader.ReadBits(2);

                switch (blockType)
                {
                    case 0:
                        {
                            reader.AlignToByte();
                            int length = reader.ReadBits(16);
                            int complement = reader.ReadBits(16);
                            if ((length ^ 0xFFFF) != complement)
                            {
                                throw new InvalidDataException("Invalid stored block length in DEFLATE stream.");
                            }

                            if (outPosition + length > destination.Length)
                            {
                                throw new InvalidDataException("DEFLATE stored block overruns the output buffer.");
                            }

                            for (int i = 0; i < length; i++)
                            {
                                Store((byte)reader.ReadBits(8), destination, ref outPosition);
                            }

                            break;
                        }

                    case 1:
                        InflateHuffmanBlock(ref reader, destination, ref outPosition, FixedLiterals, FixedDistances);
                        break;

                    case 2:
                        {
                            ReadDynamicTables(ref reader, out CanonicalHuffman literals, out CanonicalHuffman distances);
                            InflateHuffmanBlock(ref reader, destination, ref outPosition, literals, distances);
                            break;
                        }

                    default:
                        throw new InvalidDataException("Invalid DEFLATE block type.");
                }

                if (isFinalBlock)
                {
                    return outPosition;
                }
            }
        }

        private static void ReadDynamicTables(ref BitReader reader, out CanonicalHuffman literals, out CanonicalHuffman distances)
        {
            int literalCount = reader.ReadBits(5) + 257;
            int distanceCount = reader.ReadBits(5) + 1;
            int codeLengthCount = reader.ReadBits(4) + 4;

            Span<byte> codeLengthLengths = stackalloc byte[19];
            for (int i = 0; i < codeLengthCount; i++)
            {
                codeLengthLengths[CodeLengthOrder[i]] = (byte)reader.ReadBits(3);
            }

            CanonicalHuffman codeLengths = CanonicalHuffman.Build(codeLengthLengths)
                ?? throw new InvalidDataException("Invalid code length code in DEFLATE stream.");

            int total = literalCount + distanceCount;
            Span<byte> lengths = stackalloc byte[286 + 30];
            int position = 0;

            while (position < total)
            {
                int symbol = DecodeSymbol(ref reader, codeLengths);

                if (symbol < 16)
                {
                    lengths[position++] = (byte)symbol;
                    continue;
                }

                int repeat;
                byte value;
                if (symbol == 16)
                {
                    if (position == 0)
                    {
                        throw new InvalidDataException("DEFLATE repeat code with no previous length.");
                    }

                    repeat = reader.ReadBits(2) + 3;
                    value = lengths[position - 1];
                }
                else if (symbol == 17)
                {
                    repeat = reader.ReadBits(3) + 3;
                    value = 0;
                }
                else if (symbol == 18)
                {
                    repeat = reader.ReadBits(7) + 11;
                    value = 0;
                }
                else
                {
                    throw new InvalidDataException("Invalid code length symbol in DEFLATE stream.");
                }

                if (position + repeat > total)
                {
                    throw new InvalidDataException("DEFLATE code length repeat overruns the table.");
                }

                for (int i = 0; i < repeat; i++)
                {
                    lengths[position++] = value;
                }
            }

            literals = CanonicalHuffman.Build(lengths[..literalCount])
                ?? throw new InvalidDataException("Invalid literal/length code in DEFLATE stream.");
            distances = CanonicalHuffman.Build(lengths.Slice(literalCount, distanceCount))
                ?? throw new InvalidDataException("Invalid distance code in DEFLATE stream.");
        }

        private void InflateHuffmanBlock(
            ref BitReader reader,
            Span<byte> output,
            ref int outPosition,
            CanonicalHuffman literals,
            CanonicalHuffman distances)
        {
            while (true)
            {
                int symbol = DecodeSymbol(ref reader, literals);

                if (symbol < 256)
                {
                    Store((byte)symbol, output, ref outPosition);
                    continue;
                }

                if (symbol == 256)
                {
                    return;
                }

                int lengthIndex = symbol - 257;
                if (lengthIndex >= LengthBase.Length)
                {
                    throw new InvalidDataException("Invalid length symbol in DEFLATE stream.");
                }

                int length = LengthBase[lengthIndex] + reader.ReadBits(LengthExtra[lengthIndex]);

                int distanceSymbol = DecodeSymbol(ref reader, distances);
                if (distanceSymbol >= DistanceBase.Length)
                {
                    throw new InvalidDataException($"Invalid distance symbol {distanceSymbol} in DEFLATE stream.");
                }

                int distance = DistanceBase[distanceSymbol] + reader.ReadBits(DistanceExtra[distanceSymbol]);

                if (distance > totalProduced)
                {
                    throw new InvalidDataException($"DEFLATE match distance exceeds the produced data (distance {distance}, produced {totalProduced}).");
                }

                CopyMatch(distance, length, output, ref outPosition);
            }
        }

        private void Store(byte value, Span<byte> output, ref int outPosition)
        {
            if (outPosition >= output.Length)
            {
                throw new InvalidDataException("DEFLATE block overruns the output buffer.");
            }

            output[outPosition++] = value;
            window[windowPosition] = value;
            windowPosition = (windowPosition + 1) & WindowMask;
            totalProduced++;
        }

        private void CopyMatch(int distance, int length, Span<byte> output, ref int outPosition)
        {
            if (outPosition + length > output.Length)
            {
                throw new InvalidDataException("DEFLATE block overruns the output buffer.");
            }

            int source = (windowPosition - distance) & WindowMask;
            for (int i = 0; i < length; i++)
            {
                byte value = window[source];
                source = (source + 1) & WindowMask;
                output[outPosition++] = value;
                window[windowPosition] = value;
                windowPosition = (windowPosition + 1) & WindowMask;
            }

            totalProduced += length;
        }

        private static int DecodeSymbol(ref BitReader reader, CanonicalHuffman table)
        {
            if (table.IsEmpty)
            {
                throw new InvalidDataException("DEFLATE stream references an empty Huffman table.");
            }

            int code = 0;
            int first = 0;
            int index = 0;

            for (int length = 1; length <= 15; length++)
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

            throw new InvalidDataException("Invalid Huffman code in DEFLATE stream.");
        }

        private static CanonicalHuffman BuildFixedLiterals()
        {
            byte[] lengths = new byte[288];
            for (int i = 0; i < 144; i++)
            {
                lengths[i] = 8;
            }

            for (int i = 144; i < 256; i++)
            {
                lengths[i] = 9;
            }

            for (int i = 256; i < 280; i++)
            {
                lengths[i] = 7;
            }

            for (int i = 280; i < 288; i++)
            {
                lengths[i] = 8;
            }

            return CanonicalHuffman.Build(lengths);
        }

        private static CanonicalHuffman BuildFixedDistances()
        {
            byte[] lengths = new byte[30];
            for (int i = 0; i < lengths.Length; i++)
            {
                lengths[i] = 5;
            }

            return CanonicalHuffman.Build(lengths);
        }

        /// <summary>LSB-first bit reader as required by DEFLATE.</summary>
        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> source;
            private int position;
            private ulong buffer;
            private int bitCount;

            internal BitReader(ReadOnlySpan<byte> data)
            {
                source = data;
                position = 0;
                buffer = 0;
                bitCount = 0;
            }

            internal int ReadBit()
            {
                return ReadBits(1);
            }

            internal int ReadBits(int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                while (bitCount < count)
                {
                    if (position >= source.Length)
                    {
                        throw new InvalidDataException("DEFLATE stream ended unexpectedly.");
                    }

                    buffer |= (ulong)source[position++] << bitCount;
                    bitCount += 8;
                }

                int value = (int)(buffer & ((1UL << count) - 1));
                buffer >>= count;
                bitCount -= count;
                return value;
            }

            internal void AlignToByte()
            {
                int drop = bitCount % 8;
                buffer >>= drop;
                bitCount -= drop;
            }
        }
    }
}
