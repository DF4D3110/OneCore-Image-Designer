using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.ViewModels.OEMInput;
using OneCore_Image_Designer.ViewModels.DeviceLayout;
using OneCore_Image_Designer.ViewModels.ImageBuild;
using OneCore_Image_Designer.ViewModels.SystemReconstruct;
using OneCore_Image_Designer.ViewModels.FFUBrowser;
using OneCore_Image_Designer.Views.Tools;
using System.ComponentModel;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>
    /// 应用程序主视图模型 —— ICD 式外壳：
    /// 固定起始页 + 每个功能一个可关闭标签页（同 Key 复用），脏页签关闭前保护。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        public ObservableCollection<AppTabViewModel> Tabs { get; } = new();

        [ObservableProperty]
        private AppTabViewModel? _selectedTab;

        [ObservableProperty]
        private string _title = "OneCore Image Designer";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "就绪";

        private readonly AppTabViewModel _startTab;

        // 各功能 VM 缓存（同 Key 复用同一实例，切标签不丢状态）
        private OEMInputDesignerViewModel? _oemInputVM;
        private DeviceLayoutPreviewViewModel? _deviceLayoutVM;
        private ImageBuildViewModel? _imageBuildVM;
        private SystemReconstructViewModel? _systemReconstructVM;
        private FullReconstructViewModel? _fullReconstructVM;
        private SelectReconstructViewModel? _selectReconstructVM;
        private FFUBrowserViewModel? _ffuBrowserVM;
        private DeviceLayout.DeviceLayoutEditorViewModel? _deviceLayoutEditorVM;
        private Cbs.CbsBuildViewModel? _cbsBuildVM;
        private UupViewModel? _uupVM;
        private SxsViewModel? _sxsVM;
        private AddonKitViewModel? _addonKitVM;

        public void NavigateToUup()
        {
            OpenTab("uup", "UUP 更新查询与下载", () =>
            {
                _uupVM ??= new UupViewModel();
                return ((object)_uupVM, null);
            });
        }

        public void NavigateToSxs()
        {
            OpenTab("sxs", "SxS / CBS 工具", () =>
            {
                _sxsVM ??= new SxsViewModel();
                return ((object)_sxsVM, null);
            });
        }

        [RelayCommand] public void OpenUup() => NavigateToUup();
        [RelayCommand] public void OpenSxs() => NavigateToSxs();

        /// <summary>打开（或激活）内置 addonkit 工作区标签页。</summary>
        public void NavigateToAddonKit()
        {
            OpenTab("addonkit", "addonkit 工作区", () =>
            {
                _addonKitVM ??= new AddonKitViewModel(this);
                return ((object)_addonKitVM, null);
            });
        }

        [RelayCommand] public void OpenAddonKit() => NavigateToAddonKit();

        /// <summary>打开（或激活）CBS 构建标签页。</summary>
        [RelayCommand] public void OpenCbsBuild() => NavigateToCbsBuild();

        public void NavigateToCbsBuild()
        {
            Services.AppTrace.Log("NavigateToCbsBuild 进入");
            OpenTab("cbsbuild", "CBS 构建", () =>
            {
                _cbsBuildVM ??= new Cbs.CbsBuildViewModel(this);
                return ((object)_cbsBuildVM, null);
            });
        }

        /// <summary>OEMInput 联动用：取得（必要时创建）设备布局编辑器 VM，并在需要时载入模板。</summary>
        public DeviceLayout.DeviceLayoutEditorViewModel EnsureDeviceLayoutEditor(Services.DeviceLayoutTemplateInfo? templateToLoad = null)
        {
            NavigateToDeviceLayoutEditor();
            _deviceLayoutEditorVM ??= new DeviceLayout.DeviceLayoutEditorViewModel(this);
            if (templateToLoad != null) _deviceLayoutEditorVM.LoadTemplate(templateToLoad);
            return _deviceLayoutEditorVM;
        }

        public MainViewModel()
        {
            _startTab = new AppTabViewModel("start", "起始页", new StartPageViewModel(this), isPinned: true);
            Tabs.Add(_startTab);
            SelectedTab = _startTab;
        }

        /// <summary>
        /// 打开（或激活）一个功能标签页。factory 只在首次创建时执行。
        /// </summary>
        private AppTabViewModel OpenTab(string key, string title, Func<(object content, Func<bool>? closeGuard)> factory)
        {
            var existing = Tabs.FirstOrDefault(t => t.Key == key);
            if (existing != null)
            {
                SelectedTab = existing;
                return existing;
            }

            var (content, guard) = factory();
            var tab = new AppTabViewModel(key, title, content, isPinned: false) { ConfirmClosing = guard };
            Tabs.Add(tab);
            SelectedTab = tab;
            return tab;
        }

        partial void OnSelectedTabChanged(AppTabViewModel? value)
        {
            if (value != null)
                StatusMessage = value.Key == "start" ? "就绪" : value.Title;
        }

        /// <summary>请求关闭标签页（固定页忽略）。脏保护返回 false 时不关闭。</summary>
        [RelayCommand]
        private void CloseTab(AppTabViewModel? tab)
        {
            if (tab == null || tab.IsPinned) return;
            if (tab.ConfirmClosing != null && !tab.ConfirmClosing()) return;

            int idx = Tabs.IndexOf(tab);
            Tabs.Remove(tab);
            if (SelectedTab == tab)
                SelectedTab = Tabs[Math.Min(idx, Tabs.Count - 1)];
        }

        private void GoToStart() => SelectedTab = _startTab;

        /// <summary>OEMInput 页签标题跟随工程名（打开/另存工程后刷新）。</summary>
        public void RefreshTabTitle()
        {
            var tab = Tabs.FirstOrDefault(t => t.Key == "oeminput");
            if (tab != null && _oemInputVM != null)
                tab.Title = string.IsNullOrWhiteSpace(_oemInputVM.CurrentProjectPath)
                    ? "OEMInput 设计器"
                    : $"OEMInput · {_oemInputVM.ProjectName}";
        }

        // ════════════════════ 导航（供各功能 VM 与起始页调用） ════════════════════

        [RelayCommand]
        public void NavigateToStart() => GoToStart();

        public void NavigateToOEMInputDesigner()
        {
            OpenTab("oeminput", "OEMInput 设计器", () =>
            {
                _oemInputVM ??= new OEMInputDesignerViewModel(this);
                return ((object)_oemInputVM, _oemInputVM.ConfirmDiscardIfDirty);
            });
        }

        /// <summary>mode: new=新建工程；open=打开工程(.cwproj)；openxml=直接打开 OEMInput.xml。</summary>
        [RelayCommand]
        public void OpenOemInput(string mode)
        {
            if (mode == "open" || mode == "openxml")
            {
                NavigateToOEMInputDesigner();
                if (_oemInputVM != null && !_oemInputVM.ConfirmDiscardIfDirty()) return;

                bool wantProject = mode == "open";
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = wantProject ? "打开工程" : "打开 OEMInput.xml",
                    Filter = wantProject ? Services.DesignerProjectService.Filter
                                         : "OEMInput (*.xml)|*.xml|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                bool ok = wantProject
                    ? _oemInputVM!.OpenProject(dlg.FileName)
                    : _oemInputVM!.LoadFromXml(dlg.FileName);

                if (ok)
                {
                    if (!wantProject) Services.RecentFilesService.AddRecentFile(dlg.FileName);
                    RefreshTabTitle();
                    ((StartPageViewModel)_startTab.Content).RefreshRecents();
                }
                else
                {
                    MessageBox.Show($"无法打开该文件:\n{dlg.FileName}", "打开失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            // 新建工程
            NavigateToOEMInputDesigner();
            if (_oemInputVM != null && !_oemInputVM.ConfirmDiscardIfDirty()) return;
            _oemInputVM?.ResetForNewProject();
            RefreshTabTitle();
        }

        public void NavigateToDeviceLayoutPreview()
        {
            OpenTab("devicelayout", "设备磁盘布局预览", () =>
            {
                _deviceLayoutVM ??= new DeviceLayoutPreviewViewModel(this);
                return ((object)_deviceLayoutVM, null);
            });
        }

        public void NavigateToImageBuild()
        {
            OpenTab("imagebuild", "镜像构建", () =>
            {
                _imageBuildVM ??= new ImageBuildViewModel(this);
                return ((object)_imageBuildVM, null);
            });
        }

        public void NavigateToSystemReconstruct()
        {
            OpenTab("sysreconstruct", "系统重构", () =>
            {
                _systemReconstructVM ??= new SystemReconstructViewModel(this);
                return ((object)_systemReconstructVM, null);
            });
        }

        /// <summary>完全构建 / 选择构建各自独立标签页；从系统重构页进入。</summary>
        public void NavigateToFullReconstruct()
        {
            OpenTab("sysrecon-full", "系统重构 - 完全构建", () =>
            {
                _fullReconstructVM ??= new FullReconstructViewModel(this);
                return ((object)_fullReconstructVM, null);
            });
        }

        public void NavigateToSelectReconstruct()
        {
            OpenTab("sysrecon-select", "系统重构 - 选择构建", () =>
            {
                _selectReconstructVM ??= new SelectReconstructViewModel(this);
                return ((object)_selectReconstructVM, null);
            });
        }

        public void NavigateToFFUBrowser()
        {
            OpenTab("ffubrowser", "FFU 文件浏览器", () =>
            {
                _ffuBrowserVM ??= new FFUBrowserViewModel(this);
                return ((object)_ffuBrowserVM, null);
            });
        }

        public void SetStatus(string message) => StatusMessage = message;

        // 供主窗口"功能"菜单绑定的命令包装
        [RelayCommand] public void GotoOemInput() => NavigateToOEMInputDesigner();
        [RelayCommand] public void GotoDeviceLayout() => NavigateToDeviceLayoutPreview();
        [RelayCommand] public void OpenDeviceLayoutBuilder() => NavigateToDeviceLayoutEditor();
        [RelayCommand] public void GotoImageBuild() => NavigateToImageBuild();
        public void NavigateToDeviceLayoutEditor()
        {
            Services.AppTrace.Log("NavigateToDeviceLayoutEditor 进入");
            OpenTab("dlbuilder", "设备布局创建", () =>
            {
                _deviceLayoutEditorVM ??= new DeviceLayout.DeviceLayoutEditorViewModel(this);
                Services.AppTrace.Log("EditorVM 就绪");
                return ((object)_deviceLayoutEditorVM, null);
            });
            Services.AppTrace.Log("OpenTab 返回（等渲染器构造+首帧）");
        }

        [RelayCommand] public void GotoSystemReconstruct() => NavigateToSystemReconstruct();
        [RelayCommand] public void GotoFfuBrowser() => NavigateToFFUBrowser();

        /// <summary>起始页"最近使用"点击分发：OEMInput.xml → 设计器；镜像文件 → FFU 浏览器。</summary>
        [RelayCommand]
        public void OpenRecentFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("文件不存在或已被移动。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".xml")
            {
                NavigateToOEMInputDesigner();
                if (_oemInputVM != null && _oemInputVM.ConfirmDiscardIfDirty())
                    _oemInputVM.LoadFromXml(path);
            }
            else if (ext is ".ffu" or ".vhdx" or ".vhd" or ".img" or ".bin")
            {
                NavigateToFFUBrowser();
                _ffuBrowserVM?.OpenImageFile(path);
            }
            else
            {
                MessageBox.Show($"无法打开此类型文件: {ext}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ════════════════════ 菜单：工程（对齐 ICD File 菜单） ════════════════════

        [RelayCommand]
        public void SaveProject()
        {
            NavigateToOEMInputDesigner();
            _oemInputVM?.SaveProjectCommand.Execute(null);
            RefreshTabTitle();
            ((StartPageViewModel)_startTab.Content).RefreshRecents();
        }

        [RelayCommand]
        public void CloseCurrentProject()
        {
            var tab = SelectedTab;
            if (tab != null && !tab.IsPinned) CloseTab(tab);
        }

        /// <summary>首页 Recent projects 磁贴点击：重开 .cwproj 工程。</summary>
        [RelayCommand]
        public void OpenRecentProject(string? path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("工程文件不存在或已被移动。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                ((StartPageViewModel)_startTab.Content).RefreshRecents();
                return;
            }
            NavigateToOEMInputDesigner();
            if (_oemInputVM != null && _oemInputVM.ConfirmDiscardIfDirty())
            {
                if (_oemInputVM.OpenProject(path))
                {
                    RefreshTabTitle();
                    ((StartPageViewModel)_startTab.Content).RefreshRecents();
                }
                else
                    MessageBox.Show($"无法打开工程:\n{path}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ════════════════════ 菜单：工具窗口 ════════════════════

        [RelayCommand] public void OpenToolRegistry() => ShowToolWindow(() => new OfflineRegistryEditorWindow(), "离线注册表编辑器");
        [RelayCommand] public void OpenToolDriver() => ShowToolWindow(() => new DriverExtractorWindow(), "驱动提取与整理");
        [RelayCommand] public void OpenToolFirmware() => ShowToolWindow(() => new FirmwareParserWindow(), "UEFI 固件解析器");
        [RelayCommand] public void OpenToolHash() => ShowToolWindow(() => new WinSxSHashCalculatorWindow(), "WinSxS Hash 计算器");
        [RelayCommand] public void OpenToolDependency() => ShowToolWindow(() => new DependencyViewerWindow(), "包依赖关系分析器");
        [RelayCommand] public void OpenToolStoreVerify() => ShowToolWindow(() => new ComponentStoreVerifierWindow(), "组件存储完整性校验");
        [RelayCommand] public void OpenAdkTool() => ShowToolWindow(() => new AdkToolWindow(), "ADK 工具下载");

        private void ShowToolWindow(Func<Window> create, string name)
        {
            var win = create();
            win.Owner = Application.Current.MainWindow;
            win.Show();
            StatusMessage = name;
        }

        [RelayCommand]
        private void ExitApp() => Application.Current.Shutdown();

        /// <summary>窗口关闭时逐个过脏保护；用户取消则返回 false 阻止退出。</summary>
        public bool ConfirmCloseAllTabs()
        {
            foreach (var tab in Tabs.Where(t => !t.IsPinned && t.ConfirmClosing != null).ToList())
                if (!tab.ConfirmClosing()) { SelectedTab = tab; return false; }
            return true;
        }
    }
}
