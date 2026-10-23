namespace OneCore_Image_Designer.Services
{
    public sealed record UupQueryPlan(
        string Filter,
        string FlightRing,
        string FlightingBranchName,
        string CurrentBranch,
        string ReportingVersion);

    public static class UupQueryPlanner
    {
        private static readonly string[] RetailBranches =
            ["ge_release", "ni_release", "vb_release", "fe_release"];

        private static readonly (string Branch, string FlightingBranch)[] InsiderProfiles =
        [
            ("ge_release", ""),
            ("ni_release", "ReleasePreview"),
            ("ni_release", "Beta"),
            ("ni_release", "Dev"),
            ("ni_release", "CanaryChannel"),
            ("rs_prerelease", "CanaryChannel"),
            ("vb_release", "ReleasePreview")
        ];

        public static UupQueryPlan[] Build(
            IEnumerable<string> filters,
            string? reportingVersion,
            string? flightRing,
            string? flightingBranch,
            string? currentBranch)
        {
            var normalizedFilters = UupQueryParser.ParseFilters(filters);
            var versionOverride = reportingVersion?.Trim() ?? string.Empty;
            if (versionOverride.Length > 0)
                versionOverride = UupQueryParser.ParseVersion(versionOverride);
            var ringOverride = flightRing?.Trim() ?? string.Empty;
            var flightBranchOverride = flightingBranch?.Trim() ?? string.Empty;
            var branchOverride = currentBranch?.Trim() ?? string.Empty;
            var plans = new List<UupQueryPlan>();

            foreach (var filter in normalizedFilters)
            {
                var isInsider = string.Equals(filter, "OSFlight", StringComparison.OrdinalIgnoreCase);
                var ring = ringOverride.Length > 0 ? ringOverride : isInsider ? "External" : "Retail";

                if (branchOverride.Length > 0)
                {
                    string[] flightBranches = flightBranchOverride.Length > 0
                        ? new[] { flightBranchOverride }
                        : isInsider ? ["", "ReleasePreview", "Beta", "Dev", "CanaryChannel"] : [""];
                    foreach (var flight in flightBranches)
                        plans.Add(new UupQueryPlan(filter, ring, flight, branchOverride,
                            versionOverride.Length > 0 ? versionOverride : VersionForBranch(branchOverride)));
                    continue;
                }

                if (isInsider)
                {
                    foreach (var profile in InsiderProfiles)
                    {
                        if (flightBranchOverride.Length > 0 &&
                            !string.Equals(flightBranchOverride, profile.FlightingBranch, StringComparison.OrdinalIgnoreCase))
                            continue;
                        plans.Add(new UupQueryPlan(filter, ring, flightBranchOverride.Length > 0
                            ? flightBranchOverride : profile.FlightingBranch, profile.Branch,
                            versionOverride.Length > 0 ? versionOverride : VersionForBranch(profile.Branch)));
                    }
                }
                else
                {
                    foreach (var branch in RetailBranches)
                        plans.Add(new UupQueryPlan(filter, ring, flightBranchOverride, branch,
                            versionOverride.Length > 0 ? versionOverride : VersionForBranch(branch)));
                }
            }

            return plans
                .Distinct()
                .ToArray();
        }

        private static string VersionForBranch(string branch) =>
            branch.Equals("vb_release", StringComparison.OrdinalIgnoreCase) ? "10.0.19041.1" :
            branch.Equals("ni_release", StringComparison.OrdinalIgnoreCase) ? "10.0.22621.1" :
            "10.0.26100.1";
    }
}
