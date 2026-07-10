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
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Bắt lỗi trên luồng UI
            this.DispatcherUnhandledException += (s, args) =>
            {
                LogCrash(args.Exception, "UI_Thread");
                args.Handled = true; // Ngăn app văng
            };

            // Bắt lỗi trên luồng nền (Task)
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash(args.Exception, "Task_Background");
                args.SetObserved();
            };

            // Bắt tất cả các lỗi không được xử lý khác
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                LogCrash(ex, "AppDomain_Unhandled");
            };
        }

        private void LogCrash(Exception? ex, string source)
        {
            // Tắt tạo crash.log theo yêu cầu
        }

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
