using System.Text;
using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace gsm
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        internal static TimeSpan WebViewShutdownTimeout { get; } =
            TimeSpan.FromSeconds(3);

        private bool _closeAfterWebViewDisposed;
        private bool _webViewDisposing;

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_closeAfterWebViewDisposed)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_webViewDisposing) return;

            _webViewDisposing = true;
            Hide();
            (Application.Current as App)?.BeginShutdown();
            try
            {
                // A stuck WebView renderer must never keep the hidden ToolGSM
                // process and its GSM cycles alive forever.
                await BlazorHost.DisposeAsync()
                    .AsTask()
                    .WaitAsync(WebViewShutdownTimeout);
            }
            catch (Exception ex) when (ex is TimeoutException
                                           or ObjectDisposedException
                                           or InvalidOperationException
                                           or InvalidCastException
                                           or COMException)
            {
                // App.OnExit owns the remaining bounded cleanup.
            }
            finally
            {
                _closeAfterWebViewDisposed = true;
                _webViewDisposing = false;
                Close();
            }
        }
    }
}
