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
using gsm.ViewModels;

namespace gsm
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly gsm.Services.WebServerService _webServer;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            
            // Khởi động Web Server ngầm
            _webServer = new gsm.Services.WebServerService(_viewModel);
            _ = _webServer.StartAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            _ = _webServer.StopAsync();
            _viewModel.ModemService.DisconnectAll();
            base.OnClosed(e);
        }
    }
}