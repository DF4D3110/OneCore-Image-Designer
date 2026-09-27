using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OneCore_Image_Designer.ViewModels;

namespace OneCore_Image_Designer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        Services.AppTrace.Log("MainWindow .ctor 进入");
        InitializeComponent();
        Loaded += (_, _) => Services.AppTrace.Log("MainWindow Loaded（起始页首帧渲染）");

        // 快捷键：Ctrl+N 新建 OEMInput，Ctrl+O 打开
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New,
            (s, e) => ((MainViewModel)DataContext).OpenOemInput("new")));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open,
            (s, e) => ((MainViewModel)DataContext).OpenOemInput("open")));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // ICD 式关闭保护：任一脏页签取消则阻止退出
        if (DataContext is MainViewModel vm && !vm.ConfirmCloseAllTabs())
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                Arguments = "shell32.dll,ShellAbout",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"无法打开 Windows Shell 关于对话框：{ex.Message}",
                "打开关于对话框失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
