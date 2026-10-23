using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

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
        public string FlightRing { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public string Build { get; init; } = string.Empty;
        public string ReleaseVersion { get; init; } = string.Empty;
        public string Revision { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string UpdateId { get; init; } = string.Empty;
        public string PayloadKey { get; init; } = string.Empty;
        public string ServerDate { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Filter { get; init; } = string.Empty;
        public List<string> Profiles { get; init; } = new();
        public int FileCount { get; init; }
        public long EstimatedDownloadBytes { get; init; }
        [JsonIgnore]
        public string ProfilesSummary => string.Join("; ", Profiles);
        [JsonIgnore]
        public string DetailsSummary =>
            $"SKU：{ProductLine}    架构：{Architecture}\n" +
            $"NT Build：{(string.IsNullOrWhiteSpace(Build) ? "未从扫描元数据解析" : Build)}    " +
            $"版本：{(string.IsNullOrWhiteSpace(ReleaseVersion) ? "未知" : ReleaseVersion)}    修订：{(string.IsNullOrWhiteSpace(Revision) ? "未知" : Revision)}\n" +
            $"通道：{(string.IsNullOrWhiteSpace(FlightRing) ? "未知" : FlightRing)}    " +
            $"文件：{FileCount}    估算下载：{EstimatedSizeText}\n" +
            $"最后变更：{(string.IsNullOrWhiteSpace(ServerDate) ? "未知" : ServerDate)}\n" +
            $"命中组合：{(string.IsNullOrWhiteSpace(ProfilesSummary) ? "未知" : ProfilesSummary)}";
        [JsonIgnore]
        public string EstimatedSizeText => EstimatedDownloadBytes <= 0
            ? "未知"
            : EstimatedDownloadBytes >= 1024L * 1024 * 1024
                ? $"{EstimatedDownloadBytes / (1024d * 1024 * 1024):F1} GiB"
                : EstimatedDownloadBytes >= 1024L * 1024
                    ? $"{EstimatedDownloadBytes / (1024d * 1024):F1} MiB"
                    : $"{EstimatedDownloadBytes / 1024d:F0} KiB";
    }

    public sealed class UupScanSnapshot
    {
        public DateTimeOffset ScanTime { get; init; } = DateTimeOffset.Now;
        public int TotalRequests { get; init; }
        public int FailedRequests { get; init; }
        public List<UupScanEntry> Entries { get; init; } = new();
        public List<UupQueryProfileSummary> QueryProfiles { get; init; } = new();
        public string PayloadFile { get; init; } = string.Empty;
        public string ReportingSku { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public string FlightRing { get; init; } = string.Empty;
        public string CurrentBranch { get; init; } = string.Empty;
        public string SnapshotPath { get; set; } = string.Empty;
    }

    public sealed class UupQueryProfileSummary
    {
        public string Profile { get; init; } = string.Empty;
        public int ResultCount { get; init; }
        public int NewCount { get; init; }
        public int SkippedCount { get; init; }
        public string Error { get; init; } = string.Empty;
    }

    public sealed class UupScanRequest
    {
        public string ReportingSku { get; init; } = "Professional";
        public string ReportingVersion { get; init; } = string.Empty;
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
    public sealed record UupQueryOption(string Value, string DisplayName);
    public sealed record UupLanguageOption(string Code, string DisplayName);

    public sealed class ToolProbeResult
    {
        public string Name { get; init; } = string.Empty;
        public string? ExecutablePath { get; init; }
        public string? WorkingDirectory { get; init; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
        public string Message => IsAvailable ? ExecutablePath! : "未发现工具";
    }
}
