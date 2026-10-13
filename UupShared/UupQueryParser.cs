using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    public static class UupQueryParser
    {
        private static readonly HashSet<string> Architectures =
            new(StringComparer.OrdinalIgnoreCase) { "amd64", "arm64", "x86", "arm" };
        private static readonly HashSet<string> Filters =
            new(StringComparer.OrdinalIgnoreCase) { "ProductRelease", "OSFlight" };

        public static string ParseVersion(string value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(normalized, @"^\d+\.\d+\.\d+\.\d+$"))
                throw new ArgumentException("Reporting version must contain four numeric components.", nameof(value));
            return normalized;
        }

        public static string ParseArchitecture(string value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!Architectures.Contains(normalized))
                throw new ArgumentException($"Unsupported UUP architecture: {value}", nameof(value));
            return normalized.ToLowerInvariant();
        }

        public static string[] ParseFilters(IEnumerable<string>? values)
        {
            var parsed = (values ?? Array.Empty<string>())
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => Filters.FirstOrDefault(filter =>
                    string.Equals(filter, value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unsupported UUP filter: {value}", nameof(values)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (parsed.Length == 0)
                throw new ArgumentException("At least one UUP update filter is required.", nameof(values));
            return parsed;
        }
    }
}
