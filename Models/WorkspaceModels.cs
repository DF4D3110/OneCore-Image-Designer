using System;
using System.Collections.Generic;

namespace OneCore_Image_Designer.Models
{
    public sealed class IoTWorkspaceConfig
    {
        public string FilePath { get; init; } = string.Empty;
        public string CurrentEnvironment { get; init; } = string.Empty;
        public string OemName { get; init; } = string.Empty;
        public string BuildDirectory { get; init; } = string.Empty;
        public string MicrosoftPackageRoot { get; init; } = string.Empty;
        public string WindowsSdkVersion { get; init; } = string.Empty;
        public IReadOnlyList<string> EnvironmentNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> SecurityCertificates { get; init; } = Array.Empty<string>();
        public List<string> Warnings { get; } = new();
    }

    public enum ValidationSeverity { Error, Warning, Information }

    public sealed record ValidationDiagnostic(
        ValidationSeverity Severity,
        string Source,
        string Message,
        string? Location = null);

    public sealed class ProjectValidationReport
    {
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public List<ValidationDiagnostic> Diagnostics { get; } = new();
        public bool Passed => Diagnostics.TrueForAll(d => d.Severity != ValidationSeverity.Error);
        public int Errors => Diagnostics.Count(d => d.Severity == ValidationSeverity.Error);
        public int Warnings => Diagnostics.Count(d => d.Severity == ValidationSeverity.Warning);
    }
}
