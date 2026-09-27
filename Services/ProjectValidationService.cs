using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Models.FM;

namespace OneCore_Image_Designer.Services
{
    public static class ProjectValidationService
    {
        public static ProjectValidationReport ValidateOemInputDocument(OEMInputDoc doc)
        {
            var report = new ProjectValidationReport();
            var detailed = OEMInputService.ValidateDetailed(doc);
            foreach (var error in detailed.Errors)
                Add(report, ValidationSeverity.Error, "OEMInput", error);
            foreach (var warning in detailed.Warnings)
                Add(report, ValidationSeverity.Warning, "OEMInput", warning);
            Add(report, ValidationSeverity.Information, "版本",
                VersionCapabilityService.GetProfile(doc.IsSchemaV2
                    ? PlatformGeneration.CoreOs20348 : PlatformGeneration.IoTCore17763).DisplayName);
            return report;
        }

        public static ProjectValidationReport ValidateOemInput(string path)
        {
            if (!File.Exists(path))
                return Error("OEMInput", "文件不存在。", path);
            var doc = OEMInputService.LoadFromFile(path);
            return doc is null
                ? Error("OEMInput", "无法解析 OEMInput XML。", path)
                : ValidateOemInputDocument(doc);
        }

        public static ProjectValidationReport ValidateFeatureManifests(IEnumerable<string> paths)
        {
            var report = new ProjectValidationReport();
            var manifests = new List<FeatureManifest>();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    Add(report, ValidationSeverity.Warning, "FM", "FM 文件不存在。", path);
                    continue;
                }
                var fm = FMService.ParseFM(path);
                if (fm is null)
                {
                    Add(report, ValidationSeverity.Error, "FM", "FM XML 无法解析。", path);
                    continue;
                }
                manifests.Add(fm);
            }

