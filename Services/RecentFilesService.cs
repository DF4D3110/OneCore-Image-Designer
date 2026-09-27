using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 最近打开文件管理服务
    /// </summary>
    public static class RecentFilesService
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OneCoreImageDesigner", "recent_files.json");

        private static readonly int MaxRecentFiles = 10;

        /// <summary>
        /// 获取最近打开的文件列表
        /// </summary>
        public static List<string> GetRecentFiles()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new List<string>();
                string json = File.ReadAllText(ConfigPath);
                var files = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                // 过滤掉不存在的文件
                return files.Where(File.Exists).Take(MaxRecentFiles).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// 添加文件到最近打开列表
        /// </summary>
        public static void AddRecentFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                var files = GetRecentFiles();
                // 移除已存在的相同路径
                files.RemoveAll(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
                // 插入到最前面
                files.Insert(0, filePath);
                // 限制数量
                if (files.Count > MaxRecentFiles)
                    files = files.Take(MaxRecentFiles).ToList();

                // 保存
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        /// <summary>
        /// 清空最近打开列表
        /// </summary>
        public static void ClearRecentFiles()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    File.Delete(ConfigPath);
            }
            catch { }
        }
    }
}
