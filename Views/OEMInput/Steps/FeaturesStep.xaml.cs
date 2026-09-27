using System.Windows;
using System.Windows.Controls;
using OneCore_Image_Designer.Services;
using OneCore_Image_Designer.ViewModels.OEMInput;

namespace OneCore_Image_Designer.Views.OEMInput.Steps
{
    public partial class FeaturesStep : UserControl
    {
        public FeaturesStep() => InitializeComponent();

        private OEMInputDesignerViewModel? Vm => (DataContext as StepViewModelBase)?.Parent;

        /// <summary>复选框变更后：交由 VM 做冲突检测、标脏与统计刷新。</summary>
        private void FeatureFlag_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is FeatureInfo feature)
                Vm?.OnFeatureCheckedChanged(feature);
        }
    }
}
