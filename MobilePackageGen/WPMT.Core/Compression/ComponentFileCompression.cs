using System.Buffers.Binary;
using System.IO.Hashing;
using WPMT.Core.Exceptions;
using static WPMT.Core.Compression.DeltaCompression;

namespace WPMT.Core.Compression;

public class ComponentFileCompression
{
    public static bool HasPeHeader(byte[] data)
    {
        if (data.Length < 0x40)
            return false;

        if (data[0] != 'M' || data[1] != 'Z')
            return false;

        int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));

        if (offset < 0 || offset > data.Length - 4)
            return false;

        return data[offset] == 'P' && data[offset + 1] == 'E' && data[offset + 2] == 0 && data[offset + 3] == 0;
    }

    private static void PrependChecksum(
        byte[] compressed,
        out byte[] final)
    {
        byte[] crcHeader = Crc32.Hash(compressed);
        final = new byte[crcHeader.Length + compressed.Length];
        Buffer.BlockCopy(crcHeader, 0, final, 0, crcHeader.Length);
        Buffer.BlockCopy(compressed, 0, final, crcHeader.Length, compressed.Length);
    }

    private static bool ValidateChecksum(
        byte[] data)
    {
        if (data.Length < 4)
            return false;
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        uint actual = Crc32.HashToUInt32(data.AsSpan(4));

        return expected == actual;
    }

    public static void DecompressFileBufferDelta(
        ref byte[]? reference,
        ref byte[] compressed,
        out byte[] uncompressed)
    {
        if (!ValidateChecksum(compressed))
            throw new CompressionException("Unmatching checksum detected.");
        DeltaDecompress(
            CompressionBufferOptionsFlags.None,
            ref reference,
            4,
            ref compressed,
            out uncompressed);
    }

    public static void CompressFileBufferDelta(
        ref byte[]? reference,
        ref byte[] data,
        out byte[] final)
    {
        DeltaCompressFormatFlags flags = HasPeHeader(data) ? DeltaCompressFormatFlags.Executables : DeltaCompressFormatFlags.Raw;

        DeltaCompression.DeltaCompress(
            flags,
            CompressionBufferOptionsFlags.None,
            ref reference,
            ref data,
            out byte[] compressed);

        PrependChecksum(compressed, out final);
    }
}