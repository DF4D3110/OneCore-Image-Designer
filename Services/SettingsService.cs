using System;
using System.IO;
using System.Text.Json;

namespace OneCore_Image_Designer.Services
{
    public class AppSettings
    {
        public double SelectReconstructRightColumnWidth { get; set; } = 350; // 像素
        public string LastImagePath { get; set; } = "";
        public string LastOutputPath { get; set; } = "";
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 800;
    }

    public static class SettingsService
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OneCore Image Designer");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        private static AppSettings? _current;
        public static AppSettings Current => _current ??= Load();

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    return settings ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                {
                    Directory.CreateDirectory(SettingsDir);
                }
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        public static void Save(Action<AppSettings> update)
        {
            update(Current);
            Save();
        }
    }
}
