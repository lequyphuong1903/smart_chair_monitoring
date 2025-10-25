using PatientMonitoring.Services;
using System.Windows;

namespace PatientMonitoring
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            PythonBackendHost.Start();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                await PythonBackendHost.StopAsync();
            }
            catch { /* ignore */ }

            base.OnExit(e);
        }
    }
}