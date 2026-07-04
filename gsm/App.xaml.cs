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
            if (ex == null) return;
            try
            {
                string logFile = "crash.log";
                string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\r\n{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n";
                if (ex.InnerException != null)
                {
                    content += $"Inner Exception:\r\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\r\n{ex.InnerException.StackTrace}\r\n\r\n";
                }
                System.IO.File.AppendAllText(logFile, content);
            }
            catch { }
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
