using System;
using System.Collections.Generic;
using System.IO;

namespace OneCore_Image_Designer.Models
{
    public enum IntegrationArtifactKind
    {
        UupSnapshot,
        UupPackage,
        MediaImage,
        SxsOutput,
        CbsOutput
    }

    public sealed record IntegrationArtifact(
        IntegrationArtifactKind Kind,
        string Path,
        string Description,
        string? Version = null,
        string? Architecture = null)
    {
        public bool Exists => File.Exists(Path) || Directory.Exists(Path);
    }

    public sealed class UupScanEntry
    {
        public string ProductLine { get; init; } = string.Empty;
        public string Ring { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public string Build { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string UpdateId { get; init; } = string.Empty;
        public string ServerDate { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Filter { get; init; } = string.Empty;
    }

    public sealed class UupScanSnapshot
    {
        public DateTimeOffset ScanTime { get; init; } = DateTimeOffset.Now;
        public int TotalRequests { get; init; }
        public int FailedRequests { get; init; }
        public List<UupScanEntry> Entries { get; init; } = new();
        public string PayloadFile { get; init; } = string.Empty;
        public string ReportingSku { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public string FlightRing { get; init; } = string.Empty;
        public string CurrentBranch { get; init; } = string.Empty;
        public string SnapshotPath { get; set; } = string.Empty;
    }

    public sealed class UupScanRequest
    {
        public string ReportingSku { get; init; } = "Professional";
        public string ReportingVersion { get; init; } = "10.0.26100.1";
        public string Architecture { get; init; } = "amd64";
        public string FlightRing { get; init; } = "Retail";
        public string FlightingBranchName { get; init; } = string.Empty;
        public string BranchReadinessLevel { get; init; } = "CB";
        public string CurrentBranch { get; init; } = "ge_release";
        public string ReleaseType { get; init; } = "Production";
        public string ContentType { get; init; } = "Mainline";
        public bool SyncCurrentVersionOnly { get; init; }
        public string[] Filters { get; init; } = ["ProductRelease", "OSFlight"];
    }

    public sealed record UupSkuOption(string Name, string DisplayName);

    public sealed class ToolProbeResult
    {
        public string Name { get; init; } = string.Empty;
        public string? ExecutablePath { get; init; }
        public string? WorkingDirectory { get; init; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
        public string Message => IsAvailable ? ExecutablePath! : "未发现工具";
    }
}
