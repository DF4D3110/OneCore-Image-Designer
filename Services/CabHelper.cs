using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 轻量 CAB 读取器（列条目 + 抽单文件），完全走进程内的 <see cref="CabExtraction"/>，
    /// 不再调用 Windows 自带的 <c>expand.exe</c>。sparse / 多卷包会明确抛
    /// <see cref="NotSupportedException"/>，由调用方按失败处理。
    /// 结果按 CAB 路径缓存（LastWriteTime 参与键，文件变化自动失效）。
    /// </summary>
    public static class CabHelper
    {
        class CabInfo
        {
            public DateTime Mtime;
            public List<(string name, long size)> Entries = new();
        }

        static readonly ConcurrentDictionary<string, CabInfo> _index =
            new(StringComparer.OrdinalIgnoreCase);

        static CabInfo GetIndex(string cab)
        {
            var mt = File.GetLastWriteTimeUtc(cab);
            if (_index.TryGetValue(cab, out var info) && info.Mtime == mt) return info;
            info = new CabInfo { Mtime = mt, Entries = CabExtraction.List(cab) };
            _index[cab] = info;
            return info;
        }

        public static bool HasXmlEntries(string cab)
        {
            try { return GetIndex(cab).Entries.Any(e =>
                e.name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        /// <summary>枚举 CAB 内全部 *.xml 条目的字节内容。</summary>
        public static IEnumerable<(string entry, byte[] data)> EnumerateXmlEntries(string cab, List<string> errors)
        {
            List<(string, long)> entries;
            try { entries = GetIndex(cab).Entries; }
            catch (Exception ex) { errors.Add($"列 CAB 失败 {Path.GetFileName(cab)}: {ex.Message}"); yield break; }

            foreach (var (name, _) in entries)
            {
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = ExtractEntry(cab, name);
                if (bytes != null) yield return (name, bytes);
            }
        }

        /// <summary>按 FM ID 猜 CAB 内条目名：ID.xml 优先，再任何含 ID 的 .xml。</summary>
        public static string FindFmEntry(string cab, string fmId)
        {
            try
            {
                var xs = GetIndex(cab).Entries
                    .Select(e => e.name)
                    .Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return xs.FirstOrDefault(n => Path.GetFileNameWithoutExtension(n)
                        .Equals(fmId, StringComparison.OrdinalIgnoreCase))
                    ?? xs.FirstOrDefault(n => n.Contains(fmId, StringComparison.OrdinalIgnoreCase))
                    ?? xs.FirstOrDefault() ?? "";
            }
            catch { return ""; }
        }

        public static byte[]? ExtractEntry(string cab, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            try { return CabExtraction.ReadEntry(cab, entryName); }
            catch (NotSupportedException) { return null; }
            catch (InvalidDataException) { return null; }
        }
    }
}
