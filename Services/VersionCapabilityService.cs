using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// Describes version-specific artifact capabilities without changing the source XML.
    /// The profiles intentionally represent observed baselines, not universal schema claims.
    /// </summary>
    public static class VersionCapabilityService
    {
        public const string OemInputV1Namespace = "http://schemas.microsoft.com/embedded/2004/10/ImageUpdate";
        public const string OemInputV2Namespace = "http://schemas.microsoft.com/embedded/2019/06/ImageUpdate";

        private static readonly VersionCapabilityProfile Iot17763 = new()
        {
            Generation = PlatformGeneration.IoTCore17763,
            DisplayName = "Windows 10 IoT Core 17763",
            Namespace = OemInputV1Namespace,
            SupportsOemInputV1 = true,
            SupportsIoTWorkspace = true,
            Notes = new[]
            {
                "Observed AddonKit model uses OEMInput V1 and IoTWorkspace.xml.",
                "DeviceLayout validation is primarily IoT partition and recovery oriented.",
                "FM package relationships are expressed through FeatureIDs and BasePackages."
            }
        };

        private static readonly VersionCapabilityProfile CoreOs20348 = new()
        {
            Generation = PlatformGeneration.CoreOs20348,
            DisplayName = "Windows Core OS 20348",
            Namespace = OemInputV2Namespace,
            SupportsOemInputV1 = true,
            SupportsOemInputV2 = true,
            SupportsFeatureGroups = true,
            SupportsConditionalFeatures = true,
            SupportsDeviceTargeting = true,
            SupportsStoragePools = true,
            Notes = new[]
            {
                "Observed FeatureAPI model includes FeatureGroups, conditions and DeviceTargetInfo.",
                "DeviceLayout rules can describe stores and storage pools in addition to flat layouts.",
                "OEMInput V2 fields must not be projected onto older IoT Core inputs."
            }
        };

        public static IReadOnlyList<VersionCapabilityProfile> Profiles { get; } =
            new[] { Iot17763, CoreOs20348 };

        public static VersionCapabilityProfile GetProfile(PlatformGeneration generation) =>
            generation switch
            {
                PlatformGeneration.IoTCore17763 => Iot17763,
                PlatformGeneration.CoreOs20348 => CoreOs20348,
                _ => new VersionCapabilityProfile
                {
                    Generation = PlatformGeneration.Unknown,
                    DisplayName = "未知版本",
                    Notes = new[] { "未识别版本；仅允许通用 XML 结构分析。" }
                }
            };

        public static ArtifactScanResult ScanFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("找不到要扫描的文件。", filePath);

            var result = new ArtifactScanResult { FilePath = filePath };
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using var reader = XmlReader.Create(filePath, settings);
                reader.MoveToContent();
                result = new ArtifactScanResult
                {
                    FilePath = filePath,
                    RootElement = reader.LocalName,
                    Namespace = reader.NamespaceURI,
                    Kind = GetArtifactKind(reader.LocalName),
                    Generation = InferGeneration(reader.LocalName, reader.NamespaceURI),
                    IsWellFormed = true
                };
            }
            catch (XmlException ex)
            {
                result.Warnings.Add($"XML 无法解析: {ex.Message}");
                return result;
            }

            var profile = GetProfile(result.Generation);
            CollectXmlFeatures(result);
            if (result.Kind == ArtifactKind.FeatureManifest &&
                result.Features.Any(f => f is "FeatureGroups" or "ConditionalFeatures" or "DeviceTargetInfo"))
            {
                result.Generation = PlatformGeneration.CoreOs20348;
                profile = GetProfile(result.Generation);
            }
            result.Profile = profile;
            AddShapeDiagnostics(result, profile);
            return result;
        }

        public static IReadOnlyList<ArtifactScanResult> ScanDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("目录路径不能为空。", nameof(directoryPath));
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"找不到目录: {directoryPath}");

            var files = Directory.EnumerateFiles(directoryPath, "*.xml", SearchOption.AllDirectories);
            return files.Select(ScanFile).ToList();
        }

        private static ArtifactKind GetArtifactKind(string root)
        {
            return root switch
            {
                "OEMInput" => ArtifactKind.OEMInput,
                "FeatureManifest" => ArtifactKind.FeatureManifest,
                "DeviceLayout" => ArtifactKind.DeviceLayout,
                "IoTWorkspace" => ArtifactKind.IoTWorkspace,
                _ => ArtifactKind.Unknown
            };
        }

        private static PlatformGeneration InferGeneration(string root, string ns)
        {
            if (string.Equals(ns, OemInputV1Namespace, StringComparison.Ordinal))
                return PlatformGeneration.IoTCore17763;
            if (string.Equals(ns, OemInputV2Namespace, StringComparison.Ordinal))
                return PlatformGeneration.CoreOs20348;
            if (string.Equals(root, "IoTWorkspace", StringComparison.Ordinal))
                return PlatformGeneration.IoTCore17763;
            return PlatformGeneration.Unknown;
        }

        private static void AddShapeDiagnostics(ArtifactScanResult result, VersionCapabilityProfile profile)
        {
            if (result.Kind == ArtifactKind.Unknown)
                result.Warnings.Add($"未识别的 XML 根元素: {result.RootElement}");
            if (result.Kind == ArtifactKind.OEMInput && string.IsNullOrEmpty(result.Namespace))
                result.Warnings.Add("OEMInput 缺少命名空间，无法可靠判断 V1/V2。");
            if (result.Kind == ArtifactKind.DeviceLayout && profile.Generation == PlatformGeneration.IoTCore17763)
                AddFeature(result, "IoT flat/store layout");
            if (result.Kind == ArtifactKind.DeviceLayout && profile.SupportsStoragePools)
                AddFeature(result, "StoragePools");
        }

        private static void CollectXmlFeatures(ArtifactScanResult result)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using var reader = XmlReader.Create(result.FilePath, settings);
                var document = XDocument.Load(reader, LoadOptions.None);
                var names = document.Descendants()
                    .Select(element => element.Name.LocalName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                AddIfPresent(result, names, "FeatureGroups", "FeatureGroups");
                AddIfPresent(result, names, "Condition", "ConditionalFeatures");
                AddIfPresent(result, names, "FeatureCondition", "ConditionalFeatures");
                AddIfPresent(result, names, "DeviceTargetInfo", "DeviceTargetInfo");
                AddIfPresent(result, names, "StoragePools", "StoragePools");
                AddIfPresent(result, names, "IoTWorkspace", "IoTWorkspace configuration");
                AddIfPresent(result, names, "AdditionalFM", "AdditionalFMs");
                AddIfPresent(result, names, "BasePackages", "BasePackages");
                AddIfPresent(result, names, "DeviceLayoutPackages", "DeviceLayoutPackages");
            }
            catch (XmlException ex)
            {
                result.Warnings.Add($"XML 结构扫描失败: {ex.Message}");
            }
        }

        private static void AddIfPresent(ArtifactScanResult result, HashSet<string> names,
            string elementName, string feature)
        {
            if (names.Contains(elementName))
                AddFeature(result, feature);
        }

        private static void AddFeature(ArtifactScanResult result, string feature)
        {
            if (!result.Features.Contains(feature, StringComparer.OrdinalIgnoreCase))
                result.Features.Add(feature);
        }
    }
}
