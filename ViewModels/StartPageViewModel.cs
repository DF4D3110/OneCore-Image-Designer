using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>起始页"最近使用"条目。</summary>
    public class RecentItem
    {
        public string Path { get; set; } = string.Empty;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Kind { get; set; } = string.Empty;   // OEMInput / 镜像
        public string Glyph { get; set; } = "\uE8A5";        // Segoe MDL2 图标
    }

    /// <summary>ICD 首页 Recent projects 磁贴条目（.cwproj 工程）。</summary>
    public class RecentProjectItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string DirectorySummary => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        public string TimeText
        {
            get
            {
                var span = DateTime.UtcNow - LastOpenedUtc;
                if (span.TotalDays >= 7) return LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd");
                if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天前";
                if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时前";
                return "刚刚";
            }
        }
        public DateTime LastOpenedUtc { get; set; }
    }

    /// <summary>
    /// 起始页视图模型 —— ICD 式分组：新建工程 / 最近使用 / 实用工具。
    /// </summary>
    public partial class StartPageViewModel : ObservableObject
    {
        private readonly MainViewModel _mainVM;

        [ObservableProperty]
        private string _welcomeMessage = "OneCore Image Designer";

        [ObservableProperty]
        private string _subtitle = "WCOS 镜像设计与工具集 · 选择一个功能开始";

        public ObservableCollection<RecentItem> RecentFiles { get; } = new();

        public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

        public bool HasRecentFiles => RecentFiles.Count > 0;
        public bool HasRecentProjects => RecentProjects.Count > 0;

        public StartPageViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            RefreshRecents();
        }

        /// <summary>刷新最近工程磁贴（ICD 首页用）+ 兼容刷新最近文件。</summary>
        public void RefreshRecents()
        {
            RecentProjects.Clear();
            foreach (var e in DesignerProjectService.GetRecentProjects())
                RecentProjects.Add(new RecentProjectItem
                {
                    Name = e.Name,
                    Path = e.Path,
                    LastOpenedUtc = e.LastOpenedUtc,
                });

            RecentFiles.Clear();
            foreach (var f in RecentFilesService.GetRecentFiles())
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                RecentFiles.Add(new RecentItem
                {
                    Path = f,
                    Kind = ext == ".xml" ? "OEMInput" : "镜像",
                    Glyph = ext == ".xml" ? "\uE8A5" : "\uE950",
                });
            }
            OnPropertyChanged(nameof(HasRecentFiles));
            OnPropertyChanged(nameof(HasRecentProjects));
        }

        // ── 新建/功能入口（Tile 点击）──
        [RelayCommand] private void NewOemInput() => _mainVM.OpenOemInput("new");
        [RelayCommand] private void OpenOemInput() => _mainVM.OpenOemInput("open");
        [RelayCommand] private void OpenDeviceLayout() => _mainVM.NavigateToDeviceLayoutPreview();
        [RelayCommand] private void OpenDeviceLayoutBuilder() => _mainVM.OpenDeviceLayoutBuilder();
        [RelayCommand] private void OpenCbsBuild() => _mainVM.OpenCbsBuild();
        [RelayCommand] private void OpenImageBuild() => _mainVM.NavigateToImageBuild();
        [RelayCommand] private void OpenSystemReconstruct() => _mainVM.NavigateToSystemReconstruct();
        [RelayCommand] private void OpenFfuBrowser() => _mainVM.NavigateToFFUBrowser();
        [RelayCommand] private void OpenUup() => _mainVM.NavigateToUup();
        [RelayCommand] private void OpenSxs() => _mainVM.NavigateToSxs();

        // ── 实用工具入口（直接开窗口）──
        [RelayCommand] private void OpenToolRegistry() => _mainVM.OpenToolRegistry();
        [RelayCommand] private void OpenToolDriver() => _mainVM.OpenToolDriver();
        [RelayCommand] private void OpenToolFirmware() => _mainVM.OpenToolFirmware();
        [RelayCommand] private void OpenToolHash() => _mainVM.OpenToolHash();
        [RelayCommand] private void OpenToolDependency() => _mainVM.OpenToolDependency();
        [RelayCommand] private void OpenToolStoreVerify() => _mainVM.OpenToolStoreVerify();

        [RelayCommand]
        private void OpenRecent(RecentItem? item)
        {
            if (item == null) return;
            _mainVM.OpenRecentFile(item.Path);
            RefreshRecents();
        }

        [RelayCommand]
        private void OpenRecentProjectTile(RecentProjectItem? item)
        {
            if (item == null) return;
            _mainVM.OpenRecentProject(item.Path);
            RefreshRecents();
        }

        [RelayCommand]
        private void ClearRecent()
        {
            RecentFilesService.ClearRecentFiles();
            RefreshRecents();
        }
    }
}
