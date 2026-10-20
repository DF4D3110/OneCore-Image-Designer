using System;
using System.Collections.Generic;
using System.IO;

namespace OcidCabinet.Delta
{
    /// <summary>
    /// One pseudo-LZX compression format: a 600-entry main alphabet (256 literals plus the
    /// match slots), a 256-entry length alphabet and a 16-entry aligned-offset alphabet.
    /// </summary>
    internal sealed class CompressionFormat
    {
        public CompressionFormat(byte[] mainLengths, byte[] lengthLengths, byte[] alignedLengths)
        {
            MainLengths = (byte[])mainLengths.Clone();
            LengthLengths = (byte[])lengthLengths.Clone();
            AlignedLengths = (byte[])alignedLengths.Clone();
            MainTable = new DecoderTable(new Codes(MainLengths));
            LengthTable = new DecoderTable(new Codes(LengthLengths));
            AlignedTable = new DecoderTable(new Codes(AlignedLengths));
        }

        public byte[] MainLengths { get; }

        public byte[] LengthLengths { get; }

        public byte[] AlignedLengths { get; }

        public DecoderTable MainTable { get; }

        public DecoderTable LengthTable { get; }

        public DecoderTable AlignedTable { get; }

        /// <summary>Default format used when <c>CompositeFormat</c> selects the single-format path.</summary>
        public static CompressionFormat CreateDefault()
        {
            var main = new byte[600];
            Array.Fill(main, (byte)9, 0, 424);
            Array.Fill(main, (byte)10, 424, 176);
            var lengths = new byte[256];
            Array.Fill(lengths, (byte)8);
            var aligned = new byte[16];
            Array.Fill(aligned, (byte)4);
            return new CompressionFormat(main, lengths, aligned);
        }

        /// <summary>Decodes one symbol; returns the distance marker and the copy length (1 = literal).</summary>
        public (uint Marker, int Length) ReadSymbol(BitReader reader)
        {
            int symbol = Decode(reader, MainTable, MainLengths);
            if (symbol < 0x100)
                return ((uint)symbol, 1);

            int value = symbol - 0x100;
            int slot = value >> 3;
            int lengthCode = value & 7;
            uint marker;
            if (slot < 3)
            {
                if (slot == 0)
                {
                    marker = (reader.Read(14) - 0x2000u + 0x2A000u);
                }
                else if (slot == 1)
                {
                    uint v = reader.Read(16) - 0x8000u;
                    marker = (v < 0x8000 ? v + 0x2000u : v - 0x2000u) + 0x2A000u;
                }
                else
                {
                    uint v = reader.Read(18) - 0x20000u;
                    marker = v < 0x20000 ? v + 0x34000u : (v - 40960u) + 0x2A000u;
                }
            }
            else if (slot < 7)
            {
                marker = 0x54000u + (uint)(slot - 3);
            }
            else
            {
                int v21 = slot - 7;
                if (v21 == 0)
                {
                    if (reader.Read(1) != 0)
                        v21 = reader.Read(1) != 0 ? (int)reader.Read(4) + 48 : (int)reader.Read(3) + 40;
                    else
                        v21 = (int)reader.Read(2) + 36;
                }

                if (v21 < 4)
                {
                    marker = 0x54003u + (uint)v21;
                }
                else
                {
                    int v22 = (v21 >> 1) - 1;
                    int v23 = (v21 & 1) + 2;
                    if (v22 >= 4)
                    {
                        int v24 = (v21 >> 1) - 5;
                        if ((v21 >> 1) != 5)
                            v23 = (int)reader.Read(v24) | (v23 << v24);
                        int aligned = Decode(reader, AlignedTable, AlignedLengths);
                        v21 = aligned | (16 * v23);
                    }
                    else
                    {
                        v21 = (int)reader.Read(v22) | (v23 << v22);
                    }

                    if ((uint)v21 > 0xFFFABFFCu)
                        throw new InvalidDataException("PA30: invalid match distance");
                    marker = (uint)v21 + 344067u;
                }
            }

            if (lengthCode != 0)
                return (marker, lengthCode + 1);

            int symbol2 = Decode(reader, LengthTable, LengthLengths);
            if (symbol2 == 0)
            {
                ulong extended = reader.ReadNumber();
                if (extended > 0xFFFFFFF7UL)
                    throw new InvalidDataException("PA30: invalid match length");
                symbol2 = (int)extended;
            }

            return (marker, symbol2 + 8);
        }

