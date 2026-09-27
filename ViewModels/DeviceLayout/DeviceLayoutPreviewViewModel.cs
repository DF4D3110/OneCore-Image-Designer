using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Models.DeviceLayout;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels.DeviceLayout
{
    /// <summary>
    /// 设备磁盘布局预览视图模型
    /// </summary>
    public class DeviceLayoutPreviewViewModel : ObservableObject
    {
        private readonly MainViewModel _mainVM;

        private string _cabPath = string.Empty;
        public string CabPath { get => _cabPath; set => SetProperty(ref _cabPath, value); }

        private string _statusMessage = "请选择DeviceLayout CAB文件";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        private bool _hasData;
        public bool HasData { get => _hasData; set => SetProperty(ref _hasData, value); }

        private DeviceLayoutDoc? _deviceLayout;
        public DeviceLayoutDoc? DeviceLayout
        {
            get => _deviceLayout;
            set
            {
                if (SetProperty(ref _deviceLayout, value))
                {
                    OnPropertyChanged(nameof(DisplayStores));
                }
            }
        }

        private Store? _selectedStore;
        public Store? SelectedStore { get => _selectedStore; set => SetProperty(ref _selectedStore, value); }

        private string _msPackageRoot = string.Empty;
        public string MSPackageRoot { get => _msPackageRoot; set => SetProperty(ref _msPackageRoot, value); }

        public ObservableCollection<Store> Stores { get; } = new();
        public ObservableCollection<string> FoundCabs { get; } = new();

        /// <summary>
        /// 用于UI显示的Store列表
        /// - 有StoragePools时: 只显示直接Stores（StoragePools内的Store单独分组显示）
        /// - 没有StoragePools时: 显示AllStores（包括v1格式的虚拟Store）
        /// </summary>
        public IEnumerable<Store> DisplayStores
        {
            get
            {
                if (DeviceLayout?.StoragePools.Count > 0)
                    return Stores; // 只显示直接Stores
                if (DeviceLayout != null)
                    return DeviceLayout.AllStores;
                return Stores;
            }
        }

        // Commands
        public ICommand GoToStartCommand { get; }
        public ICommand BrowseCabCommand { get; }
        public ICommand BrowseMSPackageRootCommand { get; }
        public ICommand SearchCabsCommand { get; }
        public ICommand LoadSelectedCabCommand { get; }
        public ICommand SelectStoreCommand { get; }

        public DeviceLayoutPreviewViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;

            GoToStartCommand = new RelayCommand(GoToStart);
            BrowseCabCommand = new RelayCommand(BrowseCab);
            BrowseMSPackageRootCommand = new RelayCommand(BrowseMSPackageRoot);
            SearchCabsCommand = new RelayCommand(SearchCabs);
            LoadSelectedCabCommand = new RelayCommand<string>(LoadSelectedCab);
            SelectStoreCommand = new RelayCommand<Store?>(SelectStore);
        }

        private void GoToStart()
        {
            _mainVM.NavigateToStart();
        }

        private void BrowseCab()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 DeviceLayout CAB 文件",
                Filter = "CAB Files (*.cab)|*.cab|All Files (*.*)|*.*",
                InitialDirectory = Directory.Exists(MSPackageRoot) ? MSPackageRoot : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                CabPath = dialog.FileName;
                LoadDeviceLayout();
            }
        }

        private void BrowseMSPackageRoot()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 MSPackageRoot 目录 (自动搜索DeviceLayout CAB)",
                InitialDirectory = Directory.Exists(MSPackageRoot) ? MSPackageRoot : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                MSPackageRoot = dialog.FolderName;
                SearchCabs();
            }
        }

        private void SearchCabs()
        {
            if (string.IsNullOrEmpty(MSPackageRoot) || !Directory.Exists(MSPackageRoot))
            {
                StatusMessage = "请先设置MSPackageRoot目录";
                return;
            }

            var cabs = DeviceLayoutService.SearchDeviceLayoutCabs(MSPackageRoot);
            FoundCabs.Clear();
            foreach (var cab in cabs)
                FoundCabs.Add(cab);

            StatusMessage = $"找到 {cabs.Count} 个DeviceLayout CAB文件";
        }

        private void LoadSelectedCab(string path)
        {
            CabPath = path;
            LoadDeviceLayout();
        }

        private void LoadDeviceLayout()
        {
            if (string.IsNullOrEmpty(CabPath) || !File.Exists(CabPath))
            {
                StatusMessage = "CAB文件不存在";
                return;
            }

            StatusMessage = "正在解析DeviceLayout...";
            HasData = false;
            Stores.Clear();

            var doc = DeviceLayoutService.ParseFromCab(CabPath);
            if (doc == null)
            {
                StatusMessage = $"解析失败: {DeviceLayoutService.LastError}\n详细日志: {DeviceLayoutService.LastLogFile}";
                return;
            }

            DeviceLayout = doc;
            doc.SourceCabPath = CabPath;

            Stores.Clear();
            // 只添加直接Stores（不在StoragePools中的）
            // StoragePools中的Store通过 DeviceLayout.StoragePools 绑定显示
            foreach (var store in doc.Stores)
            {
                Stores.Add(store);
            }

            HasData = Stores.Count > 0 || (DeviceLayout?.AllStores.Count > 0);
            StatusMessage = $"解析成功: {Stores.Count} 个磁盘, {Stores.Sum(s => s.Partitions.Count)} 个分区";
            _mainVM.SetStatus(StatusMessage);
        }

        private void SelectStore(Store? store)
        {
            SelectedStore = store;
        }
    }
}
