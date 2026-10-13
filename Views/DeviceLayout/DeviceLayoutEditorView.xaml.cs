using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OneCore_Image_Designer.Models.DeviceLayout;
using OneCore_Image_Designer.ViewModels.DeviceLayout;

namespace OneCore_Image_Designer.Views.DeviceLayout
{
    /// <summary>
    /// 设备布局编辑器（标签页内嵌）：4 步向导 + DiskGenius 式图形分区条。
    /// 逻辑都在 <see cref="DeviceLayoutEditorViewModel"/>，这里只做控件交互桥接。
    /// </summary>
    public partial class DeviceLayoutEditorView : UserControl
    {
        DeviceLayoutEditorViewModel Vm => (DeviceLayoutEditorViewModel)DataContext;
        bool _syncing;

        public DeviceLayoutEditorView()
        {
            Services.AppTrace.Log("EditorView .ctor 进入");
            InitializeComponent();
            Services.AppTrace.Log("EditorView InitializeComponent 完成");
            Loaded += (_, _) =>
            {
                Services.AppTrace.Log("EditorView Loaded");
                if (Vm == null) return;
                Vm.PropertyChanged += Vm_PropertyChanged;
                SyncFromVm();
                Services.AppTrace.Log("EditorView Loaded 完成");
            };
            Unloaded += (_, _) => { if (Vm != null) Vm.PropertyChanged -= Vm_PropertyChanged; };
        }

