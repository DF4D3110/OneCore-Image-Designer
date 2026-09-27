using CSharpFunctionalExtensions;
using DotnetPackaging.Msix.Core;
using DotnetPackaging.Msix.Core.Manifest;

namespace DotnetPackaging.Msix;

public class Msix
{
    public static Result<IByteSource> FromDirectory(IContainer container, Maybe<ILogger> logger, bool bundleMode, bool unsignedMode, string inputPath)
    {
        return new MsixPackager(logger).Pack(container, bundleMode, unsignedMode, inputPath);
    }

    public static Result<IByteSource> FromDirectoryAndMetadata(IContainer container, AppManifestMetadata metadata, Maybe<ILogger> logger, bool bundleMode, bool unsignedMode, string inputPath)
    {
        string generateAppManifest = AppManifestGenerator.GenerateAppManifest(metadata);
        Resource appxManifiest = new("AppxManifest.xml", ByteSource.FromString(generateAppManifest));
        RootContainer finalContainer = new(container.Resources.Append(appxManifiest), container.Subcontainers);
        return FromDirectory(finalContainer, logger, bundleMode, unsignedMode, inputPath);
    }
}