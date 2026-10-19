using System;
using System.IO;

namespace OcidCabinet.Delta
{
    /// <summary>Canonical Huffman code lengths plus the bit-reversed codes msdelta uses.</summary>
    internal sealed class Codes
    {
        public Codes(byte[] lengths)
        {
            Lengths = (byte[])lengths.Clone();
            Count = lengths.Length;
            MaxLength = 0;
            foreach (byte length in lengths)
            {
                if (length > MaxLength)
                    MaxLength = length;
            }

            Reserved = new uint[Count];
            Calculate();
        }

        public byte[] Lengths { get; }

        public int Count { get; }

        public int MaxLength { get; }

        public uint[] Reserved { get; }

        private void Calculate()
        {
            var counts = new int[MaxLength + 1];
            foreach (byte length in Lengths)
            {
                if (length != 0)
                    counts[length]++;
            }

            int remaining = 2;
            for (int length = 1; length <= MaxLength; length++)
            {
                if (counts[length] > remaining)
                    throw new InvalidDataException("PA30: invalid Huffman code lengths");
                remaining = 2 * (remaining - counts[length]);
            }

            var start = new int[MaxLength + 1];
            int current = 0;
            for (int length = MaxLength; length > 0; length--)
            {
                start[length] = current;
                current = (counts[length] + current) >> 1;
            }

            for (int symbol = 0; symbol < Count; symbol++)
            {
                int length = Lengths[symbol];
                if (length == 0)
                    continue;
                int value = start[length]++;
                uint code = 0;
                for (int bit = 0; bit < length; bit++)
                {
                    code = (uint)(((value & 1)) | (code << 1));
                    value >>= 1;
                }

                Reserved[symbol] = code;
            }
        }
    }

    /// <summary>Lookup table keyed by the lowest set bit of the peeked window.</summary>
    internal sealed class DecoderTable
    {
        private readonly int[] _base;
        private readonly int[] _mask;
        private readonly int[] _symbols;

        public DecoderTable(Codes codes)
        {
            byte[] lengths = codes.Lengths;
            uint[] reserved = codes.Reserved;
            int count = codes.Count;

            var width = new int[32];
            int effective = count;
            int maxLow = 0;
            for (int symbol = 0; symbol < count; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                    continue;
                uint code = reserved[symbol];
                if (code != 0)
                {
                    int low = DeltaBits.LowestSetBit(code);
                    if (low >= 32)
                        throw new InvalidDataException("PA30: bad Huffman code");
                    int extra = length - low - 1;
                    if (extra < 0)
                        throw new InvalidDataException("PA30: bad Huffman code length");
                    if (low > maxLow)
                        maxLow = low;
                    if (width[low] < extra)
                        width[low] = extra;
                }
                else
                {
                    if (effective != count)
                        throw new InvalidDataException("PA30: duplicate all-zero Huffman code");
                    effective = symbol;
                }
            }

            int maxBits = maxLow + 1;
            _base = new int[32];
            _mask = new int[32];
            int total = 0;
            for (int low = 0; low < maxBits; low++)
            {
                _mask[low] = (1 << width[low]) - 1;
                _base[low] = total;
                total += 1 << width[low];
            }

            for (int low = maxBits; low < 32; low++)
            {
                _mask[low] = 0;
                _base[low] = total;
            }

            _symbols = new int[total + 1];
            for (int symbol = 0; symbol < count; symbol++)
            {
                int length = lengths[symbol];
                uint code = reserved[symbol];
                if (length == 0 || code == 0)
                    continue;
                int low = DeltaBits.LowestSetBit(code);
                int stride = 1 << (length - low - 1);
                int index = _base[low] + (int)(code >> (low + 1));
                int repeats = 1 << (width[low] - (length - low - 1));
                for (int repetition = 0; repetition < repeats; repetition++)
                {
                    _symbols[index] = symbol;
                    index += stride;
                }
            }

            if (count > effective)
                _symbols[total] = effective;
        }

        public int Decode(BitReader reader)
        {
            uint peek = (reader.Peek32() | 0x80000000u);
            int low = DeltaBits.LowestSetBit(peek);
            return _symbols[_base[low] + (int)((peek >> (low + 1)) & (uint)_mask[low])];
        }
    }
}
