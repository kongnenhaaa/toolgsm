using System;
using System.IO;

namespace gsm.Services
{
    public interface IFileDialogService
    {
        string? OpenFile(string filter = "JSON files (*.json)|*.json|All files (*.*)|*.*");
        string? SaveFile(string defaultName, string filter = "JSON files (*.json)|*.json");
    }

    public class FileDialogService : IFileDialogService
    {
        public string? OpenFile(string filter = "JSON files (*.json)|*.json|All files (*.*)|*.*")
        {
            string? result = null;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = filter,
                    Title = "Chọn file"
                };
                if (dlg.ShowDialog() == true)
                    result = dlg.FileName;
            });
            return result;
        }

        public string? SaveFile(string defaultName, string filter = "JSON files (*.json)|*.json")
        {
            string? result = null;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = filter,
                    FileName = defaultName,
                    Title = "Lưu file"
                };
                if (dlg.ShowDialog() == true)
                    result = dlg.FileName;
            });
            return result;
        }
    }
}
