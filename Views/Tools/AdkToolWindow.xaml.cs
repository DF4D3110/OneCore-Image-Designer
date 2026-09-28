using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    /// <summary>
    /// ADK 工具下载管理窗口：
    /// 查询 GitHub Release（adktool）资产清单 → 下载指定 build 工具包 → SHA256 校验 → 解压安装，
    /// 并提供内置 addonkit PowerShell shell 的启动入口。
    /// </summary>
    public partial class AdkToolWindow : Window
    {
        private readonly AdkToolService _service = new();
        private readonly ObservableCollection<AdkToolRow> _rows = new();
        private bool _busy;

        public AdkToolWindow()
        {
            InitializeComponent();
            GridAssets.ItemsSource = _rows;

            TxtModule.Text = AddonKitShellService.IsModuleAvailable
                ? $"✓ 内置 addonkit 模块就绪（{AdkToolService.AddonKitRoot}）"
                : $"✗ 未找到内置模块，期望位置：{AddonKitShellService.ModuleManifest}（请重新拉取仓库并完整构建）";

            var installed = AdkToolService.GetInstalledBuilds();
            Log(installed.Count > 0
                ? "已安装 ADK：" + string.Join(", ", installed)
                : "尚未安装任何 ADK 工具包，点击“刷新列表”获取可下载版本。");
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            BtnRefresh.IsEnabled = false;
            try
            {
                Log("正在查询 GitHub Release（adktool）…");
                var assets = await _service.FetchAssetsAsync();
                var installed = new HashSet<int>(AdkToolService.GetInstalledBuilds());
                _rows.Clear();
                foreach (var a in assets)
                    _rows.Add(new AdkToolRow(a, installed.Contains(a.Build)));
                Log($"找到 {_rows.Count} 个可用工具包。");
            }
            catch (Exception ex)
            {
                Log("查询失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                BtnRefresh.IsEnabled = true;
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if ((sender as Button)?.Tag is not AdkToolRow row) return;

            _busy = true;
            var btn = (Button)sender;
            btn.Content = "下载中…";
            ProgressDownload.Value = 0;
            try
            {
                // Progress<T> 在 UI 线程创建，回调自动回到 UI 线程
                var progress = new Progress<double>(v => ProgressDownload.Value = v);
                var log = new Progress<string>(Log);
                await _service.DownloadAndInstallAsync(row.Asset, progress, log);
                row.Installed = true;
                // 清除本地值，让样式的“已安装/禁用”触发器接管（本地值优先级高于 Style 触发器）
                btn.ClearValue(ContentProperty);
                Log($"ADK {row.Build} 安装完成：{Path.Combine(AdkToolService.AdkRoot, row.Build)}");
            }
            catch (Exception ex)
            {
                Log("下载失败：" + ex.Message);
                btn.Content = "重试";
                btn.IsEnabled = true;
            }
            finally
            {
                _busy = false;
                ProgressDownload.Value = 0;
            }
        }

        private void Shell_Click(object sender, RoutedEventArgs e)
        {
            var log = new Progress<string>(Log);
            AddonKitShellService.LaunchShell(null, log);
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AdkToolService.AdkRoot);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AdkToolService.AdkRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("无法打开目录：" + ex.Message);
            }
        }

        private void Log(string text) =>
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");

        /// <summary>资产行模型（DataGrid 绑定用）。</summary>
        public sealed class AdkToolRow : INotifyPropertyChanged
        {
            private bool _installed;

            public AdkToolRow(AdkToolReleaseAsset asset, bool installed)
            {
                Asset = asset;
                _installed = installed;
            }

            public AdkToolReleaseAsset Asset { get; }
            public string Build => Asset.Build.ToString();
            public string FileName => Asset.FileName;
            public string Size => Asset.SizeDisplay;
            public string DigestDisplay => Asset.Sha256 != null ? Asset.Sha256 : "(GitHub 未提供)";

            public bool Installed
            {
                get => _installed;
                set { _installed = value; OnPropertyChanged(nameof(Installed)); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
