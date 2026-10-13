using System.IO;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public sealed record ArtifactPreflightResult(IntegrationArtifact Artifact, IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings)
    {
        public bool IsReady => Errors.Count == 0;
    }

    public sealed class IntegrationArtifactRegistry
    {
        private readonly List<IntegrationArtifact> _artifacts = new();
        private readonly object _gate = new();

        public static IntegrationArtifactRegistry Shared { get; } = new();

        public IReadOnlyList<IntegrationArtifact> Artifacts
        {
            get { lock (_gate) return _artifacts.ToArray(); }
        }

        public IntegrationArtifact Register(IntegrationArtifact artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.Path))
                throw new ArgumentException("Artifact path is required.", nameof(artifact));
            var normalizedPath = Path.GetFullPath(artifact.Path);
            var normalized = artifact with { Path = normalizedPath };
            lock (_gate)
            {
                var existing = _artifacts.FindIndex(item => item.Kind == normalized.Kind &&
                    string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) _artifacts[existing] = normalized;
                else _artifacts.Add(normalized);
            }
            return normalized;
        }

        public ArtifactPreflightResult Preflight(IntegrationArtifact artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            var errors = new List<string>();
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(artifact.Path))
                errors.Add("Artifact path is empty.");
            else if (!artifact.Exists)
                errors.Add($"Artifact does not exist: {artifact.Path}");

            if (artifact.Kind == IntegrationArtifactKind.UupSnapshot &&
                artifact.Exists && File.Exists(artifact.Path))
            {
                try
                {
                    var snapshot = new UupIntegrationService().LoadSnapshot(artifact.Path);
                    if (snapshot.Entries.Count == 0) warnings.Add("Snapshot contains no update entries.");
                    if (!string.IsNullOrWhiteSpace(snapshot.PayloadFile))
                    {
                        var payloadPath = UupPayloadLocator.ResolvePayloadPath(artifact.Path, snapshot.PayloadFile);
                        if (!File.Exists(payloadPath)) errors.Add($"Snapshot payload is missing: {payloadPath}");
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException)
                {
                    errors.Add($"Snapshot validation failed: {ex.Message}");
                }
            }

            return new ArtifactPreflightResult(artifact, errors, warnings);
        }
    }
}
