using System.Runtime.InteropServices.ComTypes;
using WPMT.Core.Exceptions;

namespace WPMT.Core.Compression;

public static class Common
{
    public enum DeltaCompressionType : byte
    {
        None = 0,
        D = 1,
        N = 2,
        H = 3,
        M = 4,
        S = 5,
        X = 6
    }

    public enum ComponentFileCompressionType
    {
        Invalid,
        None,
        Forward,
        Null,
        Reverse
    }

    public readonly struct DeltaCompressionHeader
    {
        public byte SignatureLow { get; }
        public byte SignatureHigh { get; }
        public byte Type { get; }
        public byte Version { get; }

        public DeltaCompressionHeader(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length < 4)
                throw new WPMTException("File is too small to be a valid compressed file.");

            SignatureLow = data[0];
            SignatureHigh = data[1];
            Type = data[2];
            Version = data[3];
        }

        public DeltaCompressionHeader(BinaryReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            if (reader.BaseStream.Length < 4)
                throw new WPMTException("File is too small to be a valid compressed file.");

            SignatureLow = reader.ReadByte();
            SignatureHigh = reader.ReadByte();
            Type = reader.ReadByte();
            Version = reader.ReadByte();
        }

        public DeltaCompressionHeader(DeltaCompressionType type, byte version = 0x01)
        {
            SignatureLow = (byte)'D';
            SignatureHigh = (byte)'C';
            Type = GetDeltaCompressionTypeAsByte(type);
            Version = version;
        }

        public static DeltaCompressionType GetDeltaCompressionType(byte type)
        {
            return type switch
            {
                (byte)'D' => DeltaCompressionType.D,
                (byte)'N' => DeltaCompressionType.N,
                (byte)'H' => DeltaCompressionType.H,
                (byte)'M' => DeltaCompressionType.M,
                (byte)'S' => DeltaCompressionType.S,
                (byte)'X' => DeltaCompressionType.X,
                _ => DeltaCompressionType.None
            };
        }

        public static byte GetDeltaCompressionTypeAsByte(DeltaCompressionType type)
        {
            return type switch
            {
                DeltaCompressionType.D => (byte)'D',
                DeltaCompressionType.N => (byte)'N',
                DeltaCompressionType.H => (byte)'H',
                DeltaCompressionType.M => (byte)'M',
                DeltaCompressionType.S => (byte)'S',
                DeltaCompressionType.X => (byte)'X',
                _ => 0
            };
        }


        public bool IsValid(bool AllowNoneAsValid = false)
        {
            if (SignatureLow != (byte)'D' || SignatureHigh != (byte)'C')
            {
                return false;
            }

            if (Version != 0x01)
            {
                return false;
            }

            return AllowNoneAsValid || GetDeltaCompressionType(Type) != DeltaCompressionType.None;
        }

        public byte[] ToArray()
        {
            return [SignatureLow, SignatureHigh, Type, Version];
        }
    }

    public static DeltaCompressionType GetCompressedFileType(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 4)
            throw new WPMTException("File is too small to be a valid compressed file.");

        DeltaCompressionHeader header = new(data);
        if (header.IsValid())
        {
            return DeltaCompressionHeader.GetDeltaCompressionType(header.Type);
        }

        return DeltaCompressionType.None;
    }

    public static DeltaCompressionType GetCompressedFileType(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.BaseStream.Length < 4)
            throw new WPMTException("File is too small to be a valid compressed file.");

        DeltaCompressionHeader header = new(reader);
        if (header.IsValid(true))
        {
            return DeltaCompressionHeader.GetDeltaCompressionType(header.Type);
        }

        return DeltaCompressionType.None;
    }

    public static FILETIME GetFileTime(DateTime dateTime)
    {
        long fileTimeAsLong = dateTime.ToFileTimeUtc();
        FILETIME fileTime = new()
        {
            dwLowDateTime = (int)(fileTimeAsLong & uint.MaxValue),
            dwHighDateTime = (int)(fileTimeAsLong >> 32)
        };

        return fileTime;
    }

    public static DateTime GetDateTime(FILETIME fileTime)
    {
        long fileTimeAsLong = ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTimeAsLong);
    }
}