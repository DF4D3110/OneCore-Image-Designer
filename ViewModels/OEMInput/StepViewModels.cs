using CommunityToolkit.Mvvm.ComponentModel;

namespace OneCore_Image_Designer.ViewModels.OEMInput
{
    /// <summary>
    /// 步骤ViewModel基类
    /// </summary>
    public abstract class StepViewModelBase : ObservableObject
    {
        public OEMInputDesignerViewModel Parent { get; }

        protected StepViewModelBase(OEMInputDesignerViewModel parent)
        {
            Parent = parent;
        }
    }

    /// <summary>
    /// 步骤1: 自动识别（包根 + FM 确认 + DeviceLayout，原三步合一）
    /// </summary>
    public class DetectStepViewModel : StepViewModelBase
    {
        public DetectStepViewModel(OEMInputDesignerViewModel parent) : base(parent) { }
    }

    /// <summary>
    /// 步骤2: BasicInfo
    /// </summary>
    public class BasicInfoStepViewModel : StepViewModelBase
    {
        public BasicInfoStepViewModel(OEMInputDesignerViewModel parent) : base(parent) { }
    }

    /// <summary>
    /// 步骤5: Languages
    /// </summary>
    public class LanguagesStepViewModel : StepViewModelBase
    {
        public LanguagesStepViewModel(OEMInputDesignerViewModel parent) : base(parent) { }
    }

    /// <summary>
    /// 步骤6: Features
    /// </summary>
    public class FeaturesStepViewModel : StepViewModelBase
    {
        public FeaturesStepViewModel(OEMInputDesignerViewModel parent) : base(parent) { }
    }

    /// <summary>
    /// 步骤7: Preview
    /// </summary>
    public class PreviewStepViewModel : StepViewModelBase
    {
        public PreviewStepViewModel(OEMInputDesignerViewModel parent) : base(parent) { }
    }
}
