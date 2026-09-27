using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OneCore_Image_Designer.Helpers;

namespace OneCore_Image_Designer.ViewModels.SystemReconstruct
{
    /// <summary>
    /// 系统重构主视图模型 - 二选一：完全构建 / 选择构建
    /// </summary>
    public class SystemReconstructViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _mainVM;

        public SystemReconstructViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            GoBackCommand = new RelayCommand(GoBack);
            OpenFullReconstructCommand = new RelayCommand(OpenFullReconstruct);
            OpenSelectReconstructCommand = new RelayCommand(OpenSelectReconstruct);
        }

        public RelayCommand GoBackCommand { get; }
        public RelayCommand OpenFullReconstructCommand { get; }
        public RelayCommand OpenSelectReconstructCommand { get; }

        private void GoBack()
        {
            _mainVM.NavigateToStart();
        }

        private void OpenFullReconstruct()
        {
            _mainVM.NavigateToFullReconstruct();
        }

        private void OpenSelectReconstruct()
        {
            _mainVM.NavigateToSelectReconstruct();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
