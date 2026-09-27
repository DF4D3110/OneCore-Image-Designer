using System.Windows.Controls;
using System.Windows.Input;
using OneCore_Image_Designer.ViewModels.OEMInput;

namespace OneCore_Image_Designer.Views.OEMInput.Steps
{
    public partial class LanguagesStep : UserControl
    {
        public LanguagesStep() => InitializeComponent();

        private void AvailableLanguage_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is OEMInputDesignerViewModel vm && sender is ListBox lb && lb.SelectedItem is string lang)
            {
                vm.AddLanguageCommand.Execute(lang);
            }
        }
    }
}
