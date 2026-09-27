using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.SystemReconstruct
{
    public partial class SelectReconstructView : UserControl
    {
        private bool _isLoaded = false;

        public SelectReconstructView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;

            // 恢复保存的右列宽度
            var settings = SettingsService.Current;
            if (settings.SelectReconstructRightColumnWidth > 100)
            {
                RightColumn.Width = new GridLength(settings.SelectReconstructRightColumnWidth);
            }

            // 监听分隔条拖动完成
            ContentSplitter.DragCompleted += ContentSplitter_DragCompleted;
        }

        private void ContentSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            // 保存右列宽度
            var rightWidth = RightColumn.ActualWidth;
            if (rightWidth > 100)
            {
                SettingsService.Save(s =>
                {
                    s.SelectReconstructRightColumnWidth = rightWidth;
                });
            }
        }
    }
}
