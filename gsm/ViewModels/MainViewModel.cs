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
using System.Text;
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
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        
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
                    
                    // Giải mã UCS2 (Hex sang string UTF-8) để đọc được tiếng Việt
                    ussdContent = DecodeUcs2(ussdContent);
                    
                    var moneyMatch = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ)", RegexOptions.IgnoreCase);
                    if (moneyMatch.Success) port.Balance = moneyMatch.Value;
                    else port.Balance = "Thành công";
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] USSD: {ussdContent}");
                }
            }
            else if (e.Data.Contains("+COPS:"))
            {
                // Parse Network Provider from AT+COPS?
                // Example: +COPS: 0,0,"VIETTEL"
                var match = Regex.Match(e.Data, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
                if (match.Success)
                {
                    port.NetworkProvider = match.Groups[1].Value;
                }
            }
        });
    }

    private void ModemService_PortDisconnected(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port != null)
            {
                port.Status = "Mất kết nối";
                port.SignalStrength = 0;
            }
            AddLog($"[{e.PortName}] {e.Data}", "ERROR");
            SnackbarMessageQueue.Enqueue($"Cổng {e.PortName} bị ngắt kết nối!");
        });
    }

    private void ModemService_SmsReceived(object? sender, GsmDataEventArgs e)
    {
        // Raw Data trả về thường có dạng:
        // +CMGR: "REC UNREAD","+84999999999",,"26/05/01,10:00:00+28"
        // Ma xac nhan Zalo cua ban la 123456

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            string senderPhone = "UNKNOWN";
            string extractedOtp = "N/A";
            string cleanContent = e.Data;

            // 1. Tìm người gửi (Sender)
            var senderMatch = Regex.Match(e.Data, @"\+CMGR:\s*""[^""]+"",""([^""]+)""");
            if (senderMatch.Success)
            {
                senderPhone = senderMatch.Groups[1].Value;
                // Xóa dòng header +CMGR đi để lấy nội dung text sạch
                cleanContent = Regex.Replace(e.Data, @"\+CMGR:.*?\r\n", "").Trim();
                cleanContent = cleanContent.Replace("OK", "").Trim();
            }

            // 2. Tìm OTP (Lọc ra chuỗi từ 4-6 số liên tiếp)
            var otpMatch = Regex.Match(cleanContent, @"\b\d{4,6}\b");
            if (otpMatch.Success)
            {
                extractedOtp = otpMatch.Value;
                
                // GỌI HÀM BẮN TELEGRAM
                _ = TelegramService.SendMessageAsync($"📩 <b>OTP Mới Từ {e.PortName}</b>\n📱 SĐT: {senderPhone}\n🔑 OTP: <code>{extractedOtp}</code>");
            }

            // 3. Đưa lên UI (Cập nhật Tab SMS)
            SmsMessages.Insert(0, new SmsMessage
            {
                PortName = e.PortName,
                ReceivedTime = DateTime.Now.ToString("HH:mm:ss"),
                Content = cleanContent,
                Sender = senderPhone,
                Otp = extractedOtp,
                ReceiverPhone = Ports.FirstOrDefault(p => p.PortName == e.PortName)?.PhoneNumber ?? ""
            });
            
            // 4. Đưa lên UI (Cập nhật Tab GSM)
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port != null)
            {
                port.Sender = senderPhone;
                port.Otp = extractedOtp;
                port.LastMessageContent = cleanContent;
                port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
            }
            
            SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");
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

    [RelayCommand]
    private void SimulateSms()
    {
        // Tin nhắn ảo giống hệt định dạng trả về từ phần cứng
        string rawSms = "+CMGR: \"REC UNREAD\",\"+84123456789\",,\"26/05/01,10:00:00+28\"\r\nMa xac nhan Facebook cua ban la 889922. Vui long khong chia se cho bat ky ai.\r\n\r\nOK";
        
        // Tạo một cổng ảo để test nếu chưa có
        if (!Ports.Any(p => p.PortName == "COM_VIRTUAL"))
        {
            Ports.Insert(0, new SimPort { PortName = "COM_VIRTUAL", Status = "Đang hoạt động", SignalStrength = 100, PhoneNumber = "0987654321" });
        }

        // Bắn trực tiếp dữ liệu vào event như cách GsmModemService làm
        ModemService_SmsReceived(this, new GsmDataEventArgs { PortName = "COM_VIRTUAL", Data = rawSms });
        AddLog("Đã giả lập 1 tin nhắn nhận được trên cổng COM_VIRTUAL.");
    }

    private string DecodeUcs2(string hexString)
    {
        try
        {
            // Kiểm tra xem có phải chuỗi HEX không và độ dài phải chia hết cho 4
            if (!Regex.IsMatch(hexString, @"^[0-9A-Fa-f]+$") || hexString.Length % 4 != 0)
            {
                return hexString; // Không phải UCS2
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hexString.Length; i += 4)
            {
                string hexChar = hexString.Substring(i, 4);
                sb.Append((char)Convert.ToInt32(hexChar, 16));
            }
            return sb.ToString();
        }
        catch
        {
            return hexString; // Trả về nguyên bản nếu lỗi
        }
    }
}
