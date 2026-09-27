using System.Windows.Controls;
using System.Windows.Input;
using OneCore_Image_Designer.ViewModels.DeviceLayout;

namespace OneCore_Image_Designer.Views.DeviceLayout
{
    public partial class DeviceLayoutPreviewView : UserControl
    {
        public DeviceLayoutPreviewView()
        {
            InitializeComponent();
            DataContextChanged += DeviceLayoutPreviewView_DataContextChanged;
        }

        private void DeviceLayoutPreviewView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is DeviceLayoutPreviewViewModel vm)
            {
                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(vm.HasData))
                    {
                        NoDataPanel.Visibility = vm.HasData ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                        DataPanel.Visibility = vm.HasData ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    }
                };
            }
        }

        private void FoundCab_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is DeviceLayoutPreviewViewModel vm && sender is ListBox lb && lb.SelectedItem is string path)
            {
                vm.LoadSelectedCabCommand.Execute(path);
            }
        }
    }
}
