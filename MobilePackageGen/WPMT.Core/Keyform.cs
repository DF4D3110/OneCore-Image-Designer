using System.Text;
using System.Xml.Linq;
using WPMT.Core.Common;
using WPMT.Core.Exceptions;
using WPMT.Core.Interfaces;

namespace WPMT.Core;

public class Keyform : IKeyform
{
    private const int XpKeyFormMaxChars = 154;
    private const int KeyFormMaxChars = 140;

    public string Name
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public PseudoArchitecture ProcessorArchitecture
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public BuildType BuildType
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public string ReleaseType
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public string Language
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public VersionScopeType VersionScope
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public Version Version
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public string PublicKeyToken
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public string InstallType
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public string TypeName
    {
        get;
        set
        {
            if (field != value)
                IsHashDirty = true;
            field = value;
        }
    }
    public KeyformHashes Hashes
    {
        get
        {
            if (IsHashDirty)
            {
                field = new(this);
                IsHashDirty = false;
            }
            return field;
        }
    }
    public bool IsHashDirty { get; set; }

    public Keyform()
    {
        Name = string.Empty;
        ProcessorArchitecture = new(Architecture.Unknown, Architecture.Unknown);
        BuildType = BuildType.Invalid;
        ReleaseType = string.Empty;
        Language = Constants.CamelCase.Neutral;
        VersionScope = VersionScopeType.SxS;
        Version = Constants.InvalidVersion;
        PublicKeyToken = string.Empty;
        InstallType = string.Empty;
        TypeName = string.Empty;
    }

    public Keyform(Keyform keyform)
    {
        Name = keyform.Name;
        ProcessorArchitecture = keyform.ProcessorArchitecture;
        BuildType = keyform.BuildType;
        ReleaseType = keyform.ReleaseType;
        Language = keyform.Language;
        VersionScope = keyform.VersionScope;
        Version = keyform.Version;
        PublicKeyToken = keyform.PublicKeyToken;
        InstallType = keyform.InstallType;
        TypeName = keyform.TypeName;
    }

    public Keyform(string name, PseudoArchitecture processorArchitecture, BuildType buildType, string releaseType, string language, VersionScopeType versionScope, Version version, string publicKeyToken)
    {
        Name = name;
        ProcessorArchitecture = processorArchitecture;
        BuildType = buildType;
        ReleaseType = releaseType;
        Language = language;
        VersionScope = versionScope;
        Version = version;
        PublicKeyToken = publicKeyToken;
        InstallType = string.Empty;
        TypeName = string.Empty;
    }

    public Keyform(string name, PseudoArchitecture processorArchitecture, BuildType buildType, string releaseType, string language, VersionScopeType versionScope, Version version, string publicKeyToken, string installType)
    {
        Name = name;
        ProcessorArchitecture = processorArchitecture;
        BuildType = buildType;
        ReleaseType = releaseType;
        Language = language;
        VersionScope = versionScope;
        Version = version;
        PublicKeyToken = publicKeyToken;
        InstallType = installType;
        TypeName = string.Empty;
    }