        private static int Decode(BitReader reader, DecoderTable table, byte[] lengths)
        {
            int symbol = table.Decode(reader);
            reader.Consume(lengths[symbol]);
            return symbol;
        }
    }

    /// <summary>
    /// The 872 code lengths of one format (600 + 256 + 16), delta-coded against the previous
    /// format with a 39-symbol alphabet.
    /// </summary>
    internal sealed class CompressionLengths
    {
        public CompressionLengths(BitReader reader, Codes codes, DecoderTable table, byte[] previous)
        {
            var lengths = new byte[872];
            int index = 0;
            while (index < 872)
            {
                int symbol = table.Decode(reader);
                reader.Consume(codes.Lengths[symbol]);
                if (symbol < 0x11)
                {
                    lengths[index++] = (byte)symbol;
                }
                else if (symbol < 0x17)
                {
                    int delta = symbol - 0x11;
                    int prior = previous[index];
                    int value = delta < 3 ? prior + delta + 1 : prior - delta + 2;
                    if (value < 0 || value > 16 || (delta < 3 ? value <= prior : value >= prior))
                        throw new InvalidDataException("PA30: bad code length delta");
                    lengths[index++] = (byte)value;
                }
                else
                {
                    int v19 = symbol - 0x17;
                    int bits = v19 & 7;
                    int count = bits >= 3 ? (1 << (bits - 1)) | (int)reader.Read(bits - 1) : bits + 1;
                    if (index + count > 872)
                        throw new InvalidDataException("PA30: code length run overruns the table");
                    if (v19 < 8)
                    {
                        if (index == 0)
                            throw new InvalidDataException("PA30: code length run at offset zero");
                        for (int k = 0; k < count; k++)
                            lengths[index + k] = lengths[index - 1 + k];
                    }
                    else
                    {
                        for (int k = 0; k < count; k++)
                            lengths[index + k] = previous[index + k];
                    }

                    index += count;
                }
            }

            Lengths = lengths;
        }

        public byte[] Lengths { get; }
    }

    /// <summary>One or more <see cref="CompressionFormat"/> instances switched by output position.</summary>
    internal sealed class CompositeFormat
    {
        private readonly List<CompressionFormat> _formats = new();
        private readonly List<long> _bounds = new() { long.MaxValue };

        public CompositeFormat(BitReader reader)
        {
            if (reader.Read(1) != 0)
            {
                _formats.Add(CompressionFormat.CreateDefault());
                return;
            }

            int count = (int)reader.ReadBit();

            long accumulated = 0;
            var bounds = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                accumulated += (long)reader.ReadBit();
                bounds.Add(accumulated);
            }

            _bounds = bounds;

            var codeLengths = new byte[39];
            for (int i = 0; i < 39; i++)
                codeLengths[i] = (byte)reader.Read(4);
            var codes = new Codes(codeLengths);
            var table = new DecoderTable(codes);

            var previous = new byte[872];
            for (int i = 0; i < count; i++)
            {
                var lengths = new CompressionLengths(reader, codes, table, previous);
                var main = new byte[600];
                var lengthLengths = new byte[256];
                var aligned = new byte[16];
                Array.Copy(lengths.Lengths, 0, main, 0, 600);
                Array.Copy(lengths.Lengths, 600, lengthLengths, 0, 256);
                Array.Copy(lengths.Lengths, 856, aligned, 0, 16);
                _formats.Add(new CompressionFormat(main, lengthLengths, aligned));
                previous = lengths.Lengths;
            }
        }

        /// <summary>
        /// Mirrors <c>CompositeFormat::GetCompressionFormatRead</c>: the last format whose cumulative
        /// bound is &lt;= position wins (each bound is the start of the next format), and the final
        /// format extends to the end of the buffer. Positions are unified-buffer offsets
        /// (source bytes first, then the target), not target-relative ones.
        /// </summary>
        public CompressionFormat FormatAt(long position)
        {
            int index = 0;
            for (int i = 0; i < _bounds.Count; i++)
            {
                if (_bounds[i] <= position)
                    index = i;
            }

            return _formats[index];
        }
    }
}
