using System.Text;
using System.Windows;
using System.ComponentModel;

namespace gsm
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
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
            try
            {
                // Wait for WebView2 to release child processes and file
                // mappings before WPF tears down the final window.
                await BlazorHost.DisposeAsync();
            }
            catch
            {
                // Shutdown must continue if Windows already tore WebView2 down.
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
