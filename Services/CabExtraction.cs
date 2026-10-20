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
    /// WU container 包（内含 <c>_manifest_.cix.xml</c>，条目按 id 编号、真实名字与 PA30 delta
    /// 都在清单里）由 <c>Cabinet.ContainerCab</c> 按清单还原；sparse 包（同一文件拆成多条
    /// CFFILE，续段用 iFolder 0xFFFD..0xFFFF 标记）与多卷包仍超出本读取器能力，会抛
    /// <see cref="NotSupportedException"/>，由调用方按失败处理（桌面侧不再调用 <c>expand.exe</c>）。
    /// </summary>
    public static class CabExtraction
    {
        const string ContainerManifest = "_manifest_.cix.xml";

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

        /// <summary>列出条目名与解压后大小；container 包给出的是真实文件名。</summary>
        public static List<(string Name, long Size)> List(string cabPath)
        {
            using FileStream fs = OpenStream(cabPath);
            Cabinet.Cabinet cab = ReadCabinet(fs);
            if (IsContainer(cab))
                return Cabinet.ContainerCab.ReadAll(cab).Select(f => (f.Name, (long)f.Content.Length)).ToList();

            return cab.Files
                .Select(f => (f.FileName, (long)f.UncompressedSize))
                .ToList();
        }

        /// <summary>全部解到 <paramref name="destDir"/>（保留包内相对路径）。</summary>
        public static void ExtractAll(string cabPath, string destDir, Action<int, string>? progress = null)
        {
            using FileStream fs = OpenStream(cabPath);
            Cabinet.Cabinet cab = ReadCabinet(fs);
            Directory.CreateDirectory(destDir);
            if (IsContainer(cab))
            {
                Cabinet.ContainerCab.ExtractAll(cab, destDir, progress);
                return;
            }

            EnsurePathsAreSafe(cab);
            cab.ExtractAllFiles(destDir, progress);
        }

        /// <summary>按条目名（大小写不敏感，'/' 与 '\' 等价）取单个文件的原始字节。</summary>
        public static byte[]? ReadEntry(string cabPath, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            using FileStream fs = OpenStream(cabPath);
            Cabinet.Cabinet cab = ReadCabinet(fs);
            string normalized = Normalize(entryName);

            if (IsContainer(cab))
            {
                return Cabinet.ContainerCab.ReadAll(cab)
                    .FirstOrDefault(f => string.Equals(Normalize(f.Name), normalized, StringComparison.OrdinalIgnoreCase))
                    .Content;
            }

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

        static bool IsContainer(Cabinet.Cabinet cab) =>
            cab.Files.Any(f => string.Equals(Path.GetFileName(f.FileName), ContainerManifest, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// sparse 包的续段用 iFolder 0xFFFD..0xFFFF 标记，本读取器不拼分段文件，必须交给
        /// 不支持的包。container 包已由 <c>ContainerCab</c> 支持，不再算不支持。
        /// </summary>
        static bool IsUnsupported(Cabinet.Cabinet cab, out string reason)
        {
            if (cab.IsSparse)
            {
                reason = "sparse CAB (iFolder 0xFFFD..0xFFFF) is not supported by the in-process reader";
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
