using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 基于 expand.exe 的轻量 CAB 读取器（列条目 + 抽单文件）。
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
            info = new CabInfo { Mtime = mt, Entries = ListEntriesRaw(cab) };
            _index[cab] = info;
            return info;
        }

        /// <summary>expand.exe -D 输出行: "path\to.cab: entryname"。条目名含路径分隔时归一。</summary>
        static List<(string, long)> ListEntriesRaw(string cab)
        {
            var list = new List<(string, long)>();
            var psi = new ProcessStartInfo
            {
                FileName = "expand.exe",
                Arguments = $"-D \"{cab}\"",
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return list;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                foreach (var line in outp.Split('\n'))
                {
                    int idx = line.IndexOf(".cab: ", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    string name = line.Substring(idx + 6).Trim();
                    if (name.Length > 0) list.Add((name, 0));
                }
            }
            catch { /* expand 不可用：调用方按失败处理 */ }
            return list;
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
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "icd_cab_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "expand.exe",
                        Arguments = $"-F:\"{entryName}\" \"{cab}\" \"{tmp}\"",
                        UseShellExecute = false, RedirectStandardOutput = true,
                        RedirectStandardError = true, CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(20000);
                    if (p != null && p.ExitCode != 0) return null;

                    // expand 可能保留目录结构：按条目基名找
                    string baseName = Path.GetFileName(entryName);
                    var hit = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileName(f).Equals(baseName,
                            StringComparison.OrdinalIgnoreCase));
                    return hit != null ? File.ReadAllBytes(hit) : null;
                }
                finally { try { Directory.Delete(tmp, true); } catch { } }
            }
            catch { return null; }
        }
    }
}
