using DotnetPackaging.Msix.Core.Compression;

namespace DotnetPackaging.Msix.Core;

public static class MsixEntryFactory
{
    public static MsixEntry Compress(string entryName, IByteSource data)
    {
        if (MakeAppxDeflate.USE_EXTERNAL_DEFLATE_VIA_MAKEAPPX)
        {
            CompressionLevel compressionLevel = CompressionLevel.Optimal;

            MsixEntry msixEntry = new()
            {
                Original = data,
                Compressed = ByteSource.FromByteObservable(MakeAppxDeflate.GetMakeAppxVersionOfDeflate(data)),
                FullPath = entryName,
                CompressionLevel = compressionLevel
            };

            return msixEntry;
        }
        else
        {
            CompressionLevel compressionLevel = CompressionLevel.Optimal;

            MsixEntry msixEntry = new()
            {
                Original = data,
                Compressed = ByteSource.FromByteObservable(data.Bytes.Compressed()),
                FullPath = entryName,
                CompressionLevel = compressionLevel,
            };

            return msixEntry;
        }
    }
}