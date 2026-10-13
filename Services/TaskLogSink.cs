using System;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 通用后台任务日志 sink：长任务（组件提取、UUP 下载等）的工作线程把行写进这里，
    /// UI 侧用 TaskLogWindow 以固定节拍批量拉取刷新——避免每行 Dispatcher.Invoke
    /// 造成主程序卡顿。同时落盘（可选），窗口关闭后可回看文件。
    /// </summary>
    public sealed class TaskLogSink : IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly object _fileLock = new();
        private DispatcherTimer? _uiTimer;
        private Action<string[]>? _uiFlush;
        private volatile bool _done;

        public string Title { get; }
        public string LogFilePath { get; }

        /// <summary>任务是否已报告结束（窗口据此启用「关闭」并提示）。</summary>
        public bool Finished => _done;

        public TaskLogSink(string title, bool persistToFile = true)
        {
            Title = title;
            if (persistToFile)
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OneCoreImageDesigner_Logs");
                System.IO.Directory.CreateDirectory(dir);
                LogFilePath = System.IO.Path.Combine(dir,
                    title.Replace(' ', '_') + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            }
            else LogFilePath = string.Empty;
        }

        /// <summary>工作线程调用：入队一行（自动带时间戳），并即时落盘。</summary>
        public void Write(string line)
        {
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _queue.Enqueue(stamped);
            if (LogFilePath.Length > 0)
            {
                try { lock (_fileLock) System.IO.File.AppendAllText(LogFilePath, stamped + Environment.NewLine); }
                catch { /* 日志落盘失败不影响任务 */ }
            }
        }

        /// <summary>任务完成标记（窗口标题栏提示用）。</summary>
        public void MarkFinished() => _done = true;

        /// <summary>
        /// 在 UI 线程绑定一个窗口刷新器：定时器每 400ms 批量出队刷新一次。
        /// </summary>
        public void AttachUi(Action<string[]> flushBatch, Dispatcher dispatcher)
        {
            _uiFlush = flushBatch;
            _uiTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _uiTimer.Tick += (_, _) => Pump();
            _uiTimer.Start();
        }

        private void Pump()
        {
            if (_uiFlush == null) return;
            var batch = new System.Collections.Generic.List<string>(256);
            while (batch.Count < 2000 && _queue.TryDequeue(out var line)) batch.Add(line);
            if (batch.Count > 0) _uiFlush(batch.ToArray());
        }

        public void DetachUi()
        {
            _uiTimer?.Stop();
            _uiTimer = null;
            _uiFlush = null;
            // 残余行冲刷到文件（已即时落盘，这里仅兜底）
        }

        public void Dispose()
        {
            DetachUi();
        }
    }
}
