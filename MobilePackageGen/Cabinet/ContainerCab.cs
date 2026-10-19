using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OcidCabinet;

namespace Cabinet
{
    /// <summary>One entry of a container cabinet's <c>_manifest_.cix.xml</c>.</summary>
    public sealed class ContainerIndexFile
    {
        public string Name { get; internal set; } = string.Empty;
        public long Length { get; internal set; }
        public string Hash { get; internal set; } = string.Empty;
        public string HashAlgorithm { get; internal set; } = "SHA1";
        public string SourceType { get; internal set; } = "RAW";
        public string SourceName { get; internal set; } = string.Empty;

        public override string ToString() => $"{Name} ({Length} bytes, {SourceType})";
    }

    /// <summary>
    /// Parser for the container index a Windows Update "container" cab carries as
    /// <c>_manifest_.cix.xml</c>.
    /// </summary>
    /// <remarks>
    /// <para>Layout (reverse engineered from dpx.dll, which is what <c>expand.exe</c> uses for
    /// these cabs; <c>cabinet.dll</c> itself does not know about container indexes):</para>
    /// <code>
    /// &lt;Container xmlns="urn:ContainerIndex" name=... type="CAB" length=... version="1"&gt;
    ///   &lt;File id="664" name="amd64_..\wucltux.dll.mui" length="39936" time="..." attr="32"&gt;
    ///     &lt;Hash alg="SHA1" value="..."/&gt;
    ///     &lt;Delta&gt;&lt;Source type="RAW|PA30" name="0"&gt;&lt;Hash alg="SHA1" value="..."/&gt;&lt;/Source&gt;&lt;/Delta&gt;
    ///   &lt;/File&gt;
    /// ...
    /// </code>
    /// <para><c>Source name</c> is the cab entry that carries the payload; <c>RAW</c> payloads are the
    /// file itself, <c>PA30</c> payloads are a delta that must be applied to a basis taken from
    /// another entry of the same cabinet. Entries are ordered exactly like the index, so the entry
    /// at position i belongs to file i; entry names must not be matched against <c>name</c> because
    /// the index abbreviates component names (<c>w..client-ui</c>).</para>
    /// </remarks>
    public sealed class ContainerIndex
    {
        private ContainerIndex(IReadOnlyList<ContainerIndexFile> files) => Files = files;

        public IReadOnlyList<ContainerIndexFile> Files { get; }

        public static ContainerIndex Parse(byte[] xml)
        {
            XDocument document;
            using (var stream = new MemoryStream(xml, writable: false))
                document = XDocument.Load(stream);

            XElement root = document.Root ?? throw new InvalidDataException("container index: no root element");
            XNamespace ns = root.Name.Namespace;

            var files = new List<ContainerIndexFile>();
            foreach (XElement file in root.Descendants(ns + "File"))
            {
                string name = (string?)file.Attribute("name") ?? throw new InvalidDataException("container index: <File> without name");
                XElement? hash = file.Element(ns + "Hash");
                XElement? source = file.Element(ns + "Delta")?.Element(ns + "Source");

                files.Add(new ContainerIndexFile
                {
                    Name = name,
                    Length = (long?)file.Attribute("length") ?? 0,
                    Hash = NormalizeHash((string?)hash?.Attribute("value")),
                    HashAlgorithm = (string?)hash?.Attribute("alg") ?? "SHA1",
                    SourceType = (string?)source?.Attribute("type") ?? "RAW",
                    SourceName = (string?)source?.Attribute("name") ?? name,
                });
            }

            if (files.Count == 0)
                throw new InvalidDataException("container index: no <File> entries");

            return new ContainerIndex(files);
        }

        private static string NormalizeHash(string? value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }
    }

    /// <summary>
    /// Extracts container cabinets (the ones carrying a <c>_manifest_.cix.xml</c> with numbered
    /// payload entries).
    /// </summary>
    public static class ContainerCab
    {
        public const string IndexEntryName = "_manifest_.cix.xml";

