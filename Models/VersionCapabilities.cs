using System;
using System.Collections.Generic;

namespace OneCore_Image_Designer.Models
{
    public enum PlatformGeneration
    {
        Unknown,
        IoTCore17763,
        CoreOs20348
    }

    public enum ArtifactKind
    {
        Unknown,
        OEMInput,
        FeatureManifest,
        DeviceLayout,
        IoTWorkspace
    }

    public sealed class VersionCapabilityProfile
    {
        public PlatformGeneration Generation { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string? Namespace { get; init; }
        public bool SupportsOemInputV1 { get; init; }
        public bool SupportsOemInputV2 { get; init; }
        public bool SupportsFeatureGroups { get; init; }
        public bool SupportsConditionalFeatures { get; init; }
        public bool SupportsDeviceTargeting { get; init; }
        public bool SupportsIoTWorkspace { get; init; }
        public bool SupportsStoragePools { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    public sealed class ArtifactScanResult
    {
        public string FilePath { get; init; } = string.Empty;
        public ArtifactKind Kind { get; init; }
        public PlatformGeneration Generation { get; set; }
        public string RootElement { get; init; } = string.Empty;
        public string Namespace { get; init; } = string.Empty;
        public bool IsWellFormed { get; init; }
        public List<string> Features { get; } = new();
        public List<string> Warnings { get; } = new();
        public VersionCapabilityProfile? Profile { get; set; }
    }
}
