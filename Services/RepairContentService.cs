using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// Repair Content Package 的解包编排。
    ///
    /// 【为什么单独一个服务】Repair Content Package 不是"一堆独立更新包"，而是一整套
    /// 互有冲突的包集合，**解包顺序会决定结果是否正确**。这个顺序是硬性要求，
    /// 不能靠通用解包流程顺其自然。
    ///
    /// 【硬性规则】（现场确认）
    ///   1. 文件名含 `neutral-mergedcomponents` 的包必须**先单独解包**；
    ///   2. 解包完成后**删除**它的 PSF 与 CAB；
    ///   3. 然后才解包其余 PSF；
    ///   4. 语言相关的安全目录（.cat）存在**截断**现象 —— 由于这些版本都提供独立的
    ///      语言包 CAB，**优先采用独立语言包版本的 .cat**。
    ///
    /// 【机理】解包时 delta 的源文件是按 SHA256 在"已解出的输出目录"里查找的
    /// （现代 PSFExtractor 的做法）。`neutral-mergedcomponents` 提供的是**完整版本**；
    /// 若顺序颠倒，后面的包会找到先前解出的**异常截断文件**当源，产出损坏组件，
    /// 导致后续安装失败。因此本服务对**每个包单独调用一次** <see cref="UpdatePackageService.Extract"/>，
    /// 并把输出目录一并作为源目录，使每一次调用都重新索引"当前已解出的一切"。
    ///
    /// 【实测目标集】E:\windesktop\8400_x86_repair_content：39 psf + 40 cab，
    /// 其中 neutral-mergedcomponents 1 个（psf 6.6 MB / cab 217 KB），
    /// 语言包 19 个，neutral 组件包 19 个。
    /// </summary>
    public static class RepairContentService
    {
        public enum PackageKind { MergedComponents, LanguagePack, Component, Unknown }

        public sealed class Package
        {
            public string CabPath = "";
            public string? PsfPath;
            public PackageKind Kind = PackageKind.Unknown;
            public string Language = "";
            public string Group = "";
            public long PsfSize;
            public bool Extracted;
            public int FilesOut;
            public string? Error;

            public string Name => Path.GetFileNameWithoutExtension(CabPath);
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public readonly List<Package> Phase1 = new();   // mergedcomponents（先解）
            public readonly List<Package> Phase2 = new();   // 其余（后解）
            public int TotalFiles;
            public int DeletedSources;
            public readonly List<string> LanguageCatNotes = new();
            public readonly List<string> Log = new();

            public string Summary()
                => $"第一阶段 {Phase1.Count} 个包 / 第二阶段 {Phase2.Count} 个包，共解出 {TotalFiles} 个文件，"
                 + $"删除源 {DeletedSources} 组，语言 .cat 处理 {LanguageCatNotes.Count} 条";
        }

        public sealed class Config
        {
            /// <summary>Repair Content 所在目录（含 cab/psf）。</summary>
            public string SourceDir = "";
            /// <summary>解包输出目录（SxS 存储的还原目标）。</summary>
            public string OutputDir = "";
            /// <summary>额外的源(base)目录：用于 delta 还原，例如同版本已安装镜像的文件。</summary>
            public List<string> BaseDirs = new();
            /// <summary>解完 neutral-mergedcomponents 后删除其 PSF/CAB。</summary>
            public bool DeleteMergedComponentsSources = true;
            /// <summary>只分类与报告顺序，不动文件。</summary>
            public bool ClassifyOnly = true;
            /// <summary>只解第一阶段（neutral-mergedcomponents）。</summary>
            public bool Phase1Only = false;
        }

        // 语言代码识别（ISO 形式 xx-xx）
        static readonly HashSet<string> KnownLangs = new(StringComparer.OrdinalIgnoreCase)
        {
            "ar-sa","ca-es","cs-cz","da-dk","de-de","el-gr","en-gb","en-us","es-es","es-mx",
            "et-ee","fi-fi","fr-fr","he-il","hr-hr","hu-hu","it-it","ja-jp","ko-kr","lt-lt",
            "lv-lv","nb-no","nl-nl","pl-pl","pt-br","pt-pt","ro-ro","ru-ru","sk-sk","sl-si",
            "sr-latn-rs","sv-se","th-th","tr-tr","uk-ua","zh-cn","zh-hk","zh-tw"
        };

        /// <summary>分类：mergedcomponents / 语言包 / 组件包。</summary>
        public static List<Package> Classify(string sourceDir, Action<string>? log = null)
        {
            var list = new List<Package>();
            foreach (var cab in Directory.EnumerateFiles(sourceDir, "*.cab")
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string stem = Path.GetFileNameWithoutExtension(cab);
                var p = new Package { CabPath = cab, PsfPath = UpdatePackageService.FindPairedPsf(cab) };
                if (p.PsfPath != null) p.PsfSize = new FileInfo(p.PsfPath).Length;

                if (stem.Contains("neutral-mergedcomponents", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kind = PackageKind.MergedComponents;
                    p.Group = "mergedcomponents";
                }
                else
                {
                    // 从名字里挑语言段：kb777777-x86-<lang>-merged_<hash>
                    // ⚠ 不能用 '-' 切分后找段：语言代码本身就含 '-'（pt-br / zh-cn / sr-latn-rs），
                    //   切分会把 pt-br 拆成 pt 与 br，导致这类语言包被误判为组件包。
                    //   改用 "-<lang>-" 的子串匹配。
                    string found = KnownLangs.FirstOrDefault(x =>
                        stem.Contains("-" + x + "-", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (found.Length > 0)
                    {
                        p.Kind = PackageKind.LanguagePack;
                        p.Language = found;
                        p.Group = found + "-merged";
                    }
                    else
                    {
                        p.Kind = PackageKind.Component;
                        // neutral-<group>_<hash>
                        int i = stem.IndexOf("neutral-", StringComparison.OrdinalIgnoreCase);
                        if (i >= 0)
                        {
                            string rest = stem[(i + "neutral-".Length)..];
                            int us = rest.IndexOf('_');
                            p.Group = us > 0 ? rest[..us] : rest;
                        }
                        else p.Group = stem;
                    }
                }
                list.Add(p);
            }

            log?.Invoke($"共 {list.Count} 个包：mergedcomponents {list.Count(x => x.Kind == PackageKind.MergedComponents)}，"
                      + $"语言包 {list.Count(x => x.Kind == PackageKind.LanguagePack)}，"
                      + $"组件包 {list.Count(x => x.Kind == PackageKind.Component)}");
            return list;
        }

        /// <summary>渲染解包顺序计划（供 UI / 日志预览）。</summary>
        public static string RenderPlan(List<Package> pkgs)
        {
            var sb = new StringBuilder();
            var p1 = pkgs.Where(p => p.Kind == PackageKind.MergedComponents).ToList();
            var p2 = pkgs.Where(p => p.Kind != PackageKind.MergedComponents)
                         .OrderBy(p => p.Kind == PackageKind.Component ? 0 : 1)
                         .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            sb.AppendLine("解包顺序（不可调换）:");
            sb.AppendLine();
            sb.AppendLine("── 第一阶段：neutral-mergedcomponents（必须最先，解完删除其 PSF/CAB）──");
            if (p1.Count == 0) sb.AppendLine("   （未找到 —— 若该版本应有此包，请确认素材完整）");
            foreach (var p in p1)
                sb.AppendLine($"   {p.Name}   psf={p.PsfSize:N0} B   cab={new FileInfo(p.CabPath).Length:N0} B");

            sb.AppendLine();
            sb.AppendLine("── 第二阶段：其余包（组件包在前，语言包在后）──");
            foreach (var p in p2)
            {
                string tag = p.Kind == PackageKind.LanguagePack ? $" [语言 {p.Language}]" : $" [组件 {p.Group}]";
                sb.AppendLine($"   {p.Name}{tag}   psf={(p.PsfSize == 0 ? "—" : p.PsfSize.ToString("N0") + " B")}");
            }
            sb.AppendLine();
            sb.AppendLine($"合计 {pkgs.Count} 个包；单独解包的 mergedcomponents {p1.Count} 个。");
            return sb.ToString();
        }

        /// <summary>
        /// 按硬性顺序解包整套 Repair Content。
        /// </summary>
        public static Result Extract(Config cfg, Action<string>? log = null,
                                     Action<int, int, string>? progress = null,
                                     System.Threading.CancellationToken ct = default)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }

            try
            {
                if (!Directory.Exists(cfg.SourceDir)) { res.Error = "源目录不存在: " + cfg.SourceDir; return res; }

                var pkgs = Classify(cfg.SourceDir, log);
                res.Phase1.AddRange(pkgs.Where(p => p.Kind == PackageKind.MergedComponents));
                res.Phase2.AddRange(pkgs.Where(p => p.Kind != PackageKind.MergedComponents)
                                        .OrderBy(p => p.Kind == PackageKind.Component ? 0 : 1)
                                        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase));

                L("");
                foreach (var line in RenderPlan(pkgs).Split('\n')) L(line);

                if (res.Phase1.Count == 0)
                    L("⚠ 未找到 neutral-mergedcomponents 包 —— 顺序规则无法生效，结果可能包含截断组件。");

                if (cfg.ClassifyOnly) { L("== 只分类（ClassifyOnly），未解包 =="); res.Success = true; return res; }

                Directory.CreateDirectory(cfg.OutputDir);
                L("");
                L("目标输出目录: " + cfg.OutputDir);

                // ── 源索引：**只建一次**并在解包过程中增量维护 ──
                // base 可能是完整镜像（实测 8 GB / 数万文件），每个包重建一遍不可接受。
                var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in cfg.BaseDirs.Where(Directory.Exists))
                {
                    L($"建立源索引: {d}");
                    int n = 0;
                    foreach (var f in UpdatePackageService.SafeEnumerateFiles(d))
                    {
                        try
                        {
                            string hash = Sha256OfFile(f);
                            if (!index.ContainsKey(hash)) { index[hash] = f; n++; }
                        }
                        catch { }
                    }
                    L($"  +{n}");
                }
                L($"源索引共 {index.Count} 条（后续增量维护，不再重扫）");

                // ── 第一阶段 ──
                L("");
                L("═══ 第一阶段：neutral-mergedcomponents ═══");
                int done = 0, total = cfg.Phase1Only ? res.Phase1.Count : pkgs.Count;
                foreach (var p in res.Phase1)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(++done, total, p.Name);
                    int n = ExtractOne(p, cfg, index, log);
                    res.TotalFiles += n;
                }

                // 解完删除其 PSF 与 CAB（关键步骤：防止后续包再用到它 / 再被处理）
                if (cfg.DeleteMergedComponentsSources && !cfg.Phase1Only)
                {
                    foreach (var p in res.Phase1)
                    {
                        try
                        {
                            if (p.PsfPath != null && File.Exists(p.PsfPath)) { File.Delete(p.PsfPath); L($"  已删除 {Path.GetFileName(p.PsfPath)}"); }
                            if (File.Exists(p.CabPath)) { File.Delete(p.CabPath); L($"  已删除 {Path.GetFileName(p.CabPath)}"); }
                            res.DeletedSources++;
                        }
                        catch (Exception ex) { L($"  删除失败（可忽略）: {ex.Message}"); }
                    }
                }

                if (cfg.Phase1Only) { L("== 只做第一阶段 =="); res.Success = true; return res; }

                // ── 第二阶段 ──
                L("");
                L("═══ 第二阶段：其余包 ═══");
                foreach (var p in res.Phase2)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(++done, total, p.Name);
                    int n = ExtractOne(p, cfg, index, log);
                    res.TotalFiles += n;

                    // 语言相关 .cat 截断：优先用独立语言包版本替代
                    if (p.Kind == PackageKind.LanguagePack)
                        res.LanguageCatNotes.Add(ResolveLanguageCats(cfg.OutputDir, p, log));
                }

                res.Success = true;
                L("");
                L("结果: " + res.Summary());
                return res;
            }
            catch (OperationCanceledException) { res.Error = "已取消"; return res; }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>
        /// 解一个包。关键：把【输出目录】也作为源目录，使 <see cref="UpdatePackageService.Extract"/>
        /// 重新索引"当前已解出的一切"—— 这正是顺序规则起作用的地方。
        /// </summary>
        static int ExtractOne(Package p, Config cfg, Dictionary<string, string> index, Action<string>? log)
        {
            try
            {
                var sub = new UpdatePackageService.Config
                {
                    PackagePath = p.CabPath,
                    OutputDir = cfg.OutputDir,
                    ListOnly = false,
                    // ★ 关键：把维护中的索引传进去。它既含 base 镜像的文件，也含
                    //   此前各包已解出的文件 —— 顺序规则正是靠"当前已解出的一切"起作用。
                    SourceIndex = index
                };
                var r = UpdatePackageService.Extract(sub, m => log?.Invoke("    " + m));
                p.Extracted = r.Success;
                p.FilesOut = r.FilesExtracted;
                p.Error = r.Error.Length > 0 ? r.Error : null;

                // 增量维护：把刚写出的文件加入索引，供后续包当 delta 源
                int added = 0;
                foreach (var f in r.WrittenFiles)
                {
                    try
                    {
                        string h = Sha256OfFile(f);
                        if (!index.ContainsKey(h)) { index[h] = f; added++; }
                    }
                    catch { }
                }
                if (added > 0) log?.Invoke($"    索引 +{added}（共 {index.Count}）");
                return r.FilesExtracted;
            }
            catch (Exception ex) { p.Error = ex.Message; return 0; }
        }

        static string Sha256OfFile(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        /// <summary>
        /// 语言相关安全目录（.cat）截断的处理说明与检查。
        ///
        /// 现场结论：Repair Content 里部分语言相关的 .cat 存在截断；这些版本都提供了
        /// **独立的语言包 CAB**，所以应当用独立语言包版本的 .cat 替代。
        /// 这里做的是：把该语言解出的 .cat 找出来、算哈希、报告数量与可疑（过小）项，
        /// 便于调用方用独立语言包覆盖。
        /// </summary>
        static string ResolveLanguageCats(string outDir, Package p, Action<string>? log)
        {
            try
            {
                var cats = Directory.EnumerateFiles(outDir, "*.cat", SearchOption.AllDirectories)
                                    .Where(f => f.IndexOf(p.Language, StringComparison.OrdinalIgnoreCase) >= 0)
                                    .ToList();
                long min = cats.Count == 0 ? 0 : cats.Min(f => new FileInfo(f).Length);
                // 语言 .cat 正常在数十 KB 量级；明显偏小的列为可疑
                var suspicious = cats.Where(f => new FileInfo(f).Length < 4096).ToList();
                foreach (var s in suspicious)
                    log?.Invoke($"    ⚠ 语言 .cat 偏小（可能截断，建议用独立语言包版本替代）: {Path.GetFileName(s)} ({new FileInfo(s).Length} B)");
                return $"{p.Language}: .cat {cats.Count} 个，最小 {min} B，可疑 {suspicious.Count} 个";
            }
            catch (Exception ex) { return $"{p.Language}: 检查失败 {ex.Message}"; }
        }

        /// <summary>统计输出目录（还原出的 SxS 存储）的规模与结构，供"能否重新组装"判断。</summary>
        public static string InspectOutput(string outDir)
        {
            var sb = new StringBuilder();
            if (!Directory.Exists(outDir)) { sb.AppendLine("输出目录不存在: " + outDir); return sb.ToString(); }

            var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
            long total = 0;
            foreach (var f in files) { try { total += new FileInfo(f).Length; } catch { } }

            int mums = files.Count(f => f.EndsWith(".mum", StringComparison.OrdinalIgnoreCase));
            int mans = files.Count(f => f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));
            int cats = files.Count(f => f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase));
            int zero = files.Count(f => { try { return new FileInfo(f).Length == 0; } catch { return false; } });

            sb.AppendLine("还原出的存储:");
            sb.AppendLine($"  文件总数 : {files.Count:N0}");
            sb.AppendLine($"  总大小   : {total / 1024.0 / 1024.0:N1} MB");
            sb.AppendLine($"  .mum     : {mums:N0}");
            sb.AppendLine($"  .manifest: {mans:N0}");
            sb.AppendLine($"  .cat     : {cats:N0}");
            sb.AppendLine($"  0 字节   : {zero:N0}   ← 应接近 0；偏多说明有截断");
            return sb.ToString();
        }
    }
}
