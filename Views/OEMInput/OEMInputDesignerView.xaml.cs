using System.Windows.Controls;
using System.Windows.Input;
using OneCore_Image_Designer.ViewModels.OEMInput;

namespace OneCore_Image_Designer.Views.OEMInput
{
    public partial class OEMInputDesignerView : UserControl
    {
        public OEMInputDesignerView()
        {
            InitializeComponent();
            DataContextChanged += OEMInputDesignerView_DataContextChanged;
        }

        private void OEMInputDesignerView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is OEMInputDesignerViewModel vm)
            {
                // 初始化步骤列表
                var steps = new System.Collections.Generic.List<StepItem>();
                for (int i = 0; i < vm.StepNames.Length; i++)
                {
                    steps.Add(new StepItem
                    {
                        Index = i + 1,
                        Name = vm.StepNames[i],
                        IsCurrent = i == vm.CurrentStepIndex,
                        IsCompleted = i < vm.CurrentStepIndex
                    });
                }
                StepsItemsControl.ItemsSource = steps;

                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(vm.CurrentStepIndex))
                    {
                        foreach (StepItem item in StepsItemsControl.Items)
                        {
                            item.IsCurrent = item.Index - 1 == vm.CurrentStepIndex;
                            item.IsCompleted = item.Index - 1 < vm.CurrentStepIndex;
                        }
                    }
                };
            }
        }

        private void StepBorder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is int index && DataContext is OEMInputDesignerViewModel vm)
            {
                vm.GoToStepCommand.Execute(index - 1);
            }
        }
    }

    public class StepItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isCurrent;
        private bool _isCompleted;

        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;

        public bool IsCurrent
        {
            get => _isCurrent;
            set { _isCurrent = value; OnPropertyChanged(nameof(IsCurrent)); }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set { _isCompleted = value; OnPropertyChanged(nameof(IsCompleted)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
