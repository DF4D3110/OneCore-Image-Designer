using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public class DesignerProject
    {
        public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public string? OemInputXml { get; set; }
        public List<string> FmFiles { get; set; } = new();
        public string MSPackageRoot { get; set; } = string.Empty;
        public string DeviceLayoutCabPath { get; set; } = string.Empty;
        public int StepIndex { get; set; }
        public Dictionary<string, bool> SelectedFeatures { get; set; } = new();
        public List<IntegrationArtifact> IntegrationArtifacts { get; set; } = new();
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public class RecentProjectEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTime LastOpenedUtc { get; set; }
    }

    public static class DesignerProjectService
    {
        public const string Extension = ".cwproj";
        public const string Filter = "设计器工程 (*.cwproj)|*.cwproj|所有文件 (*.*)|*.*";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static string StoreDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneCoreImageDesigner");
        private static string RecentPath => Path.Combine(StoreDir, "recent_projects.json");
        private const int MaxRecent = 6;

        public static DesignerProject? LoadProject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<DesignerProject>(File.ReadAllText(path), JsonOpts);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static bool SaveProject(DesignerProject project, string path)
        {
            try
            {
                project.SavedAtUtc = DateTime.UtcNow;
                File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOpts));
                AddRecent(new RecentProjectEntry
                {
                    Name = string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileNameWithoutExtension(path) : project.Name,
                    Path = path,
                    LastOpenedUtc = DateTime.UtcNow,
                });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static string LastError { get; private set; } = string.Empty;

        public static List<RecentProjectEntry> GetRecentProjects()
        {
            try
            {
                if (!File.Exists(RecentPath)) return new();
                var list = JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(RecentPath)) ?? new();
                var alive = list.Where(e => File.Exists(e.Path)).OrderByDescending(e => e.LastOpenedUtc).Take(MaxRecent).ToList();
                if (alive.Count != list.Count) SaveRecent(alive);
                return alive;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new();
            }
        }

        public static void AddRecent(RecentProjectEntry entry)
        {
            var list = GetRecentProjects();
            list.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, entry);
            if (list.Count > MaxRecent) list = list.Take(MaxRecent).ToList();
            SaveRecent(list);
        }

        private static void SaveRecent(List<RecentProjectEntry> list)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(RecentPath, JsonSerializer.Serialize(list, JsonOpts));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
