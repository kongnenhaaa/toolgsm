using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using gsm.Models;
using gsm.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using MaterialDesignThemes.Wpf;
using System;
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
        LoadMockData();
        
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

    private void LoadMockData()
    {
        Ports.Add(new SimPort { PortName = "COM216", PhoneNumber = "0828099482", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:45:40 01/06/2026", Otp = "491633", LastMessageContent = "ONEBSS OTP: 491633", Imei = "354123456789012", Balance = "50000", ExpiryDate = "10/10/2026", CallCount = 1, SignalStrength = 90 });
        Ports.Add(new SimPort { PortName = "COM222", PhoneNumber = "0833680008", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:30:17 01/06/2026", Otp = "", LastMessageContent = "(TB) UU DAI +200MB data/24h", Imei = "354123456789033", Balance = "0", ExpiryDate = "02/06/2026", SignalStrength = 30 });
        Ports.Add(new SimPort { PortName = "COM227", PhoneNumber = "0822222708", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:30:16 01/06/2026", Otp = "", LastMessageContent = "(TB) UU DAI +200MB data/24h", Balance = "15000", SignalStrength = 60 });
        
        SmsMessages.Add(new SmsMessage { PortName = "COM216", Sender = "VNPT", ReceiverPhone = "0828099482", ReceivedTime = "14:45:40 01/06/2026", Otp = "491633", Content = "ONEBSS OTP: 491633" });
        SmsMessages.Add(new SmsMessage { PortName = "COM222", Sender = "888", ReceiverPhone = "0833680008", ReceivedTime = "14:30:17 01/06/2026", Otp = "", Content = "(TB) UU DAI +200MB data/24h khi thue bao 84833680008 soan..." });
        SmsMessages.Add(new SmsMessage { PortName = "COM228", Sender = "VNPT", ReceiverPhone = "0836522379", ReceivedTime = "13:54:59 01/06/2026", Otp = "993963", Content = "ONEBSS OTP: 993963" });
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
