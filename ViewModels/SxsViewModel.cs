using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.ViewModels
{
    public partial class SxsViewModel : ObservableObject
    {
        private readonly IntegrationToolService _service = new();
        private static readonly string SxsRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ThirdParty", "SxS");

        [ObservableProperty] private string _status = "正在检查随应用集成的 SxS 源码";

        public ObservableCollection<ToolProbeResult> Tools { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();
        public string BundledSourceDirectory => SxsRoot;

        public SxsViewModel() => DiscoverTools();

        [RelayCommand]
        private void DiscoverTools()
        {
            Tools.Clear();
            foreach (var tool in _service.DiscoverSxs(SxsRoot)) Tools.Add(tool);
            Status = $"已发现 {Tools.Count(t => t.IsAvailable)} / {Tools.Count} 个 SxS 工具";
            Log(Status);
        }

        [RelayCommand]
        private void OpenSourceDirectory()
        {
            try
            {
                if (!Directory.Exists(SxsRoot))
                    throw new DirectoryNotFoundException($"随应用集成的 SxS 源码目录不存在：{SxsRoot}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = SxsRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DirectoryNotFoundException)
            {
                MessageBox.Show(ex.Message, "无法打开 SxS 源码目录",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        private void ShowUsage()
        {
            MessageBox.Show(
                "SxS 功能独立于 UUP。\n\n" +
                "pureSxS：处理组件来源和纯 SxS 目录。\n" +
                "explodeSxS：通过 prepare/captureimg/cbss/finishimg 流程处理镜像中的 CBS/SxS。\n\n" +
                "源码随应用集成；运行 pureSxS 需要 Python。explodeSxS 还依赖 DISM 和已获许可的 CBS 工具。\n\n" +
                "注意：prepare.cmd 会快速格式化 X:。当前应用不会自动运行这些上游脚本；执行前请审阅脚本、确认目标磁盘与备份。",
                "SxS 工具说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Log(string text) => Application.Current.Dispatcher.Invoke(
            () => LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {text}"));
    }
}
