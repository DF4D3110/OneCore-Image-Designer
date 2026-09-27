using System.Windows.Controls;
using System.Windows.Input;

namespace OneCore_Image_Designer.Views.SystemReconstruct
{
    public partial class SystemReconstructView : UserControl
    {
        public SystemReconstructView()
        {
            InitializeComponent();
        }

        private void FullReconstructCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ViewModels.SystemReconstruct.SystemReconstructViewModel vm)
            {
                vm.OpenFullReconstructCommand.Execute(null);
            }
        }

        private void SelectReconstructCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ViewModels.SystemReconstruct.SystemReconstructViewModel vm)
            {
                vm.OpenSelectReconstructCommand.Execute(null);
            }
        }
    }
}
