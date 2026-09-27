using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OneCore_Image_Designer.ViewModels
{
    /// <summary>
    /// 主窗口一个标签页（ICD 式：起始页固定 + 每个功能/项目一个可关闭 Tab）。
    /// </summary>
    public partial class AppTabViewModel : ObservableObject
    {
        /// <summary>稳定键：同一功能只允许一个页签（重复打开=激活）。</summary>
        public string Key { get; }

        public object Content { get; }

        /// <summary>固定页（起始页）不可关闭。</summary>
        public bool IsPinned { get; }

        /// <summary>关闭前回调；返回 false 表示用户取消关闭（如脏标记保护）。</summary>
        public Func<bool>? ConfirmClosing { get; set; }

        private string _title;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public AppTabViewModel(string key, string title, object content, bool isPinned = false)
        {
            Key = key;
            _title = title;
            Content = content;
            IsPinned = isPinned;
        }
    }
}
