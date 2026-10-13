namespace WPMT.Core;

public enum Architecture
{
    Unknown,
    amd64,
    x86,
    arm,
    ia64,
    msil,
    arm64
}

public enum BuildType
{
    Invalid,
    Release
}

public enum VersionScopeType
{
    SxS,
    nonSxS
}

[Flags]
public enum KeyformFlags
{
    None = 0,
    UseXPFormat = 1 << 0,
    WildcardMissingAttributes = 1 << 1,
    IgnoreVersion = 1 << 2,
    IgnoreServicing = 1 << 3,
    AllAttributesMask = UseXPFormat | WildcardMissingAttributes | IgnoreVersion | IgnoreServicing
}

public enum ManifestType
{
    Package,
    Manifest
}

public enum PseudoKeyType
{
    Standard,
    ServicingIndependent,
    VersionIndependent
}

[Flags]
public enum AttributeFlags
{
    None = 0,
    Name = 1 << 0,
    PublicKeyToken = 1 << 1,
    Version = 1 << 2,
    Culture = 1 << 3,
    ProcessorArchitecture = 1 << 4,
    Type = 1 << 5,
    TypeName = 1 << 6,
    VersionScope = 1 << 7,
    AllAttributesMask = Name | PublicKeyToken | Version | Culture | ProcessorArchitecture | Type | TypeName | VersionScope
}
