using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using gsm.Models;
using gsm.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using MaterialDesignThemes.Wpf;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace gsm.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGsmModemService _modemService;
    public IGsmModemService ModemService => _modemService;

    private static readonly TimeSpan UssdMinIntervalPerPort = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UssdMinIntervalGlobal = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, DateTime> _lastUssdByPort = new();
    private readonly SemaphoreSlim _ussdSendLock = new SemaphoreSlim(1, 1);
    private DateTime _lastUssdGlobalUtc = DateTime.MinValue;

    private readonly string _cacheFilePath = "sim_cache.json";
    private ConcurrentDictionary<string, string> _simCache = new();

    private static readonly IReadOnlyDictionary<string, string> BalanceUssdByProvider =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VINAPHONE", "*110#" },
            { "VINA", "*110#" }
        };

    [ObservableProperty]
    private ObservableCollection<SimPort> _ports = new();

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _smsMessages = new();

    [ObservableProperty]
    private SimPort? _selectedPort;

    [ObservableProperty]
    private int _selectedTabIndex = 0; 

    [ObservableProperty]
    private ISnackbarMessageQueue _snackbarMessageQueue = new SnackbarMessageQueue(TimeSpan.FromSeconds(3));

    [ObservableProperty]
    private ObservableCollection<LogMessage> _systemLogs = new();

    [ObservableProperty]
    private LogMessage? _selectedLog;

    [ObservableProperty]
    private string _topUpInput = string.Empty;

    [ObservableProperty]
    private bool _isTopUpDialogOpen;

    [ObservableProperty]
    private string _topUpMode = "Selected";

    public ISeries[] ConnectionSeries { get; set; }
    public ISeries[] SmsSeries { get; set; }

    public MainViewModel()
    {
        LoadSimCache();
        _modemService = new GsmModemService();
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        
        InitializeHardware();
        
        ConnectionSeries = new ISeries[]
        {
            new PieSeries<int> { Values = new[] { 0 }, Name = "Đang hoạt động" },
            new PieSeries<int> { Values = new[] { 0 }, Name = "Mất kết nối" }
        };

        SmsSeries = new ISeries[]
        {
            new ColumnSeries<int> { Values = new[] { 0 }, Name = "Tin nhắn nhận được" }
        };

        AddLog("Hệ thống khởi động thành công.");
        Ports.CollectionChanged += (s, e) => UpdateDashboard();
        SmsMessages.CollectionChanged += (s, e) => UpdateDashboard();

        // Khởi động API Server chạy ngầm
        new ApiServer(this).Start();
    }

    private void UpdateDashboard()
    {
        int activeCount = Ports.Count(p => p.Status == "Đang hoạt động");
        int disconnectedCount = Ports.Count - activeCount;

        ConnectionSeries = new ISeries[]
        {
            new PieSeries<int> { Values = new[] { activeCount }, Name = "Đang hoạt động" },
            new PieSeries<int> { Values = new[] { disconnectedCount }, Name = "Mất kết nối" }
        };

        SmsSeries = new ISeries[]
        {
            new ColumnSeries<int> { Values = new[] { SmsMessages.Count }, Name = "Tin nhắn nhận được" }
        };

        OnPropertyChanged(nameof(ConnectionSeries));
        OnPropertyChanged(nameof(SmsSeries));
    }

    private void AddLog(string message, string level = "INFO")
    {
        SystemLogs.Insert(0, new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Message = message });
        if (SystemLogs.Count > 500)
        {
            SystemLogs.RemoveAt(SystemLogs.Count - 1);
        }
    }

    [RelayCommand]
    private void CopySelectedLog(LogMessage? log)
    {
        var target = log ?? SelectedLog;
        if (target == null) return;

        Clipboard.SetText(FormatLogLine(target));
        SnackbarMessageQueue.Enqueue("Đã sao chép log.");
    }

    [RelayCommand]
    private void CopyAllLogs()
    {
        if (SystemLogs.Count == 0) return;

        var builder = new StringBuilder();
        for (int i = SystemLogs.Count - 1; i >= 0; i--)
        {
            builder.AppendLine(FormatLogLine(SystemLogs[i]));
        }

        Clipboard.SetText(builder.ToString().TrimEnd());
        SnackbarMessageQueue.Enqueue("Đã sao chép toàn bộ log.");
    }

    private static string FormatLogLine(LogMessage log)
    {
        return $"{log.Time} {log.Level} {log.Message}";
    }

    private void InitializeHardware()
    {
        Ports.Clear();
        SmsMessages.Clear();

        Task.Run(() =>
        {
            _modemService.ConnectAll(115200);
        });
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        SnackbarMessageQueue.Enqueue("Đang cập nhật tình trạng thiết bị...");
        AddLog("Bắt đầu cập nhật tình trạng thiết bị...");
        
        Application.Current.Dispatcher.Invoke(() =>
        {
            var availablePorts = _modemService.GetAvailablePorts();
            var removedPorts = Ports.Where(p => !availablePorts.Contains(p.PortName) && p.PortName != "COM_VIRTUAL").ToList();
            foreach (var p in removedPorts) Ports.Remove(p);
        });

        Task.Run(() =>
        {
            _modemService.ConnectAll(115200);

            var currentPorts = Ports.Where(p => p.PortName != "COM_VIRTUAL").Select(p => p.PortName).ToList();
            foreach (var p in currentPorts)
            {
                _ = Task.Run(async () =>
                {
                    await _modemService.SendCommandAsync(p, "AT+CSQ", 5000, true);
                    await _modemService.SendCommandAsync(p, "AT+COPS?", 5000, true);
                    
                    // Nạp lại CCID để phát hiện nếu người dùng thay SIM sống (hot-plug)
                    string ccid = await _modemService.SendCommandAsync(p, "AT+CCID", 5000, true);
                    if (!ccid.Contains("ERROR"))
                    {
                        var match = Regex.Match(ccid, @"\+CCID:\s*([A-Za-z0-9]+)");
                        if (match.Success)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                var port = Ports.FirstOrDefault(x => x.PortName == p);
                                if (port != null)
                                {
                                    string newSerial = match.Groups[1].Value;
                                    if (port.Serial != newSerial)
                                    {
                                        port.Serial = newSerial;
                                        port.NetworkProvider = string.Empty;
                                        
                                        if (_simCache.TryGetValue(port.Serial, out var cachedPhone))
                                        {
                                            port.PhoneNumber = cachedPhone;
                                            AddLog($"[{p}] Đã nạp SĐT từ cache: {cachedPhone}", "SUCCESS");
                                        }
                                        else
                                        {
                                            // SIM mới không có trong cache, xóa SĐT cũ và khởi động lại luồng quét nhà mạng
                                            port.PhoneNumber = string.Empty;
                                            _modemService.StartPollingNetwork(p);
                                        }
                                    }
                                }
                            });
                        }
                    }
                });
            }
        });
    }

    private void ModemService_LogMessage(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            bool isInternalEvent = e.Data.StartsWith("[PARSE_") || e.Data == "[STATUS_ACTIVE]";
            if (!isInternalEvent) AddLog($"[{e.PortName}] {e.Data}");
            
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);

            if (port == null)
            {
                if (e.Data.StartsWith("[PARSE_IMEI]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+CSQ:") || e.Data.Contains("+COPS:"))
                {
                    port = new SimPort { PortName = e.PortName, Status = "Đang hoạt động", SignalStrength = 0 };
                    Ports.Add(port);
                }
                else
                {
                    return;
                }
            }

            if (e.Data.Contains("+CSQ:"))
            {
                var match = Regex.Match(e.Data, @"\+CSQ:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int csq))
                {
                    port.SignalStrength = csq >= 99 ? 0 : (int)((csq / 31.0) * 100);
                }
            }
            else if (e.Data.Contains("+CUSD:"))
            {
                var match = Regex.Match(e.Data, @"\+CUSD:.*?""(.*?)(?:""|$)", RegexOptions.Singleline);
                if (match.Success)
                {
                    string ussdContent = match.Groups[1].Value;
                    
                    // Giải mã UCS2 (Hex sang string UTF-8) để đọc được tiếng Việt
                    ussdContent = DecodeUcs2(ussdContent);

                    var phoneMatch = Regex.Match(ussdContent, @"(?:84|0)(8[1-5|8]|9[1|4])\d{7}");
                    if (!phoneMatch.Success)
                    {
                        // Fallback
                        phoneMatch = Regex.Match(ussdContent, @"([345789][0-9]{8})");
                    }

                    if (phoneMatch.Success)
                    {
                        string foundNumber = phoneMatch.Value;
                        if (foundNumber.StartsWith("84")) foundNumber = "0" + foundNumber.Substring(2);
                        else if (!foundNumber.StartsWith("0")) foundNumber = "0" + foundNumber;

                        port.PhoneNumber = foundNumber;
                        string networkLabel = string.IsNullOrWhiteSpace(port.NetworkProvider) ? "UNKNOWN" : port.NetworkProvider;
                        AddLog($"[{e.PortName}] SĐT chuẩn: {foundNumber} ({networkLabel})", "SUCCESS");

                        if (!string.IsNullOrWhiteSpace(port.Serial))
                        {
                            _simCache[port.Serial] = foundNumber;
                            SaveSimCache();
                        }
                    }
                    
                    var moneyMatch = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ)", RegexOptions.IgnoreCase);
                    if (moneyMatch.Success) port.Balance = moneyMatch.Value;
                    else port.Balance = "Thành công";

                    var expiryMatch = Regex.Match(ussdContent, @"\b(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})\b");
                    if (expiryMatch.Success) port.ExpiryDate = expiryMatch.Groups[1].Value;

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

                    string networkUpper = port.NetworkProvider.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(port.PhoneNumber) && 
                       (networkUpper.Contains("VINAPHONE") || networkUpper.Contains("VINA")))
                    {
                        _ = SendUssdThrottledAsync(port.PortName, "*110#", "Tự động lấy SĐT");
                    }
                }
            }
            else if (e.Data.StartsWith("[PARSE_IMEI]"))
            {
                port.Imei = e.Data.Replace("[PARSE_IMEI]", "").Trim();
            }
            else if (e.Data.StartsWith("[PARSE_CCID]"))
            {
                port.Serial = e.Data.Replace("[PARSE_CCID]", "").Replace("+CCID:", "").Trim();
                if (_simCache.TryGetValue(port.Serial, out var cachedPhone))
                {
                    port.PhoneNumber = cachedPhone;
                    AddLog($"[{e.PortName}] Đã nạp SĐT từ cache: {cachedPhone}", "SUCCESS");
                }
            }
            else if (e.Data.StartsWith("[PARSE_CNUM]"))
            {
                string cnumRaw = e.Data.Replace("[PARSE_CNUM]", "").Trim();

                var quotedMatch = Regex.Match(cnumRaw, @"\+CNUM:\s*""[^""]*"",""([^""]+)""");
                string rawNumber = quotedMatch.Success ? quotedMatch.Groups[1].Value : string.Empty;

                if (string.IsNullOrWhiteSpace(rawNumber))
                {
                    var numMatch = Regex.Match(cnumRaw, @"(\+?\d{9,15})");
                    rawNumber = numMatch.Success ? numMatch.Groups[1].Value : string.Empty;
                }

                if (rawNumber.StartsWith("+84", StringComparison.Ordinal))
                {
                    rawNumber = "0" + rawNumber.Substring(3);
                }
                else if (rawNumber.StartsWith("84", StringComparison.Ordinal) && rawNumber.Length >= 11)
                {
                    rawNumber = "0" + rawNumber.Substring(2);
                }
                else if (rawNumber.Length == 9 && Regex.IsMatch(rawNumber, @"^[35789]"))
                {
                    rawNumber = "0" + rawNumber;
                }

                if (!string.IsNullOrWhiteSpace(rawNumber))
                {
                    port.PhoneNumber = rawNumber;
                }
            }
            else if (e.Data == "[STATUS_ACTIVE]")
            {
                port.Status = "Đang hoạt động";
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
                foreach (var sms in SmsMessages.Where(s => s.PortName == e.PortName)) sms.Status = "Đang hoạt động";
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
                UpdateDashboard();
                foreach (var sms in SmsMessages.Where(s => s.PortName == e.PortName)) sms.Status = "Mất kết nối";
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

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                string senderPhone = "UNKNOWN";
                string extractedOtp = "N/A";
                string cleanContent = e.Data;

                // 1. Tìm người gửi (Sender)
                var senderMatch = Regex.Match(e.Data, @"\+CMGR:\s*""[^""]+"",""([^""]+)""");
                if (senderMatch.Success)
                {
                    senderPhone = DecodeUcs2(senderMatch.Groups[1].Value); // Giải mã HEX nếu có
                    
                    // Xóa dòng header +CMGR đi để lấy nội dung text sạch
                    cleanContent = Regex.Replace(e.Data, @"\+CMGR:.*?\r\n", "").Trim();
                    cleanContent = Regex.Replace(cleanContent, @"\r?\nOK\r?\n?$", "").Trim();
                    cleanContent = DecodeUcs2(cleanContent); // Giải mã Tiếng Việt
                    
                    // Gộp nội dung thành 1 dòng để tránh lỗi hiển thị trên UI bị mất chữ (do rớt dòng)
                    cleanContent = cleanContent.Replace("\r", " ").Replace("\n", " ").Trim();
                    // Thay thế khoảng trắng kép
                    cleanContent = Regex.Replace(cleanContent, @"\s+", " ");
                }

                // 2. Tìm OTP
                // Xóa các mẫu số điện thoại bị che (VD: ***7628) để tránh việc regex bị nhận nhầm
                string textForOtp = Regex.Replace(cleanContent, @"\*+\d+", "");

                // Tìm các mẫu OTP có từ khóa đi kèm
                var otpMatch = Regex.Match(textForOtp, @"(?:mã|code|otp|là|la|zalo|viber|telegram|facebook|google|apple|tiktok|tinder)\s*[:\-]?\s*(\d{4,8})", RegexOptions.IgnoreCase);
                if (!otpMatch.Success)
                {
                    // Fallback: Tìm một dãy số đứng riêng lẻ (không liền kề chữ cái)
                    // Loại trừ luôn các đầu số tổng đài (1900, 1800) để không bắt nhầm thành OTP
                    otpMatch = Regex.Match(textForOtp, @"(?<![\w:/])(?!1900|1800)\b(\d{4,8})\b(?![\w:/])", RegexOptions.IgnoreCase);
                }

                // 3. Tìm cổng tương ứng để lấy thông tin SIM (SĐT, Nhà mạng)
                var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

                if (otpMatch.Success)
                {
                    extractedOtp = otpMatch.Groups.Count > 1 && !string.IsNullOrEmpty(otpMatch.Groups[1].Value) ? otpMatch.Groups[1].Value : otpMatch.Value;
                    // Escape HTML characters for Telegram parse_mode = HTML
                    string safeContent = System.Net.WebUtility.HtmlEncode(cleanContent);
                    string safeSender = System.Net.WebUtility.HtmlEncode(senderPhone);
                    
                    // GỌI HÀM BẮN TELEGRAM
                    _ = TelegramService.SendMessageAsync($"📩 <b>OTP Mới Từ {e.PortName}</b>\n📱 SĐT: {receiverPhone}\n👤 Từ: {safeSender}\n🔑 OTP: <code>{extractedOtp}</code>\n📝 Nội dung: <i>{safeContent}</i>");
                }

                // 4. Đưa lên UI (Cập nhật Tab SMS)
                SmsMessages.Insert(0, new SmsMessage
                {
                    PortName = e.PortName,
                    ReceivedTime = DateTime.Now.ToString("HH:mm:ss"),
                    Content = cleanContent,
                    Sender = senderPhone,
                    Otp = extractedOtp,
                    ReceiverPhone = port?.PhoneNumber ?? "",
                    NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
                    Status = port?.Status ?? "Đang kết nối...",
                    CallCount = "0",
                    ForwardContent = "Không"
                });
                
                // 5. Đưa lên UI (Cập nhật Tab GSM)
                if (port != null)
                {
                    port.Sender = senderPhone;
                    port.Otp = extractedOtp;
                    port.LastMessageContent = cleanContent;
                    port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                }
                
                if (extractedOtp != "N/A")
                {
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");
                    
                    // Chỉ xóa tin nhắn sau khi đã trích xuất OTP thành công
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                }
                else
                {
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Tin nhắn mới từ {senderPhone}");
                    // Giữ lại SMS để debug nếu không bắt được OTP
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        AddLog($"[{e.PortName}] Giữ lại tin nhắn {e.MsgIndex} để debug do không thấy OTP.", "WARN");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"[{e.PortName}] Lỗi xử lý SMS: {ex.Message}", "ERROR");
            }
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
            if (string.IsNullOrWhiteSpace(SelectedPort.NetworkProvider))
            {
                SnackbarMessageQueue.Enqueue("Chưa xác định được nhà mạng để chọn mã USSD.");
                return;
            }

            if (!BalanceUssdByProvider.TryGetValue(SelectedPort.NetworkProvider.Trim(), out var ussdCode))
            {
                SnackbarMessageQueue.Enqueue($"Chưa hỗ trợ mã USSD cho nhà mạng: {SelectedPort.NetworkProvider}");
                return;
            }

            AddLog($"Đang gửi lệnh kiểm tra số dư tới {SelectedPort.PortName}...");
            string result = await SendUssdThrottledAsync(SelectedPort.PortName, ussdCode, "Kiểm tra số dư", logResult: true);
            SnackbarMessageQueue.Enqueue($"Đã kiểm tra số dư cổng {SelectedPort.PortName}");
        }
        else
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn một cổng để kiểm tra số dư.");
        }
    }

    private async Task<string> SendUssdThrottledAsync(string portName, string ussdCode, string reason, bool logResult = false)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(ussdCode))
        {
            return "ERROR: Invalid USSD request";
        }

        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (reason == "Tự động lấy SĐT" && port != null && !string.IsNullOrWhiteSpace(port.PhoneNumber))
        {
            return "SKIPPED: Đã có SĐT";
        }

        await _ussdSendLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;

            if (_lastUssdByPort.TryGetValue(portName, out var lastPortUtc))
            {
                var remaining = UssdMinIntervalPerPort - (now - lastPortUtc);
                if (remaining > TimeSpan.Zero)
                {
                    WarnUssdThrottle(portName, reason, remaining, "SIM");
                    await Task.Delay(remaining);
                    now = DateTime.UtcNow;
                }
            }

            var globalRemaining = UssdMinIntervalGlobal - (now - _lastUssdGlobalUtc);
            if (globalRemaining > TimeSpan.Zero)
            {
                WarnUssdThrottle(portName, reason, globalRemaining, "GLOBAL");
                await Task.Delay(globalRemaining);
                now = DateTime.UtcNow;
            }

            _lastUssdByPort[portName] = now;
            _lastUssdGlobalUtc = now;
        }
        finally
        {
            _ussdSendLock.Release();
        }

        // 1. Chuyển bảng mã về GSM
        await _modemService.SendCommandAsync(portName, "AT+CSCS=\"GSM\"");

        // 2. Gửi lệnh USSD
        string result = await _modemService.SendCommandAsync(portName, $"AT+CUSD=1,\"{ussdCode}\",15");

        // 3. Chuyển lại UCS2
        await _modemService.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"");

        if (logResult)
        {
            AddLog($"Kết quả từ {portName}: {result}", "SUCCESS");
        }

        return result;
    }

    private void WarnUssdThrottle(string portName, string reason, TimeSpan remaining, string scope)
    {
        string message = $"USSD đang xếp hàng ({scope}) cho {portName} - {reason}. Đợi {remaining.TotalSeconds:0.#}s.";
        AddLog(message, "INFO");
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
    private void DummyFeature(string featureName)
    {
        SnackbarMessageQueue.Enqueue($"Tính năng {featureName} đang được phát triển.");
    }

    [RelayCommand]
    private void CopyOtp(SmsMessage? sms)
    {
        if (sms != null && !string.IsNullOrEmpty(sms.Otp))
        {
            Clipboard.SetText(sms.Otp);
            SnackbarMessageQueue.Enqueue("Đã sao chép OTP vào Clipboard.");
        }
    }

    [RelayCommand]
    private void DeleteSms(SmsMessage? sms)
    {
        if (sms != null)
        {
            SmsMessages.Remove(sms);
            SnackbarMessageQueue.Enqueue("Đã xóa tin nhắn.");
        }
    }

    [RelayCommand]
    private void OpenTopUpDialog(string mode)
    {
        TopUpMode = string.IsNullOrEmpty(mode) ? "Selected" : mode;
        TopUpInput = string.Empty;
        IsTopUpDialogOpen = true;
    }

    [RelayCommand]
    private async Task ExecuteTopUpAsync()
    {
        IsTopUpDialogOpen = false;
        if (string.IsNullOrWhiteSpace(TopUpInput))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng nhập mã thẻ cào hoặc cú pháp USSD.");
            return;
        }

        string ussdCode = TopUpInput.Trim();
        if (Regex.IsMatch(ussdCode, @"^\d+$"))
        {
            // Tự động format mã thẻ cào thành cú pháp USSD nạp tiền (Chuẩn Vinaphone)
            ussdCode = $"*100*{ussdCode}#";
        }

        var targetPorts = new System.Collections.Generic.List<SimPort>();
        if (TopUpMode == "Selected")
        {
            if (SelectedPort != null) targetPorts.Add(SelectedPort);
        }
        else if (TopUpMode == "Checked")
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
        }
        else if (TopUpMode == "All")
        {
            targetPorts = Ports.Where(p => p.Status == "Đang hoạt động").ToList();
        }

        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Không có cổng nào được chọn để nạp thẻ.");
            return;
        }

        SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh nạp thẻ cho {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu nạp thẻ cho {targetPorts.Count} cổng với cú pháp: {ussdCode}");

        foreach (var port in targetPorts)
        {
            _ = SendUssdThrottledAsync(port.PortName, ussdCode, "Nạp tiền", logResult: true);
        }
    }

    [RelayCommand]
    private void SimulateSms()
    {
        // Tin nhắn ảo giống hệt định dạng trả về từ phần cứng
        string rawSms = "+CMGR: \"REC UNREAD\",\"+84123456789\",,\"26/05/01,10:00:00+28\"\r\nMa xac nhan Facebook cua ban la 889933. Vui long khong chia se cho bat ky ai.\r\n\r\nOK";
        
        // Tạo một cổng ảo để test nếu chưa có
        if (!Ports.Any(p => p.PortName == "COM_VIRTUAL"))
        {
            Ports.Insert(0, new SimPort 
            { 
                PortName = "COM_VIRTUAL", 
                Status = "Đang hoạt động", 
                SignalStrength = 100, 
                PhoneNumber = "0987654321",
                NetworkProvider = "VINAPHONE",
                Imei = "359837042531092",
                Serial = "8984040001234567890",
                Balance = "50,000 đ",
                ExpiryDate = "31/12/2026",
                UpdatedAt = DateTime.Now.ToString("HH:mm:ss")
            });
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

    private void LoadSimCache()
    {
        if (File.Exists(_cacheFilePath))
        {
            try
            {
                string json = File.ReadAllText(_cacheFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null) _simCache = new ConcurrentDictionary<string, string>(dict);
            }
            catch { }
        }
    }

    private void SaveSimCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_simCache);
            File.WriteAllText(_cacheFilePath, json);
        }
        catch { }
    }
}
