using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using gsm.Models;
using gsm.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using MaterialDesignThemes.Wpf;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace gsm.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGsmModemService _modemService;

    [ObservableProperty]
    private ObservableCollection<SimPort> _ports = new();

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _smsMessages = new();

    [ObservableProperty]
    private SimPort? _selectedPort;

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0 for GSM, 1 for SMS, 2 for Dashboard

    [ObservableProperty]
    private ISnackbarMessageQueue _snackbarMessageQueue = new SnackbarMessageQueue(TimeSpan.FromSeconds(3));

    [ObservableProperty]
    private ObservableCollection<LogMessage> _systemLogs = new();

    public ISeries[] ConnectionSeries { get; set; }
    public ISeries[] SmsSeries { get; set; }

    public MainViewModel()
    {
        _modemService = new GsmModemService();
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        
        InitializeHardware();
        
        ConnectionSeries = new ISeries[]
        {
            new PieSeries<int> { Values = new[] { 80 }, Name = "Đang hoạt động" },
            new PieSeries<int> { Values = new[] { 20 }, Name = "Mất kết nối" }
        };

        SmsSeries = new ISeries[]
        {
            new ColumnSeries<int> { Values = new[] { 150, 300, 250, 400, 350, 600, 850 }, Name = "Tin nhắn nhận được" }
        };

        AddLog("Hệ thống khởi động thành công.");
    }

    private void AddLog(string message, string level = "INFO")
    {
        SystemLogs.Insert(0, new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Message = message });
    }

    private void InitializeHardware()
    {
        Ports.Clear();
        SmsMessages.Clear();
        _modemService.ConnectAll(115200);
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        SnackbarMessageQueue.Enqueue("Đang quét lại danh sách Cổng COM...");
        AddLog("Bắt đầu quét lại danh sách Cổng COM...");
        
        // Cập nhật lại UI
        Application.Current.Dispatcher.Invoke(() =>
        {
            Ports.Clear();
            var availablePorts = _modemService.GetAvailablePorts();
            foreach(var p in availablePorts)
            {
                Ports.Add(new SimPort { PortName = p, Status = "Đã tìm thấy", SignalStrength = 0 });
            }
        });
        
        // Kết nối
        _modemService.ConnectAll(115200);
    }

    private void ModemService_LogMessage(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AddLog($"[{e.PortName}] {e.Data}");
            
            // Xử lý cập nhật giao diện dựa trên Log
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port == null) return;

            if (e.Data.Contains("+CSQ:"))
            {
                var match = Regex.Match(e.Data, @"\+CSQ:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int csq))
                {
                    port.SignalStrength = (int)((csq / 31.0) * 100);
                }
            }
            else if (e.Data.Contains("+CUSD:"))
            {
                var match = Regex.Match(e.Data, @"\+CUSD:.*?""(.*?)""");
                if (match.Success)
                {
                    string ussdContent = match.Groups[1].Value;
                    
                    // Thử tìm số tiền
                    var moneyMatch = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ)", RegexOptions.IgnoreCase);
                    if (moneyMatch.Success)
                    {
                        port.Balance = moneyMatch.Value;
                    }
                    else
                    {
                        port.Balance = "Thành công";
                    }
                    
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] USSD: {ussdContent}");
                }
            }
        });
    }

    private void ModemService_SmsReceived(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SmsMessages.Insert(0, new SmsMessage
            {
                PortName = e.PortName,
                ReceivedTime = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                Content = e.Data,
                Sender = "UNKNOWN",
                Otp = ""
            });
            SnackbarMessageQueue.Enqueue($"[{e.PortName}] Có tin nhắn mới!");
        });
    }

    [RelayCommand]
    private void SwitchTab(string tabIndex)
    {
        if (int.TryParse(tabIndex, out int index))
        {
            SelectedTabIndex = index;
        }
    }

    [RelayCommand]
    private async Task CheckBalanceAsync()
    {
        if (SelectedPort != null)
        {
            AddLog($"Đang gửi lệnh kiểm tra số dư tới {SelectedPort.PortName}...");
            string result = await _modemService.SendCommandAsync(SelectedPort.PortName, "AT+CUSD=1,\"*101#\",15");
            AddLog($"Kết quả từ {SelectedPort.PortName}: {result}", "SUCCESS");
            SnackbarMessageQueue.Enqueue($"Đã kiểm tra số dư cổng {SelectedPort.PortName}");
        }
        else
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn một cổng để kiểm tra số dư.");
        }
    }

    [RelayCommand]
    private void RenewSim()
    {
        SnackbarMessageQueue.Enqueue("Đang xử lý gia hạn SIM...");
        AddLog("Gửi yêu cầu gia hạn SIM.");
    }

    [RelayCommand]
    private void ChangeImei()
    {
        SnackbarMessageQueue.Enqueue("Đang thực hiện đổi IMEI...");
        AddLog("Bắt đầu đổi IMEI thiết bị.");
    }
}
