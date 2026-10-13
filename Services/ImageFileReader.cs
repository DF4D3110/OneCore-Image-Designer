using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using MobilePackageGen;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 把「磁盘N[存储池]\分区名\相对路径」形态的树节点路径解析回镜像内 (diskIndex, partitionIndex, fsPath)，
    /// 从对应分区文件系统读出文件内容；XML 自动格式化，二进制探测后只给摘要。
    /// </summary>
    public static class ImageFileReader
    {
        public const int MaxPreviewBytes = 2 * 1024 * 1024;   // 2MB 截断保护

        public class ReadResult
        {
            public bool Ok;
            public string Text = "";
            public string Note = "";     // 截断/二进制/错误说明
            public bool IsXml;
        }

        /// <summary>解析树路径 → (diskIndex, partitionIndex, relative path inside fs)。失败返回 false。</summary>
        public static bool TryParseTreePath(string treePath, IEnumerable<IDisk> disks,
            out int diskIndex, out int partitionIndex, out string fsPath)
        {
            diskIndex = partitionIndex = -1; fsPath = "";
            if (string.IsNullOrWhiteSpace(treePath)) return false;
            var parts = treePath.Split('\\');
            if (parts.Length < 3) return false;
            var m = System.Text.RegularExpressions.Regex.Match(parts[0], @"^磁盘(\d+)(\[存储池\])?$");
            if (!m.Success) return false;
            diskIndex = int.Parse(m.Groups[1].Value);
            string wantPart = parts[1];
            var disk = disks.ElementAtOrDefault(diskIndex);
            if (disk == null) { diskIndex = -1; return false; }
            var parts2 = disk.Partitions.ToList();
            for (int i = 0; i < parts2.Count; i++)
            {
                if (string.Equals(parts2[i].Name, wantPart, StringComparison.OrdinalIgnoreCase))
                {
                    partitionIndex = i;
                    fsPath = string.Join("\\", parts.Skip(2));
                    return true;
                }
            }
            diskIndex = -1;
            return false;
        }

        public static ReadResult Read(IDisk disk, int partitionIndex, string fsPath)
        {
            var r = new ReadResult();
            try
            {
                var fs = disk.Partitions.ElementAtOrDefault(partitionIndex)?.FileSystem;
                if (fs == null) { r.Note = "分区文件系统不可用"; return r; }
                if (!fs.FileExists(fsPath)) { r.Note = "文件在镜像中不存在: " + fsPath; return r; }
                var fi = fs.GetFileInfo(fsPath);
                long len = fi.Length;
                using var s = fs.OpenFile(fsPath, FileMode.Open, FileAccess.Read);
                int take = (int)Math.Min(len, MaxPreviewBytes);
                var buf = new byte[take];
                int off = 0;
                while (off < take)
                {
                    int n = s.Read(buf, off, take - off);
                    if (n <= 0) break;
                    off += n;
                }

                // 二进制探测：NUL 或高比例不可打印 → 不展开
                int ctrl = 0;
                for (int i = 0; i < Math.Min(off, 4096); i++)
                    if (buf[i] == 0) { ctrl++; if (ctrl > 4) break; }
                if (ctrl > 4)
                {
                    r.Ok = true;
                    r.Text = $"（二进制文件，不预览。大小 {len:N0} 字节）";
                    return r;
                }

                string text = DetectAndDecode(buf, off);
                r.Ok = true;
                if (len > MaxPreviewBytes)
                    r.Note = $"仅显示前 {MaxPreviewBytes / 1024} KB（完整 {len:N0} 字节）";

                if (LooksXml(text))
                {
                    r.IsXml = true;
                    try
                    {
                        var doc = new XmlDocument { PreserveWhitespace = false };
                        doc.LoadXml(text);
                        var sb = new StringBuilder();
                        using var w = new XmlTextWriter(new StringWriter(sb))
                        { Formatting = Formatting.Indented, Indentation = 2 };
                        doc.WriteTo(w);
                        text = sb.ToString();
                    }
                    catch { r.Note = (r.Note + " XML 格式异常，按原文显示").Trim(); }
                }
                r.Text = text;
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Note = "读取失败: " + ex.Message;
                return r;
            }
        }

        static string DetectAndDecode(byte[] buf, int len)
        {
            if (len >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                return Encoding.UTF8.GetString(buf, 3, len - 3);
            if (len >= 2 && buf[0] == 0xFF && buf[1] == 0xFE)
                return Encoding.Unicode.GetString(buf, 2, len - 2);
            if (len >= 2 && buf[0] == 0xFE && buf[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(buf, 2, len - 2);
            if (len >= 2 && buf[0] == 0x3C && buf[1] == 0x00)   // "<\0" = UTF-16LE no BOM
                return Encoding.Unicode.GetString(buf, 0, len & ~1);
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        static bool LooksXml(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i < s.Length && s[i] == '<';
        }
    }
}
