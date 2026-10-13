using System.Text;
using System.Xml.Linq;

namespace WPMT.Core.Common;

public static class Constants
{
    public const string CBSPackageExtension = ".cab";
    public const string SPKGPackageExtension = ".spkg";
    public const string CdfExtension = ".cdf";
    public const string CatExtension = ".cat";
    public const string MumExtension = ".mum";
    public const string ManifestExtension = ".manifest";
    public const string PriExtension = ".pri";
    public const string BlueprintExtension = ".blueprint";
    public const string ESDExtension = ".esd";
    public const string WIMExtension = ".wim";
    public const string UpdateMum = "update.mum";
    public const string UpdateCat = "update.cat";
    public static readonly XNamespace TaskNS = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static readonly XNamespace RescacheV1NS = "urn:schemas-microsoft-com:rescache.v1";
    public static readonly XNamespace AsmInternalV1NS = "urn:schemas-microsoft-com:asm.internal.v1";
    public static readonly XNamespace MsmqV1NS = "urn:schemas-microsoft-com:msmq.v1";
    public static readonly XNamespace SppInstallerNS = "urn:schemas-microsoft-com:spp:installer";
    public static readonly XNamespace CMIV1NS = "urn:schemas-microsoft-com:asm.v1";
    public static readonly XNamespace CMIV2NS = "urn:schemas-microsoft-com:asm.v2";
    public static readonly XNamespace CMIV3NS = "urn:schemas-microsoft-com:asm.v3";
    public static readonly string EmbeddedSigningCategory_None = "None";
    public static readonly string MainOsPartition = "MainOS";
    public static readonly string UpdateOsPartition = "UpdateOS";
    public static readonly string EfiPartition = "EFIESP";
    public static readonly string DataPartition = "Data";
    public static readonly Version InvalidVersion = new("0.0.0.0");
    public static readonly Version DefaultVersion = new("10.0.0.0");
    public static readonly string DefaultPublicKeyToken = "31bf3856ad364e35";
    public static readonly string EnUsCulture = "en-us";
    public static readonly string DefaultReleaseType = "Feature Pack";
    public static readonly string DefaultLanguagePattern = "_Lang_";
    public static readonly string DefaultWowPattern = "_Wow_";
    public static readonly int EncodingTypeDefault = 0x00010001;
    public static readonly int PeTrustedAndDrmLevelEncodingType = 0x10010001;
    public static readonly int HintEncodingType = 0x11010001;
    public static readonly HashSet<string> CatalogExtensionHint =
    [
        ".exe", ".dll", ".sys", ".ocx", ".cpl", ".winmd", ".scr", ".drv", ".ax", ".acm", ".rll", ".rs", ".tsp"
    ];
    public static readonly Dictionary<string, string> DefaultFallbackLanguages = new() { { "*", "en-us" } };
    public static readonly string PkgMetadataFolder = "\\Windows\\Packages";
    public static readonly string DsmDeviceFolder = "\\Windows\\Packages\\DsmFiles";
    public static readonly string MumDeviceFolder = "\\Windows\\servicing\\Packages";
    public static readonly string PackageStringPattern = "^[0-9a-zA-Z_\\-.]+$";
    public static readonly string[] DefaultPhoneSubComponent = ["Guest"];
    public static readonly byte[] BomHeader = new UTF8Encoding(true).GetPreamble();
    public static readonly string Iso8601UtcDateTimeFormatWithoutMilliseconds = "yyyy-MM-ddTHH:mm:ssZ";
    public const string XmlDsigNamespaceUrl = "http://www.w3.org/2000/09/xmldsig#";
    public const string XmlDsigSHA1Url = $"{XmlDsigNamespaceUrl}sha1";
    public const string XmlDsigSHA256Url = $"{XmlDsigNamespaceUrl}sha256";
    public const string DataArchitecture = "data";
    public const string Wow64Architecture = "wow64";
    public const string NoDashPublicDashKey = "no-public-key";
    public const string XDashWorldwideLanguage = "x-ww";

    public static class CamelCase
    {
        public const string Name = "name";
        public const string Culture = "culture";
        public const string Type = "type";
        public const string TypeName = "typeName";
        public const string Version = "version";
        public const string PublicKeyToken = "publicKeyToken";
        public const string ProcessorArchitecture = "processorArchitecture";
        public const string VersionScope = "versionScope";
        public const string Neutral = "neutral";
        public const string Language = "language";
    }

    public static class PascalCase
    {
        public const string Name = "Name";
        public const string Culture = "Culture";
        public const string Type = "Type";
        public const string TypeName = "TypeName";
        public const string Version = "Version";
        public const string PublicKeyToken = "PublicKeyToken";
        public const string ProcessorArchitecture = "ProcessorArchitecture";
        public const string VersionScope = "VersionScope";
        public const string Neutral = "Neutral";
    }
}