        void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DeviceLayoutEditorViewModel.Shape) or nameof(DeviceLayoutEditorViewModel.Era))
                Dispatcher.BeginInvoke(SyncFromVm);
        }

        void SyncFromVm()
        {
            if (Vm == null || CmbShape == null) return;
            _syncing = true;
            CmbShape.SelectedIndex = Vm.Shape switch
            {
                "flat" => 0, "stores" => 1, "pools" => 2, "mspools" => 3, _ => 0
            };
            if (CmbEra != null) CmbEra.SelectedIndex = Vm.Era;
            _syncing = false;
        }

        // ── 步1：形状 / 时代 / 导入 / 空白 ──
        void Shape_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm == null || CmbShape == null) return;
            Vm.Shape = CShape();
        }
        string CShape() => CmbShape.SelectedIndex switch
        {
            1 => "stores", 2 => "pools", 3 => "mspools", _ => "flat"
        };

        void Era_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm == null || CmbEra == null) return;
            Vm.Era = CmbEra.SelectedIndex;
        }

        void Arch_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || Vm == null || CmbArch2 == null) return;
            Vm.Architecture = ((ComboBoxItem)CmbArch2.SelectedItem)?.Content?.ToString() ?? "arm64";
        }

        void Import_Click(object sender, RoutedEventArgs e) => DoImport();
        void Import2_Click(object sender, RoutedEventArgs e) => DoImport();

        void DoImport()
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入布局起点",
                Filter = "布局/包 (*.xml;*.cab)|*.xml;*.cab|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                Vm.LoadFromFile(dlg.FileName);
        }

        void Blank_Click(object sender, RoutedEventArgs e)
        {
            Vm.SelectedTemplate = null;
            var doc = new Models.DeviceLayout.DeviceLayoutDoc { SectorSize = Vm.SectorSize, ChunkSize = 128 };
            doc.Partitions.Add(new Models.DeviceLayout.Partition { Name = "EFIESP", Type = "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}", FileSystem = "FAT", TotalSectors = 102400, Bootable = true, RequiredToFlash = true });
            doc.Partitions.Add(new Models.DeviceLayout.Partition { Name = "MainOS", Type = "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}", FileSystem = "NTFS", UseAllSpace = true });
            Vm.ClearTemplateBinding();
            Vm.ApplyDoc(doc);
            Vm.Shape = "flat";
            Vm.LastLog = "从空白单盘开始（EFIESP + MainOS）";
        }

        // ── 步2：色块点击 / 增删 / 属性应用 ──
        void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;   // 阻止冒泡到磁盘条带，避免点分区把盘选中冲掉
            if (sender is FrameworkElement { Tag: PartitionTile tile })
                Vm.SelectTile(tile);
        }

        void DiskBand_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: DiskNode d })
                Vm.SelectDisk(d);
        }

        // ── 磁盘级操作 ──
        void AddDiskLevel_Click(object sender, RoutedEventArgs e) => Vm.AddDiskLevel();
        void AddPoolDiskLevel_Click(object sender, RoutedEventArgs e) => Vm.AddPoolDiskLevel();
        void UpgradePool_Click(object sender, RoutedEventArgs e) => Vm.UpgradeToPool();
        void RemoveDisk_Click(object sender, RoutedEventArgs e) => Vm.RemoveSelectedDisk();
        void CommitDisk_Click(object sender, RoutedEventArgs e) => Vm.CommitDiskEdits();
        void MoveSelectedHere_Click(object sender, RoutedEventArgs e)
        {
            Vm.MoveSelectedPartition(Vm.SelectedDiskNode!);
            foreach (var d in Vm.Disks) d.RebuildTiles();
            SyncTiles();
        }

        void AddPart_Click(object sender, RoutedEventArgs e)
        {
            Vm.AddPartitionToSelectedDisk();
            SyncTiles();
        }

        void DelPart_Click(object sender, RoutedEventArgs e)
        {
            Vm.RemoveSelectedPartition();
            SyncTiles();
        }

        void PartUp_Click(object sender, RoutedEventArgs e)
        {
            Vm.MovePartitionUp();
            SyncTiles();
        }

        void PartDown_Click(object sender, RoutedEventArgs e)
        {
            Vm.MovePartitionDown();
            SyncTiles();
        }

        void ApplyProp_Click(object sender, RoutedEventArgs e)
        {
            // TextBox 已即时写回分区对象；这里刷新色块与校验态
            Vm.OnPartitionEdited();
            SyncTiles();
        }

        void SyncTiles()
        {
            if (Vm.SelectedPartition == null) return;
            foreach (var d in Vm.Disks)
                foreach (var t in d.Tiles)
                    t.IsSelected = ReferenceEquals(t.P, Vm.SelectedPartition);
        }

        // ── 步3：验证 ──
        async void Validate_Click(object sender, RoutedEventArgs e)
        {
            BtnValidate.IsEnabled = false;
            try { await Vm.RunValidationAsync(); }
            finally { BtnValidate.IsEnabled = true; }
        }

        // ── 步4：打包 ──
        void BrowseOut_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择输出目录" };
            if (System.IO.Directory.Exists(Vm.OutputDir)) dlg.InitialDirectory = Vm.OutputDir;
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                Vm.OutputDir = dlg.FolderName;
        }

        void SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (Vm.Disks.Sum(d => d.Parts.Count) == 0)
            {
                MessageBox.Show("分区表为空，无模板可存。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new Views.Tools.SaveTemplateDialog(Window.GetWindow(this)!) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            try
            {
                Vm.SaveAsTemplate(dlg.TemplateKey, dlg.TemplateTitle, dlg.TemplateDeviceType,
                    dlg.TemplateNotes, dlg.RuleChoice);
                Vm.LastLog = "✅ 已存为自定义模板：" + dlg.TemplateTitle;
                Vm.RefreshTemplates();
            }
            catch (Exception ex) { MessageBox.Show("保存模板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        async void Build_Click(object sender, RoutedEventArgs e)
        {
            BtnBuild.IsEnabled = false;
            try
            {
                var res = await Vm.BuildAsync();
                string msg = res.Success
                    ? "✅ 打包完成：" + res.CabPath + (string.IsNullOrEmpty(res.FipCabPath) ? "" : "；FIP：" + res.FipCabPath) + "；FM：" + res.FmPath
                    : "❌ 打包失败：" + res.Error;
                if (res.Success && Vm.HasAnyBspComponent)
                {
                    var cres = await Vm.BuildComponentsAsync();
                    var done = new System.Collections.Generic.List<string>();
                    if (!string.IsNullOrEmpty(cres.DevicePlatformCabPath)) done.Add("DevicePlatform");
                    if (!string.IsNullOrEmpty(cres.DeviceInfoCabPath)) done.Add("DeviceInfo");
                    if (!string.IsNullOrEmpty(cres.MachineInfoPath)) done.Add("machineinfo");
                    if (!string.IsNullOrEmpty(cres.SmbiosSourceDir)) done.Add("SMBIOS源");
                    if (cres.DriverPackages.Count > 0) done.Add($"驱动包×{cres.DriverPackages.Count}");
                    if (!string.IsNullOrEmpty(cres.FmPath)) done.Add("统一FM");
                    msg += cres.Error == null
                        ? "\n✅ BSP 组件完成：" + string.Join("、", done)
                        : "\n❌ BSP 组件失败：" + cres.Error;
                }
                Vm.LastLog = msg;
            }
            finally { BtnBuild.IsEnabled = true; }
        }

        async void BuildComponents_Click(object sender, RoutedEventArgs e)
        {
            if (!Vm.HasAnyBspComponent)
            {
                Vm.LastLog = "未勾选任何 BSP 组件。";
                return;
            }
            BtnBuildComponents.IsEnabled = false;
            try
            {
                var cres = await Vm.BuildComponentsAsync();
                var done = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(cres.DevicePlatformCabPath)) done.Add("DevicePlatform：" + cres.DevicePlatformCabPath);
                if (!string.IsNullOrEmpty(cres.DeviceInfoCabPath)) done.Add("DeviceInfo：" + cres.DeviceInfoCabPath);
                if (!string.IsNullOrEmpty(cres.MachineInfoPath)) done.Add("machineinfo：" + cres.MachineInfoPath);
                if (!string.IsNullOrEmpty(cres.SmbiosSourceDir)) done.Add("SMBIOS 源目录：" + cres.SmbiosSourceDir);
                foreach (var dp in cres.DriverPackages)
                    done.Add("驱动 " + dp.OutputName + "（" + dp.FeatureId + "）" +
                             (string.IsNullOrEmpty(dp.CabPath) ? "：源包 " + dp.PackageDir : "：cab " + dp.CabPath));
                if (!string.IsNullOrEmpty(cres.FmPath)) done.Add("统一 FM：" + cres.FmPath);
                Vm.LastLog = cres.Error == null
                    ? "✅ BSP 组件生成完成：\n" + string.Join("\n", done)
                    : "❌ BSP 组件失败：" + cres.Error;
            }
            finally { BtnBuildComponents.IsEnabled = true; }
        }

        void BrowseDriverRoot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择驱动源目录（递归扫描 *.inf，每个含 INF 的目录成一个驱动包）" };
            if (dlg.ShowDialog() == true) Vm.DriverScanRoot = dlg.FolderName;
        }

        void ScanDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Vm.DriverScanRoot) || !System.IO.Directory.Exists(Vm.DriverScanRoot))
            {
                Vm.LastLog = "请先选择有效的驱动源目录。";
                return;
            }
            BtnScanDrv.IsEnabled = false;
            try
            {
                Vm.ScanDriversPreview();
                Vm.LastLog = Vm.DriverPreviewSummary;
            }
            finally { BtnScanDrv.IsEnabled = true; }
        }

        // ── 步进 ──
        void Next_Click(object sender, RoutedEventArgs e)
        {
            Vm.Next();
            if (Vm.Step == 3 && Vm.ValidatedOk == null)
                Dispatcher.BeginInvoke(async () => await Vm.RunValidationAsync());
        }
        void Prev_Click(object sender, RoutedEventArgs e) => Vm.Prev();
    }
}
