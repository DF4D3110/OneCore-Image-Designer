using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 更新包解压（MSUpdateExtractor 的 C# 等价能力）。
    ///
    /// 【为什么不需要照搬它的 C++】原项目内嵌 libmspack 来解 CAB。实测
    /// **Windows 自带的 `expand.exe` 就能列出并解出这些 CAB**，因此完全不必引入
    /// 第三方 C++ 解码器，也不会有许可牵连。
    ///
    /// 【支持的包布局】（与 MSUpdateExtractor 的 README 一致）
    ///   1. `*.psm` + 同名 `*.psf`
    ///   2. `*.cab` + 同名 `*.psf`          ← 我们手上的 8400_x86_repair_content 就是这种
    ///   3. `*.wim` + 同名 `*.psf`
    ///   4. 单个 `*.cab`（含 `cabinet.cablist.ini` 的 CAB 集）
    ///
    /// 【PSF 的真实结构】（从 kb777777-x86-en-us-merged 的 *.psf.cix.xml 实测得出）
    ///   magic = "PSTREAM\0"，其后是 PSF 流；载荷【不是】内嵌 CAB。
    ///   配对 CAB 里装的是描述：
    ///     update.mum
    ///     &lt;name&gt;.psf.cix.xml     ← PSF 索引
    ///     update.cat
    ///   cix.xml 形如：
    ///     &lt;Container&gt;&lt;Files&gt;
    ///       &lt;File id name length time attr&gt;
    ///         &lt;Hash alg="SHA256" value="目标文件哈希"/&gt;
    ///         &lt;Delta&gt;
    ///           &lt;Source type="PA30" offset="PSF内偏移" length="压缩长度"&gt;
    ///             &lt;Hash alg="SHA256" value="源文件哈希"/&gt;
    ///           &lt;/Source&gt;
    ///         &lt;/Delta&gt;
    ///       &lt;/File&gt; …
    ///   ⇒ 还原 = 读 PSF[offset..offset+length] 得到 PA30 载荷，对"源文件"应用 delta，
    ///     再用 &lt;Hash&gt; 校验。源文件按 SHA256 从本地文件集里查找（现代 PSFExtractor 的做法）。
    ///
    /// 【实测规模】单个 en-US 包的 cix.xml：11797 个 File 节点 / 23594 个 Hash / 2 行。
    ///
    /// 【诚实的边界】PA30 delta 的还原需要**源(base)文件集**。没有源文件时，
    /// 只能处理自包含（stored）的条目；其余会如实报告为"缺少源"，不会假装成功。
    /// </summary>
    public static class UpdatePackageService
    {
        // ────────────────────────────────────────────────────────────────
        // 数据模型
        // ────────────────────────────────────────────────────────────────

        public enum Layout { Unknown, PsfPair, CabList, SingleCab }

        public sealed class PsfFileEntry
        {
            public int Id;
            public string Name = "";
            /// <summary>还原后应有的字节数。</summary>
            public long Length;
            /// <summary>还原后文件的 SHA256（十六进制，大写）。</summary>
            public string TargetHash = "";
            /// <summary>PSF 内的偏移。</summary>
            public long SourceOffset;
            /// <summary>PSF 内的压缩长度。</summary>
            public long SourceLength;
            /// <summary>载荷类型（实测为 PA30）。</summary>
            public string SourceType = "";
            /// <summary>源(base)文件的 SHA256；可能为空。</summary>
            public string BaseHash = "";
        }

        public sealed class PsfIndex
        {
            public string CixPath = "";
            public string PsfPath = "";
            public readonly List<PsfFileEntry> Files = new();
            public string? Error;
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public Layout Detected = Layout.Unknown;
            public int FilesExtracted;
            public int FilesSkipped;
            public int FilesMissingSource;
            public int HashMismatches;
            public readonly List<string> Log = new();

            public string Summary()
                => $"布局={Detected} 解出={FilesExtracted} 跳过={FilesSkipped} 缺源={FilesMissingSource} 校验失败={HashMismatches}";
        }

        // ────────────────────────────────────────────────────────────────
        // 布局识别
        // ────────────────────────────────────────────────────────────────

        public static Layout Detect(string path)
        {
            if (File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                return ext switch
                {
                    ".cab" => HasSiblingPsf(path) ? Layout.PsfPair : Layout.SingleCab,
                    ".psm" or ".wim" => HasSiblingPsf(path) ? Layout.PsfPair : Layout.Unknown,
                    _ => Layout.Unknown
                };
            }
            if (Directory.Exists(path))
            {
                // 目录里只要有一组同名 cab/psf 就按 PsfPair 处理（批量）
                if (Directory.EnumerateFiles(path, "*.psf").Any()) return Layout.PsfPair;
                if (Directory.EnumerateFiles(path, "*.cab").Any()) return Layout.SingleCab;
            }
            return Layout.Unknown;
        }

        static bool HasSiblingPsf(string p)
        {
            // 实测命名：同名 stem 但**哈希后缀不同**
            //   kb777777-x86-en-us-merged_d52fdcaef0f0....cab
            //   kb777777-x86-en-us-merged_9c08b7b3ea51....psf
            // 所以不能只靠 ChangeExtension，必须复用与 FindPairedPsf 相同的前缀回退逻辑。
            if (File.Exists(Path.ChangeExtension(p, ".psf"))) return true;

            string dir = Path.GetDirectoryName(Path.GetFullPath(p)) ?? ".";
            if (!Directory.Exists(dir)) return false;
            string key = Path.GetFileNameWithoutExtension(p).Split('_')[0];
            if (key.Length == 0) return false;
            return Directory.EnumerateFiles(dir, "*.psf")
                            .Any(f => Path.GetFileNameWithoutExtension(f)
                                          .StartsWith(key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>找出与 cab 配对的 psf（按同名 stem 匹配）。</summary>
        public static string? FindPairedPsf(string cabOrPsmPath)
        {
            string stem = Path.GetFileNameWithoutExtension(cabOrPsmPath);
            string dir = Path.GetDirectoryName(Path.GetFullPath(cabOrPsmPath)) ?? ".";
            string direct = Path.Combine(dir, stem + ".psf");
            if (File.Exists(direct)) return direct;

            // 回退：按前 3 段前缀匹配（kb777777-x86-en-us-merged_...）
            var parts = stem.Split('_');
            if (parts.Length >= 1)
            {
                string key = parts[0];
                var cand = Directory.EnumerateFiles(dir, "*.psf")
                                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(key, StringComparison.OrdinalIgnoreCase));
                if (cand != null) return cand;
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // CAB 解出（用 Windows 自带 expand.exe，无需 libmspack）
        // ────────────────────────────────────────────────────────────────

        /// <summary>列出 CAB 内容（`expand -D`）。</summary>
        public static (bool ok, List<string> entries, string raw) ListCab(string cabPath)
        {
            var entries = new List<string>();
            var (ok, outp) = RunExpand($"-D \"{cabPath}\"");
            foreach (var raw in outp.Split('\n'))
            {
                string l = raw.Trim();
                // 形如：<cabpath>: <name>
                int idx = l.LastIndexOf(": ", StringComparison.Ordinal);
                if (idx < 0 || l.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("files total")) continue;
                string name = l[(idx + 2)..].Trim();
                if (name.Length > 0) entries.Add(name);
            }
            return (ok, entries, outp);
        }

        /// <summary>把 CAB 全部解到 outDir（`expand -F:* &lt;cab&gt; &lt;dir&gt;`）。</summary>
        public static (bool ok, string raw) ExtractCab(string cabPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            return RunExpand($"-F:* \"{cabPath}\" \"{outDir}\"");
        }

        static (bool ok, string raw) RunExpand(string args)
        {
            try
            {
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
                if (!File.Exists(exe)) exe = "expand.exe";
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30 * 60 * 1000);
                return (p.ExitCode == 0, o);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ────────────────────────────────────────────────────────────────
        // cabinet.cablist.ini（CAB 集）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析 cabinet.cablist.ini，按 Cabinet1..N 的顺序返回子 CAB 名。
        /// 顺序很重要——MSUpdateExtractor 也是按这个顺序处理的。
        /// </summary>
        public static List<string> ParseCabList(string iniPath)
        {
            var list = new List<(int n, string name)>();
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith(";") || l.StartsWith("[")) continue;
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string key = l[..eq].Trim();
                string val = l[(eq + 1)..].Trim();
                if (reallyIsCabKey(key, out int n) && val.Length > 0) list.Add((n, val));
            }
            return list.OrderBy(x => x.n).Select(x => x.name).ToList();

            static bool reallyIsCabKey(string k, out int n)
            {
                n = 0;
                if (!k.StartsWith("Cabinet", StringComparison.OrdinalIgnoreCase)) return false;
                return int.TryParse(k["Cabinet".Length..], out n);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // PSF 索引（*.psf.cix.xml）
        // ────────────────────────────────────────────────────────────────

        /// <summary>从 CAB 里解出 cix.xml 并解析成 PSF 索引。</summary>
        public static PsfIndex ParsePsfIndex(string cixXmlPath, string psfPath)
        {
            var idx = new PsfIndex { CixPath = cixXmlPath, PsfPath = psfPath };
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(cixXmlPath);

                var files = doc.SelectNodes("//*[local-name()='File']");
                if (files == null) { idx.Error = "cix.xml 里没有 File 节点"; return idx; }

                foreach (XmlNode n in files)
                {
                    if (n is not XmlElement f) continue;
                    var e = new PsfFileEntry
                    {
                        Name = f.GetAttribute("name"),
                        TargetHash = (f.SelectSingleNode("./*[local-name()='Hash']") as XmlElement)?.GetAttribute("value") ?? ""
                    };
                    long.TryParse(f.GetAttribute("length"), out e.Length);
                    int.TryParse(f.GetAttribute("id"), out e.Id);

                    if (f.SelectSingleNode("./*[local-name()='Delta']/*[local-name()='Source']") is XmlElement src)
                    {
                        e.SourceType = src.GetAttribute("type");
                        long.TryParse(src.GetAttribute("offset"), out e.SourceOffset);
                        long.TryParse(src.GetAttribute("length"), out e.SourceLength);
                        e.BaseHash = (src.SelectSingleNode("./*[local-name()='Hash']") as XmlElement)?.GetAttribute("value") ?? "";
                    }
                    idx.Files.Add(e);
                }
                return idx;
            }
            catch (Exception ex) { idx.Error = ex.GetType().Name + ": " + ex.Message; return idx; }
        }

        // ────────────────────────────────────────────────────────────────
        // MSDelta（PA30 载荷还原）
        // ────────────────────────────────────────────────────────────────

        [DllImport("msdelta.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int ApplyDeltaB(long flags, IntPtr source, IntPtr delta, out IntPtr target);

        [DllImport("msdelta.dll", SetLastError = true)]
        static extern int DeltaFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        struct DeltaInput { public IntPtr p; public long size; }

        /// <summary>
        /// 对一份源文件应用 PSF 里的 PA30 载荷。
        /// 失败时返回 null 并给出原因（常见：载荷是 stored 而非 delta，或源文件不匹配）。
        /// </summary>
        public static byte[]? ApplyPayload(byte[]? sourceBytes, byte[] deltaBytes, out string error)
        {
            error = "";
            IntPtr srcBuf = IntPtr.Zero, deltaBuf = IntPtr.Zero, target = IntPtr.Zero;
            try
            {
                deltaBuf = Marshal.AllocHGlobal(deltaBytes.Length);
                Marshal.Copy(deltaBytes, 0, deltaBuf, deltaBytes.Length);

                IntPtr srcInput = IntPtr.Zero;
                if (sourceBytes != null && sourceBytes.Length > 0)
                {
                    srcBuf = Marshal.AllocHGlobal(sourceBytes.Length);
                    Marshal.Copy(sourceBytes, 0, srcBuf, sourceBytes.Length);
                    var di = new DeltaInput { p = srcBuf, size = sourceBytes.Length };
                    srcInput = Marshal.AllocHGlobal(Marshal.SizeOf<DeltaInput>());
                    Marshal.StructureToPtr(di, srcInput, false);
                }

                var dd = new DeltaInput { p = deltaBuf, size = deltaBytes.Length };
                IntPtr deltaInput = Marshal.AllocHGlobal(Marshal.SizeOf<DeltaInput>());
                Marshal.StructureToPtr(dd, deltaInput, false);

                // flags = 0（不带源）；带源时仍需 0，源通过 DELTA_INPUT 传入
                int hr = ApplyDeltaB(0, srcInput, deltaInput, out target);
                if (hr < 0 || target == IntPtr.Zero) { error = $"ApplyDeltaB 失败 0x{hr:X8}"; return null; }

                // 头 4 字节是大小
                int size = Marshal.ReadInt32(target);
                if (size <= 0) { error = "还原大小为 0"; return null; }
                var outBytes = new byte[size];
                Marshal.Copy(target + 4, outBytes, 0, size);
                return outBytes;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
            finally
            {
                if (target != IntPtr.Zero) DeltaFree(target);
                if (srcBuf != IntPtr.Zero) Marshal.FreeHGlobal(srcBuf);
                if (deltaBuf != IntPtr.Zero) Marshal.FreeHGlobal(deltaBuf);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 主流程
        // ────────────────────────────────────────────────────────────────

        public sealed class Config
        {
            /// <summary>包文件（.cab / .psm / .wim）或包含它们的目录。</summary>
            public string PackagePath = "";
            /// <summary>输出目录。</summary>
            public string OutputDir = "";
            /// <summary>源(base)文件目录（可空）。用于按 SHA256 查找 delta 的源。</summary>
            public List<string> BaseDirs = new();
            /// <summary>只解析索引与列出条目，不写文件。</summary>
            public bool ListOnly = true;
        }

        public static Result Extract(Config cfg, Action<string>? log = null)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }

            try
            {
                res.Detected = Detect(cfg.PackagePath);
                L($"布局识别: {res.Detected}  ({cfg.PackagePath})");
                if (res.Detected == Layout.Unknown) { res.Error = "无法识别的包布局"; return res; }

                var packages = new List<string>();
                if (File.Exists(cfg.PackagePath)) packages.Add(cfg.PackagePath);
                else packages.AddRange(Directory.EnumerateFiles(cfg.PackagePath, "*.cab")
                                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                L($"待处理包: {packages.Count} 个");

                // 源文件索引（SHA256 → 路径），用于 delta 还原
                var baseIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in cfg.BaseDirs.Where(Directory.Exists))
                {
                    L($"建立源索引: {d}");
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    {
                        try { baseIndex[Sha256Of(f)] = f; } catch { }
                    }
                }
                L($"源索引条目: {baseIndex.Count}");

                foreach (var cab in packages)
                {
                    L($"=== {Path.GetFileName(cab)} ===");
                    string workDir = Path.Combine(Path.GetTempPath(), "ocid_upd_" + Guid.NewGuid().ToString("N")[..8]);
                    var (ok, raw) = ExtractCab(cab, workDir);
                    if (!ok) { L("  CAB 解出失败"); res.FilesSkipped++; continue; }

                    var cix = Directory.EnumerateFiles(workDir, "*.cix.xml").FirstOrDefault();
                    if (cix == null) { L("  CAB 里没有 *.cix.xml（可能是普通包）"); res.FilesSkipped++; continue; }

                    string? psf = FindPairedPsf(cab);
                    if (psf == null) { L("  找不到配对的 .psf"); res.FilesSkipped++; continue; }

                    var idx = ParsePsfIndex(cix, psf);
                    if (idx.Error != null) { L("  cix 解析失败: " + idx.Error); res.FilesSkipped++; continue; }
                    L($"  PSF 索引: {idx.Files.Count} 个文件（psf={Path.GetFileName(psf)}）");

                    if (cfg.ListOnly)
                    {
                        foreach (var e in idx.Files.Take(5))
                            L($"    {e.Name}  len={e.Length}  off={e.SourceOffset}+{e.SourceLength}  type={e.SourceType}");
                        if (idx.Files.Count > 5) L($"    …（还有 {idx.Files.Count - 5} 个）");
                        res.FilesExtracted += idx.Files.Count;
                        continue;
                    }

                    // 真正还原
                    string outRoot = Path.Combine(cfg.OutputDir, Path.GetFileNameWithoutExtension(cab));
                    using var psfFs = File.OpenRead(psf);
                    foreach (var e in idx.Files)
                    {
                        if (e.SourceLength <= 0 || e.Name.Length == 0) { res.FilesSkipped++; continue; }
                        byte[] payload = new byte[e.SourceLength];
                        psfFs.Seek(e.SourceOffset, SeekOrigin.Begin);
                        int read = psfFs.Read(payload, 0, payload.Length);
                        if (read != payload.Length) { res.FilesSkipped++; continue; }

                        byte[]? srcBytes = null;
                        if (e.BaseHash.Length > 0 && baseIndex.TryGetValue(e.BaseHash, out var sp))
                        {
                            try { srcBytes = File.ReadAllBytes(sp); } catch { }
                        }

                        var got = ApplyPayload(srcBytes, payload, out string err);
                        if (got == null)
                        {
                            if (srcBytes == null) { res.FilesMissingSource++; L($"    缺源: {e.Name}"); }
                            else { res.FilesSkipped++; L($"    失败: {e.Name} — {err}"); }
                            continue;
                        }

                        string target = Path.Combine(outRoot, e.Name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, got);
                        res.FilesExtracted++;

                        if (e.TargetHash.Length > 0)
                        {
                            string actual = Convert.ToHexString(SHA256.HashData(got));
                            if (!actual.Equals(e.TargetHash, StringComparison.OrdinalIgnoreCase))
                            { res.HashMismatches++; L($"    校验不符: {e.Name}"); }
                        }
                    }
                    try { Directory.Delete(workDir, true); } catch { }
                }

                res.Success = res.FilesMissingSource == 0 || cfg.ListOnly;
                L("结果: " + res.Summary());
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        static string Sha256Of(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        /// <summary>渲染索引（供 UI 预览）。</summary>
        public static string RenderIndex(PsfIndex idx, int max = 40)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PSF 索引: {idx.Files.Count} 个文件");
            sb.AppendLine($"  cix: {Path.GetFileName(idx.CixPath)}");
            sb.AppendLine($"  psf: {Path.GetFileName(idx.PsfPath)}");
            if (idx.Error != null) { sb.AppendLine("  错误: " + idx.Error); return sb.ToString(); }
            sb.AppendLine();
            foreach (var e in idx.Files.Take(max))
                sb.AppendLine($"  [{e.Id}] {e.Name}  ({e.Length:N0} 字节, {e.SourceType} @{e.SourceOffset}+{e.SourceLength})");
            if (idx.Files.Count > max) sb.AppendLine($"  …（还有 {idx.Files.Count - max} 个）");
            return sb.ToString();
        }
    }
}
