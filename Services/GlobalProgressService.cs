using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 全局进度服务：管理长时间操作的进度显示和取消
    /// 单例模式，所有ViewModel共享
    /// </summary>
    public class GlobalProgressService : INotifyPropertyChanged
    {
        private static readonly Lazy<GlobalProgressService> _instance = new(() => new GlobalProgressService());
        public static GlobalProgressService Instance => _instance.Value;

        private bool _isRunning;
        private double _progress;
        private string _statusText = string.Empty;
        private string _detailText = string.Empty;
        private bool _isIndeterminate;
        private bool _cancelRequested;
        private bool _canCancel = true;

        /// <summary>
        /// 是否有操作正在运行
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            private set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotRunning)); }
        }

        public bool IsNotRunning => !_isRunning;

        /// <summary>
        /// 当前进度（0-100）
        /// </summary>
        public double Progress
        {
            get => _progress;
            private set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }

        /// <summary>
        /// 进度文本显示
        /// </summary>
        public string ProgressText => _isIndeterminate ? "处理中..." : $"{_progress:F0}%";

        /// <summary>
        /// 当前操作描述
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 详细信息（当前子操作）
        /// </summary>
        public string DetailText
        {
            get => _detailText;
            private set { _detailText = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 是否为不确定进度（无法计算百分比）
        /// </summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set { _isIndeterminate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }

        /// <summary>
        /// 是否请求取消
        /// </summary>
        public bool CancelRequested
        {
            get => _cancelRequested;
            private set { _cancelRequested = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 是否允许取消
        /// </summary>
        public bool CanCancel
        {
            get => _canCancel;
            private set { _canCancel = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 取消命令
        /// </summary>
        public ICommand CancelCommand { get; }

        private GlobalProgressService()
        {
            CancelCommand = new RelayCommand(
                () => { if (_canCancel) CancelRequested = true; },
                () => _isRunning && _canCancel
            );
        }

        /// <summary>
        /// 开始一个操作
        /// </summary>
        /// <param name="statusText">操作描述</param>
        /// <param name="canCancel">是否允许取消</param>
        /// <param name="isIndeterminate">是否为不确定进度</param>
        public void StartOperation(string statusText, bool canCancel = true, bool isIndeterminate = false)
        {
            IsRunning = true;
            Progress = 0;
            StatusText = statusText;
            DetailText = string.Empty;
            IsIndeterminate = isIndeterminate;
            CancelRequested = false;
            CanCancel = canCancel;
        }

        /// <summary>
        /// 更新进度
        /// </summary>
        /// <param name="progress">进度（0-100）</param>
        /// <param name="detailText">详细信息（可选）</param>
        public void UpdateProgress(double progress, string? detailText = null)
        {
            Progress = Math.Clamp(progress, 0, 100);
            if (detailText != null) DetailText = detailText;
        }

        /// <summary>
        /// 更新详细信息（不改变进度）
        /// </summary>
        public void UpdateDetail(string detailText)
        {
            DetailText = detailText;
        }

        /// <summary>
        /// 完成操作
        /// </summary>
        public void CompleteOperation()
        {
            IsRunning = false;
            Progress = 100;
            CancelRequested = false;
        }

        /// <summary>
        /// 重置状态（出错时调用）
        /// </summary>
        public void Reset()
        {
            IsRunning = false;
            Progress = 0;
            StatusText = string.Empty;
            DetailText = string.Empty;
            IsIndeterminate = false;
            CancelRequested = false;
        }

        /// <summary>
        /// 检查是否已请求取消，如果已取消则抛出OperationCanceledException
        /// </summary>
        public void ThrowIfCancellationRequested()
        {
            if (_cancelRequested)
                throw new OperationCanceledException("操作已被用户取消");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
