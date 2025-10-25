using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientMonitoring.Models;

namespace PatientMonitoring.ViewModels
{
    public partial class SettingViewModel : ObservableObject
    {
        [ObservableProperty] private string host = "127.0.0.1";
        [ObservableProperty] private int port = 12345;
        [ObservableProperty] private int maxPoints = 1000;

        // Sự kiện yêu cầu đóng cửa sổ
        public event Action? RequestClose;

        partial void OnMaxPointsChanged(int value)
        {
            if (value <= 0) return;
            //_plots.ChangeCapacity(value);
        }
        [RelayCommand]
        private void Save()
        {
            Config.Host = Host;
            Config.Port = Port;
            Config.MaxPoints = MaxPoints;

            RequestClose?.Invoke();

        }
        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke();
        }
    }
}
