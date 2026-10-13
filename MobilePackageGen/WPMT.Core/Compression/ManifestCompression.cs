using System.ComponentModel;
using WPMT.Core.Exceptions;
using static WPMT.Core.Compression.Common;

namespace WPMT.Core.Compression;

public class ManifestCompression
{
    public static readonly byte[] ManifestCompressionDictionary = DeltaCompression.LoadDictionary("ManifestCompressionDictionary");

    public static bool IsManifestCompressed(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return GetCompressedFileType(reader) != DeltaCompressionType.None;
    }

    public static bool IsManifestCompressed(byte[] manifestContents)
    {
        ArgumentNullException.ThrowIfNull(manifestContents);

        return GetCompressedFileType(manifestContents) != DeltaCompressionType.None;
    }

    public static void DecompressManifest(
        DecompressManifestFlags flags,
        ref byte[] manifestContents,
        out DecompressManifestDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(manifestContents);

        if ((flags & ~DecompressManifestFlags.IgnoreManifestCorruption) != DecompressManifestFlags.None)
        {
            throw new ArgumentException("Invalid compression options.");
        }

        disposition = DecompressManifestDisposition.None;

        if (!IsManifestCompressed(manifestContents))
        {
            disposition |= DecompressManifestDisposition.ManifestNotCompressed;
            return;
        }

        DeltaCompression.CompressionBufferOptionsFlags options = DeltaCompression.CompressionBufferOptionsFlags.InputMutable | DeltaCompression.CompressionBufferOptionsFlags.ReferenceMutable;
        // Deep-copy the shared static dictionary: DeltaDecompress with InputMutable|ReferenceMutable
        // may modify the buffer in place, which would corrupt the shared dictionary for subsequent calls.
        byte[]? dictionary = (byte[])ManifestCompressionDictionary.Clone();
        try
        {
            DeltaCompression.DeltaDecompress(options, ref dictionary, 4, ref manifestContents, out byte[] uncompressedData);
            manifestContents = uncompressedData;
        }
        catch (Win32Exception ex)
        {
            if (flags.HasFlag(DecompressManifestFlags.IgnoreManifestCorruption))
            {
                disposition |= DecompressManifestDisposition.ManifestCorrupted;
            }
            else
            {
                throw new CompressionException("Failed to decompress manifest.", ex);
            }
        }
    }

    public static void CompressManifest(
        CompressManifestFlags flags,
        bool isIsolatedSxs,
        ref byte[] manifestContents)
    {
        ArgumentNullException.ThrowIfNull(manifestContents);

        if ((flags & ~CompressManifestFlags.IgnoreFailures) != CompressManifestFlags.None)
        {
            throw new ArgumentException("Invalid compression options.");
        }

        if (IsManifestCompressed(manifestContents))
        {
            if (isIsolatedSxs)
            {
                throw new CompressionException("Manifest is already compressed.");
            }
            return;
        }

        DeltaCompression.DeltaCompressFormatFlags formatFlags = DeltaCompression.DeltaCompressFormatFlags.Raw;
        DeltaCompression.CompressionBufferOptionsFlags options = DeltaCompression.CompressionBufferOptionsFlags.None;
        byte[]? dictionary = (byte[])ManifestCompressionDictionary.Clone();
        DeltaCompressionHeader header = new(DeltaCompressionType.M, 0x01);
        try
        {
            DeltaCompression.DeltaCompressWithHeader(
                formatFlags,
                options,
                header,
                ref dictionary,
                ref manifestContents,
                out byte[] compressedData);
            manifestContents = compressedData;
        }
        catch (Win32Exception ex)
        {
            if (flags.HasFlag(CompressManifestFlags.IgnoreFailures))
            {
                return;
            }

            throw new CompressionException("Failed to compress manifest.", ex);
        }
    }

    [Flags]
    public enum DecompressManifestDisposition
    {
        None = 0,
        ManifestNotCompressed = 1 << 0,
        ManifestCorrupted = 1 << 1
    }

    [Flags]
    public enum DecompressManifestFlags
    {
        None = 0,
        IgnoreManifestCorruption = 1 << 0
    }

    [Flags]
    public enum CompressManifestFlags
    {
        None = 0,
        IgnoreFailures = 1 << 0
    }
}