        public static bool IsContainer(Cabinet cabinet)
        {
            if (cabinet is null) throw new ArgumentNullException(nameof(cabinet));
            return cabinet.Files.Any(f => string.Equals(f.FileName, IndexEntryName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Expands every file described by the container index and returns it under its real name.
        /// </summary>
        public static IReadOnlyList<(string Name, byte[] Content)> ReadAll(Cabinet cabinet, Action<int, string>? progress = null)
        {
            if (cabinet is null) throw new ArgumentNullException(nameof(cabinet));

            List<string> entryNames = cabinet.Files.Select(f => f.FileName).ToList();
            string indexEntry = entryNames.FirstOrDefault(n => string.Equals(n, IndexEntryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("not a container cabinet: no " + IndexEntryName);

            var payloadNames = entryNames.Where(n => !string.Equals(n, indexEntry, StringComparison.OrdinalIgnoreCase)).ToList();
            ContainerIndex index = ContainerIndex.Parse(cabinet.ReadFile(indexEntry));

            if (payloadNames.Count != index.Files.Count)
                throw new InvalidDataException($"container index lists {index.Files.Count} files but the cabinet stores {payloadNames.Count} payload entries");

            var payloads = new byte[payloadNames.Count][];
            for (int i = 0; i < payloadNames.Count; i++)
            {
                payloads[i] = cabinet.ReadFile(payloadNames[i]);
                progress?.Invoke(5 + (int)(40.0 * (i + 1) / payloadNames.Count), payloadNames[i]);
            }

            var contents = new byte[index.Files.Count][];
            var pending = new List<int>();
            for (int i = 0; i < index.Files.Count; i++)
            {
                if (IsRaw(index.Files[i]))
                    contents[i] = payloads[i];
                else
                    pending.Add(i);
            }

            // Delta payloads need a basis that lives in another entry of the same cabinet. The index
            // does not record which one (dpx.dll matches it by content hash), so resolve by applying
            // the delta to already-decoded entries and checking the target hash from the index.
            int resolvedPass = 0;
            while (pending.Count > 0)
            {
                bool progressed = false;
                var stillPending = new List<int>();
                foreach (int i in pending)
                {
                    byte[]? resolved = TryResolveDelta(index.Files[i], payloads[i], contents);
                    if (resolved is null)
                    {
                        stillPending.Add(i);
                    }
                    else
                    {
                        contents[i] = resolved;
                        progressed = true;
                        progress?.Invoke(45 + (int)(55.0 * (index.Files.Count - stillPending.Count) / index.Files.Count), index.Files[i].Name);
                    }
                }

                pending = stillPending;
                if (!progressed)
                    break;
                resolvedPass++;
            }

            if (pending.Count > 0)
                throw new InvalidDataException($"container index: {pending.Count} delta payload(s) have no basis inside the cabinet (first: {index.Files[pending[0]].Name})");

            for (int i = 0; i < index.Files.Count; i++)
            {
                byte[] content = contents[i] ?? Array.Empty<byte>();
                if (index.Files[i].Hash.Length != 0 && !string.Equals(HashOf(content, index.Files[i].HashAlgorithm), index.Files[i].Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"container index: {index.Files[i].HashAlgorithm} mismatch for '{index.Files[i].Name}'");
                if (index.Files[i].Length != 0 && index.Files[i].Length != content.Length)
                    throw new InvalidDataException($"container index: length mismatch for '{index.Files[i].Name}' (index says {index.Files[i].Length}, decoded {content.Length})");
                contents[i] = content;
            }

            _ = resolvedPass;
            return index.Files.Select((file, i) => (file.Name, contents[i] ?? Array.Empty<byte>())).ToList();
        }

        public static void ExtractAll(Cabinet cabinet, string destinationDirectory, Action<int, string>? progress = null)
        {
            if (destinationDirectory is null) throw new ArgumentNullException(nameof(destinationDirectory));
            IReadOnlyList<(string Name, byte[] Content)> files = ReadAll(cabinet, progress);

            string root = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(root);
            for (int i = 0; i < files.Count; i++)
            {
                string relative = NormalizeEntryName(files[i].Name);
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"container index: entry '{files[i].Name}' escapes the destination directory");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, files[i].Content);
                progress?.Invoke(95, relative);
            }
            progress?.Invoke(100, string.Empty);
        }

        /// <summary>Rejects rooted names and parent traversal inside an index entry name.</summary>
        public static string NormalizeEntryName(string name)
        {
            string normalized = name.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0)
                    throw new InvalidDataException($"container index: unsafe entry name '{name}'");
            }
            return string.Join(Path.DirectorySeparatorChar, parts);
        }

        private static bool IsRaw(ContainerIndexFile file)
        {
            return string.Equals(file.SourceType, "RAW", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[]? TryResolveDelta(ContainerIndexFile file, byte[] delta, byte[][] contents)
        {
            if (!Pa30Delta.IsPa30(delta))
                return null;

            for (int candidate = 0; candidate < contents.Length; candidate++)
            {
                byte[]? basis = contents[candidate];
                if (basis is null || ReferenceEquals(basis, delta))
                    continue;

                byte[]? result = Pa30Delta.TryApply(basis, delta);
                if (result is null)
                    continue;
                if (file.Hash.Length != 0 && !string.Equals(HashOf(result, file.HashAlgorithm), file.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;
                return result;
            }

            return null;
        }

        private static string HashOf(byte[] data, string algorithm)
        {
            // The index names the digest it stores; SHA-1 is what Windows Update uses, SHA-256 shows
            // up in tiny packages (dummyrepairpackage_*.cab) whose payload is empty.
            return algorithm.ToUpperInvariant() switch
            {
                "SHA1" or "SHA-1" => Convert.ToHexString(SHA1.HashData(data)),
                "SHA256" or "SHA-256" => Convert.ToHexString(SHA256.HashData(data)),
                "MD5" => Convert.ToHexString(MD5.HashData(data)),
                _ => throw new InvalidDataException($"container index: unsupported hash algorithm '{algorithm}'"),
            };
        }
    }
}
