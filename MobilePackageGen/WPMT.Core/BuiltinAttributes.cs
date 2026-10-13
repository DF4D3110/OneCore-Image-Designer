using System.Numerics;
using WPMT.Core.Common;

namespace WPMT.Core
{
    public class BuiltinAttributes
    {
        public AttributeFlags NeutralAttributes { get; private set; }
        public AttributeFlags PresentAttributes { get; private set; }
        public string Name { get; private set; }
        public string PublicKeyToken { get; private set; }
        public Version Version { get; private set; }
        public string Culture { get; private set; }
        public PseudoArchitecture ProcessorArchitecture { get; private set; }
        public string Type { get; private set; }
        public string TypeName { get; private set; }
        public VersionScopeType VersionScope { get; private set; }

        public BuiltinAttributes(Keyform keyform)
        {
            NeutralAttributes = AttributeFlags.None;
            PresentAttributes = AttributeFlags.None;
            Name = keyform.Name;
            if (!string.IsNullOrWhiteSpace(Name))
            {
                PresentAttributes |= AttributeFlags.Name;
            }
            PublicKeyToken = keyform.PublicKeyToken;
            if (!string.IsNullOrWhiteSpace(PublicKeyToken) && !string.Equals(PublicKeyToken, "null", StringComparison.OrdinalIgnoreCase))
            {
                PresentAttributes |= AttributeFlags.PublicKeyToken;
            }
            Version = keyform.Version;
            if (!Version.Equals(Constants.InvalidVersion))
            {
                PresentAttributes |= AttributeFlags.Version;
            }
            Culture = keyform.Language;
            if (!string.IsNullOrWhiteSpace(Culture) && !Equals(Culture, '*') && !string.Equals(Culture, Constants.CamelCase.Neutral, StringComparison.OrdinalIgnoreCase))
            {
                PresentAttributes |= AttributeFlags.Culture;
            }
            ProcessorArchitecture = keyform.ProcessorArchitecture;
            if (PseudoArchitecture.Validate(ProcessorArchitecture))
            {
                PresentAttributes |= AttributeFlags.ProcessorArchitecture;
            }
            Type = keyform.InstallType;
            if (!string.IsNullOrWhiteSpace(Type))
            {
                PresentAttributes |= AttributeFlags.Type;
            }
            else
            {
                // NeutralAttributes |= AttributeFlags.Type;
            }
            TypeName = keyform.TypeName;
            if (!string.IsNullOrWhiteSpace(TypeName))
            {
                PresentAttributes |= AttributeFlags.TypeName;
            }
            else
            {
                // NeutralAttributes |= AttributeFlags.TypeName;
            }
            VersionScope = keyform.VersionScope;
            if (VersionScope != VersionScopeType.SxS)
            {
                PresentAttributes |= AttributeFlags.VersionScope;
            }
            else
            {
                // NeutralAttributes |= AttributeFlags.VersionScope;
            }
        }

        public static int CountValidAttributes(AttributeFlags flags)
        {
            return BitOperations.PopCount((uint)(flags & AttributeFlags.AllAttributesMask));
        }
    }
}