            var packagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fm in manifests)
            {
                foreach (var package in fm.BasePackages.Concat(fm.Features.MicrosoftPackages)
                    .Concat(fm.Features.OEMPackages).Concat(fm.DeviceLayoutPackages))
                {
                    var name = string.IsNullOrWhiteSpace(package.Name) ? package.ID : package.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        Add(report, ValidationSeverity.Warning, "FM", "PackageFile 缺少 Name/ID。", fm.FilePath);
                    else if (packagePaths.TryGetValue(name, out var previous))
                        Add(report, ValidationSeverity.Warning, "FM", $"重复包名: {name}（另见 {previous}）", fm.FilePath);
                    else
                        packagePaths[name] = fm.FilePath;

                    if (!string.IsNullOrWhiteSpace(package.Path) && package.Path.Contains('%'))
                        Add(report, ValidationSeverity.Information, "FM",
                            $"包路径包含构建变量: {package.Path}", fm.FilePath);
                }
            }

            foreach (var group in FMService.ExtractFeatureGroups(manifests))
                if (group.ConstraintType == FeatureGroupConstraint.None)
                    Add(report, ValidationSeverity.Warning, "FM",
                        $"FeatureGroup 未识别约束: {group.Name}", group.SourceFM);

            Add(report, ValidationSeverity.Information, "版本",
                manifests.Any(f => f.Features.MSFeatureGroups.Count > 0 || f.Features.OEMFeatureGroups.Count > 0)
                    ? "检测到 FeatureGroup，按现代 FM 能力处理"
                    : "未检测到 FeatureGroup，按传统 FM 能力处理");
            return report;
        }

        public static ProjectValidationReport ValidateDeviceLayout(string path, string? ruleFile = null)
        {
            if (!File.Exists(path))
                return Error("DeviceLayout", "布局文件不存在。", path);
            var report = new ProjectValidationReport();
            var layout = DeviceLayoutService.ParseFromXml(path);
            if (layout is null)
                return Error("DeviceLayout", "无法解析 DeviceLayout XML。", path);

            layout.ApplySectorSize();
            Add(report, ValidationSeverity.Information, "DeviceLayout",
                $"已解析 {layout.AllPartitions.Count} 个分区，扇区大小 {layout.SectorSize}。", path);
            if (!string.IsNullOrWhiteSpace(ruleFile) && File.Exists(ruleFile))
            {
                var result = DeviceLayoutRuleChecker.CheckWithParentSemantics(layout, ruleFile);
                foreach (var issue in result.Issues)
                    Add(report, issue.IsError ? ValidationSeverity.Error : ValidationSeverity.Warning,
                        "DeviceLayout", issue.Message, issue.Where);
            }
            return report;
        }

        public static ProjectValidationReport ValidateProject(DesignerProject project, string? projectDirectory = null)
        {
            var report = new ProjectValidationReport();
            if (project == null)
                return Error("工程", "工程对象为空。");

            string Resolve(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty :
                Path.IsPathRooted(value) || string.IsNullOrWhiteSpace(projectDirectory)
                    ? value : Path.GetFullPath(Path.Combine(projectDirectory, value));

            Merge(report, string.IsNullOrWhiteSpace(project.OemInputXml)
                ? Warning("工程", "未关联 OEMInput.xml。")
                : ValidateOemInput(Resolve(project.OemInputXml)));

            var fmPaths = project.FmFiles.Select(Resolve).Where(p => p.Length > 0).ToList();
            Merge(report, fmPaths.Count == 0
                ? Warning("工程", "未关联 FM 文件。")
                : ValidateFeatureManifests(fmPaths));

            if (string.IsNullOrWhiteSpace(project.DeviceLayoutCabPath))
                Add(report, ValidationSeverity.Warning, "工程", "未关联 DeviceLayout。");
            else
            {
                var layoutPath = Resolve(project.DeviceLayoutCabPath);
                Merge(report, Path.GetExtension(layoutPath).Equals(".cab", StringComparison.OrdinalIgnoreCase)
                    ? ValidateDeviceLayoutCab(layoutPath) : ValidateDeviceLayout(layoutPath));
            }

            if (string.IsNullOrWhiteSpace(project.MSPackageRoot))
                Add(report, ValidationSeverity.Warning, "工具链", "未配置 MSPackageRoot。");
            else
            {
                var root = Resolve(project.MSPackageRoot);
                Add(report, Directory.Exists(root) ? ValidationSeverity.Information : ValidationSeverity.Warning,
                    "工具链", Directory.Exists(root) ? $"MSPackageRoot 可用: {root}" : $"MSPackageRoot 不存在: {root}", root);
            }

            foreach (var artifact in project.IntegrationArtifacts ?? new List<IntegrationArtifact>())
            {
                if (string.IsNullOrWhiteSpace(artifact.Path))
                {
                    Add(report, ValidationSeverity.Error, "集成产物",
                        $"产物“{artifact.Description}”没有路径。");
                    continue;
                }

                var artifactPath = Resolve(artifact.Path);
                Add(report, artifact.Exists || File.Exists(artifactPath) || Directory.Exists(artifactPath)
                    ? ValidationSeverity.Information : ValidationSeverity.Warning,
                    "集成产物",
                    File.Exists(artifactPath) || Directory.Exists(artifactPath)
                        ? $"产物可用: {artifact.Description}"
                        : $"产物不存在: {artifact.Description}",
                    artifactPath);
            }
            return report;
        }

        private static ProjectValidationReport ValidateDeviceLayoutCab(string path)
        {
            if (!File.Exists(path))
                return Error("DeviceLayout", "布局 CAB 不存在。", path);
            var report = new ProjectValidationReport();
            var layout = DeviceLayoutService.ParseFromCab(path);
            if (layout is null)
                return Error("DeviceLayout", "无法从 CAB 解析 DeviceLayout。", path);
            Add(report, ValidationSeverity.Information, "DeviceLayout",
                $"已从 CAB 解析 {layout.AllPartitions.Count} 个分区，扇区大小 {layout.SectorSize}。", path);
            return report;
        }

        private static ProjectValidationReport Error(string source, string message, string? location = null)
        {
            var report = new ProjectValidationReport();
            Add(report, ValidationSeverity.Error, source, message, location);
            return report;
        }

        private static ProjectValidationReport Warning(string source, string message)
        {
            var report = new ProjectValidationReport();
            Add(report, ValidationSeverity.Warning, source, message);
            return report;
        }

        private static void Merge(ProjectValidationReport target, ProjectValidationReport source) =>
            target.Diagnostics.AddRange(source.Diagnostics);

        private static void Add(ProjectValidationReport report, ValidationSeverity severity,
            string source, string message, string? location = null) =>
            report.Diagnostics.Add(new ValidationDiagnostic(severity, source, message, location));
    }
}
