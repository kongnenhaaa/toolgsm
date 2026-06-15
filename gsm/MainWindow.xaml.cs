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

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Dispose();
            base.OnClosed(e);
        }

        private void DataGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Không xử lý nếu click vào thanh cuộn hoặc tiêu đề cột
            if (e.OriginalSource is System.Windows.Controls.Primitives.ScrollBar ||
                e.OriginalSource is System.Windows.Controls.Primitives.DataGridColumnHeader)
                return;

            DependencyObject dep = (DependencyObject)e.OriginalSource;

            // Truy ngược lên cây UI để tìm DataGridCell
            while (dep != null && !(dep is DataGridCell))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (dep is DataGridCell cell)
            {
                string? textToCopy = null;

                // Nếu là cột text thông thường
                if (cell.Column is DataGridBoundColumn boundColumn)
                {
                    var binding = boundColumn.Binding as System.Windows.Data.Binding;
                    if (binding != null)
                    {
                        var propertyPath = binding.Path.Path;
                        var rowData = cell.DataContext;
                        var property = rowData?.GetType().GetProperty(propertyPath);
                        if (property != null)
                        {
                            textToCopy = property.GetValue(rowData)?.ToString();
                        }
                    }
                }
                // Nếu là cột chứa Control (như cột SĐT, OTP dạng Button hoặc thẻ HSD đổi màu)
                else if (cell.Column is DataGridTemplateColumn)
                {
                    if (e.OriginalSource is TextBlock tb) textToCopy = tb.Text;
                    else if (e.OriginalSource is Button btn && btn.Content is string s) textToCopy = s;
                    else if (e.OriginalSource is Run run) textToCopy = run.Text;
                    
                    if (string.IsNullOrEmpty(textToCopy) && cell.Content is ContentPresenter cp)
                    {
                        if (VisualTreeHelper.GetChildrenCount(cp) > 0)
                        {
                            var child = VisualTreeHelper.GetChild(cp, 0);
                            if (child is Button b && b.Content is string bText) textToCopy = bText;
                            else if (child is TextBlock tText) textToCopy = tText.Text;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(textToCopy) && textToCopy != "☑") // Bỏ qua ô checkbox
                {
                    Clipboard.SetText(textToCopy);
                    _viewModel.SnackbarMessageQueue.Enqueue($"Đã sao chép: {textToCopy}");
                }
            }
        }
    }
}
