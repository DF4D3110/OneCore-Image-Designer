using System.Text;
using BlockCompressor;
using CSharpFunctionalExtensions;
using DotnetPackaging.Msix.Core.BlockMap;
using DotnetPackaging.Msix.Core.Compression;
using DotnetPackaging.Msix.Core.ContentTypes;
using Zafiro.Mixins;
using Zafiro.Reactive;

namespace DotnetPackaging.Msix.Core;

public class MsixPackager(Maybe<ILogger> logger)
{
    private static readonly HashSet<string> NonCompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "appx",
        "avi",
        "cab",
        "docm",
        "docx",
        "dotm",
        "dotx",
        "gif",
        "gz",
        "jpeg",
        "jpg",
        "m4a",
        "mov",
        "mp3",
        "mpeg",
        "mpg",
        "png",
        "potm",
        "potx",
        "ppam",
        "ppsm",
        "ppsx",
        "pptm",
        "pptx",
        "rar",
        "tar",
        "wma",
        "wmv",
        "xap",
        "xlam",
        "xlsb",
        "xlsm",
        "xlsx",
        "xltm",
        "xltx",
        "zip"
    };

    private static readonly IComparer<INamedByteSourceWithPath> FileOrderingComparer =
        Comparer<INamedByteSourceWithPath>.Create(CompareFiles);

    public Result<IByteSource> Pack(IContainer container, bool bundleMode, bool unsignedMode, string inputPath)
    {
        IEnumerable<INamedByteSourceWithPath> files = container.ResourcesWithPathsRecursive();

        files = files.Where(file =>
        {
            if (file.Name.Equals("[Content_Types].xml"))
                return false;

            if (unsignedMode)
            {
                if (file.Name.Equals("AppxSignature.p7x"))
                    return false;

                if (file.Path.Value.Equals("AppxMetadata") && file.Name.Equals("CodeIntegrity.cat"))
                    return false;
            }

            return true;
        });

        return Result.Success()
            .Map(() => Compress(files, bundleMode, unsignedMode, inputPath));
    }

    private IByteSource Compress(IEnumerable<INamedByteSourceWithPath> files, bool bundleMode, bool unsignedMode, string inputPath)
    {
        return ByteSource.FromAsyncStreamFactory(() => GetStream([.. files], bundleMode, unsignedMode, inputPath));
    }

    private async Task<Stream> GetStream(IList<INamedByteSourceWithPath> files, bool bundleMode, bool unsignedMode, string inputPath)
    {
        MemoryStream zipStream = new();
        List<INamedByteSourceWithPath> orderedFiles = [.. files.OrderBy(file => file, FileOrderingComparer)];

        await using (MsixBuilder zipper = new(zipStream, logger, inputPath))
        {
            await WritePayload(orderedFiles, zipper, bundleMode);
            await WriteContentTypes(orderedFiles, zipper, bundleMode);

            if (!unsignedMode && !bundleMode)
                await AddAppxMetadata(zipper, files);

            if (!unsignedMode)
                await AddAppxSignature(zipper, files);
        }

        MemoryStream finalStream = new();
        zipStream.Position = 0;
        await zipStream.CopyToAsync(finalStream);

        finalStream.Position = 0;
        return finalStream;
    }

    private static async Task WriteContentTypes(IEnumerable<INamedByteSourceWithPath> files, MsixBuilder msix, bool bundleMode)
    {
        IEnumerable<string> allFileNames = files.Select(x => x.Name);
        if (!allFileNames.Contains("AppxBlockMap.xml"))
        {
            allFileNames.Append("AppxBlockMap.xml");
        }

        ContentTypesModel contentTypes = ContentTypesGenerator.Create(allFileNames, bundleMode);
        string xml = ContentTypesSerializer.Serialize(contentTypes);
        await msix.PutNextEntry(MsixEntryFactory.Compress("[Content_Types].xml", ByteSource.FromString(xml, Encoding.UTF8)));
    }

    private async Task WritePayload(IEnumerable<INamedByteSourceWithPath> files, MsixBuilder msix, bool bundleMode)
    {
        List<FileBlockInfo> blockInfos = [];

        foreach (INamedByteSourceWithPath file in files)
        {
            if (file.Name.Equals("AppxBlockMap.xml"))
                continue;

            if (file.Name.Equals("AppxSignature.p7x"))
                continue;

            if (file.Path.Value.Equals("AppxMetadata") && file.Name.Equals("CodeIntegrity.cat"))
                continue;

            logger.Debug("Processing {File}", file.FullPath());
            MsixEntry entry;
            IList<DeflateBlock> blocks;

            if (ShouldStore(file.Name))
            {
                entry = new MsixEntry()
                {
                    Compressed = file,
                    Original = file,
                    FullPath = file.FullPath(),
                    CompressionLevel = CompressionLevel.NoCompression,
                };

                blocks = await file.Bytes.Flatten().Buffer(64 * 1024).Select(list => new DeflateBlock
                {
                    CompressedData = [.. list],
                    OriginalData = [.. list],
                }).ToList();
            }
            else
            {
                if (MakeAppxDeflate.USE_EXTERNAL_DEFLATE_VIA_MAKEAPPX)
                {
                    IObservable<byte[]> compressionBlocks = MakeAppxDeflate.GetMakeAppxVersionOfDeflate(ByteSource.FromByteObservable(file.Bytes));
                    entry = new MsixEntry
                    {
                        Original = file,
                        Compressed = ByteSource.FromByteObservable(compressionBlocks),
                        FullPath = file.FullPath(),
                        CompressionLevel = CompressionLevel.Optimal
                    };

                    blocks = await file.Bytes.Flatten().Buffer(Compressed.DefaultBlockSize).Select(list => new DeflateBlock
                    {
                        CompressedData = MakeAppxDeflate.GetMakeAppxVersionOfDeflate([.. list]).GetAwaiter().GetResult(),
                        OriginalData = [.. list],
                    }).ToList();
                }
                else
                {
                    IObservable<DeflateBlock> compressionBlocks = file.Bytes.CompressionBlocks();
                    entry = new MsixEntry
                    {
                        Original = file,
                        Compressed = ByteSource.FromByteObservable(compressionBlocks.Select(x => x.CompressedData)),
                        FullPath = file.FullPath(),
                        CompressionLevel = CompressionLevel.Optimal,
                    };

                    blocks = await compressionBlocks.ToList();

                    /*IObservable<byte[]> compressionBlocks = ByteSource.FromByteObservable(file.Bytes).Compressed();
                    entry = new MsixEntry
                    {
                        Original = file,
                        Compressed = ByteSource.FromByteObservable(compressionBlocks),
                        FullPath = file.FullPath(),
                        CompressionLevel = CompressionLevel.Optimal
                    };

                    blocks = await file.Bytes.Flatten().Buffer(Compressed.DefaultBlockSize).Select(list => new DeflateBlock
                    {
                        CompressedData = ByteSource.FromBytes([.. list]).Bytes.Compressed().Array(),
                        OriginalData = [.. list],
                    }).ToList();*/
                }
            }

            await msix.PutNextEntry(entry);

            logger.Debug("Added entry for {File}", file.FullPath());

            if (!bundleMode || !file.FullPath().Value.EndsWith("appx"))
            {
                FileBlockInfo fileBlockInfo = new(entry, blocks);
                blockInfos.Add(fileBlockInfo);
            }
        }

        await AddBlockMap(msix, blockInfos, files);
    }

    private static bool ShouldStore(string fileName)
    {
        string extension = System.IO.Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return NonCompressibleExtensions.Contains(extension.TrimStart('.'));
    }

    private static int FileDepth(INamedByteSourceWithPath file)
    {
        string fullPath = file.FullPath().ToString().Replace('\\', '/');
        int depth = 0;
        foreach (char ch in fullPath)
        {
            if (ch == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    private static string FileExtension(INamedByteSourceWithPath file)
    {
        string extension = System.IO.Path.GetExtension(file.Name);
        return string.IsNullOrEmpty(extension) ? string.Empty : extension.TrimStart('.');
    }

    private static int CompareFiles(INamedByteSourceWithPath? left, INamedByteSourceWithPath? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int depthComparison = FileDepth(right).CompareTo(FileDepth(left));
        if (depthComparison != 0)
        {
            return depthComparison;
        }

        int extensionComparison = string.Compare(FileExtension(left), FileExtension(right), StringComparison.OrdinalIgnoreCase);
        if (extensionComparison != 0)
        {
            return extensionComparison;
        }

        return CompareNatural(left.FullPath().ToString(), right.FullPath().ToString());
    }

    private static int CompareNatural(string left, string right)
    {
        int indexLeft = 0;
        int indexRight = 0;

        while (indexLeft < left.Length && indexRight < right.Length)
        {
            char charLeft = left[indexLeft];
            char charRight = right[indexRight];

            if (char.IsDigit(charLeft) && char.IsDigit(charRight))
            {
                long numberLeft = 0;
                while (indexLeft < left.Length && char.IsDigit(left[indexLeft]))
                {
                    numberLeft = numberLeft * 10 + (left[indexLeft] - '0');
                    indexLeft++;
                }

                long numberRight = 0;
                while (indexRight < right.Length && char.IsDigit(right[indexRight]))
                {
                    numberRight = numberRight * 10 + (right[indexRight] - '0');
                    indexRight++;
                }

                if (numberLeft != numberRight)
                {
                    return numberLeft.CompareTo(numberRight);
                }
            }
            else
            {
                int comparison = char.ToUpperInvariant(charLeft).CompareTo(char.ToUpperInvariant(charRight));
                if (comparison != 0)
                {
                    return comparison;
                }

                indexLeft++;
                indexRight++;
            }
        }

        return (left.Length - indexLeft).CompareTo(right.Length - indexRight);
    }

    private async Task AddBlockMap(MsixBuilder msix, List<FileBlockInfo> blockInfos, IEnumerable<INamedByteSourceWithPath> files)
    {
        IObservable<byte[]> blockMapXml = files.First(t => t.Name.Equals("AppxBlockMap.xml")).Bytes;

        logger.Debug("Adding Block Map entry to package");
        await msix.PutNextEntry(MsixEntryFactory.Compress("AppxBlockMap.xml", ByteSource.FromByteObservable(blockMapXml)));

        logger.Debug("Block map added");
    }

    private async Task AddAppxMetadata(MsixBuilder msix, IEnumerable<INamedByteSourceWithPath> files)
    {
        if (!files.Any(t => t.Path.Value.Equals("AppxMetadata") && t.Name.Equals("CodeIntegrity.cat")))
        {
            return;
        }

        IObservable<byte[]> AppxMetadata = files.First(t => t.Path.Value.Equals("AppxMetadata") && t.Name.Equals("CodeIntegrity.cat")).Bytes;

        logger.Debug("Adding AppxMetadata entry to package");
        await msix.PutNextEntry(MsixEntryFactory.Compress("AppxMetadata/CodeIntegrity.cat", ByteSource.FromByteObservable(AppxMetadata)));

        logger.Debug("AppxMetadata added");
    }

    private async Task AddAppxSignature(MsixBuilder msix, IEnumerable<INamedByteSourceWithPath> files)
    {
        if (!files.Any(t => t.Name.Equals("AppxSignature.p7x")))
        {
            return;
        }

        IObservable<byte[]> AppxSignature = files.First(t => t.Name.Equals("AppxSignature.p7x")).Bytes;

        logger.Debug("Adding AppxSignature.p7x entry to package");
        await msix.PutNextEntry(MsixEntryFactory.Compress("AppxSignature.p7x", ByteSource.FromByteObservable(AppxSignature)));

        logger.Debug("AppxSignature.p7x added");
    }
}