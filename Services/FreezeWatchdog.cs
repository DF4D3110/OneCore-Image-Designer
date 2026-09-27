using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace OneCore_Image_Designer.Services
{
    /// <summary>轻量动作轨迹：追加写 crash_logs\trace_last.log（启动时清空）。冻结排查靠它定位卡在哪一步。</summary>
    public static class AppTrace
    {
        static readonly object Gate = new();
        static readonly string LogPath = Path.Combine(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_logs"), "trace_last.log");
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        static bool _headWritten;

        public static void Log(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    if (!_headWritten)
                    {
                        File.WriteAllText(LogPath, $"=== 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n", Encoding.UTF8);
                        _headWritten = true;
                    }
                    File.AppendAllText(LogPath, $"[{Sw.ElapsedMilliseconds,8}ms] {msg}\n", Encoding.UTF8);
                }
            }
            catch { /* 轨迹永远不能把程序带崩 */ }
        }

        /// <summary>读最后 N 行（看门狗写冻结报告时嵌入现场）。</summary>
        public static string Tail(int lines = 40)
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(LogPath)) return "(无轨迹)";
                    string[] all = File.ReadAllLines(LogPath);
                    int n = Math.Min(lines, all.Length);
                    var sb = new StringBuilder();
                    for (int i = all.Length - n; i < all.Length; i++) sb.AppendLine(all[i]);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return "(读轨迹失败: " + ex.Message + ")"; }
        }
    }

    /// <summary>
    /// UI 冻结看门狗：每 2s 向 Dispatcher 投一个 Loaded 优先级心跳（UI 空闲即被处理，
    /// 排在渲染/输入之后）；心跳 10s 没回来 = UI 线程在死循环或死锁（这种情况不会产生
    /// Dispatcher 异常日志）。触发即写 freeze_*.log：全部线程状态 + 最近动作轨迹 + minidump。
    /// 恢复后自动重新布防，可连续抓多次冻结。
    /// </summary>
    public static class FreezeWatchdog
    {
        static Dispatcher _ui = null!;
        static long _lastBeatMs;
        static int _uiManagedThreadId = -1;
        static Timer? _timer;
        static int _reported;   // 0=武装 1=本轮已报
        static long _beat;      // 心跳处理计数

        /// <summary>可选：每处理一次心跳回调一次（App 用于往轨迹里打点）。</summary>
        public static Action<long>? BeatLogged;

        public static void Start(Application app)
        {
            _ui = app.Dispatcher;
            try { if (_ui.Thread != null) _uiManagedThreadId = _ui.Thread.ManagedThreadId; } catch { }
            _lastBeatMs = Environment.TickCount64;
            _timer = new Timer(Check, null, 2000, 2000);
            AppTrace.Log("看门狗已启动（UI 心跳 10s 无响应 → freeze 日志 + dump）");
        }

        static void Check(object? _)
        {
            try
            {
                _ui.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    long b = Interlocked.Increment(ref _beat);
                    Interlocked.Exchange(ref _lastBeatMs, Environment.TickCount64);
                    if (b % 5 == 0) BeatLogged?.Invoke(b);
                }));
            }
            catch { return; }   // 正在关闭

            long idle = Environment.TickCount64 - Interlocked.Read(ref _lastBeatMs);
            if (idle > 10_000 && Interlocked.CompareExchange(ref _reported, 1, 0) == 0)
                Capture(idle);
            else if (idle < 3_000)
                Volatile.Write(ref _reported, 0);   // 恢复 → 重新武装
        }

        static void Capture(long idleMs)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_logs");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string file = Path.Combine(dir, $"freeze_{stamp}.log");

                var sb = new StringBuilder();
                sb.AppendLine("=== UI 冻结报告 ===");
                sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"UI 心跳已停: {idleMs / 1000.0:F1}s（Loaded 心跳排队 10s 未执行 → UI 线程在死循环或死锁，非异常路径）");
                sb.AppendLine($"UI 线程 ManagedThreadId: {_uiManagedThreadId}");
                sb.AppendLine();
                sb.AppendLine("=== 最近动作轨迹（trace_last.log 尾部）===");
                sb.Append(AppTrace.Tail(60));
                sb.AppendLine();
                sb.AppendLine("=== 进程线程快照 ===");
                var p = Process.GetCurrentProcess();
                foreach (ProcessThread t in p.Threads)
                    sb.AppendLine($"  OS线程 {t.Id,8}  {t.ThreadState,-14} prio={t.PriorityLevel,-12} 启动 {t.StartTime:HH:mm:ss.fff}");
                sb.AppendLine();
                sb.AppendLine("=== 环境 ===");
                sb.AppendLine($"工作集 {Environment.WorkingSet / 1048576} MB  GC堆 {GC.GetTotalMemory(false) / 1048576} MB  线程数 {p.Threads.Count}");
                sb.AppendLine($"主窗口标题: {TryMainWindowTitle()}");
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                AppTrace.Log($"❄ 冻结报告已写: freeze_{stamp}.log");

                string dump = WriteMiniDump(dir, stamp);
                try { File.AppendAllText(file, $"\n内存转储: {dump}\n", Encoding.UTF8); } catch { }
            }
            catch (Exception ex)
            {
                AppTrace.Log("冻结报告失败: " + ex.Message);
            }
        }

        static string TryMainWindowTitle()
        {
            try
            {
                string title = "(取不到)";
                _ui.Invoke(() => { title = Application.Current?.MainWindow?.Title ?? "(无主窗口)"; });
                return title;
            }
            catch { return "(取不到)"; }
        }

        [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType, IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId", ExactSpelling = true)]
        static extern uint GetCurrentProcessId();

        static string WriteMiniDump(string dir, string stamp)
        {
            string dump = Path.Combine(dir, $"freeze_{stamp}.dmp");
            try
            {
                using var fs = new FileStream(dump, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                // 冻结时线程栈都在内存里：用含间接引用内存的 dump（比 MiniDumpNormal 多栈信息，比全内存小）
                const uint MiniDumpWithIndirectlyReferencedMemory = 0x40;
                bool ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(),
                    fs.SafeFileHandle.DangerousGetHandle(), MiniDumpWithIndirectlyReferencedMemory,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return ok ? dump : "(dump 失败)";
            }
            catch (Exception ex) { return "(dump 异常: " + ex.Message + ")"; }
        }
    }
}
