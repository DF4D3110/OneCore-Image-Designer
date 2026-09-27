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
                Status = $"✅ 完成：{res.PackagesBuilt} 包 / {res.FilesWritten} 文件（WinSxS manifest {res.ManifestsToWinSxS}、servicing mum {res.MumsToServicing}"
                       + (res.FilesMissing > 0 ? $"，缺源 {res.FilesMissing}" : "，缺源 0") + $"）→ {OutputDir}";
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
    }
}
