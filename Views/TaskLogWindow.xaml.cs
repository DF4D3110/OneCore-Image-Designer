using System;
using System.Diagnostics;
using System.Text;
using System.Windows;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views
{
    /// <summary>
    /// 通用任务日志窗口：挂接 <see cref="TaskLogSink"/>，400ms 批量刷新，
    /// 主线程零逐行 Invoke；支持复制/清空/打开落盘文件。任务结束后窗口保留供回看。
    /// </summary>
    public partial class TaskLogWindow : Window
    {
        private readonly TaskLogSink _sink;
        private readonly StringBuilder _all = new();

        public TaskLogWindow(TaskLogSink sink)
        {
            InitializeComponent();
            _sink = sink;
            Title = "任务日志 — " + sink.Title;
            TxtFile.Text = sink.LogFilePath;
            _sink.AttachUi(Flush, Dispatcher);
            Closed += (_, _) => _sink.DetachUi();
        }

        private void Flush(string[] batch)
        {
            bool scroll = ChkAutoScroll.IsChecked == true;
            bool atEnd = TxtLog.VerticalOffset >= TxtLog.ExtentHeight - TxtLog.ViewportHeight - 24;
            foreach (var line in batch)
            {
                _all.AppendLine(line);
                TxtLog.AppendText(line + "\n");
            }
            if (scroll && atEnd) TxtLog.ScrollToEnd();
            if (_sink.Finished && TxtState.Text != "已完成")
            {
                TxtState.Text = "已完成";
                TxtState.Foreground = (System.Windows.Media.Brush)FindResource("ICDBlue");
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_all.ToString()); } catch { }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (System.IO.File.Exists(_sink.LogFilePath))
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _sink.LogFilePath + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
