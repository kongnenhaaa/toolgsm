using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using gsm.Models;
using Microsoft.Win32;

namespace gsm.ViewModels
{
    public partial class ImeiManagerViewModel : ObservableObject
    {
        private readonly string _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "imei_database.json");

        [ObservableProperty]
        private ObservableCollection<ImeiRecord> _allRecords = new();

        [ObservableProperty]
        private ObservableCollection<ImeiRecord> _pagedRecords = new();

        [ObservableProperty]
        private string _searchText = "";

        [ObservableProperty]
        private int _currentPage = 1;

        [ObservableProperty]
        private int _totalPages = 1;

        [ObservableProperty]
        private int _pageSize = 5000;

        public List<int> PageSizeOptions { get; } = new List<int> { 1000, 2000, 5000, 10000 };

        public ImeiManagerViewModel()
        {
            LoadDatabase();
            UpdatePagedData();
        }

        partial void OnSearchTextChanged(string value) => UpdatePagedData();
        partial void OnPageSizeChanged(int value)
        {
            CurrentPage = 1;
            UpdatePagedData();
        }

        private void LoadDatabase()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    string json = File.ReadAllText(_dbPath);
                    var list = JsonSerializer.Deserialize<List<ImeiRecord>>(json);
                    if (list != null)
                    {
                        AllRecords = new ObservableCollection<ImeiRecord>(list);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi tải DB IMEI: {ex.Message}");
            }
        }

        private void SaveDatabase()
        {
            try
            {
                string json = JsonSerializer.Serialize(AllRecords.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dbPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi lưu DB IMEI: {ex.Message}");
            }
        }

        private void UpdatePagedData()
        {
            var filtered = string.IsNullOrWhiteSpace(SearchText) 
                ? AllRecords.ToList() 
                : AllRecords.Where(r => r.Iccid.Contains(SearchText) || r.Imei.Contains(SearchText) || r.PhoneNumber.Contains(SearchText)).ToList();

            TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
            if (CurrentPage > TotalPages) CurrentPage = TotalPages;
            if (CurrentPage < 1) CurrentPage = 1;

            var paged = filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
            
            // Cập nhật lại số thứ tự (Id) cho giao diện đẹp
            for (int i = 0; i < paged.Count; i++)
            {
                paged[i].Id = (CurrentPage - 1) * PageSize + i + 1;
            }

            PagedRecords = new ObservableCollection<ImeiRecord>(paged);
        }

        [RelayCommand]
        private void NextPage()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                UpdatePagedData();
            }
        }

        [RelayCommand]
        private void PreviousPage()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                UpdatePagedData();
            }
        }

        [RelayCommand]
        private void ExportTxt()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = "Imei_Export.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = AllRecords.Select(r => $"{r.Iccid}|{r.Imei}");
                    File.WriteAllLines(dialog.FileName, lines);
                    MessageBox.Show("Xuất file thành công!");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi xuất file: {ex.Message}");
                }
            }
        }

        [RelayCommand]
        private void CopyQuick()
        {
            try
            {
                var lines = PagedRecords.Select(r => $"{r.Iccid}|{r.Imei}");
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                MessageBox.Show("Đã copy danh sách ICCID|IMEI (trang hiện tại) vào Clipboard!");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi copy: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ImportDataAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(dialog.FileName);
                    int addedCount = 0;

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        
                        // Hỗ trợ ICCID|IMEI hoặc ICCID IMEI
                        string[] parts = line.Split(new[] { '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        
                        if (parts.Length >= 2)
                        {
                            string iccid = parts[0].Trim();
                            string imei = parts[1].Trim();

                            // Kiểm tra trùng lặp
                            var existing = AllRecords.FirstOrDefault(r => r.Iccid == iccid);
                            if (existing == null)
                            {
                                AllRecords.Add(new ImeiRecord { Iccid = iccid, Imei = imei });
                                addedCount++;
                            }
                            else
                            {
                                existing.Imei = imei; // Cập nhật nếu đã có
                            }
                        }
                    }

                    SaveDatabase();
                    UpdatePagedData();
                    MessageBox.Show($"Đã nhập thành công {addedCount} bản ghi mới (Cập nhật các bản ghi trùng).", "Nhập dữ liệu");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi nhập file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private async Task CheckImeiAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Chọn file txt chứa danh sách ICCID hoặc Số Điện Thoại"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(dialog.FileName);
                    var results = new List<string>();

                    foreach (var line in lines)
                    {
                        string query = line.Trim();
                        if (string.IsNullOrEmpty(query)) continue;

                        var match = AllRecords.FirstOrDefault(r => r.Iccid == query || r.PhoneNumber == query);
                        if (match != null)
                        {
                            results.Add($"{query}|{match.Imei}");
                        }
                        else
                        {
                            results.Add($"{query}|Không tìm thấy");
                        }
                    }

                    // Lưu file kết quả
                    string resultFile = dialog.FileName.Replace(".txt", "_checked.txt");
                    await File.WriteAllLinesAsync(resultFile, results);
                    
                    MessageBox.Show($"Kiểm tra hoàn tất!\nĐã lưu kết quả tại:\n{resultFile}", "Thành công");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi kiểm tra: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