    private static List<ElementAttribute> InitializeAttributeList(BuiltinAttributes attributes, bool fIncludeNeutralAttributesInList)
    {
        if ((attributes.PresentAttributes & attributes.NeutralAttributes) != AttributeFlags.None)
            throw new WPMTException("Incorrect attribute flags.");

        int validAttributeCount = BuiltinAttributes.CountValidAttributes(attributes.PresentAttributes);
        if (fIncludeNeutralAttributesInList)
            validAttributeCount += BuiltinAttributes.CountValidAttributes(attributes.NeutralAttributes);

        if (validAttributeCount == 0)
            return [];

        List<ElementAttribute> attributeList = [with(validAttributeCount)];

        string name;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Name))
        {
            name = attributes.Name;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.Name))
        {
            name = Constants.PascalCase.Neutral;
        }
        else
        {
            name = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(name))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.Name, name));

        string culture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Culture))
        {
            culture = attributes.Culture;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.Culture))
        {
            culture = Constants.CamelCase.Neutral;
        }
        else
        {
            culture = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(culture))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.Culture, culture));

        string typeName;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.TypeName))
        {
            typeName = attributes.TypeName;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.TypeName))
        {
            typeName = Constants.PascalCase.Neutral;
        }
        else
        {
            typeName = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(typeName))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.TypeName, typeName));

        string type;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Type))
        {
            type = attributes.Type;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.Type))
        {
            type = Constants.PascalCase.Neutral;
        }
        else
        {
            type = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(type))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.Type, type));

        string version;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Version))
        {
            version = attributes.Version.ToString(4);
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.Version))
        {
            version = Constants.CamelCase.Neutral;
        }
        else
        {
            version = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(version))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.Version, version));

        string publicKeyToken;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.PublicKeyToken))
        {
            publicKeyToken = attributes.PublicKeyToken;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.PublicKeyToken))
        {
            publicKeyToken = Constants.PascalCase.Neutral;
        }
        else
        {
            publicKeyToken = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(publicKeyToken))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.PublicKeyToken, publicKeyToken));

        string processorArchitecture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.ProcessorArchitecture))
        {
            processorArchitecture = attributes.ProcessorArchitecture.ToString();
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.ProcessorArchitecture))
        {
            processorArchitecture = Constants.PascalCase.Neutral;
        }
        else
        {
            processorArchitecture = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(processorArchitecture))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.ProcessorArchitecture, processorArchitecture));

        string versionScope;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.VersionScope))
        {
            versionScope = Enum.IsDefined(attributes.VersionScope) ? attributes.VersionScope.ToString() : Constants.PascalCase.Neutral;
        }
        else if (fIncludeNeutralAttributesInList && attributes.NeutralAttributes.HasFlag(AttributeFlags.VersionScope))
        {
            versionScope = Constants.PascalCase.Neutral;
        }
        else
        {
            versionScope = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(versionScope))
            attributeList.Add(new(XNamespace.None, Constants.PascalCase.VersionScope, versionScope));

        return attributeList;
    }

    private static string GenerateKeyform_XPFormat(KeyformFlags flags, BuiltinAttributes attributes)
    {
        if ((flags & KeyformFlags.AllAttributesMask) != flags)
            throw new ArgumentException("Invalid flags.");

        bool wildcardMissingAttributes = flags.HasFlag(KeyformFlags.WildcardMissingAttributes);
        bool ignoreVersion = flags.HasFlag(KeyformFlags.IgnoreVersion);
        bool ignoreServicing = flags.HasFlag(KeyformFlags.IgnoreServicing);

        bool areNeutralBuiltinAttributesConsideredPresent = true;

        if (wildcardMissingAttributes || ignoreServicing)
            throw new NotSupportedException("WildcardMissingAttributes and IgnoreServicing flags are not supported.");

        List<ElementAttribute> attributeList = InitializeAttributeList(attributes, areNeutralBuiltinAttributesConsideredPresent);

        Hashing.GenerateHashValue(ignoreVersion, attributeList, out ulong hashValue);
        StringBuilder stringBuilder = new(XpKeyFormMaxChars, XpKeyFormMaxChars);

        string architecture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.ProcessorArchitecture))
        {
            architecture = attributes.ProcessorArchitecture.ToString();
        }
        else
        {
            architecture = Constants.DataArchitecture;
        }

        StringUtils.SanitizeAndAppend(true, stringBuilder, architecture, 8);

        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Name) && !string.IsNullOrWhiteSpace(attributes.Name))
        {
            stringBuilder.Append('_');
            stringBuilder.Append(StringUtils.ConvertNamePrime(attributes.Name));
        }
        else
        {
            throw new WPMTException("Name is required.");
        }

        string publicKeyToken;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.PublicKeyToken) && !string.IsNullOrWhiteSpace(attributes.PublicKeyToken))
        {
            publicKeyToken = attributes.PublicKeyToken;
        }
        else
        {
            publicKeyToken = Constants.NoDashPublicDashKey;
        }

        StringUtils.SanitizeAndAppend(true, stringBuilder, publicKeyToken, 16);

        if (!ignoreVersion && attributes.PresentAttributes.HasFlag(AttributeFlags.Version))
            StringUtils.SanitizeAndAppend(true, stringBuilder, attributes.Version.ToString(4), 23);

        string culture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Culture) && !string.IsNullOrWhiteSpace(attributes.Culture))
        {
            culture = attributes.Culture;
        }
        else
        {
            culture = Constants.XDashWorldwideLanguage;
        }

        StringUtils.SanitizeAndAppend(true, stringBuilder, culture, 12);

        stringBuilder.Append('_');
        stringBuilder.AppendFormat("{0:x8}", (uint)hashValue);

        return stringBuilder.ToString();
    }

    private static string GenerateKeyform_LHFormat(KeyformFlags flags, BuiltinAttributes attributes)
    {
        if ((flags & KeyformFlags.AllAttributesMask) != flags)
            throw new ArgumentException("Invalid flags.");

        bool wildcardMissingAttributes = flags.HasFlag(KeyformFlags.WildcardMissingAttributes);
        bool ignoreVersion = flags.HasFlag(KeyformFlags.IgnoreVersion);
        bool ignoreServicing = flags.HasFlag(KeyformFlags.IgnoreServicing);
        bool useXPFormat = flags.HasFlag(KeyformFlags.UseXPFormat);

        bool wildcardUsed = false;
        StringBuilder stringBuilder = new(KeyFormMaxChars, KeyFormMaxChars);

        string architecture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.ProcessorArchitecture))
        {
            architecture = attributes.ProcessorArchitecture.ToString();
        }
        else if (wildcardMissingAttributes)
        {
            wildcardUsed = true;
            architecture = "*";
        }
        else
        {
            architecture = "none";
        }

        if (!string.IsNullOrWhiteSpace(architecture))
            stringBuilder.Append(architecture);

        string typeName;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.TypeName))
        {
            typeName = attributes.TypeName;
        }
        else if (useXPFormat && attributes.NeutralAttributes.HasFlag(AttributeFlags.TypeName))
        {
            typeName = Constants.PascalCase.Neutral;
        }
        else
        {
            typeName = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(typeName))
            StringUtils.SanitizeAndAppend(true, stringBuilder, typeName, 16);

        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Name) && !string.IsNullOrWhiteSpace(attributes.Name))
        {
            StringUtils.SanitizeAndAppend(true, stringBuilder, attributes.Name, 40);
        }
        else
        {
            throw new WPMTException("Name is required.");
        }

        string publicKeyToken;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.PublicKeyToken))
        {
            publicKeyToken = attributes.PublicKeyToken;
        }
        else if (useXPFormat && attributes.NeutralAttributes.HasFlag(AttributeFlags.PublicKeyToken))
        {
            publicKeyToken = Constants.CamelCase.Neutral;
        }
        else if (wildcardMissingAttributes)
        {
            wildcardUsed = true;
            publicKeyToken = "*";
        }
        else
        {
            publicKeyToken = "none";
        }

        if (!string.IsNullOrWhiteSpace(publicKeyToken))
        {
            stringBuilder.Append('_');
            stringBuilder.Append(publicKeyToken);
        }

        if (!ignoreVersion)
        {
            string version;
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.Version))
            {
                if (ignoreServicing)
                {
                    version = attributes.Version.ToString(2);
                }
                else
                {
                    version = attributes.Version.ToString(4);
                }
            }
            else if (useXPFormat && attributes.NeutralAttributes.HasFlag(AttributeFlags.Version))
            {
                version = Constants.CamelCase.Neutral;
            }
            else if (wildcardMissingAttributes)
            {
                wildcardUsed = true;
                version = "*";
            }
            else
            {
                version = "none";
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                stringBuilder.Append('_');
                stringBuilder.Append(version);
            }
        }

        string culture;
        if (attributes.PresentAttributes.HasFlag(AttributeFlags.Culture))
        {
            culture = attributes.Culture;
        }
        else if (useXPFormat && attributes.NeutralAttributes.HasFlag(AttributeFlags.Culture))
        {
            culture = Constants.CamelCase.Neutral;
        }
        else if (wildcardMissingAttributes)
        {
            wildcardUsed = true;
            culture = "*";
        }
        else
        {
            culture = "none";
        }

        if (!string.IsNullOrWhiteSpace(culture))
            StringUtils.SanitizeAndAppend(true, stringBuilder, culture, 8);

        stringBuilder.Append('_');

        if (!wildcardUsed)
        {
            Hashing.GeneratePseudoKeys(attributes, out ulong pseudoKey, out ulong versionIndependentPseudoKey, out ulong servicingIndependentPseudoKey);
            if (ignoreVersion)
            {
                stringBuilder.AppendFormat("{0:x16}", versionIndependentPseudoKey);
            }
            else if (ignoreServicing)
            {
                stringBuilder.AppendFormat("{0:x16}", servicingIndependentPseudoKey);
            }
            else
            {
                stringBuilder.AppendFormat("{0:x16}", pseudoKey);
            }
        }
        else
        {
            stringBuilder.Append('*');
        }

        if (stringBuilder.Length > KeyFormMaxChars)
            throw new WPMTException("Keyform exceeds maximum length.");

        return stringBuilder.ToString();
    }

    public static string GenerateKeyform(KeyformFlags flags, BuiltinAttributes attributes)
    {
        if (flags.HasFlag(KeyformFlags.UseXPFormat))
        {
            flags &= ~KeyformFlags.UseXPFormat;
            return GenerateKeyform_XPFormat(flags, attributes);
        }
        else
        {
            return GenerateKeyform_LHFormat(flags, attributes);
        }
    }

    public static string GenerateKeyform(Keyform keyform, bool isPackage)
    {
        return keyform.GenerateKeyform(isPackage);
    }

    public static string GenerateKeyform(Keyform keyform, ManifestType manifestType)
    {
        return keyform.GenerateKeyform(manifestType);
    }

    public string GenerateKeyform(bool isPackage)
    {
        return isPackage ? GenerateCBSKeyform() : GenerateKeyform(KeyformFlags.None, new(this));
    }

    public string GenerateKeyform(ManifestType manifestType)
    {
        return GenerateKeyform(manifestType == ManifestType.Package);
    }

    public static string GenerateCBSKeyform(string name, string publicKey, string arch, string language, string version)
    {
        if (string.IsNullOrEmpty(language) || language.Equals(Constants.CamelCase.Neutral, StringComparison.OrdinalIgnoreCase))
            return $"{name}~{publicKey}~{arch}~~{version}";
        return $"{name}~{publicKey}~{arch}~{language}~{version}";
    }

    public static string GenerateCBSKeyform(Keyform keyform)
    {
        return keyform.GenerateCBSKeyform();
    }

    public string GenerateCBSKeyform()
    {
        return GenerateCBSKeyform(Name, PublicKeyToken, ProcessorArchitecture.ToString(), Language, Version.ToString());
    }

    public static string GenerateVersionFreeCBSKeyform(string name, string publicKey, string arch, string language)
    {
        if (string.IsNullOrEmpty(language) || language.Equals(Constants.CamelCase.Neutral, StringComparison.OrdinalIgnoreCase))
            return $"{name}~{publicKey}~{arch}~~";
        return $"{name}~{publicKey}~{arch}~{language}~";
    }

    public static string GenerateVersionFreeCBSKeyform(Keyform keyform)
    {
        return GenerateVersionFreeCBSKeyform(keyform.Name, keyform.PublicKeyToken, keyform.ProcessorArchitecture.ToString(), keyform.Language);
    }

    public static ulong GetManifestKeyformHash(string keyform)
    {
        string[] array = keyform.Split('_');
        if (array.Length < 6)
            throw new WPMTException("Invalid manifest keyform.");
        return Convert.ToUInt64(array[^1], 16);
    }

    public bool CompareKeyforms(Keyform keyform)
    {
        return keyform.PublicKeyToken == PublicKeyToken && keyform.ProcessorArchitecture == ProcessorArchitecture && keyform.Name.Equals(Name, StringComparison.OrdinalIgnoreCase) && keyform.Language.Equals(Language, StringComparison.OrdinalIgnoreCase) && keyform.VersionScope == VersionScope && keyform.Version.Equals(Version);
    }

    public bool CompareKeyforms(Keyform keyform, ManifestType manifestType)
    {
        if (manifestType == ManifestType.Package)
            return GenerateCBSKeyform(this).Equals(GenerateCBSKeyform(keyform), StringComparison.OrdinalIgnoreCase);
        return GenerateKeyform(this, manifestType).Equals(GenerateKeyform(keyform, manifestType), StringComparison.OrdinalIgnoreCase);
    }

    public bool CompareVersionFreeCBSKeyforms(Keyform keyform, ManifestType manifestType)
    {
        return manifestType == ManifestType.Package && GenerateVersionFreeCBSKeyform(this).Equals(GenerateVersionFreeCBSKeyform(keyform), StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        return GenerateCBSKeyform();
    }

    public static bool IsValidPkgKeyForm(string keyform)
    {
        if (string.IsNullOrEmpty(keyform))
            return false;
        return keyform.Split('~').Length == 5;
    }

    public static string GetPkgNameFromKeyform(string keyform)
    {
        if (!IsValidPkgKeyForm(keyform))
            throw new WPMTException("Invalid keyform.");
        string[] array = keyform.Split('~');
        return array[0];
    }

    public struct KeyformHashes
    {
        public ulong PseudoKey;
        public ulong VersionIndependentPseudoKey;
        public ulong ServicingIndependentPseudoKey;

        public KeyformHashes(Keyform keyform)
        {
            Hashing.GeneratePseudoKeys(new(keyform), out PseudoKey, out VersionIndependentPseudoKey, out ServicingIndependentPseudoKey);
        }
    }

    private static class Hashing
    {
        private enum HashStringAlgorithm
        {
            Invalid,
            X65599,
            X65599Variant
        }

        private const uint HashMultiplierX65599 = 65599U;
        private const ulong HashMultiplierLonghorn = 0x1FFFFFFF7UL;
        private static readonly ulong[] HashMultipliers =
        [
            0x1E5FFFFFD27UL,
            0xFFFFFFDC00000051UL,
            HashMultiplierLonghorn,
            0x1UL
        ];

        private readonly struct PrecalculatedHashes
        {
            public readonly ulong Name;
            public readonly ulong Culture;
            public readonly ulong TypeName;
            public readonly ulong Type;
            public readonly ulong Version;
            public readonly ulong PublicKeyToken;
            public readonly ulong ProcessorArchitecture;
            public readonly ulong VersionScope;

            public PrecalculatedHashes(HashStringAlgorithm algorithm)
            {
                HashString(Constants.PascalCase.Name, true, algorithm, out Name);
                HashString(Constants.PascalCase.Culture, true, algorithm, out Culture);
                HashString(Constants.PascalCase.TypeName, true, algorithm, out TypeName);
                HashString(Constants.PascalCase.Type, true, algorithm, out Type);
                HashString(Constants.PascalCase.Version, true, algorithm, out Version);
                HashString(Constants.PascalCase.PublicKeyToken, true, algorithm, out PublicKeyToken);
                HashString(Constants.PascalCase.ProcessorArchitecture, true, algorithm, out ProcessorArchitecture);
                HashString(Constants.PascalCase.VersionScope, true, algorithm, out VersionScope);
            }
        }

        private static readonly PrecalculatedHashes PrecalculatedX65599Hashes = new(HashStringAlgorithm.X65599);
        private static readonly PrecalculatedHashes PrecalculatedX65599VariantHashes = new(HashStringAlgorithm.X65599Variant);

        private static void HashStringX65599(string nameOrValue, bool caseInSensitive, out ulong pseudoKey)
        {
            pseudoKey = 0UL;
            for (int i = 0; i < nameOrValue.Length; i++)
            {
                uint tokenValue = caseInSensitive ? char.ToUpper(nameOrValue[i]) : nameOrValue[i];
                pseudoKey = pseudoKey * HashMultiplierX65599 + tokenValue;
            }
        }

        private static void HashStringX65599Variant(string nameOrValue, bool caseInSensitive, out ulong pseudoKey)
        {
            uint[] accumulators = new uint[4];
            pseudoKey = 0UL;

            for (int i = 0; i < nameOrValue.Length; i++)
            {
                uint tokenValue = caseInSensitive ? char.ToLower(nameOrValue[i]) : nameOrValue[i];

                int index = i % 4;
                accumulators[index] = accumulators[index] * HashMultiplierX65599 + tokenValue;
            }

            for (int i = 0; i < accumulators.Length; i++)
            {
                pseudoKey += accumulators[i] * HashMultipliers[i];
            }
        }

        private static void HashString(string nameOrValue, bool caseInSensitive, HashStringAlgorithm algorithm, out ulong pseudoKey)
        {
            ArgumentNullException.ThrowIfNull(nameOrValue);

            switch (algorithm)
            {
                case HashStringAlgorithm.X65599:
                    HashStringX65599(nameOrValue, caseInSensitive, out pseudoKey);
                    break;

                case HashStringAlgorithm.X65599Variant:
                    HashStringX65599Variant(nameOrValue, caseInSensitive, out pseudoKey);
                    break;

                default:
                    throw new NotSupportedException("Unsupported hash algorithm.");
            }
        }

        // XP format
        public static void GenerateHashValue(bool fIgnoreVersion, List<ElementAttribute> attributeList, out ulong hashValue)
        {
            hashValue = 0UL;

            foreach (ElementAttribute attribute in attributeList)
            {
                if (attribute.Namespace == XNamespace.None && attribute.Name.Equals(Constants.CamelCase.Culture, StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Name = Constants.CamelCase.Language;
                }
            }

            attributeList.Sort();

            foreach (ElementAttribute attribute in attributeList)
            {
                ulong combinedHash = 0UL;
                if (attribute.IsVersion && fIgnoreVersion)
                    continue;

                if (attribute.Namespace != XNamespace.None)
                {
                    HashString(attribute.Namespace.NamespaceName, true, HashStringAlgorithm.X65599, out ulong namespaceHash);
                    combinedHash = namespaceHash;
                }

                ulong nameHash;
                switch (attribute.Name)
                {
                    case Constants.PascalCase.Name:
                        nameHash = PrecalculatedX65599Hashes.Name;
                        break;
                    case Constants.PascalCase.Culture:
                        nameHash = PrecalculatedX65599Hashes.Culture;
                        break;
                    case Constants.PascalCase.TypeName:
                        nameHash = PrecalculatedX65599Hashes.TypeName;
                        break;
                    case Constants.PascalCase.Type:
                        nameHash = PrecalculatedX65599Hashes.Type;
                        break;
                    case Constants.PascalCase.Version:
                        nameHash = PrecalculatedX65599Hashes.Version;
                        break;
                    case Constants.PascalCase.PublicKeyToken:
                        nameHash = PrecalculatedX65599Hashes.PublicKeyToken;
                        break;
                    case Constants.PascalCase.ProcessorArchitecture:
                        nameHash = PrecalculatedX65599Hashes.ProcessorArchitecture;
                        break;
                    case Constants.PascalCase.VersionScope:
                        nameHash = PrecalculatedX65599Hashes.VersionScope;
                        break;
                    default:
                        HashString(attribute.Name, true, HashStringAlgorithm.X65599, out nameHash);
                        break;
                }
                combinedHash = combinedHash * HashMultiplierX65599 + nameHash;
                if (!string.IsNullOrEmpty(attribute.Value))
                {
                    HashString(attribute.Value, true, HashStringAlgorithm.X65599, out ulong valueHash);
                    combinedHash = combinedHash * HashMultiplierX65599 + valueHash;
                }

                hashValue = hashValue * HashMultiplierX65599 + combinedHash;
            }
        }

        private static void HashIdentityAttributeValue(string nameOrValue, out ulong hash)
        {
            HashString(nameOrValue, true, HashStringAlgorithm.X65599Variant, out hash);
        }

        public static void GeneratePseudoKeys(
            BuiltinAttributes attributes,
            out ulong pseudoKey,
            out ulong versionIndependentPseudoKey,
            out ulong servicingIndependentPseudoKey)
        {
            pseudoKey = 0UL;
            versionIndependentPseudoKey = 0UL;
            servicingIndependentPseudoKey = 0UL;

            Dictionary<string, string> attributeMap = [with(BuiltinAttributes.CountValidAttributes(attributes.PresentAttributes))];
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.Name))
                attributeMap.Add(Constants.CamelCase.Name, attributes.Name);
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.Culture))
                attributeMap.Add(Constants.CamelCase.Culture, attributes.Culture);
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.TypeName))
                attributeMap.Add(Constants.CamelCase.TypeName, attributes.TypeName);
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.Type))
                attributeMap.Add(Constants.PascalCase.Type, attributes.Type);
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.Version))
                attributeMap.Add(Constants.CamelCase.Version, attributes.Version.ToString(4));
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.PublicKeyToken))
                attributeMap.Add(Constants.CamelCase.PublicKeyToken, attributes.PublicKeyToken);
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.ProcessorArchitecture))
                attributeMap.Add(Constants.CamelCase.ProcessorArchitecture, attributes.ProcessorArchitecture.ToString());
            if (attributes.PresentAttributes.HasFlag(AttributeFlags.VersionScope))
                attributeMap.Add(Constants.CamelCase.VersionScope, attributes.VersionScope.ToString());

            KeyValuePair<string, string> servicingIndependentVersion = new(Constants.CamelCase.Version, attributes.Version.ToString(2));

            foreach (KeyValuePair<string, string> attribute in attributeMap.Where(attr => attributes.PresentAttributes.HasFlag(GetAttributeFlag(attr.Key))))
            {
                HashNameAndValue(attribute, out ulong nameValueHash);
                pseudoKey = pseudoKey * HashMultiplierLonghorn + nameValueHash;
                if (!attribute.Key.Equals(Constants.CamelCase.Version, StringComparison.OrdinalIgnoreCase))
                {
                    versionIndependentPseudoKey = versionIndependentPseudoKey * HashMultiplierLonghorn + nameValueHash;
                    servicingIndependentPseudoKey = servicingIndependentPseudoKey * HashMultiplierLonghorn + nameValueHash;
                }
                else
                {
                    HashNameAndValue(servicingIndependentVersion, out nameValueHash);
                    servicingIndependentPseudoKey = servicingIndependentPseudoKey * HashMultiplierLonghorn + nameValueHash;
                }
            }
        }

        private static void HashNameAndValue(KeyValuePair<string, string> nameValuePair, out ulong nameValueHash)
        {
            ulong nameHash;
            switch (nameValuePair.Key)
            {
                case Constants.CamelCase.Name:
                    nameHash = PrecalculatedX65599VariantHashes.Name;
                    break;
                case Constants.CamelCase.Culture:
                    nameHash = PrecalculatedX65599VariantHashes.Culture;
                    break;
                case Constants.CamelCase.TypeName:
                    nameHash = PrecalculatedX65599VariantHashes.TypeName;
                    break;
                case Constants.PascalCase.Type:
                    nameHash = PrecalculatedX65599VariantHashes.Type;
                    break;
                case Constants.CamelCase.Version:
                    nameHash = PrecalculatedX65599VariantHashes.Version;
                    break;
                case Constants.CamelCase.PublicKeyToken:
                    nameHash = PrecalculatedX65599VariantHashes.PublicKeyToken;
                    break;
                case Constants.CamelCase.ProcessorArchitecture:
                    nameHash = PrecalculatedX65599VariantHashes.ProcessorArchitecture;
                    break;
                case Constants.CamelCase.VersionScope:
                    nameHash = PrecalculatedX65599VariantHashes.VersionScope;
                    break;
                default:
                    HashIdentityAttributeValue(nameValuePair.Key, out nameHash);
                    break;
            }
            HashIdentityAttributeValue(nameValuePair.Value, out ulong valueHash);
            nameValueHash = nameHash * HashMultiplierLonghorn + valueHash;
        }

        private static AttributeFlags GetAttributeFlag(string attributeName)
        {
            return attributeName switch
            {
                Constants.CamelCase.Name or Constants.PascalCase.Name => AttributeFlags.Name,
                Constants.CamelCase.Culture or Constants.PascalCase.Culture => AttributeFlags.Culture,
                Constants.CamelCase.TypeName or Constants.PascalCase.TypeName => AttributeFlags.TypeName,
                Constants.CamelCase.Type or Constants.PascalCase.Type => AttributeFlags.Type,
                Constants.CamelCase.Version or Constants.PascalCase.Version => AttributeFlags.Version,
                Constants.CamelCase.PublicKeyToken or Constants.PascalCase.PublicKeyToken => AttributeFlags.PublicKeyToken,
                Constants.CamelCase.ProcessorArchitecture or Constants.PascalCase.ProcessorArchitecture => AttributeFlags.ProcessorArchitecture,
                Constants.CamelCase.VersionScope or Constants.PascalCase.VersionScope => AttributeFlags.VersionScope,
                _ => throw new ArgumentException("Invalid attribute name", nameof(attributeName))
            };
        }
    }
}