using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneCore_Image_Designer.ViewModels.OEMInput;

namespace OneCore_Image_Designer.Views.OEMInput.Steps
{
    public partial class MSPackageRootStep : UserControl
    {
        public MSPackageRootStep() => InitializeComponent();

        private OEMInputDesignerViewModel? Vm => (DataContext as StepViewModelBase)?.Parent;

        /// <summary>路径框回车 → 立即扫描。</summary>
        private void RootBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Vm?.RescanPackageCommand.Execute(null);
        }

        /// <summary>勾选变化 → 延迟到后台优先级重新应用（解析+DeviceLayout+冲突弹窗）；
        /// 延迟可让冲突弹窗内程序化取消勾引起的连锁 Unchecked 合并成一次应用，避免重入。</summary>
        private void FmFlag_Changed(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => Vm?.ApplyDiscoveredFMs()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
