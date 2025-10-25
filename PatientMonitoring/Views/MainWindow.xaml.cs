using PatientMonitoring.Services;
using PatientMonitoring.ViewModels;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PatientMonitoring
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            var viewModel = new MainViewModel();
            DataContext = viewModel;
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            // Gọi QuitCommand để gom logic (nếu muốn hủy đóng thì chỉnh e.Cancel trước khi gọi)
            if (DataContext is MainViewModel vm)
            {
                vm.QuitCommand.Execute(null);
            }
            base.OnClosing(e);
        }
    }
}