using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WPMT.Core.Common;
using static WPMT.Core.Compression.NativeMethods;
using WPMT.Core.Exceptions;
using static WPMT.Core.Compression.Common;

namespace WPMT.Core.Compression;

public static class DeltaCompression
{

    public static readonly FILETIME FileTimeInDeltaHeader = GetFileTime(DateTime.UnixEpoch + TimeSpan.FromSeconds(1));

    [Flags]
    public enum CompressionBufferOptionsFlags
    {
        None = 0,
        InputMutable = 1 << 0,
        ReferenceMutable = 1 << 1
    }

    [Flags]
    public enum DeltaCompressFormatFlags
    {
        Executables = 1 << 0,
        Raw = 1 << 1
    }

    public static byte[] LoadDictionary(string fileName)
    {
        return Resources.LoadResource(
            typeof(DeltaCompression).Namespace ?? throw new CompressionException("Failed to get namespace."),
            fileName);
    }

    public static void DeltaDecompress(
        CompressionBufferOptionsFlags bufferOptionsFlags,
        ref byte[]? reference,
        uint headerSize,
        ref byte[] compressedData,
        out byte[] uncompressedData
    )
    {
        if ((bufferOptionsFlags & ~(CompressionBufferOptionsFlags.InputMutable | CompressionBufferOptionsFlags.ReferenceMutable)) != CompressionBufferOptionsFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOptionsFlags), "Invalid compression options.");
        }

        if (headerSize > compressedData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(headerSize), "Header size cannot be greater than compressed data length.");
        }

        DELTA_INPUT referenceInput = reference is null ? new DELTA_INPUT() : new(
            reference,
            0,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable)
        );
        DELTA_INPUT compressedInput = new(
            compressedData,
            (int)headerSize,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable)
        );

        if (!ApplyDeltaB(
            DeltaFlagType.None,
            referenceInput,
            compressedInput,
            out DELTA_OUTPUT uncompressedOutput))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to apply delta compression.");
        }

        uncompressedData = new byte[uncompressedOutput.uSize.ToUInt32()];
        Marshal.Copy(uncompressedOutput.lpStart, uncompressedData, 0, (int)uncompressedOutput.uSize.ToUInt32());

        if (!DeltaFree(uncompressedOutput.lpStart))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to free delta compression output.");
        }
        /*
        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable))
        {
            reference = [];
        }

        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable))
        {
            compressedData = [];
        }
        */
    }

    public static void DeltaDecompress(
        CompressionBufferOptionsFlags bufferOptionsFlags,
        ref byte[] reference,
        uint headerSize,
        ref byte[] compressedData,
        out byte[] uncompressedData,
        out byte[] reverseData
    )
    {
        if ((bufferOptionsFlags & ~(CompressionBufferOptionsFlags.InputMutable | CompressionBufferOptionsFlags.ReferenceMutable)) != CompressionBufferOptionsFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOptionsFlags), "Invalid compression options.");
        }

        if (headerSize > compressedData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(headerSize), "Header size cannot be greater than compressed data length.");
        }

        DELTA_INPUT referenceInput = new(
            reference,
            0,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable)
        );
        DELTA_INPUT compressedInput = new(
            compressedData,
            (int)headerSize,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable)
        );

        if (!ApplyDeltaGetReverseB(
            DeltaFlagType.None,
            referenceInput,
            compressedInput,
            FileTimeInDeltaHeader,
            out DELTA_OUTPUT uncompressedOutput,
            out DELTA_OUTPUT reverseOutput))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to apply delta compression.");
        }

        uncompressedData = new byte[uncompressedOutput.uSize.ToUInt32()];
        Marshal.Copy(uncompressedOutput.lpStart, uncompressedData, 0, (int)uncompressedOutput.uSize.ToUInt32());

        reverseData = new byte[reverseOutput.uSize.ToUInt32()];
        Marshal.Copy(reverseOutput.lpStart, reverseData, 0, (int)reverseOutput.uSize.ToUInt32());

        if (!DeltaFree(uncompressedOutput.lpStart) || !DeltaFree(reverseOutput.lpStart))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to free delta compression output.");
        }
        /*
        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable))
        {
            reference = [];
        }

        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable))
        {
            compressedData = [];
        }
        */
    }

    public static void DeltaCompress(
        DeltaCompressFormatFlags formatFlags,
        CompressionBufferOptionsFlags bufferOptionsFlags,
        ref byte[]? reference,
        ref byte[] uncompressedData,
        out byte[] compressedData)
    {
        if ((bufferOptionsFlags & ~(CompressionBufferOptionsFlags.InputMutable | CompressionBufferOptionsFlags.ReferenceMutable)) != CompressionBufferOptionsFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOptionsFlags), "Invalid compression options.");
        }

        if (!Enum.IsDefined(formatFlags))
        {
            throw new ArgumentOutOfRangeException(nameof(formatFlags), "Invalid compression format flags.");
        }

        DeltaFileType deltaFileType = formatFlags switch
        {
            DeltaCompressFormatFlags.Raw => DeltaFileType.Raw,
            DeltaCompressFormatFlags.Executables => DeltaFileType.SetExecutablesLatest,
            _ => throw new ArgumentOutOfRangeException(nameof(formatFlags), "Invalid compression format flags.")
        };

        DELTA_INPUT referenceInput = reference is null ? new DELTA_INPUT() : new(
            reference,
            0,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable)
        );
        DELTA_INPUT dataInput = new(
            uncompressedData,
            0,
            bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable)
        );
        // Reserved
        DELTA_INPUT referenceOptions = new();
        DELTA_INPUT dataOptions = new();
        DELTA_INPUT globalOptions = new();

        if (!CreateDeltaB(
            deltaFileType,
            DeltaFlagType.IgnoreFileSizeLimit,
            DeltaFlagType.None,
            referenceInput,
            dataInput,
            referenceOptions,
            dataOptions,
            globalOptions,
            FileTimeInDeltaHeader,
            AlgorithmId.None,
            out DELTA_OUTPUT deltaOutput))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create delta compression.");
        }

        compressedData = new byte[deltaOutput.uSize.ToUInt32()];
        Marshal.Copy(deltaOutput.lpStart, compressedData, 0, (int)deltaOutput.uSize.ToUInt32());

        if (!DeltaFree(deltaOutput.lpStart))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to free delta compression output.");
        }
        /*
        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.ReferenceMutable))
        {
            reference = [];
        }

        if (bufferOptionsFlags.HasFlag(CompressionBufferOptionsFlags.InputMutable))
        {
            uncompressedData = [];
        }
        */
    }

    public static void DeltaCompressWithHeader(
        DeltaCompressFormatFlags formatFlags,
        CompressionBufferOptionsFlags bufferOptionsFlags,
        DeltaCompressionHeader header,
        ref byte[]? reference,
        ref byte[] uncompressedData,
        out byte[] compressedData)
    {
        ArgumentNullException.ThrowIfNull(reference);
        DeltaCompress(
            formatFlags,
            bufferOptionsFlags,
            ref reference,
            ref uncompressedData,
            out byte[] compressedDataWithoutHeader);

        byte[] headerData = header.ToArray();
        compressedData = new byte[headerData.Length + compressedDataWithoutHeader.Length];
        Buffer.BlockCopy(headerData, 0, compressedData, 0, headerData.Length);
        Buffer.BlockCopy(compressedDataWithoutHeader, 0, compressedData, headerData.Length, compressedDataWithoutHeader.Length);
    }
}