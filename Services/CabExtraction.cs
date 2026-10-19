using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 进程内 CAB 读取（基于仓库自有的 <c>MobilePackageGen/Cabinet</c> 容器读取器 +
    /// <c>Source/Managed/OcidCabinet</c> 的 MSZIP/LZX 解码器），不再调用 <c>expand.exe</c>。
    ///
    /// 仍有三类包本读取器不支持，会抛 <see cref="NotSupportedException"/>，调用方按需退回
    /// <c>expand.exe</c>：多卷（Previous/Next Cabinet）、sparse 包（同一文件拆成多条 CFFILE，
    /// 续段用 iFolder 0xFFFD..0xFFFF 标记）、以及 WU 的 container 包（内含
    /// <c>_manifest_.cix.xml</c>，条目按 id 编号、真实名字与 PA30 delta 都在清单里，
    /// expand.exe 会按清单还原文件名并应用 delta）。
    /// </summary>
    public static class CabExtraction
    {
        /// <summary>读取器能否完整处理这个包（不检查压缩算法，只检查容器特性）。</summary>
        public static bool IsSupported(string cabPath)
        {
            try
            {
                using FileStream fs = OpenStream(cabPath);
                Cabinet.Cabinet cab = new(fs);
                return !IsUnsupported(cab, out _);
            }
            catch { return false; }
        }

        /// <summary>列出条目名与解压后大小。</summary>
        public static List<(string Name, long Size)> List(string cabPath)
        {
            using FileStream fs = OpenStream(cabPath);
            return ReadCabinet(fs).Files
                .Select(f => (f.FileName, (long)f.UncompressedSize))
                .ToList();
        }

        /// <summary>全部解到 <paramref name="destDir"/>（保留包内相对路径）。</summary>
        public static void ExtractAll(string cabPath, string destDir, Action<int, string>? progress = null)
        {
            using FileStream fs = OpenStream(cabPath);
            Cabinet.Cabinet cab = ReadCabinet(fs);
            EnsurePathsAreSafe(cab);
            Directory.CreateDirectory(destDir);
            cab.ExtractAllFiles(destDir, progress);
        }

        /// <summary>按条目名（大小写不敏感，'/' 与 '\' 等价）取单个文件的原始字节。</summary>
        public static byte[]? ReadEntry(string cabPath, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            using FileStream fs = OpenStream(cabPath);
            Cabinet.Cabinet cab = ReadCabinet(fs);
            string normalized = Normalize(entryName);
            string? match = cab.Files
                .Select(f => f.FileName)
                .FirstOrDefault(n => string.Equals(Normalize(n), normalized, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            EnsurePathsAreSafe(cab);
            return cab.ReadFile(match);
        }

        /// <summary>尝试全部解出；失败返回 false 并把原因写进 <paramref name="error"/>（供调用方回退）。</summary>
        public static bool TryExtractAll(string cabPath, string destDir, out string error)
        {
            try
            {
                ExtractAll(cabPath, destDir);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static FileStream OpenStream(string cabPath) =>
            new(cabPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        static Cabinet.Cabinet ReadCabinet(FileStream fs)
        {
            Cabinet.Cabinet cab = new(fs);
            if (IsUnsupported(cab, out string reason))
                throw new NotSupportedException(reason);
            return cab;
        }

        /// <summary>
        /// container 包的条目按 id 编号，真实文件名与 PA30 delta 都写在
        /// <c>_manifest_.cix.xml</c> 里，只有 expand.exe 会按清单还原；sparse 包的续段则用
        /// iFolder 0xFFFD..0xFFFF 标记。两种都超出本读取器能力，必须交给 expand.exe。
        /// </summary>
        static bool IsUnsupported(Cabinet.Cabinet cab, out string reason)
        {
            if (cab.IsSparse)
            {
                reason = "sparse CAB (iFolder 0xFFFD..0xFFFF) is not supported by the in-process reader";
                return true;
            }

            if (cab.Files.Any(f => Path.GetFileName(f.FileName)
                    .Equals("_manifest_.cix.xml", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "container CAB (_manifest_.cix.xml with numbered payloads) needs expand.exe";
                return true;
            }

            reason = "";
            return false;
        }

        static string Normalize(string name) =>
            name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        /// <summary>拒绝绝对路径与 ".." 段，避免包内容写到目标目录之外。</summary>
        static void EnsurePathsAreSafe(Cabinet.Cabinet cab)
        {
            foreach (Cabinet.CabinetFile f in cab.Files)
            {
                string normalized = Normalize(f.FileName);
                if (Path.IsPathRooted(normalized) ||
                    normalized.Split(Path.DirectorySeparatorChar).Any(p => p == ".."))
                {
                    throw new InvalidDataException($"CAB entry escapes the destination directory: {f.FileName}");
                }
            }
        }
    }
}
