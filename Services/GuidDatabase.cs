using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// GUID 数据库
    /// 基于 UEFITool 的 guids.csv，提供 GUID 到名称的映射
    /// </summary>
    public static class GuidDatabase
    {
        private static Dictionary<Guid, string>? _database;
        private static readonly object _lock = new();

        /// <summary>
        /// 初始化数据库（懒加载）
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_database != null) return;

            lock (_lock)
            {
                if (_database != null) return;

                _database = new Dictionary<Guid, string>();

                try
                {
                    // 优先从程序集嵌入资源加载
                    var assembly = Assembly.GetExecutingAssembly();
                    string resourceName = "OneCore_Image_Designer.Services.guids.csv";

                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        LoadFromReader(reader);
                        return;
                    }

                    // 回退：从文件系统加载
                    string csvPath = Path.Combine(AppContext.BaseDirectory, "Services", "guids.csv");
                    if (!File.Exists(csvPath))
                    {
                        csvPath = Path.Combine(AppContext.BaseDirectory, "guids.csv");
                    }
                    if (File.Exists(csvPath))
                    {
                        using var reader = new StreamReader(csvPath);
                        LoadFromReader(reader);
                    }
                }
                catch
                {
                    // 加载失败时使用空数据库
                }
            }
        }

        private static void LoadFromReader(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                int commaIndex = line.IndexOf(',');
                if (commaIndex <= 0) continue;

                string guidStr = line.Substring(0, commaIndex).Trim();
                string name = line.Substring(commaIndex + 1).Trim();

                if (Guid.TryParse(guidStr, out var guid))
                {
                    _database![guid] = name;
                }
            }
        }

        /// <summary>
        /// 根据 GUID 获取名称
        /// </summary>
        public static string GetName(Guid guid)
        {
            EnsureLoaded();
            return _database!.TryGetValue(guid, out var name) ? name : guid.ToString();
        }

        /// <summary>
        /// 根据 GUID 字符串获取名称
        /// </summary>
        public static string GetName(string guidStr)
        {
            if (Guid.TryParse(guidStr, out var guid))
            {
                return GetName(guid);
            }
            return guidStr;
        }

        /// <summary>
        /// 判断 GUID 是否在数据库中
        /// </summary>
        public static bool IsKnown(Guid guid)
        {
            EnsureLoaded();
            return _database!.ContainsKey(guid);
        }

        /// <summary>
        /// 获取数据库中的记录数
        /// </summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _database!.Count;
            }
        }
    }
}
