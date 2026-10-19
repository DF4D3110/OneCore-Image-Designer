using System;
using System.IO;

namespace OcidCabinet.Delta
{
    /// <summary>
    /// Adaptive integer alphabet (<c>compo::IntFormat</c>): a 252-entry Huffman table whose
    /// lengths are stored as two short explicit runs plus a run-length-coded middle.
    /// </summary>
    internal sealed class IntFormat
    {
        public IntFormat(BitReader reader)
        {
            int explicitCount = (int)reader.Read(8);
            int runCount = (int)reader.Read(8);
            int runLength = (int)reader.Read(8);
            if (explicitCount > 0x7E || runCount > 0x7E || 252 - runCount - explicitCount < runLength)
                throw new InvalidDataException("PA30: bad integer alphabet header");

            var lengths = new byte[252];
            for (int i = 0; i < explicitCount; i++)
                lengths[i] = ReadLength(reader);

            int index = 0;
            int last = 0;
            while (true)
            {
                last = (int)reader.Read(4);
                if (index >= runCount)
                    break;
                lengths[126 + index] = ReadLengthValue(last);
                index++;
            }

            int value = ReadLengthValue(last);
            int remaining = runLength;
            for (int position = explicitCount; position < 0x7E; position++)
            {
                int current = remaining;
                remaining--;
                if (current == 0)
                {
                    value--;
                    remaining = 252 - position - runCount;
                }

                lengths[position] = (byte)value;
            }

            remaining = runLength;
            for (int position = runCount; position < 0x7E; position++)
            {
                int current = remaining;
                byte digit;
                if (current != 0)
                {
                    digit = (byte)value;
                    remaining = current - 1;
                }
                else
                {
                    digit = (byte)(value - 1);
                    remaining = 126 - position;
                }

                lengths[126 + position] = digit;
                value = digit;
            }

            Lengths = lengths;
            Codes = new Codes(lengths);
            Table = new DecoderTable(Codes);
        }

        public byte[] Lengths { get; }

        public Codes Codes { get; }

        public DecoderTable Table { get; }

        public long ReadNumber(BitReader reader)
        {
            int symbol = Table.Decode(reader);
            reader.Consume(Lengths[symbol]);
            int value = symbol < 0x7E ? symbol : symbol - 0x7E;
            if (value < 4)
                return value;

            int shift = (value >> 1) - 1;
            ulong baseValue = (ulong)(((value & 1) + 2)) << shift;
            ulong extra;
            if (shift > 32)
            {
                ulong low = reader.Read(32);
                ulong high = reader.Read(shift - 32);
                extra = low | (high << 32);
            }
            else
            {
                extra = reader.Read(shift);
            }

            long result = (long)(extra | baseValue);
            return symbol < 0x7E ? result : ~result;
        }

        private static byte ReadLengthValue(int raw)
        {
            int value = raw + 1;
            if (value > 0x10)
                throw new InvalidDataException("PA30: bad integer alphabet length");
            return (byte)value;
        }

        private static byte ReadLength(BitReader reader) => ReadLengthValue((int)reader.Read(4));
    }

    /// <summary>
    /// Source-offset mapping table (<c>compo::RiftTable</c>). Empty for RAW/PA30 payloads
    /// seen so far; the non-empty encoding has not been reproduced yet.
    /// </summary>
    internal sealed class RiftTable
    {
        public RiftTable(BitReader reader)
        {
            if (reader.Read(1) == 0)
                return;

            var positions = new IntFormat(reader);
            var lengths = new IntFormat(reader);
            ulong count = reader.ReadBit();
            if (count > 0x0FFFFFFFFFFFFFFFUL)
                throw new InvalidDataException("PA30: bad rift table entry count");

            long threshold = 0;
            long length = 0;
            for (ulong i = 0; i < count; i++)
            {
                threshold += positions.ReadNumber(reader);
                length += lengths.ReadNumber(reader);
                Entries.Add((threshold + length, threshold));
            }
        }

        public System.Collections.Generic.List<(long Threshold, long Delta)> Entries { get; } = new();

        public long DeltaAt(long position)
        {
            if (Entries.Count == 0)
                return 0;

            long delta = 0;
            foreach ((long threshold, long entryDelta) in Entries)
            {
                if (threshold > position)
                    break;
                delta = entryDelta;
            }

            return delta;
        }
    }
}
