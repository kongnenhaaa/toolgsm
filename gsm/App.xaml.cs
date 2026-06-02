using System.Configuration;
using System.Data;
using System.Windows;
using gsm.ViewModels;

namespace gsm
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            if (Current?.MainWindow?.DataContext is MainViewModel vm)
            {
                vm.ModemService.DisconnectAll();
            }

            base.OnExit(e);
        }
    }

}
