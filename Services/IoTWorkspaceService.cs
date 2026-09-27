using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>Reads the 17763 AddonKit workspace metadata without touching certificate private keys.</summary>
    public static class IoTWorkspaceService
    {
        public static IoTWorkspaceConfig Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("IoTWorkspace.xml 不存在。", filePath);

            var result = new IoTWorkspaceConfig { FilePath = filePath };
            try
            {
                var doc = XDocument.Load(filePath, LoadOptions.None);
                var root = doc.Root;
                if (!string.Equals(root?.Name.LocalName, "IoTWorkspace", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("根元素不是 IoTWorkspace。");
                    return result;
                }

                var config = root.Element("Config");
                result = new IoTWorkspaceConfig
                {
                    FilePath = filePath,
                    CurrentEnvironment = (string?)root.Element("CurrentEnv") ?? string.Empty,
                    OemName = (string?)config?.Element("OemName") ?? string.Empty,
                    BuildDirectory = (string?)config?.Element("BuildDir") ?? string.Empty,
                    MicrosoftPackageRoot = (string?)config?.Element("MSPkgRoot") ?? string.Empty,
                    WindowsSdkVersion = (string?)config?.Element("WindowsSDKVersion") ?? string.Empty,
                    EnvironmentNames = root.Elements("Env")
                        .Select(e => (string?)e.Attribute("Name"))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    SecurityCertificates = root.Descendants("Cert")
                        .Select(e => e.Value.Trim())
                        .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };

                if (string.IsNullOrWhiteSpace(result.CurrentEnvironment))
                    result.Warnings.Add("未指定 CurrentEnv。");
                if (string.IsNullOrWhiteSpace(result.OemName))
                    result.Warnings.Add("未指定 OemName。");
                if (result.EnvironmentNames.Count == 0)
                    result.Warnings.Add("未发现任何架构环境。");
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                result.Warnings.Add($"工作区 XML 无法读取: {ex.Message}");
                return result;
            }
        }

        public static ProjectValidationReport Validate(string filePath, string? workspaceRoot = null)
            {
                var report = new ProjectValidationReport();
                IoTWorkspaceConfig config;
                try
                {
                    config = Load(filePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Diagnostics.Add(new ValidationDiagnostic(
                        ValidationSeverity.Error, "IoTWorkspace", ex.Message, filePath));
                    return report;
                }

                foreach (var warning in config.Warnings)
                    report.Diagnostics.Add(new ValidationDiagnostic(
                        ValidationSeverity.Warning, "IoTWorkspace", warning, filePath));

                string Resolve(string value) =>
                    string.IsNullOrWhiteSpace(value) ? string.Empty :
                    value.Contains('%', StringComparison.Ordinal) ? string.Empty :
                    Path.IsPathRooted(value) || string.IsNullOrWhiteSpace(workspaceRoot)
                        ? value : Path.GetFullPath(Path.Combine(workspaceRoot, value));

                foreach (var pair in new[]
                {
                    ("BuildDir", config.BuildDirectory),
                    ("MSPkgRoot", config.MicrosoftPackageRoot)
                })
                {
                    if (string.IsNullOrWhiteSpace(pair.Item2))
                        continue;
                    string resolved = Resolve(pair.Item2);
                    if (string.IsNullOrWhiteSpace(resolved))
                        report.Diagnostics.Add(new ValidationDiagnostic(
                            ValidationSeverity.Information, "IoTWorkspace",
                            $"{pair.Item1} 使用构建变量，需在目标环境展开。", pair.Item2));
                    else
                        report.Diagnostics.Add(new ValidationDiagnostic(
                            Directory.Exists(resolved) ? ValidationSeverity.Information : ValidationSeverity.Warning,
                            "IoTWorkspace",
                            Directory.Exists(resolved) ? $"{pair.Item1} 可用。" : $"{pair.Item1} 不存在。",
                            resolved));
                }

                report.Diagnostics.Add(new ValidationDiagnostic(
                    ValidationSeverity.Information, "版本",
                    VersionCapabilityService.GetProfile(PlatformGeneration.IoTCore17763).DisplayName,
                    filePath));
                return report;
            }
        }
}
