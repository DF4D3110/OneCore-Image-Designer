using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.ViewModels.Cbs
{
    /// <summary>
    /// CBS 构建（标签页内嵌）：把一组包 CAB（手选 / 扫包根 / 解析 FM 得到）按每个包
    /// update.mum 声明的目标路径展开为目录树（含 WinSxS 存储）。传 DeviceLayout（XML 或布局 CAB）
    /// 时按 diskX\PartitionN-名 组织多盘/多分区，否则平铺。落位规则全部真实包实证。
    /// </summary>
    public partial class CbsBuildViewModel : ObservableObject
    {
        readonly MainViewModel _main;
        readonly List<string> _fms = new();

        public CbsBuildViewModel(MainViewModel main)
        {
            _main = main;
            _outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CbsOut");
        }

        [ObservableProperty]
        string _outputDir;

        [ObservableProperty]
        string _status = "选择包来源，再指定输出目录，点『开始构建』";

        [ObservableProperty]
        bool _isBusy;

        [ObservableProperty]
        string _layoutCabPath = "";

        /// <summary>待构建的包 CAB（去重、有序）。</summary>
        public ObservableCollection<string> Cabs { get; } = new();

        /// <summary>构建结果：目录树文本 + 统计。</summary>
        [ObservableProperty]
        string _treeText = "";

        [ObservableProperty]
        bool _hasResult;

        string? ResolvedLayoutXml()
        {
            if (string.IsNullOrWhiteSpace(LayoutCabPath)) return null;
            string p = LayoutCabPath;
            if (p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
            if (p.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            {
                // 从布局 CAB 解出 DeviceLayout(.MS).xml 到临时目录
                string tmp = Path.Combine(Path.GetTempPath(), "cbsdl_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(tmp);
                ExpandCab(p, tmp);
                return Directory.GetFiles(tmp, "DeviceLayout*.xml", SearchOption.AllDirectories).FirstOrDefault();
            }
            return null;
        }

        // ════════ 包来源 ════════

        [RelayCommand]
        void AddCabs()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择包 CAB（可多选）",
                Filter = "CAB (*.cab)|*.cab|所有文件 (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;
            int n = 0;
            foreach (var f in dlg.FileNames) if (AddCab(f)) n++;
            Status = $"添加了 {n} 个包（当前共 {Cabs.Count} 个）";
        }

        [RelayCommand]
        void ScanFolder()
        {
            var dlg = new OpenFolderDialog { Title = "扫描其下所有 *.cab（递归）" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var found = Directory.EnumerateFiles(dlg.FolderName, "*.cab", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\WinSxS\\", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                int n = 0;
                foreach (var f in found) if (AddCab(f)) n++;
                Status = $"从包根扫描到 {found.Count} 个 cab，新增 {n}（当前共 {Cabs.Count}）";
            }
            catch (Exception ex) { Status = "扫描失败: " + ex.Message; }
        }

        [RelayCommand]
        async Task LoadFromFmAsync()
        {
            var fmd = new OpenFileDialog { Title = "选择 FeatureManifest (.xml)", Filter = "FM (*.xml)|*.xml|所有文件 (*.*)|*.*" };
            if (fmd.ShowDialog() != true) return;
            var root = new OpenFolderDialog { Title = "选择包根（MSPackages 所在目录，用于解析 $(mspackageroot) 与兜底搜索）" };
            if (root.ShowDialog() != true) return;

            IsBusy = true; Status = "解析 FM 引用包…";
            try
            {
                var cabs = await Task.Run(() =>
                {
                    var fm = FMService.ParseFM(fmd.FileName);
                    if (fm == null) return new List<string>();
                    return FMService.CollectPackageCabs(new[] { fm }, root.FolderName, onlyLayout: false);
                });
                if (cabs.Count == 0) { Status = "FM 未解析到可定位的包 CAB（检查包根是否正确）"; return; }
                int n = 0; foreach (var c in cabs) if (AddCab(c)) n++;
                if (!_fms.Contains(fmd.FileName)) _fms.Add(fmd.FileName);
                // FM 自带的设备布局包也顺带填进布局框（若为空）
                if (string.IsNullOrWhiteSpace(LayoutCabPath))
                {
                    var lay = cabs.FirstOrDefault(c => c.IndexOf("devicelay", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (lay != null) LayoutCabPath = lay;
                }
                Status = $"FM 引用 {cabs.Count} 个包，新增 {n}（当前共 {Cabs.Count}）";
            }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        void ClearCabs() { Cabs.Clear(); Status = "已清空包列表"; }

        [RelayCommand]
        void RemoveCab(string? cab) { if (cab != null) Cabs.Remove(cab); }

        bool AddCab(string f)
        {
            if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) return false;
            foreach (var e in Cabs) if (string.Equals(e, f, StringComparison.OrdinalIgnoreCase)) return false;
            Cabs.Add(f); return true;
        }

        // ════════ 布局来源 ════════

        [RelayCommand]
        void PickLayout()
        {
            var dlg = new OpenFileDialog { Title = "选择 DeviceLayout（.xml 或布局 .cab）", Filter = "布局 (*.xml;*.cab)|*.xml;*.cab|所有 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) LayoutCabPath = dlg.FileName;
        }

        [RelayCommand]
        void ClearLayout() => LayoutCabPath = "";

        [RelayCommand]
        void BrowseOutput()
        {
            var dlg = new OpenFolderDialog { Title = "选择输出目录" };
            if (Directory.Exists(OutputDir)) dlg.InitialDirectory = OutputDir;
            if (dlg.ShowDialog() == true) OutputDir = dlg.FolderName;
        }

        // ════════ 构建 ════════

        [RelayCommand]
        async Task BuildAsync()
        {
            if (Cabs.Count == 0) { Status = "请先添加要构建的包"; return; }
            IsBusy = true; HasResult = false; Status = "构建中…";
            BuildCommand.NotifyCanExecuteChanged();
            try
            {
                string? layoutXml = ResolvedLayoutXml();
                var req = new CbsBuildRequest
                {
                    PackageCabs = Cabs.ToList(),
                    OutputDirectory = OutputDir,
                    LayoutXmlPath = layoutXml,
                };
                var res = await Task.Run(() => CbsBuildService.Build(req));

                if (!res.Success) { Status = "构建失败: " + res.Error; return; }

                TreeText = BuildTree(OutputDir);
                HasResult = true;
                var artifact = IntegrationArtifactRegistry.Shared.Register(new IntegrationArtifact(
                    IntegrationArtifactKind.CbsOutput, OutputDir, "CBS package reconstruction output"));
                var preflight = IntegrationArtifactRegistry.Shared.Preflight(artifact);
                Status = $"✅ 完成：{res.PackagesBuilt} 包 / {res.FilesWritten} 文件（WinSxS manifest {res.ManifestsToWinSxS}、servicing mum {res.MumsToServicing}"
                       + (res.FilesMissing > 0 ? $"，缺源 {res.FilesMissing}" : "，缺源 0") + $"）→ {OutputDir}"
                       + (preflight.IsReady ? "；产物已登记" : "；产物预检失败: " + string.Join("; ", preflight.Errors));
            }
            catch (Exception ex) { Status = "异常: " + ex.Message; }
            finally { IsBusy = false; BuildCommand.NotifyCanExecuteChanged(); }
        }

        bool CanBuild() => !IsBusy && Cabs.Count > 0;

        partial void OnIsBusyChanged(bool value) => BuildCommand.NotifyCanExecuteChanged();

        static string BuildTree(string root)
        {
            if (!Directory.Exists(root)) return "(无输出)";
            var sb = new System.Text.StringBuilder();
            Walk(root, 0);
            return sb.ToString();
            void Walk(string dir, int ind)
            {
                if (ind > 6) return;
                var ents = Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var e in ents)
                {
                    bool isDir = Directory.Exists(e);
                    sb.Append(new string(' ', ind * 2)).Append(Path.GetFileName(e)).Append(isDir ? "\\" : "").Append('\n');
                    if (isDir) Walk(e, ind + 1);
                }
            }
        }

        static void ExpandCab(string cab, string dest)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("expand.exe", $"-F:* \"{cab}\" \"{dest}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(120_000);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════════
        //                   CBS 服务中枢（纯增量新增）
        // ════════════════════════════════════════════════════════════════════
        // 上面的能力是"把包 CAB 展开成目录树"（纯展开，不写镜像）。
        // 下面这组是"服务一个已有镜像"：世代判定 → 依赖图 → 组装计划 → 执行。
        // 全部由本程序完成，不调用任何外部 exe / 脚本。

        /// <summary>目标镜像的 Windows 目录（例：G:\Windows）。必须位于挂载的 VHD/VHDX 上。</summary>
        [ObservableProperty] string _targetWinDir = "";

        /// <summary>世代与通道横幅：打开页面就知道这个镜像走哪条路、为什么。</summary>
        [ObservableProperty] string _channelBanner = "填写目标镜像的 Windows 目录后点『判定世代与通道』。";

        /// <summary>依赖图/组装计划的 SxS 内容目录（多个用分号分隔）。</summary>
        [ObservableProperty] string _assemblySourceDirs = "";

        /// <summary>SKU 组装清单（可空）。</summary>
        [ObservableProperty] string _assemblySkuFile = "";

        /// <summary>试运行（默认开启，绝不改镜像）。</summary>
        [ObservableProperty] bool _servicingDryRun = true;

        [ObservableProperty] bool _isServicing;

        [ObservableProperty] string _servicingSummary =
            "CBS 服务：判定世代 → 算依赖图 → 排组装顺序 → 执行。\n"
            + "目标镜像必须位于【挂载的 VHD/VHDX】上；对物理盘或宿主系统盘操作会破坏系统，程序会直接拒绝。\n"
            + "所有会写镜像的操作默认『试运行』，确认无误后再关闭。";

        /// <summary>依赖图 / 组装计划 / 执行结果的文本呈现。</summary>
        /// <summary>目标架构（传给原生 staging）。</summary>
        [ObservableProperty] string _explodeArch = "amd64";

        /// <summary>语言（传给原生 staging 与 DISM set-allintl）。</summary>
        [ObservableProperty] string _explodeLang = "en-US";

        /// <summary>语言包 .mum（可空）。</summary>
        [ObservableProperty] string _explodeLanguagePack = "";

        [ObservableProperty] string _servicingReport = "";

        [ObservableProperty] bool _hasServicingReport;

        // ── 判定世代与通道 ──
        [RelayCommand]
        void CheckChannel()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(TargetWinDir)) { ChannelBanner = "请先填写目标镜像的 Windows 目录。"; return; }
                var d = ServicingRouter.Decide(TargetWinDir);
                ChannelBanner = $"【{d.Channel}】{d.Reason}"
                              + (d.ImageStackVersion != null ? $"\n镜像 servicing stack 版本：{d.ImageStackVersion}（{d.Scheme} 体系）" : "");
                Status = "世代判定完成：" + d.Channel;
            }
            catch (Exception ex) { ChannelBanner = "判定失败：" + ex.Message; }
        }

        // ── 查看依赖图（组件之间的联系）──
        [RelayCommand]
        void ShowGraph()
        {
            try
            {
                var dirs = SplitDirs(AssemblySourceDirs);
                if (dirs.Count == 0) { ServicingReport = "请填写至少一个 SxS 内容目录（多个用分号分隔）。"; HasServicingReport = true; return; }
                var sink = EnsureTaskLog("CBS 依赖图");
                var g = SystemAssemblyService.BuildGraph(dirs, m => sink.Write(m));
                ServicingReport = SystemAssemblyService.RenderGraph(g, 60);
                sink.Write(ServicingReport);
                sink.MarkFinished();
                HasServicingReport = true;
                Status = $"依赖图：{g.Nodes.Count} 个节点，缺失依赖 {g.MissingDependencies}";
            }
            catch (Exception ex) { ServicingReport = "建图异常：" + ex.Message; HasServicingReport = true; }
        }

        // ── 计算组装计划（闭包 + 拓扑序）──
        [RelayCommand]
        void ShowAssemblyPlan()
        {
            try
            {
                var dirs = SplitDirs(AssemblySourceDirs);
                if (dirs.Count == 0) { ServicingReport = "请填写至少一个 SxS 内容目录。"; HasServicingReport = true; return; }
                var sink = EnsureTaskLog("CBS 组装计划");
                var g = SystemAssemblyService.BuildGraph(dirs, m => sink.Write(m));
                var seeds = g.Nodes.Values.Where(n => n.Kind == SystemAssemblyService.NodeKind.Package).Select(n => n.Key).ToList();
                var plan = SystemAssemblyService.PlanFrom(g, seeds, m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("组装计划：" + plan.Summary());
                sb.AppendLine();
                sb.AppendLine("安装顺序（前 40）：");
                int i = 0;
                foreach (var k in plan.Order) { if (++i > 40) { sb.AppendLine($"  …（还有 {plan.Order.Count - 40} 项）"); break; } sb.AppendLine($"  {i,4}. {k}"); }
                if (plan.Missing.Count > 0) { sb.AppendLine(); sb.AppendLine("缺失依赖：" + string.Join(", ", plan.Missing.Take(20))); }
                if (plan.Cycles.Count > 0) { sb.AppendLine(); sb.AppendLine("检测到环：" + string.Join(" | ", plan.Cycles.Take(5))); }
                ServicingReport = sb.ToString();
                sink.Write(ServicingReport); sink.MarkFinished();
                HasServicingReport = true;
                Status = "组装计划：" + plan.Summary();
            }
            catch (Exception ex) { ServicingReport = "计划异常：" + ex.Message; HasServicingReport = true; }
        }

        // ── 执行原生 staging（铺底 → 装 SKU → DISM → 收尾）──
        [RelayCommand(CanExecute = nameof(CanService))]
        async Task RunNativeStagingAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetWinDir)) { Status = "请先填写目标镜像的 Windows 目录"; return; }
            IsServicing = true; HasServicingReport = false;
            ServicingSummary = ServicingDryRun ? "试运行中…" : "执行中…（不要关闭程序）";
            var sink = EnsureTaskLog("CBS 原生 staging");
            try
            {
                var cfg = new ExplodeSxSNativeService.Config
                {
                    WinDir = TargetWinDir.Trim(),
                    ImageRoot = ExplodeSxSNativeService.DeriveImageRoot(TargetWinDir.Trim()),
                    Arch = string.IsNullOrWhiteSpace(ExplodeArch) ? "amd64" : ExplodeArch.Trim(),
                    SkuAssemblyFile = string.IsNullOrWhiteSpace(AssemblySkuFile) ? "" : AssemblySkuFile.Trim(),
                    LanguagePackMum = ExplodeLanguagePack?.Trim() ?? "",
                    Lang = string.IsNullOrWhiteSpace(ExplodeLang) ? "en-US" : ExplodeLang.Trim(),
                    DryRun = ServicingDryRun
                };
                var res = await ExplodeSxSNativeService.RunAsync(cfg, m => sink.Write(m));
                var sb = new System.Text.StringBuilder();
                foreach (var (step, ok, detail) in res.Steps) sb.AppendLine((ok ? "✓ " : "✗ ") + step + "：" + detail);
                if (res.Error.Length > 0) sb.AppendLine("错误：" + res.Error);
                if (res.RebootRequired) sb.AppendLine("CBS 提示需要重启。");
                ServicingReport = sb.ToString().TrimEnd();
                HasServicingReport = true;
                Status = res.Success ? "原生 staging 完成。" : "原生 staging 失败：" + res.Error;
            }
            catch (Exception ex) { ServicingReport = "异常：" + ex.Message; HasServicingReport = true; }
            finally { sink.MarkFinished(); IsServicing = false; }
        }

        bool CanService() => !IsServicing && !IsBusy;

        // ── 更新包解压（PSF + CAB）──
        [RelayCommand]
        void PickUpdatePackage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择更新包（.cab / .psm / .wim，可多选）",
                Filter = "更新包 (*.cab;*.psm;*.wim)|*.cab;*.psm;*.wim|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            UpdatePackagePaths = string.Join(";", dlg.FileNames);
        }

        [ObservableProperty] string _updatePackagePaths = "";
        [ObservableProperty] string _updatePackageOutDir = "";

        [RelayCommand(CanExecute = nameof(CanService))]
        async Task RunUpdatePackageAsync()
        {
            var paths = SplitDirs(UpdatePackagePaths);
            if (paths.Count == 0) { Status = "请先选择要解包的更新包"; return; }
            if (string.IsNullOrWhiteSpace(UpdatePackageOutDir)) { Status = "请先指定解包输出目录"; return; }
            IsServicing = true; HasServicingReport = false;
            var sink = EnsureTaskLog("更新包解压");
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var p in paths)
                {
                    var cfg = new UpdatePackageService.Config
                    {
                        PackagePath = p,
                        OutputDir = UpdatePackageOutDir,
                        ListOnly = ServicingDryRun
                    };
                    var r = await Task.Run(() => UpdatePackageService.Extract(cfg, m => sink.Write(m)));
                    sb.AppendLine($"{Path.GetFileName(p)}：布局={r.Detected}  解出={r.FilesExtracted}  跳过={r.FilesSkipped}  缺源={r.FilesMissingSource}  校验失败={r.HashMismatches}");
                }
                if (ServicingDryRun) sb.AppendLine().AppendLine("（当前为『试运行』：只解析索引，未写出文件）");
                ServicingReport = sb.ToString().TrimEnd();
                HasServicingReport = true;
                Status = "更新包解压完成。";
            }
            catch (Exception ex) { ServicingReport = "异常：" + ex.Message; HasServicingReport = true; }
            finally { sink.MarkFinished(); IsServicing = false; }
        }

        // ── Repair Content 顺序解包（neutral-mergedcomponents 必须最先）──
        [RelayCommand]
        void PickRepairContentDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择 Repair Content 目录（含 cab/psf）" };
            if (dlg.ShowDialog() == true) RepairContentDir = dlg.FolderName;
        }

        [ObservableProperty] string _repairContentDir = "";
        [ObservableProperty] string _repairContentOutDir = "";
        [ObservableProperty] string _repairContentBaseDir = "";

        [RelayCommand]
        void ShowRepairPlan()
        {
            try
            {
                if (!Directory.Exists(RepairContentDir)) { ServicingReport = "请先选择有效的 Repair Content 目录。"; HasServicingReport = true; return; }
                var pkgs = RepairContentService.Classify(RepairContentDir);
                ServicingReport = RepairContentService.RenderPlan(pkgs);
                HasServicingReport = true;
                Status = $"Repair Content：{pkgs.Count} 个包（顺序规则已按 neutral-mergedcomponents 优先排好）";
            }
            catch (Exception ex) { ServicingReport = "分类异常：" + ex.Message; HasServicingReport = true; }
        }

        [RelayCommand(CanExecute = nameof(CanService))]
        async Task RunRepairContentAsync()
        {
            if (!Directory.Exists(RepairContentDir)) { Status = "请先选择有效的 Repair Content 目录"; return; }
            if (string.IsNullOrWhiteSpace(RepairContentOutDir)) { Status = "请先指定解包输出目录"; return; }
            IsServicing = true; HasServicingReport = false;
            var sink = EnsureTaskLog("Repair Content 解包");
            try
            {
                var cfg = new RepairContentService.Config
                {
                    SourceDir = RepairContentDir,
                    OutputDir = RepairContentOutDir,
                    ClassifyOnly = ServicingDryRun
                };
                if (!string.IsNullOrWhiteSpace(RepairContentBaseDir)) cfg.BaseDirs.Add(RepairContentBaseDir.Trim());
                var r = await Task.Run(() => RepairContentService.Extract(cfg, m => sink.Write(m)));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(r.Summary());
                if (r.LanguageCatNotes.Count > 0) { sb.AppendLine(); sb.AppendLine("语言安全目录检查："); foreach (var n in r.LanguageCatNotes) sb.AppendLine("  " + n); }
                if (r.Error.Length > 0) sb.AppendLine("错误：" + r.Error);
                if (!ServicingDryRun && Directory.Exists(RepairContentOutDir)) { sb.AppendLine(); sb.AppendLine(RepairContentService.InspectOutput(RepairContentOutDir)); }
                ServicingReport = sb.ToString().TrimEnd();
                HasServicingReport = true;
                Status = r.Success ? "Repair Content 解包完成。" : "Repair Content 解包失败：" + r.Error;
            }
            catch (Exception ex) { ServicingReport = "异常：" + ex.Message; HasServicingReport = true; }
            finally { sink.MarkFinished(); IsServicing = false; }
        }

        // ── 检验已还原的存储 ──
        [RelayCommand]
        void InspectStore()
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(RepairContentOutDir) ? RepairContentOutDir : UpdatePackageOutDir;
                ServicingReport = RepairContentService.InspectOutput(dir);
                HasServicingReport = true;
                Status = "存储检验完成。";
            }
            catch (Exception ex) { ServicingReport = "检验异常：" + ex.Message; HasServicingReport = true; }
        }

        static List<string> SplitDirs(string? text)
            => (text ?? string.Empty)
               .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(x => x.Trim().Trim('"'))
               .Where(x => x.Length > 0)
               .ToList();

        // ── 本地日志接收器 ──
        // CbsBuildViewModel 没有任务日志通道（那是 SxsViewModel 的）。这里自带一个最小实现，
        // 让下面各服务的日志回调有落点，同时把内容汇总进 ServicingReport。
        sealed class LocalSink
        {
            public readonly List<string> Lines = new();
            public void Write(string s) { if (!string.IsNullOrEmpty(s)) Lines.Add(s); }
            public void MarkFinished() { }
        }

        LocalSink EnsureTaskLog(string title) => new();

        // ── 缺失的浏览命令（供 XAML 绑定）──
        [RelayCommand]
        void BrowseTargetWinDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择目标镜像的 Windows 目录（必须在挂载的 VHD/VHDX 上）" };
            if (dlg.ShowDialog() == true) TargetWinDir = System.IO.Path.Combine(dlg.FolderName, "Windows");
        }

        [RelayCommand]
        void PickSkuFile()
        {
            var dlg = new OpenFileDialog { Title = "选择 SKU 组装清单", Filter = "清单 (*.txt)|*.txt|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) AssemblySkuFile = dlg.FileName;
        }

        [RelayCommand]
        void PickUpdateOutDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择更新包解包输出目录" };
            if (dlg.ShowDialog() == true) UpdatePackageOutDir = dlg.FolderName;
        }

        [RelayCommand]
        void PickRepairOutDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择 Repair Content 解包输出目录" };
            if (dlg.ShowDialog() == true) RepairContentOutDir = dlg.FolderName;
        }

        [RelayCommand]
        void PickRepairBaseDir()
        {
            var dlg = new OpenFolderDialog { Title = "选择 delta 源(base)目录（例如已挂载的同版本镜像）" };
            if (dlg.ShowDialog() == true) RepairContentBaseDir = dlg.FolderName;
        }
    }
}
