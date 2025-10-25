using System;
using System.Windows;
using System.Windows.Input;
using PatientMonitoring.ViewModels;

namespace PatientMonitoring.Views
{
    public partial class SettingPage : Window
    {
        private SettingViewModel? _vm;

        public SettingPage()
        {
            InitializeComponent();
            Loaded += SettingPage_Loaded;
            DataContextChanged += SettingPage_DataContextChanged;
        }

        private void SettingPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UnhookOldVm(e.OldValue as SettingViewModel);
            HookNewVm(e.NewValue as SettingViewModel);
        }

        private void SettingPage_Loaded(object? sender, RoutedEventArgs e)
        {
            HookNewVm(DataContext as SettingViewModel);
        }

        private void HookNewVm(SettingViewModel? vm)
        {
            if (vm == null) return;
            _vm = vm;
            _vm.RequestClose += Vm_RequestClose;
        }

        private void UnhookOldVm(SettingViewModel? vm)
        {
            if (vm == null) return;
            vm.RequestClose -= Vm_RequestClose;
        }

        private void Vm_RequestClose()
        {
            Close();
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm != null)
                _vm.RequestClose -= Vm_RequestClose;
            base.OnClosed(e);
        }
    }
}