using System;

namespace OcidCabinet
{
    /// <summary>
    /// Canonical Huffman decoding table shared by the DEFLATE (MSZIP) and LZX decoders.
    /// Both formats store canonical codes, and in both the code bits appear in the
    /// compressed stream most-significant-bit first, so a single bit-at-a-time walk
    /// over <see cref="LengthCounts"/> / <see cref="Symbols"/> decodes either one.
    /// </summary>
    internal sealed class CanonicalHuffman
    {
        private const int MaxCodeLength = 16;

        /// <summary>Number of symbols whose code length is exactly the index.</summary>
        internal readonly int[] LengthCounts = new int[MaxCodeLength + 1];

        /// <summary>Symbols ordered by (code length, symbol value).</summary>
        internal readonly ushort[] Symbols;

        /// <summary>True when no symbol has a code at all (only meaningful for optional trees).</summary>
        internal bool IsEmpty { get; }

        private CanonicalHuffman(int[] lengthCounts, ushort[] symbols, bool isEmpty)
        {
            Array.Copy(lengthCounts, LengthCounts, lengthCounts.Length);
            Symbols = symbols;
            IsEmpty = isEmpty;
        }

        /// <summary>
        /// Builds the decoding table. Returns <c>null</c> when the code lengths describe an
        /// over-subscribed or incomplete set, which is invalid data for both formats.
        /// A symbol set where every length is zero yields an empty (but valid) table.
        /// </summary>
        internal static CanonicalHuffman Build(ReadOnlySpan<byte> lengths)
        {
            int[] counts = new int[MaxCodeLength + 1];
            int symbolCount = 0;

            foreach (byte len in lengths)
            {
                if (len == 0)
                {
                    continue;
                }

                if (len > MaxCodeLength)
                {
                    return null;
                }

                counts[len]++;
                symbolCount++;
            }

            if (symbolCount == 0)
            {
                return new CanonicalHuffman(counts, [], true);
            }

            // Reject over-subscribed codes: sum(2^-len) must not exceed 1.
            int available = 1 << MaxCodeLength;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                available -= counts[len] << (MaxCodeLength - len);
                if (available < 0)
                {
                    return null;
                }
            }

            ushort[] symbols = new ushort[symbolCount];
            int index = 0;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                for (int symbol = 0; symbol < lengths.Length; symbol++)
                {
                    if (lengths[symbol] == len)
                    {
                        symbols[index++] = (ushort)symbol;
                    }
                }
            }

            return new CanonicalHuffman(counts, symbols, false);
        }
    }
}
