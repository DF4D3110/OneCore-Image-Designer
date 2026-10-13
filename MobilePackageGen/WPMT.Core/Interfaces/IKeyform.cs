using WPMT.Core.Common;

namespace WPMT.Core.Interfaces;

public interface IKeyform
{
    string Name { get; set; }
    PseudoArchitecture ProcessorArchitecture { get; set; }
    BuildType BuildType { get; set; }
    string ReleaseType { get; set; }
    string Language { get; set; }
    VersionScopeType VersionScope { get; set; }
    Version Version { get; set; }
    string PublicKeyToken { get; set; }
    string InstallType { get; set; }
    string TypeName { get; set; }

    Keyform.KeyformHashes Hashes { get; }
}
