using WPMT.Core.Common;

namespace WPMT.Core
{
    public struct PseudoArchitecture
    {
        public Architecture ArchitecureOrHost;
        public Architecture Guest;

        public PseudoArchitecture(string architecureOrHost, string guest)
        {
            ArchitecureOrHost = Enum.TryParse(architecureOrHost, true, out Architecture hostArch) ?
                hostArch : Architecture.Unknown;
            Guest = Enum.TryParse(guest, true, out Architecture guestArch) ?
                guestArch : Architecture.Unknown;
        }

        public PseudoArchitecture(string architecureOrHost)
        {
            if (string.Equals(architecureOrHost, "wow64", StringComparison.OrdinalIgnoreCase))
            {
                ArchitecureOrHost = Architecture.amd64;
                Guest = Architecture.x86;
                return;
            }
            ArchitecureOrHost = Enum.TryParse(architecureOrHost, true, out Architecture hostArch) ?
                hostArch : Architecture.Unknown;
            Guest = Architecture.Unknown;
        }

        public PseudoArchitecture(Architecture architecureOrHost, Architecture guest)
        {
            ArchitecureOrHost = architecureOrHost;
            Guest = guest;
        }

        public PseudoArchitecture(Architecture architecureOrHost)
        {
            ArchitecureOrHost = architecureOrHost;
            Guest = Architecture.Unknown;
        }

        private static readonly PseudoArchitecture[] AllowedArchitectures =
        [
            new(Architecture.x86),
            new(Architecture.arm),
            new(Architecture.ia64),
            new(Architecture.msil),
            new(Architecture.amd64),
            new(Architecture.arm64),
            new(Architecture.arm, Architecture.x86),
            new(Architecture.amd64, Architecture.x86),
            new(Architecture.arm64, Architecture.arm),
            new(Architecture.arm64, Architecture.x86),
            new(Architecture.Unknown, Architecture.Unknown)
        ];

        public static Architecture GetRealArchitecture(PseudoArchitecture pseudoArchitecture)
        {
            return pseudoArchitecture.GetRealArchitecture();
        }

        public Architecture GetRealArchitecture()
        {
            return Guest == Architecture.Unknown ? ArchitecureOrHost : Guest;
        }

        public static bool Validate(PseudoArchitecture pseudoArchitecture)
        {
            return AllowedArchitectures.Contains(pseudoArchitecture);
        }

        public static PseudoArchitecture Parse(string pseudoArch)
        {
            string[] parts = pseudoArch.Split('.');
            if (parts.Length == 2)
                return new PseudoArchitecture(parts[0], parts[1]);
            else if (parts.Length == 1)
                return new PseudoArchitecture(parts[0]);
            else
                throw new ArgumentException($"Invalid pseudo architecture format: {pseudoArch}");
        }

        public static bool TryParse(string pseudoArchitecture, out PseudoArchitecture result)
        {
            try
            {
                result = Parse(pseudoArchitecture);
                return true;
            }
            catch (ArgumentException)
            {
                result = default;
                return false;
            }
        }

        public override readonly string ToString()
        {
            if (!Validate(this))
                throw new InvalidOperationException($"Invalid PseudoArchitecture: {this}");

            if (ArchitecureOrHost == Architecture.Unknown && Guest == Architecture.Unknown)
                return Constants.DataArchitecture;

            if (ArchitecureOrHost == Architecture.amd64 && Guest == Architecture.x86)
                return Constants.Wow64Architecture;

            if (Guest == Architecture.Unknown)
                return ArchitecureOrHost.ToString();

            return $"{ArchitecureOrHost}.{Guest}";
        }

        public static bool operator ==(PseudoArchitecture left, PseudoArchitecture right)
        {
            return left.ArchitecureOrHost == right.ArchitecureOrHost && left.Guest == right.Guest;
        }

        public static bool operator !=(PseudoArchitecture left, PseudoArchitecture right)
        {
            return !(left == right);
        }

        public override readonly bool Equals(object? obj)
        {
            if (obj is PseudoArchitecture other)
                return this == other;
            return false;
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(ArchitecureOrHost, Guest);
        }
    }
}