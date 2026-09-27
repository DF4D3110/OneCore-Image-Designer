using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

namespace OneCore_Image_Designer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static bool _isHandlingException = false;
    private static readonly string CrashLogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 确保崩溃日志目录存在
        try { Directory.CreateDirectory(CrashLogDir); } catch { }

        // UI 冻结看门狗：心跳 10s 无响应自动写 freeze 报告 + dump（无异常冻结的唯一取证手段）
        try { Services.FreezeWatchdog.Start(this); } catch { }
        Services.AppTrace.Log("App.OnStartup 完成");
        // 心跳打点：每 5 次（10s）在轨迹里记一笔，冻结时刻即可反推最后一次健康心跳
        Services.FreezeWatchdog.BeatLogged = n => Services.AppTrace.Log($"心跳 #{n}");

        // 全局异常处理
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_isHandlingException)
        {
            // 防止重复弹窗，直接记录
            LogException(e.Exception, "Dispatcher (nested)");
            e.Handled = true;
            return;
        }

        _isHandlingException = true;
        try
        {
            var ex = e.Exception;
            var logFile = LogException(ex, "Dispatcher");

            var message = $"未处理的异常:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                          $"详细日志已保存到:\n{logFile}\n\n" +
                          $"是否继续运行应用程序？\n(是=继续, 否=退出)";

            if (ex.InnerException != null)
            {
                message += $"\n\n内部异常:\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            }

            var result = MessageBox.Show(message, "应用程序错误", MessageBoxButton.YesNo, MessageBoxImage.Error);
            e.Handled = (result == MessageBoxResult.Yes);

            if (!e.Handled)
            {
                // 用户选择退出，生成内存转储
                GenerateMiniDump();
            }
        }
        finally
        {
            _isHandlingException = false;
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (_isHandlingException) return;
        _isHandlingException = true;

        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                var logFile = LogException(ex, "AppDomain (fatal)");
                MessageBox.Show($"致命异常，应用程序即将退出:\n\n{ex.GetType().Name}: {ex.Message}\n\n日志已保存到:\n{logFile}",
                    "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // 生成内存转储
            GenerateMiniDump();
        }
        finally
        {
            _isHandlingException = false;
        }
    }

    private static string LogException(Exception ex, string source)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var logFile = Path.Combine(CrashLogDir, $"crash_{timestamp}.log");

            using var writer = new StreamWriter(logFile, false);
            writer.WriteLine($"=== 崩溃日志 ===");
            writer.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"来源: {source}");
            writer.WriteLine($"应用程序: {AppDomain.CurrentDomain.FriendlyName}");
            writer.WriteLine();
            writer.WriteLine($"异常类型: {ex.GetType().FullName}");
            writer.WriteLine($"异常消息: {ex.Message}");
            writer.WriteLine();
            writer.WriteLine($"堆栈跟踪:");
            writer.WriteLine(ex.StackTrace);
            writer.WriteLine();

            if (ex.InnerException != null)
            {
                writer.WriteLine($"=== 内部异常 ===");
                writer.WriteLine($"类型: {ex.InnerException.GetType().FullName}");
                writer.WriteLine($"消息: {ex.InnerException.Message}");
                writer.WriteLine($"堆栈:");
                writer.WriteLine(ex.InnerException.StackTrace);
            }

            writer.WriteLine();
            writer.WriteLine($"=== 环境信息 ===");
            writer.WriteLine($"OS: {Environment.OSVersion}");
            writer.WriteLine($"64位OS: {Environment.Is64BitOperatingSystem}");
            writer.WriteLine($"64位进程: {Environment.Is64BitProcess}");
            writer.WriteLine($"CLR版本: {Environment.Version}");
            writer.WriteLine($"处理器数: {Environment.ProcessorCount}");
            writer.WriteLine($"工作集: {Environment.WorkingSet / 1024 / 1024} MB");

            return logFile;
        }
        catch
        {
            return "(日志写入失败)";
        }
    }

    #region 内存转储

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType, IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId", ExactSpelling = true)]
    private static extern uint GetCurrentProcessId();

    private static void GenerateMiniDump()
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dumpFile = Path.Combine(CrashLogDir, $"crash_{timestamp}.dmp");

            using var fs = new FileStream(dumpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // MiniDumpWithFullMemory = 0x00000002, 用 MiniDumpNormal = 0x00000000 减小文件大小
            const uint dumpType = 0x00000000; // MiniDumpNormal
            var result = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle.DangerousGetHandle(), dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (result)
            {
                File.AppendAllText(Path.Combine(CrashLogDir, $"crash_{timestamp}.log"), $"\n内存转储已生成: {dumpFile}\n");
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(CrashLogDir, $"dump_error_{DateTime.Now:yyyyMMdd_HHmmss}.log"), $"生成转储失败: {ex}\n");
            }
            catch { }
        }
    }

    #endregion
}
