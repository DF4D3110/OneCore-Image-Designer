using System;
using System.IO;

namespace OcidCabinet.Delta
{
    /// <summary>
    /// LSB-first bit reader matching <c>compo::BitReaderObject</c> from msdelta.dll.
    /// </summary>
    /// <remarks>
    /// Values are consumed from the low bits of a little-endian 32-bit window: every
    /// <c>Read(n)</c> takes the low <c>n</c> bits of the window and shifts it right. The
    /// reader opened by <c>BitReaderOpen</c> always consumes three bits first, which the
    /// initial <paramref name="skipBits"/> argument reproduces.
    /// </remarks>
    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private int _position;

        public BitReader(byte[] data, int skipBits = 0)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _position = skipBits;
        }

        public int Position => _position;

        public ulong Peek(int bytes)
        {
            ulong value = 0;
            int start = _position >> 3;
            for (int i = 0; i < bytes; i++)
            {
                int index = start + i;
                ulong b = index < _data.Length ? _data[index] : 0UL;
                value |= b << (8 * i);
            }

            int offset = _position & 7;
            return offset == 0 ? value : value >> offset;
        }

        public uint Peek32() => (uint)Peek(5);

        public uint Read(int bits)
        {
            if (bits == 0)
                return 0;
            if (bits < 0 || bits > 32)
                throw new InvalidOperationException($"bit read of {bits} bits is not supported");

            uint value = (uint)(Peek(5) & ((1UL << bits) - 1));
            _position += bits;
            return value;
        }

        public void Consume(int bits) => _position += bits;

        public void AlignToByte() => _position = (_position + 7) & ~7;

        public ulong ReadBit()
        {
            uint peek = Peek32();
            int z = DeltaBits.LowestSetBit(peek | 0x10000u);
            if (z == 16)
                throw new InvalidDataException("PA30: invalid variable-length integer prefix");
            _position += z + 1;
            if (z >= 8)
            {
                ulong low = Read(32);
                ulong high = Read(4 * z - 28);
                return low | (high << 32);
            }

            return Read(4 * (z + 1));
        }

        public ulong ReadNumber()
        {
            uint peek = Peek32();
            if (peek == 0)
                throw new InvalidDataException("PA30: zero prefix in ReadNumber");
            int k = DeltaBits.LowestSetBit(peek);
            if (31 - k < 8)
                throw new InvalidDataException("PA30: invalid ReadNumber prefix");
            int n = k + 8;
            _position += k + 1;
            return (1UL << n) | Read(n);
        }

        public byte[] ReadBuffer()
        {
            ulong length = ReadBit();
            AlignToByte();
            int start = _position >> 3;
            if (length > (ulong)(_data.Length - start))
                throw new InvalidDataException("PA30: buffer length exceeds the delta payload");
            _position += (int)length * 8;
            var result = new byte[(int)length];
            Array.Copy(_data, start, result, 0, (int)length);
            return result;
        }
    }

    internal static class DeltaBits
    {
        /// <summary>Index of the lowest set bit, or 32 when the value is zero.</summary>
        public static int LowestSetBit(uint value)
        {
            if (value == 0)
                return 32;
            int index = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                index++;
            }

            return index;
        }

        public static int LowestSetBit(ulong value)
        {
            if (value == 0)
                return 64;
            int index = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                index++;
            }

            return index;
        }
    }
}
