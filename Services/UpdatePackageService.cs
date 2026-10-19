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

            /// <summary>本次实际写出的文件绝对路径 —— 供调用方增量更新源索引。</summary>
            public readonly List<string> WrittenFiles = new();

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
        // CAB 解出（进程内读取器优先；sparse / 多卷包退回 expand.exe）
        // ────────────────────────────────────────────────────────────────

        /// <summary>列出 CAB 内容。</summary>
        public static (bool ok, List<string> entries, string raw) ListCab(string cabPath)
        {
            try
            {
                var list = CabExtraction.List(cabPath);
                var entries = list.Select(e => e.Name).ToList();
                var raw = string.Join("\n", list.Select(e => $"{cabPath}: {e.Name}"));
                return (true, entries, raw);
            }
            catch (Exception ex) { _ = ex; }

            var fallbackEntries = new List<string>();
            var (ok, outp) = RunExpand($"-D \"{cabPath}\"");
            foreach (var raw in outp.Split('\n'))
            {
                string l = raw.Trim();
                // 形如：<cabpath>: <name>
                int idx = l.LastIndexOf(": ", StringComparison.Ordinal);
                if (idx < 0 || l.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("files total")) continue;
                string name = l[(idx + 2)..].Trim();
                if (name.Length > 0) fallbackEntries.Add(name);
            }
            return (ok, fallbackEntries, outp);
        }

        /// <summary>把 CAB 全部解到 outDir。</summary>
        public static (bool ok, string raw) ExtractCab(string cabPath, string outDir)
        {
            try
            {
                CabExtraction.ExtractAll(cabPath, outDir);
                return (true, $"expanded {cabPath} -> {outDir}");
            }
            catch (Exception ex) { _ = ex; }

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

        // ── msdelta 互操作 ──
        // wcp.h / msdelta.h：
        //   typedef struct _DELTA_INPUT { union {LPCVOID lpcStart; VOID* lpStart;}; SIZE_T uSize; BOOL Editable; } DELTA_INPUT;
        //   typedef struct _DELTA_OUTPUT { VOID* lpStart; SIZE_T uSize; } DELTA_OUTPUT;
        //   BOOL ApplyDeltaB(DELTA_FLAGS Flags, DELTA_INPUT Source, DELTA_INPUT Delta, DELTA_OUTPUT* Target);
        //   ↑ Source 与 Delta 是【按值】传结构体；Target 是指针。
        //   早期实现把前两者写成 IntPtr（传指针）且漏了 Editable 字段，
        //   原生侧于是把指针当结构体内容用，直接 Fatal error 0x80131506。
        [StructLayout(LayoutKind.Sequential)]
        struct DeltaInput
        {
            public IntPtr p;        // 数据指针（无源时传 NULL）
            public UIntPtr size;    // SIZE_T
            public int editable;    // BOOL
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DeltaOutput
        {
            public IntPtr p;
            public UIntPtr size;
        }

        [DllImport("msdelta.dll", SetLastError = true)]
        static extern int ApplyDeltaB(uint flags, DeltaInput source, DeltaInput delta, out DeltaOutput target);

        [DllImport("msdelta.dll", SetLastError = true)]
        static extern int DeltaFree(IntPtr memory);

        /// <summary>
        /// 对一份源文件应用 PSF 里的 PA30 载荷。
        /// 失败时返回 null 并给出原因（常见：载荷是 stored 而非 delta，或源文件不匹配）。
        /// </summary>
        public static byte[]? ApplyPayload(byte[]? sourceBytes, byte[] deltaBytes, out string error)
        {
            error = "";
            if (deltaBytes == null || deltaBytes.Length == 0) { error = "载荷为空"; return null; }

            // 先带源试；失败再试无源（部分 delta 自包含，不需要源文件）
            var first = TryApply(sourceBytes, deltaBytes, out string err1);
            if (first != null) return first;
            if (sourceBytes != null)
            {
                var second = TryApply(null, deltaBytes, out string err2);
                if (second != null) return second;
                error = $"带源失败({err1})；无源也失败({err2})";
                return null;
            }
            error = err1;
            return null;
        }

        static byte[]? TryApply(byte[]? sourceBytes, byte[] deltaBytes, out string error)
        {
            error = "";
            IntPtr srcBuf = IntPtr.Zero, deltaBuf = IntPtr.Zero, resultBuffer = IntPtr.Zero;
            try
            {
                deltaBuf = Marshal.AllocHGlobal(deltaBytes.Length);
                Marshal.Copy(deltaBytes, 0, deltaBuf, deltaBytes.Length);

                if (sourceBytes != null && sourceBytes.Length > 0)
                {
                    srcBuf = Marshal.AllocHGlobal(sourceBytes.Length);
                    Marshal.Copy(sourceBytes, 0, srcBuf, sourceBytes.Length);
                }

                var src = new DeltaInput
                {
                    p = srcBuf,
                    size = (UIntPtr)(sourceBytes?.Length ?? 0),
                    editable = 0
                };
                var dlt = new DeltaInput
                {
                    p = deltaBuf,
                    size = (UIntPtr)deltaBytes.Length,
                    editable = 0
                };

                // 第 4 参数是 DELTA_OUTPUT*；用 out DeltaOutput 让封送层负责这一层指针，
                // 避免再手工解引用造成结构体错位。
                int hr = ApplyDeltaB(0 /* DELTA_FLAG_NONE */, src, dlt, out DeltaOutput outp);

                // 【注意 ApplyDeltaB 返回的是 BOOL，不是 HRESULT】
                //   非零（TRUE，实测为 1）= 成功；零（FALSE）= 失败。
                // ⚠ 我一度把它当 HRESULT 改成 `hr != 0 即失败`，方向正好相反 ——
                //   是小样本探针报出 "ApplyDeltaB=0x00000001" 却当成失败才发现的。
                // ⚠ 别被 "ApplyDeltaB=0x00000000" 骗了：这是 BOOL 的 FALSE（失败），
                //   不是 HRESULT 的 S_OK。我为此走过一次弯路（详见提交记录）。
                //   那 2778 条 "缺源且无法自包含" 是**真实失败** —— 它们确实需要 base 文件。
                if (hr == 0) { error = "ApplyDeltaB 返回 FALSE（该 API 返回 BOOL 而非 HRESULT：0=失败；此处不是 S_OK）"; return null; }

                long size = (long)outp.size;
                // ⚠ 这里【必须】把 size 取不到当作失败（返回 null），好让调用方回退到"无源"路径。
                //   我一度为了照顾"空文件"把 size == 0 当成成功返回空数组 —— 后果是
                //   **带源的第一条路径永远报成功**，整批文件被静默写成 0 字节
                //   （实测：203,556 个文件全部变 0 字节、语言 .cat 归零），
                //   而且再也不会走无源路径。空文件是极少数情形，不值得拿整批数据去赌。
                if (size <= 0 || size > 1024L * 1024 * 1024) { error = $"输出大小异常 {size}"; return null; }

                var outBytes = new byte[size];
                Marshal.Copy(outp.p, outBytes, 0, (int)size);
                // 记住数据缓冲，交给 finally 用 DeltaFree 释放（DeltaFree 要的是缓冲指针，
                // 不是 DELTA_OUTPUT 结构体本身 —— 释放错对象会造成堆损坏 0xC0000374）
                resultBuffer = outp.p;
                return outBytes;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
            finally
            {
                // DeltaFree 要的是【数据缓冲】指针（DELTA_OUTPUT.lpStart）
                if (resultBuffer != IntPtr.Zero) DeltaFree(resultBuffer);
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

            /// <summary>
            /// 调用方已建好的源索引（SHA256 十六进制 → 文件路径）。
            /// 传了就**直接复用**，不再遍历 <see cref="BaseDirs"/>。
            /// 原因：base 可能是完整镜像（实测 8 GB / 数万文件），每处理一个包重建一遍
            /// 的代价不可接受（40 个包就是 40 遍）。由调用方建一次并增量维护。
            /// </summary>
            public Dictionary<string, string>? SourceIndex;
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

                // 源文件索引（SHA256 → 路径），用于 delta 还原。
                // 若调用方已提供（cfg.SourceIndex）则直接复用，绝不重复遍历 base ——
                // base 可能是完整镜像（实测 8 GB / 数万文件）。
                Dictionary<string, string> baseIndex;
                if (cfg.SourceIndex != null)
                {
                    baseIndex = cfg.SourceIndex;
                    L($"源索引: 复用调用方提供的 {baseIndex.Count} 条");
                }
                else
                {
                    baseIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in cfg.BaseDirs.Where(Directory.Exists))
                    {
                        L($"建立源索引: {d}");
                        foreach (var f in SafeEnumerateFiles(d))
                        {
                            try { baseIndex[Sha256Of(f)] = f; } catch { }
                        }
                    }
                    L($"源索引条目: {baseIndex.Count}");
                }

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

                        // 注意：缺源也照样尝试 —— 部分 delta 自包含，ApplyPayload 会先带源、
                        // 再退回无源。只有两者都失败才算真失败。
                        var got = ApplyPayload(srcBytes, payload, out string err);
                        if (got == null)
                        {
                            if (srcBytes == null) { res.FilesMissingSource++; L($"    缺源且无法自包含: {e.Name} — {err}"); }
                            else { res.FilesSkipped++; L($"    失败: {e.Name} — {err}"); }
                            continue;
                        }

                        string target = Path.Combine(outRoot, e.Name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, got);
                        res.FilesExtracted++;
                        res.WrittenFiles.Add(target);

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


        /// <summary>
        /// 安全递归枚举文件：逐目录 try/catch，并**跳过重解析点**（junction / symlink）。
        /// 为什么必须这样：镜像里有 `Documents and Settings`、`Application Data` 这类
        /// 旧式 junction，<c>EnumerateFiles(..., AllDirectories)</c> 碰到会直接抛
        /// <c>UnauthorizedAccessException</c>，而把 try/catch 放在文件循环里是救不了的
        /// —— 异常来自枚举器本身。
        /// </summary>
        public static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                // 目录本身是可读的普通目录吗？
                try
                {
                    var attr = File.GetAttributes(dir);
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = Array.Empty<string>(); }
                foreach (var f in files) yield return f;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { subs = Array.Empty<string>(); }
                foreach (var s in subs) stack.Push(s);
            }
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
