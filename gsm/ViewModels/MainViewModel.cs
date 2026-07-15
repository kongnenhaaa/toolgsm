using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
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
using OfficeOpenXml;

namespace gsm.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGsmModemService _modemService;
    private readonly Services.ImeiManagementService _imeiManagementService;
    public IGsmModemService ModemService => _modemService;

    private readonly SpeechToTextService _speechToTextService;
    private readonly FirebaseService _firebaseService;
    public ProxyManagerService ProxyManager { get; }
    private readonly ApiServerService? _apiServerService;
    private readonly ConcurrentDictionary<string, string> _callFailures = new();
    private readonly ConcurrentDictionary<string, bool> _activeRamRecordings = new();
    private readonly ConcurrentDictionary<string, string> _activeCallers = new();
    private readonly ConcurrentDictionary<string, bool> _pendingMyVnptPasswordPorts = new();
    private readonly object _logFileLock = new();
    
    public event Action<string, string>? OtpReceivedEvent;

    private static readonly TimeSpan UssdMinIntervalPerPort = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UssdMinIntervalGlobal = TimeSpan.FromMilliseconds(10);
    private readonly ConcurrentDictionary<string, DateTime> _lastUssdByPort = new();
    private readonly SemaphoreSlim _ussdSendLock = new SemaphoreSlim(100, 100);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSendLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _portCooldownUntilUtc = new();
    private DateTime _lastUssdGlobalUtc = DateTime.MinValue;

    // Fix #3: Dùng static Random để tránh lỗi seed trùng khi gọi liên tiếp nhanh
    private static readonly Random _rng = new Random();

    // Đánh dấu cổng nào đang có SMS được gửi để USSD tự nhường đường (tránh tranh Semaphore)
    public readonly ConcurrentDictionary<string, bool> SmsInProgressPorts = new();

    private readonly string _cacheFilePath = AppPaths.ForRuntimeFile("sim_cache.json");
    private ConcurrentDictionary<string, string> _simCache = new();

    private readonly string _imeiCacheFilePath = AppPaths.ForRuntimeFile("imei_backup.csv");
    private ConcurrentDictionary<string, SimBackupEntry> _imeiCache = new();
    private readonly object _imeiCacheLock = new();

    private static readonly IReadOnlyDictionary<string, string> BalanceUssdByProvider =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VINAPHONE", "*101#" },
            { "VINA", "*101#" },
            { "VIETTEL", "*101#" },
            { "MOBIFONE", "*101#" },
            { "MOBI", "*101#" },
            { "VIETNAMOBILE", "*101#" },
            { "GMOBILE", "*101#" },
            { "WINTEL", "*101#" },
            { "ITELECOM", "*101#" },
            { "ITEL", "*101#" },
            { "LOCAL", "*101#" },
            { "SKY", "*101#" },
            { "VNSKY", "*101#" },
            { "FPT", "*101#" }
        };

    [ObservableProperty]
    private ObservableCollection<SimPort> _ports = new();

    [ObservableProperty]
    private ObservableCollection<CommandQueueItem> _commandQueue = new();

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _smsMessages = new();

    [ObservableProperty]
    private SimPort? _selectedPort;


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

    // ComposeSms properties removed

    // Custom USSD properties removed

    [ObservableProperty] private string _commandPanelMmsRecipients = string.Empty;
    [ObservableProperty] private string _commandPanelMmsTitle = string.Empty;
    [ObservableProperty] private string _commandPanelMmsAttachmentPath = string.Empty;
    [ObservableProperty] private bool _commandPanelMmsAdvancedOpen;
    [ObservableProperty] private bool _isCommandPanelOpen;
    [ObservableProperty] private System.Windows.GridLength _commandPanelColumnWidth = new System.Windows.GridLength(0);

    partial void OnIsCommandPanelOpenChanged(bool value)
    {
        if (value)
        {
            if (CommandPanelColumnWidth.Value == 0)
                CommandPanelColumnWidth = new System.Windows.GridLength(575);
        }
        else
        {
            CommandPanelColumnWidth = new System.Windows.GridLength(0);
        }
    }
    public string AddButtonText => CommandPanelTab switch { "Call" => "+ Thêm Cuộc gọi", "Delay" => "+ Thêm Trễ", _ => $"+ Thêm {CommandPanelTab}" };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AddButtonText))] private string _commandPanelTab = "USSD";
    [ObservableProperty] private string _commandPanelCallNumber = string.Empty;
    [ObservableProperty] private string _commandPanelCallDuration = string.Empty;
    [ObservableProperty] private string _commandPanelCallDtmf = string.Empty;
    [ObservableProperty] private int _commandPanelDataAmount = 500;
    [ObservableProperty] private int _commandPanelModeIndex = 0;
    [ObservableProperty] private int _commandPanelRetryCount = 0;
    [ObservableProperty] private string _commandPanelImeiValue = string.Empty;
    [ObservableProperty] private int _commandPanelDelaySeconds = 1;
    [ObservableProperty] private string _commandPanelUssdText = string.Empty;
    [ObservableProperty] private string _commandPanelSmsRecipient = string.Empty;
    [ObservableProperty] private string _commandPanelSmsContent = string.Empty;

    [ObservableProperty] private int _queuePendingCount;
    [ObservableProperty] private int _queueSuccessCount;
    [ObservableProperty] private int _queueErrorCount;

    [ObservableProperty] private bool _hasUssdError;
    [ObservableProperty] private bool _hasSmsRecipientError;
    [ObservableProperty] private bool _hasSmsContentError;
    [ObservableProperty] private bool _hasCallNumberError;
    [ObservableProperty] private bool _hasDataAmountError;
    [ObservableProperty] private bool _hasDelaySecondsError;

    private string CurrentCommandPanelMode => CommandPanelModeIndex == 0 ? "Đồng thời" : "Tuần tự";

    private void ClearCommandPanelErrors() {
        HasUssdError = false;
        HasSmsRecipientError = false;
        HasSmsContentError = false;
        HasCallNumberError = false;
        HasDataAmountError = false;
        HasDelaySecondsError = false;
    }

    private void UpdateCommandCounts() {
        QueuePendingCount = CommandQueue.Count(x => x.Status == "Chờ");
        QueueSuccessCount = CommandQueue.Count(x => x.Status == "Xong");
        QueueErrorCount = CommandQueue.Count(x => x.Status == "Lỗi");
    }

    [RelayCommand]
    private void ClearCommandForm() {
        CommandPanelSmsRecipient = string.Empty;
        CommandPanelSmsContent = string.Empty;
        CommandPanelCallNumber = string.Empty;
        CommandPanelUssdText = string.Empty;
        CommandPanelDelaySeconds = 1;
        ClearCommandPanelErrors();
    }

    [RelayCommand]
    private async Task SetMyVnptPassword(object obj)
    {
        var targetPorts = Ports.Where(p => p.IsSelected).ToList();
        
        // Nếu click từ ContextMenu của 1 dòng cụ thể mà chưa tick chọn
        if (obj is SimPort clickedPort && !targetPorts.Contains(clickedPort))
        {
            targetPorts.Add(clickedPort);
        }

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng để đặt mật khẩu MyVNPT.");
            return;
        }

        int count = 0;
        foreach (var port in targetPorts)
        {
            if (string.IsNullOrWhiteSpace(port.PhoneNumber) || port.PhoneNumber == "Chưa lấy được số")
            {
                AddLog($"[{port.PortName}] Bỏ qua vì chưa có số điện thoại.", "WARN");
                continue;
            }

            count++;
            _ = Task.Run(async () =>
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Đang kiểm tra trạng thái tài khoản số {port.PhoneNumber}..."));
                    using var client = new System.Net.Http.HttpClient();
                    string phone = port.PhoneNumber.StartsWith("0") ? "84" + port.PhoneNumber.Substring(1) : port.PhoneNumber;
                    
                    // 1. Kiểm tra tài khoản
                    var checkPayload = new { msisdn = phone };
                    string checkJson = System.Text.Json.JsonSerializer.Serialize(checkPayload);
                    using var checkRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_check_account");
                    checkRequest.Content = new System.Net.Http.StringContent(checkJson, System.Text.Encoding.UTF8, "application/json");
                    checkRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b");
                    checkRequest.Headers.TryAddWithoutValidation("Device-Info", "a6d10733-aaed-47a5-aa83-2446121b3e4e|a6d10733-aaed-47a5-aa83-2446121b3e4e|unknown|Android||3.3.97.Prd|motog(7)|10|");
                    checkRequest.Headers.TryAddWithoutValidation("Language", "vi_VN");
                    checkRequest.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.7.2");
                    
                    var checkResponse = await client.SendAsync(checkRequest);
                    string checkResponseContent = await checkResponse.Content.ReadAsStringAsync();
                    
                    bool accountExists = checkResponseContent.Contains("\"error_code\":\"3\"") || checkResponseContent.Contains("\"error_code\": \"3\"");
                    string otpService = accountExists ? "authen_miss_password" : "authen_register";
                    string modeStr = accountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
                    
                    Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Trạng thái: {modeStr}. Đang yêu cầu OTP..."));
                    
                    var payload = new
                    {
                        msisdn = phone,
                        otp_service = otpService
                    };
                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://api-myvnpt.vnpt.vn/mapi_v2/services/otp_send");
                    request.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b");
                    request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                    request.Headers.TryAddWithoutValidation("Device-Info", "a6d10733-aaed-47a5-aa83-2446121b3e4e|a6d10733-aaed-47a5-aa83-2446121b3e4e|unknown|Android||3.3.97.Prd|motog(7)|10|");
                    request.Headers.TryAddWithoutValidation("Language", "vi_VN");
                    request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.7.2");

                    var response = await client.SendAsync(request);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    if (responseContent.Contains("\"error_code\":\"0\"") || responseContent.Contains("\"errorCode\":\"0\"") || responseContent.Contains("\"error_code\": \"0\""))
                    {
                        Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Đã gửi yêu cầu OTP thành công ({modeStr}), đang đợi tin nhắn...", "INFO"));
                        _pendingMyVnptPasswordPorts[port.PortName] = true;
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Gửi yêu cầu OTP thất bại: {responseContent}", "ERROR"));
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Lỗi gửi yêu cầu OTP: {ex.Message}", "ERROR"));
                }
            });
            await Task.Delay(500); // Tránh gửi request quá nhanh
        }
        
        if (count > 0)
            SnackbarMessageQueue.Enqueue($"Đã gửi lệnh yêu cầu OTP cho {count} cổng.");
    }

    [ObservableProperty]
    private bool _isCallManagerDialogOpen;

    private int _unreadOtpCount = 0;
    public string? UnreadOtpBadge => _unreadOtpCount > 0 ? _unreadOtpCount.ToString() : null;

    public void IncrementUnreadOtp()
    {
        _unreadOtpCount++;
        OnPropertyChanged(nameof(UnreadOtpBadge));
    }

    public void ResetUnreadOtp()
    {
        if (_unreadOtpCount > 0)
        {
            _unreadOtpCount = 0;
            OnPropertyChanged(nameof(UnreadOtpBadge));
        }
    }

    public bool IsReceiveAllSmsEnabled
    {
        get => SettingsService.Current.ReceiveAllSms;
        set
        {
            if (SettingsService.Current.ReceiveAllSms != value)
            {
                SettingsService.Current.ReceiveAllSms = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsTelegramNotificationEnabled
    {
        get => SettingsService.Current.EnableTelegramNotification;
        set
        {
            if (SettingsService.Current.EnableTelegramNotification != value)
            {
                SettingsService.Current.EnableTelegramNotification = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsWebNotificationEnabled
    {
        get => SettingsService.Current.EnableWebNotification;
        set
        {
            if (SettingsService.Current.EnableWebNotification != value)
            {
                SettingsService.Current.EnableWebNotification = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsAutoAnswerEnabled
    {
        get => SettingsService.Current.EnableAutoAnswer;
        set
        {
            if (SettingsService.Current.EnableAutoAnswer != value)
            {
                SettingsService.Current.EnableAutoAnswer = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsWatchdogEnabled
    {
        get => SettingsService.Current.EnableAutoWatchdog;
        set
        {
            if (SettingsService.Current.EnableAutoWatchdog != value)
            {
                SettingsService.Current.EnableAutoWatchdog = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsImeiRestoreEnabled
    {
        get => SettingsService.Current.EnableImeiRestore;
        set
        {
            if (SettingsService.Current.EnableImeiRestore != value)
            {
                SettingsService.Current.EnableImeiRestore = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsNewSimIntakeModeEnabled
    {
        get => SettingsService.Current.EnableNewSimIntakeMode;
        set
        {
            if (SettingsService.Current.EnableNewSimIntakeMode != value)
            {
                SettingsService.Current.EnableNewSimIntakeMode = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AppSettings));
            }
        }
    }

    public bool IsBlockUnknownSimsEnabled
    {
        get => SettingsService.Current.BlockUnknownSims;
        set
        {
            if (SettingsService.Current.BlockUnknownSims != value)
            {
                SettingsService.Current.BlockUnknownSims = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private string _callManagerSelectedPort = string.Empty;

    [ObservableProperty]
    private string _callPhoneNumber = string.Empty;

    [ObservableProperty]
    private string _dtmfTones = string.Empty;

    [ObservableProperty]
    private string _forwardNumber = string.Empty;

    [ObservableProperty]
    private string _callManagerOutput = string.Empty;

    // Network & Sim properties removed

    [ObservableProperty]
    private AppSettings _appSettings = new();

    [ObservableProperty]
    private bool _isSettingsDialogOpen;

    [ObservableProperty]
    private bool _isAtCommandDialogOpen;

    [ObservableProperty]
    private string _atCommandInput = "AT";

    public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> PredefinedAtCommands { get; } = new()
    {
        // 1. CƠ BẢN & THÔNG TIN THIẾT BỊ
        new("AT", "Kiểm tra kết nối modem"),
        new("ATI", "Xem thông tin Firmware/Version của Modem"),
        new("ATZ", "Reset modem về cấu hình mặc định (Reset Profile)"),
        new("ATE1", "Bật tính năng Echo (hiển thị ký tự gõ)"),
        new("ATE0", "Tắt tính năng Echo"),
        new("AT+CMEE=2", "Bật báo lỗi chi tiết (Verbose Error)"),
        new("AT+CFUN=1", "Khởi động đầy đủ sóng/modem (Full mode)"),
        new("AT+CFUN=4", "Bật chế độ máy bay (Tắt sóng)"),
        new("AT+CFUN=0", "Tắt modem (Minimum mode)"),
        
        // 2. THÔNG TIN SIM & MẠNG
        new("AT+CPIN?", "Kiểm tra trạng thái SIM/PIN"),
        new("AT+CSQ", "Kiểm tra cường độ sóng (Signal Quality)"),
        new("AT+CREG?", "Kiểm tra trạng thái đăng ký mạng"),
        new("AT+COPS?", "Kiểm tra nhà mạng hiện tại"),
        new("AT+COPS=0", "Bật tự động dò sóng nhà mạng"),
        new("AT+CIMI", "Đọc mã IMSI của SIM"),
        new("AT+QCCID", "Đọc mã ICCID (Serial SIM - Lệnh Quectel)"),
        new("AT+CCID", "Đọc mã ICCID (Serial SIM - Lệnh chuẩn)"),
        new("AT+QSIMSTAT?", "Kiểm tra trạng thái nhận diện SIM"),
        new("AT+CNUM", "Kiểm tra số điện thoại của SIM (Nếu có lưu)"),
        new("AT+QNWINFO", "Xem thông tin băng tần mạng (3G/4G)"),
        new("AT+CUSD=1,\"*101#\",15", "Kiểm tra tài khoản (Lệnh USSD)"),
        new("AT+CUSD=1,\"*102#\",15", "Kiểm tra tài khoản khuyến mãi (USSD)"),
        
        // 3. ĐIỀU KHIỂN CUỘC GỌI
        new("ATD0987654321;", "Thực hiện cuộc gọi (nhớ đổi SĐT và giữ dấu ;)"),
        new("ATH", "Ngắt/từ chối cuộc gọi hiện tại"),
        new("ATA", "Bắt máy cuộc gọi đến"),
        new("AT+CHUP", "Hủy tất cả các cuộc gọi"),
        new("AT+CLIP=1", "Bật hiển thị số gọi đến (Caller ID)"),
        new("AT+CLIR=1", "Ẩn số gọi đi (nếu mạng hỗ trợ)"),
        new("AT+CLCC", "Danh sách các cuộc gọi đang diễn ra"),
        new("AT+CCWA=1,1,1", "Bật tính năng chờ cuộc gọi (Call Waiting)"),
        new("AT+VTS=\"1\"", "Gửi phím DTMF '1' (Trong lúc gọi)"),
        new("AT+CCFC=0,2", "Kiểm tra trạng thái chuyển tiếp cuộc gọi"),
        
        // 4. QUẢN LÝ TIN NHẮN SMS
        new("AT+CMGF=1", "Chuyển cấu hình SMS sang chế độ Text (Dễ đọc)"),
        new("AT+CMGL=\"ALL\"", "Đọc tất cả tin nhắn SMS đang có"),
        new("AT+CMGL=\"REC UNREAD\"", "Đọc các tin nhắn SMS chưa đọc"),
        new("AT+CMGR=1", "Đọc tin nhắn ở vị trí số 1"),
        new("AT+CMGD=1,4", "Xóa toàn bộ tin nhắn SMS trên SIM"),
        new("AT+CPMS=\"SM\",\"SM\",\"SM\"", "Chuyển vùng nhớ tin nhắn sang SIM"),
        new("AT+CSCA?", "Kiểm tra số trung tâm tin nhắn (SMSC)"),
        
        // 5. DANH BẠ
        new("AT+CPBS=\"SM\"", "Đặt vùng nhớ danh bạ là SIM"),
        new("AT+CPBR=1,10", "Đọc danh bạ từ vị trí 1 đến 10")
    };

    public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> PredefinedUssdCommands { get; } = new()
    {
        new("*101#", "Kiểm tra tài khoản chính (Viettel/Mobi/Vina)"),
        new("*102#", "Kiểm tra tài khoản khuyến mãi"),
        new("*098#", "Menu Khuyến mãi (Viettel)"),
        new("*111#", "Tiện ích trả trước (Viettel)"),
        new("*901*3#", "Menu kiểm tra gói cước (MobiFone)"),
        new("*0#", "Kiểm tra SĐT (Mobi/Vina)"),
        new("*110#", "Kiểm tra thông tin thuê bao (VinaPhone)"),
        new("*101#", "Kiểm tra SĐT (Viettel - Một số dòng SIM)")
    };

    [ObservableProperty]
    private string _atCommandOutput = string.Empty;

    [ObservableProperty]
    private string _atCommandSelectedPort = string.Empty;

    private string _smsPhoneFilter = string.Empty;
    public string SmsPhoneFilter
    {
        get => _smsPhoneFilter;
        set
        {
            _smsPhoneFilter = value;
            OnPropertyChanged(nameof(SmsPhoneFilter));
            OnPropertyChanged(nameof(FilteredSmsMessages));
        }
    }

    private string _smsPortFilter = string.Empty;
    public string SmsPortFilter
    {
        get => _smsPortFilter;
        set
        {
            _smsPortFilter = value;
            OnPropertyChanged(nameof(SmsPortFilter));
            OnPropertyChanged(nameof(FilteredSmsMessages));
        }
    }

    private string _smsSenderFilter = string.Empty;
    public string SmsSenderFilter
    {
        get => _smsSenderFilter;
        set
        {
            _smsSenderFilter = value;
            OnPropertyChanged(nameof(SmsSenderFilter));
            OnPropertyChanged(nameof(FilteredSmsMessages));
        }
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            SetProperty(ref _selectedTabIndex, value);
            if (_selectedTabIndex == 3)
            {
                ResetUnreadOtp(); // Reset khi vào tab OTP
            }
        }
    }

    private string _portNameFilter = string.Empty;
    public string PortNameFilter
    {
        get => _portNameFilter;
        set { SetProperty(ref _portNameFilter, value); FilteredPortsView?.Refresh(); }
    }

    private string _imeiFilter = string.Empty;
    public string ImeiFilter
    {
        get => _imeiFilter;
        set { SetProperty(ref _imeiFilter, value); FilteredPortsView?.Refresh(); }
    }

    private string _serialFilter = string.Empty;
    public string SerialFilter
    {
        get => _serialFilter;
        set { SetProperty(ref _serialFilter, value); FilteredPortsView?.Refresh(); }
    }

    private string _phoneNumberFilter = string.Empty;
    public string PhoneNumberFilter
    {
        get => _phoneNumberFilter;
        set { SetProperty(ref _phoneNumberFilter, value); FilteredPortsView?.Refresh(); }
    }

    public System.ComponentModel.ICollectionView FilteredPortsView { get; }

    private bool _isAllPortsSelected;
    public bool IsAllPortsSelected
    {
        get => _isAllPortsSelected;
        set
        {
            if (SetProperty(ref _isAllPortsSelected, value))
            {
                if (FilteredPortsView != null)
                {
                    foreach (SimPort port in FilteredPortsView)
                    {
                        port.IsSelected = value;
                    }
                }
            }
        }
    }

    public System.Collections.IEnumerable FilteredSmsMessages =>
        SmsMessages.Where(s =>
            MatchesFilter(s.ReceiverPhone, SmsPhoneFilter) &&
            MatchesFilter(s.PortName, SmsPortFilter) &&
            MatchesFilter(s.Sender, SmsSenderFilter));

    public int TotalPortCount => Ports.Count;
    public int OnlinePortCount => Ports.Count(p => IsActive(p) && !string.IsNullOrWhiteSpace(p.Balance));
    public int OfflinePortCount => Ports.Count - OnlinePortCount;
    public int SmsReceivedCount => SmsMessages.Count;
    public int SmsFailedCount => Ports.Sum(p => p.SmsErrorCount);
    public int TimeoutTotalCount => Ports.Sum(p => p.TimeoutCount);
    public int CooldownPortCount => _portCooldownUntilUtc.Count(kv => kv.Value > DateTime.UtcNow);
    public string TopProblemPort => Ports
        .OrderByDescending(p => p.TimeoutCount + p.SmsErrorCount + p.ReconnectCount)
        .Select(p => $"{p.PortName} ({p.TimeoutCount + p.SmsErrorCount + p.ReconnectCount})")
        .FirstOrDefault() ?? "N/A";

    // #6: Bộ lọc log theo cổng
    private string _logFilter = string.Empty;
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            _logFilter = value;
            OnPropertyChanged(nameof(LogFilter));
            OnPropertyChanged(nameof(FilteredLogs));
            OnPropertyChanged(nameof(FilteredLogCount));
        }
    }

    public System.Collections.IEnumerable FilteredLogs =>
        string.IsNullOrWhiteSpace(_logFilter)
            ? (System.Collections.IEnumerable)SystemLogs
            : SystemLogs.Where(l => MatchesLogFilter(l, _logFilter));

    public int FilteredLogCount =>
        string.IsNullOrWhiteSpace(_logFilter)
            ? SystemLogs.Count
            : SystemLogs.Count(l => MatchesLogFilter(l, _logFilter));

    private static bool MatchesFilter(string value, string filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
               (value ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLogFilter(LogMessage log, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        string normalized = filter.Trim().ToUpperInvariant();
        string message = log.Message ?? string.Empty;
        string level = log.Level ?? string.Empty;

        return normalized switch
        {
            "[IMEI]" => message.Contains("[IMEI", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("IMEI", StringComparison.OrdinalIgnoreCase),
            "[FIREBASE]" => level.Contains("FIREBASE", StringComparison.OrdinalIgnoreCase)
                            || message.Contains("FIREBASE", StringComparison.OrdinalIgnoreCase),
            "[SMS]" => message.Contains("SMS", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("tin nhắn", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("OTP", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("ZALO", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("CMGS", StringComparison.OrdinalIgnoreCase),
            "[USSD]" => message.Contains("USSD", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("TKC", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("số dư", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("CUSD", StringComparison.OrdinalIgnoreCase),
            _ => message.Contains(filter, StringComparison.OrdinalIgnoreCase)
                 || level.Contains(filter, StringComparison.OrdinalIgnoreCase)
        };
    }

    public ISeries[] ConnectionSeries { get; set; }
    public ISeries[] SmsSeries { get; set; }

    [ObservableProperty]
    private bool _isExportExcelDialogOpen;

    public ObservableCollection<ExportColumnItem> ExportColumns { get; } = new();

    // ========== OTP HISTORY ==========
    [ObservableProperty]
    private ObservableCollection<Services.OtpRecord> _otpHistoryList = new();

    private string _otpHistoryFilterPhone = string.Empty;
    public string OtpHistoryFilterPhone
    {
        get => _otpHistoryFilterPhone;
        set { _otpHistoryFilterPhone = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredOtpHistory)); OnPropertyChanged(nameof(FilteredOtpHistoryCount)); }
    }

    private string _otpHistoryFilterSender = string.Empty;
    public string OtpHistoryFilterSender
    {
        get => _otpHistoryFilterSender;
        set { _otpHistoryFilterSender = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredOtpHistory)); OnPropertyChanged(nameof(FilteredOtpHistoryCount)); }
    }

    private string _otpHistoryFilterPort = string.Empty;
    public string OtpHistoryFilterPort
    {
        get => _otpHistoryFilterPort;
        set { _otpHistoryFilterPort = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredOtpHistory)); OnPropertyChanged(nameof(FilteredOtpHistoryCount)); }
    }

    private string _otpHistoryFilterDate = string.Empty;
    public string OtpHistoryFilterDate
    {
        get => _otpHistoryFilterDate;
        set { _otpHistoryFilterDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredOtpHistory)); OnPropertyChanged(nameof(FilteredOtpHistoryCount)); }
    }

    private string _otpHistoryFilterContent = string.Empty;
    public string OtpHistoryFilterContent
    {
        get => _otpHistoryFilterContent;
        set { _otpHistoryFilterContent = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredOtpHistory)); OnPropertyChanged(nameof(FilteredOtpHistoryCount)); }
    }

    public System.Collections.IEnumerable FilteredOtpHistory => OtpHistoryList.Where(r =>
        MatchesFilter(r.SimPhone,  OtpHistoryFilterPhone) &&
        MatchesFilter(r.Sender,    OtpHistoryFilterSender) &&
        MatchesFilter(r.Port,      OtpHistoryFilterPort) &&
        MatchesFilter(r.Timestamp, OtpHistoryFilterDate) &&
        MatchesFilter(r.Content,   OtpHistoryFilterContent));

    public int FilteredOtpHistoryCount => OtpHistoryList.Count(r =>
        MatchesFilter(r.SimPhone,  OtpHistoryFilterPhone) &&
        MatchesFilter(r.Sender,    OtpHistoryFilterSender) &&
        MatchesFilter(r.Port,      OtpHistoryFilterPort) &&
        MatchesFilter(r.Timestamp, OtpHistoryFilterDate) &&
        MatchesFilter(r.Content,   OtpHistoryFilterContent));

    // ========== WEBHOOK RULE DIALOG ==========
    [ObservableProperty]
    private bool _isWebhookDialogOpen;

    [ObservableProperty]
    private Models.WebhookRule _editingWebhookRule = new();

    [ObservableProperty]
    private bool _isEditingExistingWebhookRule;

    // ========== SOUND ALERT TOGGLE ==========
    public bool IsSoundAlertEnabled
    {
        get => SettingsService.Current.EnableSoundAlert;
        set
        {
            if (SettingsService.Current.EnableSoundAlert != value)
            {
                SettingsService.Current.EnableSoundAlert = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public bool IsToastSoundEnabled
    {
        get => SettingsService.Current.EnableToastSound;
        set
        {
            if (SettingsService.Current.EnableToastSound != value)
            {
                SettingsService.Current.EnableToastSound = value;
                SettingsService.SaveSettings(SettingsService.Current);
                OnPropertyChanged();
            }
        }
    }

    public MainViewModel()
    {
        FilteredPortsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Ports);
        FilteredPortsView.Filter = o => 
        {
            if (o is Models.SimPort port)
            {
                return MatchesFilter(port.PortName, PortNameFilter) &&
                       MatchesFilter(port.Imei, ImeiFilter) &&
                       MatchesFilter(port.Serial, SerialFilter) &&
                       MatchesFilter(port.PhoneNumber, PhoneNumberFilter);
            }
            return false;
        };
        
        ((System.ComponentModel.ICollectionViewLiveShaping)FilteredPortsView).IsLiveSorting = false;

        LoadSimCache();
        LoadImeiCache();
        ImportCsvToImeiCache();
        ExportColumns.Add(new ExportColumnItem("STT", "Stt"));
        ExportColumns.Add(new ExportColumnItem("Cổng", "PortName"));
        ExportColumns.Add(new ExportColumnItem("IMEI", "Imei"));
        ExportColumns.Add(new ExportColumnItem("Serial", "Serial"));
        ExportColumns.Add(new ExportColumnItem("SĐT", "PhoneNumber"));
        ExportColumns.Add(new ExportColumnItem("Tài khoản (TKC)", "Balance"));
        ExportColumns.Add(new ExportColumnItem("OTP", "Otp"));
        ExportColumns.Add(new ExportColumnItem("Nội dung tin cuối", "LastMessageContent"));
        ExportColumns.Add(new ExportColumnItem("Ngày tạo", "CreatedAt", false));
        ExportColumns.Add(new ExportColumnItem("Kết nối", "Status"));
        ExportColumns.Add(new ExportColumnItem("Nhà mạng", "NetworkProvider"));
        ExportColumns.Add(new ExportColumnItem("Hạn sử dụng", "ExpiryDate"));

        _modemService = new GsmModemService();
        _imeiManagementService = new Services.ImeiManagementService(_modemService, (msg, level) => AddLog(msg, level));
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        _modemService.CallIncoming += ModemService_CallIncoming;
        _modemService.CallEnded += ModemService_CallEnded;
        
        _speechToTextService = new SpeechToTextService();
        _speechToTextService.LogMessage += (s, msg) => AddLog(msg);
        _ = _speechToTextService.InitializeAsync();
        
        
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
        SmsMessages.CollectionChanged += (s, e) =>
        {
            UpdateDashboard();
            OnPropertyChanged(nameof(FilteredSmsMessages));
        };

        // Khởi động Firebase Service chạy ngầm
        _firebaseService = new FirebaseService(this);
        _firebaseService.Start();

        // Khởi động Proxy Manager
        ProxyManager = new ProxyManagerService();
        ProxyManager.Start();

        // API Server (port 8080)
        if (SettingsService.Current.EnableApiServer)
        {
            _apiServerService = new ApiServerService(this);
            _apiServerService.Start(SettingsService.Current.ApiServerPort);
            AddLog($"[API] REST API server đang chạy tại http://localhost:{SettingsService.Current.ApiServerPort}/api/");
        }

        var lifetimeToken = _lifetimeCts.Token;

        // #7: Tự động làm mới số dư mỗi 30 phút
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), lifetimeToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var activePorts = GetPortsSnapshot().Where(IsActive).ToList();
                if (activePorts.Count > 0)
                {
                    AddLog("[HỆ THỐNG] Tự động kiểm tra số dư định kỳ (30 phút/lần)...");
                    foreach (var p in activePorts)
                    {
                        if (lifetimeToken.IsCancellationRequested) break;
                        string ussdCode = GetUssdCodeForProvider(p.NetworkProvider);
                        await SendUssdThrottledAsync(p.PortName, ussdCode, "Làm mới số dư tự động", maxAttempts: 1);
                        await Task.Delay(2000, lifetimeToken);
                    }
                }
            }
        }, lifetimeToken);

        // Tự động đo cường độ sóng (CSQ) mỗi 15 giây
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), lifetimeToken); }
                catch (OperationCanceledException) { break; }

                var activePorts = GetPortsSnapshot().Where(IsActive).ToList();
                foreach (var p in activePorts)
                {
                    if (lifetimeToken.IsCancellationRequested) break;
                    
                    string result = await _modemService.SendCommandAsync(p.PortName, "AT+CSQ", 5000, silent: true);
                    var match = Regex.Match(result, @"\+CSQ:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int csq))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            p.SignalStrength = csq >= 99 ? 0 : (int)((csq / 31.0) * 100);
                        });
                    }
                }
            }
        }, lifetimeToken);

        // Tự động quét vét SMS (Periodic SMS Sweep)
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(3), lifetimeToken); }
                catch (OperationCanceledException) { break; }

                var portsCopy = GetPortsSnapshot();
                foreach (var p in portsCopy)
                {
                    if (lifetimeToken.IsCancellationRequested) break;
                    
                    // Chỉ quét khi cổng đang rảnh (không chạy SMS gửi) và đang hoạt động tốt
                    if (p.Status == SimStatus.Active && !SmsInProgressPorts.ContainsKey(p.PortName))
                    {
                        try
                        {
                            await _modemService.SweepUnreadSmsAsync(p.PortName);
                            Application.Current.Dispatcher.Invoke(() => p.LastSweepTime = DateTime.Now.ToString("HH:mm:ss"));
                            await Task.Delay(1000, lifetimeToken); // Tránh spam quá nhanh trên nhiều cổng
                        }
                        catch { }
                    }
                }
            }
        }, lifetimeToken);

        // Tự động kiểm tra phát hiện rút SIM (Hot-plug Auto-Airplane)
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), lifetimeToken); }
                catch (OperationCanceledException) { break; }

                var portsCopy = GetPortsSnapshot();
                foreach (var p in portsCopy)
                {
                    if (lifetimeToken.IsCancellationRequested) break;
                    
                    if (p.Status == SimStatus.Active || p.Status == SimStatus.NoResponse || p.Status == SimStatus.SecurityBlocked)
                    {
                        // Kiểm tra trạng thái SIM qua cổng COM
                        string pinStatus = await _modemService.SendCommandAsync(p.PortName, "AT+CPIN?", 3000, silent: true);
                        
                        bool isSystemError = pinStatus.Contains("Timeout") || pinStatus.Contains("ERROR: Another command is already in progress");
                        
                        if (!isSystemError && (pinStatus.Contains("ERROR") || pinStatus.Contains("+CME ERROR: 10")))
                        {
                            // Phát hiện SIM bị rút ra, lập tức khóa sóng
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                AddLog($"[{p.PortName}] Phát hiện thẻ SIM bị rút ra. Tự động chuyển cổng về chế độ Tắt sóng (AT+CFUN=4)...");
                                p.Status = SimStatus.Connecting;
                                p.PhoneNumber = string.Empty;
                                p.Serial = string.Empty;
                                p.Imei = string.Empty; // Xoá IMEI trên UI để lần cắm sau tự đọc lại IMEI thật
                                UpdateDashboard();
                            });
                            
                            // Gọi vòng lặp chờ SIM (trong vòng lặp này nó sẽ liên tục gửi AT+CFUN=4)
                            _modemService.StartHotplugWaitLoop(p.PortName);
                        }
                    }
                }
            }
        }, lifetimeToken);

        // Tự động phục hồi (Watchdog)
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), lifetimeToken); }
                catch (OperationCanceledException) { break; }

                if (!IsWatchdogEnabled) continue;

                var portsCopy = GetPortsSnapshot();
                foreach (var p in portsCopy)
                {
                    if (lifetimeToken.IsCancellationRequested) break;
                    if (p.Status == SimStatus.NoResponse || p.Status == "Offline" || p.Status == "Error")
                    {
                        // Bỏ qua watchdog reset đối với các lỗi bảo mật nghiêm trọng
                        if (p.LastError == "Sai IMEI" || p.LastError == "Lỗi đọc SIM CCID" || p.LastError == "Không tắt được sóng trước khi ghi IMEI")
                        {
                            continue;
                        }

                        AddLog($"[WATCHDOG] Cổng {p.PortName} mất kết nối. Tự động gửi lệnh phục hồi (AT+CFUN=1,1)...", "WARN");
                        _ = _modemService.SendCommandAsync(p.PortName, "AT+CFUN=1,1", silent: true);
                    }
                }
            }
        }, lifetimeToken);
    }

    private void UpdateSmsReceiverPhone(string portName, string newPhoneNumber)
    {
        if (string.IsNullOrWhiteSpace(newPhoneNumber)) return;
        
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var msg in SmsMessages)
            {
                if (msg.PortName == portName && string.IsNullOrWhiteSpace(msg.ReceiverPhone))
                {
                    msg.ReceiverPhone = newPhoneNumber;
                }
            }
        });
    }

    private void UpdateDashboard()
    {
        int activeCount = Ports.Count(p => IsActive(p) && !string.IsNullOrWhiteSpace(p.Balance));
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
        OnPropertyChanged(nameof(AtCommandPortOptions));
        OnPropertyChanged(nameof(CallManagerPortOptions));
        OnPropertyChanged(nameof(TotalPortCount));
        OnPropertyChanged(nameof(OnlinePortCount));
        OnPropertyChanged(nameof(OfflinePortCount));
        OnPropertyChanged(nameof(SmsReceivedCount));
        OnPropertyChanged(nameof(SmsFailedCount));
        OnPropertyChanged(nameof(TimeoutTotalCount));
        OnPropertyChanged(nameof(CooldownPortCount));
        OnPropertyChanged(nameof(TopProblemPort));
    }

    public void UpsertCommandQueue(string commandId, string portId, string type, string recipient, string content, string status, string? result = null, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;

        void Update()
        {
            var item = CommandQueue.FirstOrDefault(x => x.CommandId == commandId);
            if (item == null)
            {
                item = new CommandQueueItem { CommandId = commandId };
                CommandQueue.Insert(0, item);
            }

            item.PortId = portId;
            item.Type = type;
            item.Recipient = recipient;
            item.Content = content;
            item.Status = status;
            item.Result = result ?? item.Result;
            item.Error = error ?? string.Empty;
            item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");

            while (CommandQueue.Count > 200)
            {
                CommandQueue.RemoveAt(CommandQueue.Count - 1);
            }
            UpdateCommandCounts();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.InvokeAsync(Update);
    }

    private static bool IsActive(SimPort port) => port.Status == SimStatus.Active;

    private SimPort? FindPort(string portName)
    {
        return GetPortsSnapshot().FirstOrDefault(p =>
            p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
    }

    private void RecordPortError(string portName, string error)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Update()
        {
            var port = Ports.FirstOrDefault(p =>
                p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port == null) return;

            string cleanError = error ?? string.Empty;
            if (cleanError.Contains("AT+"))
            {
                // Ẩn các dòng chứa lệnh AT+ khỏi giao diện để tránh làm người dùng khó hiểu
                cleanError = string.Join(Environment.NewLine, cleanError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Where(line => !line.Trim().StartsWith("AT+")));
            }
            port.LastError = string.IsNullOrWhiteSpace(cleanError) ? "ERROR" : cleanError.Trim();

            if (error != null && error.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                port.TimeoutCount++;
            }
            if (error != null && (error.Contains("SMS", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase)))
            {
                port.SmsErrorCount++;
            }
            UpdateDashboard();
        }

        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.InvokeAsync(Update);
    }

    private void RecordSmsSuccess(string portName)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Update()
        {
            var port = Ports.FirstOrDefault(p =>
                p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port == null) return;

            port.LastSmsSentAt = DateTime.Now.ToString("HH:mm:ss");
            port.LastError = string.Empty;
            UpdateDashboard();
        }

        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.InvokeAsync(Update);
    }

    public List<SimPort> GetPortsSnapshot()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return Ports.ToList();
        }

        return dispatcher.Invoke(() => Ports.ToList());
    }

    public void AddLog(string message, string level = "INFO")
    {
        try 
        {
            lock (_logFileLock)
            {
            string logFile = AppPaths.ForRuntimeFile("system_log.txt");
            // Fix #2: Giới hạn log file tối đa 5MB, tự động xoay vòng
            var fi = new System.IO.FileInfo(logFile);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024) // 5MB
            {
                string archive = AppPaths.ForRuntimeFile($"system_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                System.IO.File.Move(logFile, archive, overwrite: true);

                // Tự động dọn dẹp, chỉ giữ lại 5 file log cũ nhất (khoảng 25MB)
                try
                {
                    var dirInfo = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(logFile) ?? "");
                    var oldLogs = dirInfo.GetFiles("system_log_*.txt")
                                         .OrderByDescending(f => f.CreationTime)
                                         .Skip(5)
                                         .ToList();
                    foreach (var oldLog in oldLogs)
                    {
                        oldLog.Delete();
                    }
                }
                catch { }
            }
            System.IO.File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SystemLogs.Insert(0, new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Message = message });
            if (SystemLogs.Count > 500)
            {
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
            }
            // Cập nhật bộ lọc log sau mỗi lần thêm dòng mới
            OnPropertyChanged(nameof(FilteredLogs));
            OnPropertyChanged(nameof(FilteredLogCount));
        });
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
        var logsToCopy = string.IsNullOrWhiteSpace(_logFilter)
            ? SystemLogs.ToList()
            : SystemLogs.Where(l => MatchesLogFilter(l, _logFilter)).ToList();

        if (logsToCopy.Count == 0) return;

        var builder = new StringBuilder();
        for (int i = logsToCopy.Count - 1; i >= 0; i--)
        {
            builder.AppendLine(FormatLogLine(logsToCopy[i]));
        }

        Clipboard.SetText(builder.ToString().TrimEnd());
        SnackbarMessageQueue.Enqueue(string.IsNullOrWhiteSpace(_logFilter) 
            ? "Đã sao chép toàn bộ log." 
            : $"Đã sao chép {logsToCopy.Count} log đã lọc.");
    }

    private static string FormatLogLine(LogMessage log)
    {
        return $"{log.Time} {log.Level} {log.Message}";
    }

    // ========== OTP HISTORY COMMANDS ==========

    [RelayCommand]
    private void LoadOtpHistory()
    {
        var records = Services.OtpHistoryService.GetRecent(2000); // Lấy tối đa 2000 bản ghi
        OtpHistoryList.Clear();
        foreach (var r in records)
            OtpHistoryList.Add(r);

        OnPropertyChanged(nameof(FilteredOtpHistory));
        OnPropertyChanged(nameof(FilteredOtpHistoryCount));
        SnackbarMessageQueue.Enqueue($"Đã tải {OtpHistoryList.Count} bản ghi lịch sử OTP.");
    }

    [RelayCommand]
    private void ClearOtpHistoryFilter()
    {
        OtpHistoryFilterPhone   = string.Empty;
        OtpHistoryFilterSender  = string.Empty;
        OtpHistoryFilterPort    = string.Empty;
        OtpHistoryFilterDate    = string.Empty;
        OtpHistoryFilterContent = string.Empty;
    }

    [RelayCommand]
    private void ExportOtpHistoryToExcel()
    {
        try
        {
            var filtered = FilteredOtpHistory.Cast<Services.OtpRecord>().ToList();
            if (filtered.Count == 0)
            {
                SnackbarMessageQueue.Enqueue("Không có dữ liệu để xuất.");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Excel Files (*.xlsx)|*.xlsx",
                FileName = $"otp_history_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dlg.ShowDialog() != true) return;

            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var pkg  = new OfficeOpenXml.ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Lịch sử OTP");

            // Header
            ws.Cells[1, 1].Value = "Thời gian";
            ws.Cells[1, 2].Value = "Cổng";
            ws.Cells[1, 3].Value = "SĐT SIM";
            ws.Cells[1, 4].Value = "Sender";
            ws.Cells[1, 5].Value = "OTP";
            ws.Cells[1, 6].Value = "Nội dung";

            using (var range = ws.Cells[1, 1, 1, 6])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(30, 30, 60));
                range.Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            // Data
            for (int i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                ws.Cells[i + 2, 1].Value = r.Timestamp;
                ws.Cells[i + 2, 2].Value = r.Port;
                ws.Cells[i + 2, 3].Value = r.SimPhone;
                ws.Cells[i + 2, 4].Value = r.Sender;
                ws.Cells[i + 2, 5].Value = r.Otp;
                ws.Cells[i + 2, 6].Value = r.Content;
            }

            ws.Cells.AutoFitColumns();
            pkg.SaveAs(new System.IO.FileInfo(dlg.FileName));
            SnackbarMessageQueue.Enqueue($"Đã xuất {filtered.Count} bản ghi OTP ra Excel.");
            AddLog($"Xuất lịch sử OTP: {filtered.Count} bản ghi → {dlg.FileName}", "SUCCESS");
        }
        catch (Exception ex)
        {
            AddLog($"Lỗi xuất Excel lịch sử OTP: {ex.Message}", "ERROR");
            SnackbarMessageQueue.Enqueue("Lỗi khi xuất Excel.");
        }
    }

    [RelayCommand]
    private void CopyOtpFromHistory(Services.OtpRecord? record)
    {
        if (record == null || string.IsNullOrWhiteSpace(record.Otp)) return;
        Clipboard.SetText(record.Otp);
        SnackbarMessageQueue.Enqueue($"Đã sao chép OTP: {record.Otp}");
    }

    // ========== WEBHOOK RULE COMMANDS ==========

    [RelayCommand]
    private void OpenAddWebhookRule()
    {
        EditingWebhookRule = new Models.WebhookRule();
        IsEditingExistingWebhookRule = false;
        IsWebhookDialogOpen = true;
    }

    [RelayCommand]
    private void OpenEditWebhookRule(Models.WebhookRule? rule)
    {
        if (rule == null) return;
        // Clone để chỉnh sửa (tránh thay đổi trực tiếp list)
        EditingWebhookRule = new Models.WebhookRule
        {
            Id           = rule.Id,
            Name         = rule.Name,
            Enabled      = rule.Enabled,
            SenderFilter = rule.SenderFilter,
            WebhookUrl   = rule.WebhookUrl,
            SecretHeader = rule.SecretHeader,
            OtpOnly      = rule.OtpOnly
        };
        IsEditingExistingWebhookRule = true;
        IsWebhookDialogOpen = true;
    }

    [RelayCommand]
    private void SaveWebhookRule()
    {
        if (string.IsNullOrWhiteSpace(EditingWebhookRule.Name) || string.IsNullOrWhiteSpace(EditingWebhookRule.WebhookUrl))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng điền Tên và URL webhook.");
            return;
        }

        var settings = AppSettings;
        if (IsEditingExistingWebhookRule)
        {
            var existing = settings.WebhookRules.FirstOrDefault(r => r.Id == EditingWebhookRule.Id);
            if (existing != null)
            {
                existing.Name         = EditingWebhookRule.Name;
                existing.Enabled      = EditingWebhookRule.Enabled;
                existing.SenderFilter = EditingWebhookRule.SenderFilter;
                existing.WebhookUrl   = EditingWebhookRule.WebhookUrl;
                existing.SecretHeader = EditingWebhookRule.SecretHeader;
                existing.OtpOnly      = EditingWebhookRule.OtpOnly;
            }
        }
        else
        {
            settings.WebhookRules.Add(new Models.WebhookRule
            {
                Id           = EditingWebhookRule.Id,
                Name         = EditingWebhookRule.Name,
                Enabled      = EditingWebhookRule.Enabled,
                SenderFilter = EditingWebhookRule.SenderFilter,
                WebhookUrl   = EditingWebhookRule.WebhookUrl,
                SecretHeader = EditingWebhookRule.SecretHeader,
                OtpOnly      = EditingWebhookRule.OtpOnly
            });
        }

        SettingsService.SaveSettings(settings);
        OnPropertyChanged(nameof(AppSettings));
        IsWebhookDialogOpen = false;
        SnackbarMessageQueue.Enqueue("Đã lưu webhook rule.");
    }

    [RelayCommand]
    private void DeleteWebhookRule(Models.WebhookRule? rule)
    {
        if (rule == null) return;
        AppSettings.WebhookRules.Remove(rule);
        SettingsService.SaveSettings(AppSettings);
        OnPropertyChanged(nameof(AppSettings));
        SnackbarMessageQueue.Enqueue($"Đã xóa rule '{rule.Name}'.");
    }

    [RelayCommand]
    private void CloseWebhookDialog()
    {
        IsWebhookDialogOpen = false;
    }


    [RelayCommand]
    private void BrowseSoundFile(string parameter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV Files (*.wav)|*.wav|All Files (*.*)|*.*",
            Title  = "Chọn file âm thanh .wav"
        };
        if (dlg.ShowDialog() != true) return;

        switch (parameter)
        {
            case "OTP":  AppSettings.SoundOtpPath  = dlg.FileName; break;
            case "SMS":  AppSettings.SoundSmsPath  = dlg.FileName; break;
            case "CALL": AppSettings.SoundCallPath = dlg.FileName; break;
        }
        OnPropertyChanged(nameof(AppSettings));
    }

    [RelayCommand]
    private void TestSoundAlert(string parameter)
    {
        switch (parameter)
        {
            case "OTP":  Services.SoundAlertService.PlayOtp();  break;
            case "SMS":  Services.SoundAlertService.PlaySms();  break;
            case "CALL": Services.SoundAlertService.PlayCall(); break;
        }
    }

    private void InitializeHardware()
    {
        Ports.Clear();
        SmsMessages.Clear();

        StartAutoPortWatcher();
    }

    private void StartAutoPortWatcher()
    {
        var lifetimeToken = _lifetimeCts.Token;
        Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                var availablePorts = _modemService.GetAvailablePorts();
                bool hasChanges = false;
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 1. Kiểm tra thiết bị bị rút ra
                    var removedPorts = Ports.Where(p => !availablePorts.Contains(p.PortName) && p.PortName != "COM_VIRTUAL").ToList();
                    foreach (var p in removedPorts)
                    {
                        Ports.Remove(p);
                        _modemService.Disconnect(p.PortName);
                        AddLog($"[{p.PortName}] Bị rút khỏi máy tính, đã xóa khỏi danh sách.", "WARN");
                        SnackbarMessageQueue.Enqueue($"Đã rút thiết bị: {p.PortName}");
                        hasChanges = true;
                    }
                });

                // 2. Kiểm tra thiết bị mới cắm vào
                var currentPortNames = GetPortsSnapshot()
                    .Select(port => port.PortName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (availablePorts.Any(p => !currentPortNames.Contains(p)))
                {
                    hasChanges = true;
                    _modemService.ConnectAll(115200);
                }

                if (hasChanges)
                {
                    Application.Current.Dispatcher.Invoke(() => UpdateDashboard());
                }

                // 3. Tự động thử lại lấy TKC nếu bị thiếu (do mạng bận hoặc timeout ngầm)
                var portsMissingBalance = GetPortsSnapshot().Where(p => 
                    IsActive(p) && 
                    !string.IsNullOrWhiteSpace(p.NetworkProvider) && 
                    string.IsNullOrWhiteSpace(p.Balance) && 
                    p.PortName != "COM_VIRTUAL").ToList();

                foreach (var port in portsMissingBalance)
                {
                    // Tránh gửi cùng lúc quá nhiều, kiểm tra cooldown 60 giây
                    if (!_lastUssdByPort.TryGetValue(port.PortName, out var lastUssdTime) || (DateTime.UtcNow - lastUssdTime).TotalSeconds > 60)
                    {
                        // Cập nhật ngay lập tức thời gian để các vòng lặp sau không spam queue
                        _lastUssdByPort[port.PortName] = DateTime.UtcNow;
                        _ = SendUssdThrottledAsync(port.PortName, GetUssdCodeForProvider(port.NetworkProvider), "Thử lại lấy TKC bị thiếu", maxAttempts: 1);
                    }
                }

                try
                {
                    await Task.Delay(3000, lifetimeToken); // Quét 3 giây 1 lần
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, lifetimeToken);
    }

    [RelayCommand]
    private async Task RegisterEzComAsync(object targetObj)
    {
        string target = targetObj as string ?? "Selected";
        var targetPorts = target == "All" ? Ports.ToList() : Ports.Where(p => p.IsSelected).ToList();

        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng!");
            return;
        }

        AddLog($"Bắt đầu đăng ký EZ COM cho {targetPorts.Count} cổng...", "INFO");
        SnackbarMessageQueue.Enqueue($"Đang gửi lệnh đăng ký EZ COM cho {targetPorts.Count} cổng...");

        int skipped = 0;
        foreach (var port in targetPorts)
        {
            if (port.Status != SimStatus.Active)
            {
                AddLog($"[{port.PortName}] Bỏ qua vì SIM không ở trạng thái Active (hiện tại: {port.Status}).", "WARN");
                skipped++;
                continue;
            }

            _ = Task.Run(async () =>
            {
                Application.Current.Dispatcher.Invoke(() => port.LastMessageContent = "Đang gửi DK EZ...");
                AddLog($"[{port.PortName}] Đang gửi lệnh DK EZ đến 888...", "INFO");
                string result = await _modemService.SendSmsAsync(port.PortName, "888", "DK EZ");
                if (result.Contains("ERROR") || result.Contains("TIMEOUT"))
                {
                    Application.Current.Dispatcher.Invoke(() => port.LastMessageContent = $"Lỗi gửi DK EZ: {result}");
                    AddLog($"[{port.PortName}] Lỗi gửi DK EZ: {result}", "ERROR");
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => port.LastMessageContent = "Đã gửi DK EZ, chờ 888 phản hồi...");
                    AddLog($"[{port.PortName}] Đã gửi DK EZ thành công, đang chờ phản hồi từ 888...", "SUCCESS");
                }
            });
            await Task.Delay(200);
        }
        
        if (skipped > 0)
        {
            SnackbarMessageQueue.Enqueue($"Đã bỏ qua {skipped} cổng do chưa kết nối xong (Status != Active).");
        }
    }

    [RelayCommand]
    private void ApproveUnknownSim(object obj)
    {
        var targetPorts = Ports.Where(p => p.IsSelected).ToList();
        
        if (obj is SimPort clickedPort && !targetPorts.Contains(clickedPort))
        {
            targetPorts.Add(clickedPort);
        }

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (tick vào ô vuông) để chấp thuận SIM.");
            return;
        }

        int successCount = 0;
        foreach (var port in targetPorts)
        {
            if (port == null) continue;
            
            if (port.Status != SimStatus.SecurityBlocked)
            {
                continue;
            }

            if (string.IsNullOrEmpty(port.Serial))
            {
                AddLog($"[{port.PortName}] Lỗi: Không tìm thấy CCID.", "ERROR");
                continue;
            }

            successCount++;
            string targetImei = gsm.Services.ImeiManagementService.GenerateRandomImei();
            string sourceFile = "manual-approve";

            var newEntry = new gsm.Models.SimBackupEntry
            {
                Ccid = port.Serial,
                Imei = targetImei,
                PhoneNumber = port.PhoneNumber ?? "",
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LicenseKeySuffix = string.Empty,
                KeyMismatch = "false",
                SourceFile = sourceFile
            };
            
            AddNewImeiCacheEntry(newEntry);
            
            AddLog($"[{port.PortName}] Đã chấp thuận thủ công SIM lạ (CCID: {port.Serial}, IMEI mục tiêu: {targetImei}).", "SUCCESS");

            _ = Task.Run(async () =>
            {
                Application.Current.Dispatcher.Invoke(() => 
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang tráng IMEI Fake...";
                });

                string currentImei = NormalizeImei(port.Imei);
                if (string.IsNullOrEmpty(currentImei))
                {
                    currentImei = await _modemService.SendCommandAsync(port.PortName, "AT+CGSN", 10000, silent: true);
                    currentImei = NormalizeImei(currentImei);
                }
                
                var result = await _imeiManagementService.ProcessImeiAsync(
                    port,
                    port.Serial,
                    currentImei,
                    AppSettings,
                    (queryCcid) => { _imeiCache.TryGetValue(queryCcid, out var e); return e; },
                    (newE) => AddNewImeiCacheEntry(newE),
                    (action) => Application.Current.Dispatcher.Invoke(action)
                );
                
                Application.Current.Dispatcher.Invoke(() => 
                {
                    if (result.Status == Services.ImeiProcessStatus.Matched)
                    {
                        port.IsRebooting = false;
                        port.Imei = result.FinalImei;
                        MarkPortActiveAfterInit(port.PortName);
                        _ = _modemService.ReinitializeSettingsAsync(port.PortName);
                    }
                    else if (result.Status == Services.ImeiProcessStatus.Applied)
                    {
                        port.IsRebooting = true;
                        port.Imei = result.FinalImei;
                        port.DeviceName = "Đang áp dụng IMEI, chờ Reset...";
                        port.Status = SimStatus.Connecting;
                        
                        // [FIX] Handle modems with separate USB bridge chips that don't drop USB on AT+CFUN=1,1
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(15000);
                            if (port.IsRebooting)
                            {
                                Application.Current.Dispatcher.Invoke(() => 
                                {
                                    port.IsRebooting = false;
                                    AddLog($"[{port.PortName}] Mạch không tự ngắt USB. Khởi động lại vòng lặp...", "INFO");
                                    _modemService.StartHotplugWaitLoop(port.PortName);
                                });
                            }
                        });
                    }
                    else if (result.Status == Services.ImeiProcessStatus.SecurityBlocked)
                    {
                        port.Status = SimStatus.SecurityBlocked;
                        port.LastError = string.IsNullOrEmpty(result.ErrorMessage) ? gsm.Models.SecurityErrors.WrongImei : result.ErrorMessage;
                        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                        UpdateDashboard();
                    }
                    else
                    {
                        port.Status = SimStatus.NoResponse;
                        port.LastError = "Lỗi xử lý IMEI";
                        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                        UpdateDashboard();
                    }
                });
            });
        }
        
        if (successCount > 0)
        {
            SnackbarMessageQueue.Enqueue($"Đã lưu {successCount} SIM lạ vào kho và đang tráng IMEI...");
        }
        else
        {
            SnackbarMessageQueue.Enqueue("Không có SIM nào bị chặn trong các cổng đã chọn.");
        }
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var targetPorts = Ports.Where(p => p.IsSelected).ToList();
        
        // Nếu không có ô nào được tick (☑), nhưng người dùng đang highlight 1 dòng, thì lấy dòng đó
        if (!targetPorts.Any() && SelectedPort != null)
        {
            targetPorts.Add(SelectedPort);
        }

        if (targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue($"Đang làm mới {targetPorts.Count} thiết bị được chọn...");
            AddLog($"Bắt đầu làm mới {targetPorts.Count} cổng đã chọn...");
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var p in targetPorts)
                {
                    Ports.Remove(p);
                }
            });

            Task.Run(async () =>
            {
                foreach (var p in targetPorts)
                {
                    _modemService.Disconnect(p.PortName);
                }
                await Task.Delay(2000);
                _modemService.ConnectAll(115200);
            });
        }
        else
        {
            SnackbarMessageQueue.Enqueue("Đang làm mới toàn bộ thiết bị...");
            AddLog("Bắt đầu khởi tạo lại toàn bộ thiết bị từ đầu...");
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                Ports.Clear();
            });

            Task.Run(async () =>
            {
                _modemService.DisconnectAll();
                await Task.Delay(2000); 
                _modemService.ConnectAll(115200);
            });
        }
    }

    public async Task RefreshPortAsync(string portName)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var p = Ports.FirstOrDefault(x => x.PortName == portName);
            if (p != null) Ports.Remove(p);
        });

        await Task.Run(async () =>
        {
            _modemService.Disconnect(portName);
            await Task.Delay(2000);
            _modemService.ConnectAll(115200);
        });
        
        Application.Current.Dispatcher.Invoke(() =>
        {
            AddLog($"Đã nhận lệnh làm mới cổng {portName} từ Web.");
            SnackbarMessageQueue.Enqueue($"Đang làm mới thiết bị {portName}...");
        });
    }

    public void RefreshAllPorts()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AddLog("Đã nhận lệnh làm mới TẤT CẢ cổng từ Web.");
            SnackbarMessageQueue.Enqueue("Đang làm mới toàn bộ thiết bị...");
            Ports.Clear();
        });

        Task.Run(async () =>
        {
            _modemService.DisconnectAll();
            await Task.Delay(2000); 
            _modemService.ConnectAll(115200);
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
                if (e.Data.StartsWith("[PARSE_CCID]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+COPS:") || e.Data.StartsWith("+CUSD:") || e.Data.StartsWith("[WAITING_FOR_SIM]") || e.Data.StartsWith("[PARSE_IMEI]") || e.Data.StartsWith("[STATUS_NO_RESPONSE]"))
                {
                    port = new SimPort { PortName = e.PortName, Status = SimStatus.Active, SignalStrength = 0 };
                    port.PhysicalIndex = _modemService.GetAvailablePorts().IndexOf(e.PortName);
                    if (port.PhysicalIndex < 0) port.PhysicalIndex = int.MaxValue;
                    port.ReconnectCount++;
                    
                    int insertIndex = 0;
                    while (insertIndex < Ports.Count && Ports[insertIndex].PhysicalIndex < port.PhysicalIndex)
                    {
                        insertIndex++;
                    }
                    Ports.Insert(insertIndex, port);
                    
                    for (int i = 0; i < Ports.Count; i++)
                    {
                        Ports[i].Stt = i + 1;
                    }
                }
                else
                {
                    return;
                }
            }

            if (e.Data.StartsWith("[WAITING_FOR_SIM]"))
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                port.PhoneNumber = string.Empty;
                port.Imei = string.Empty;
                port.Serial = string.Empty;
                port.NetworkProvider = string.Empty;
                port.Balance = string.Empty;
                port.ExpiryDate = string.Empty;
                port.Otp = string.Empty;
                port.LastMessageContent = string.Empty;
                port.Sender = string.Empty;
                port.SignalStrength = 0;
            }
            else if (e.Data.Contains("+CSQ:"))
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
                    port.LastUssdResult = ussdContent;
                    port.UpdateDisplayResult(CommandPanelTab);
                    System.IO.File.AppendAllText("ussd_debug.txt", $"[{DateTime.Now}] [{e.PortName}] {ussdContent}\n");

                    // Thử match đầu số Viettel (032-039, 086, 096, 097, 098) và Vinaphone (081-085, 088, 091, 094)
                    var phoneMatch = Regex.Match(ussdContent, @"(?:84|0)(3[2-9]|8[1-9]|9[1-9])\d{7}");
                    if (!phoneMatch.Success)
                    {
                        // Fallback: bắt bất kỳ số 9-10 chữ số bắt đầu bằng 0 hoặc 84
                        phoneMatch = Regex.Match(ussdContent, @"(?:84|0)([3-9][0-9]{8})");
                    }
                    if (!phoneMatch.Success)
                    {
                        // Fallback cuối: 9 chữ số đơn thuần
                        phoneMatch = Regex.Match(ussdContent, @"([345789][0-9]{8})");
                    }

                    if (phoneMatch.Success)
                    {
                        string foundNumber = phoneMatch.Value;
                        if (foundNumber.StartsWith("84")) foundNumber = "0" + foundNumber.Substring(2);
                        else if (!foundNumber.StartsWith("0")) foundNumber = "0" + foundNumber;

                        port.PhoneNumber = foundNumber;
                        UpdateSmsReceiverPhone(port.PortName, foundNumber);
                        string networkLabel = string.IsNullOrWhiteSpace(port.NetworkProvider) ? "UNKNOWN" : port.NetworkProvider;
                        AddLog($"[{e.PortName}] SĐT chuẩn: {foundNumber} ({networkLabel})", "SUCCESS");

                        if (!string.IsNullOrWhiteSpace(port.Serial))
                        {
                            _simCache[port.Serial] = foundNumber;
                            SaveSimCache();

                            if (_imeiCache.TryGetValue(port.Serial, out var entry))
                            {
                                if (entry.PhoneNumber != foundNumber)
                                {
                                    entry.PhoneNumber = foundNumber;
                                    SaveImeiCache();
                                }
                            }
                        }
                    }
                    
                    // Sửa lỗi Parse nhầm "1đ" từ tin nhắn báo không đủ tiền hoặc quảng cáo cước phí
                    // Cập nhật hỗ trợ TKG (Tài Khoản Gốc) của Viettel
                    var strictMatch = Regex.Match(ussdContent, @"(?:TK\s*goc|TKG|TK\s*chinh|TKC|Tai khoan chinh|Tài khoản chính|Tai khoan|Tài khoản|So du|Số dư|TK)[^\d]*?(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)", RegexOptions.IgnoreCase);
                    if (strictMatch.Success) 
                    {
                        port.Balance = strictMatch.Groups[1].Value + " " + strictMatch.Groups[2].Value;
                    }
                    else
                    {
                        // Fallback nếu nhà mạng trả về format lạ, nhưng phải tránh các từ khóa rác và tránh cước phí (vd: 1000d/ngay)
                        if (!Regex.IsMatch(ussdContent, @"khong du|chua du|cuoc|uu dai|tang|gia|khong lo|ho tro|phi|dang ky", RegexOptions.IgnoreCase))
                        {
                            var fallback = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)(?!/)", RegexOptions.IgnoreCase);
                            if (fallback.Success) port.Balance = fallback.Groups[1].Value + " " + fallback.Groups[2].Value;
                        }
                    }

                    var expiryMatch = Regex.Match(ussdContent, @"\b(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})\b");
                    if (expiryMatch.Success) port.ExpiryDate = expiryMatch.Groups[1].Value;

                    UpdateDashboard(); // Refresh online/offline count when Balance is updated

                    // SnackbarMessageQueue.Enqueue($"[{e.PortName}] USSD: {ussdContent}");
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
                    
                    _ = Task.Run(async () => 
                    {
                        // Theo yêu cầu của người dùng: Tất cả các nhà mạng (Viettel, Vina, Mobi, Vietnamobile...) 
                        // đều sẽ dùng chung 1 lệnh *101# để vừa lấy SĐT vừa lấy Số dư (TKC).
                        if (string.IsNullOrWhiteSpace(port.PhoneNumber) || string.IsNullOrWhiteSpace(port.Balance))
                        {
                            await SendUssdThrottledAsync(port.PortName, "*101#", "Tự động lấy SĐT & TKC", maxAttempts: 99999);
                            await Task.Delay(2000); // Đợi mạng xử lý xong lệnh trước
                        }

                        // Tự động chuyển hướng cuộc gọi nếu tính năng được bật
                        if (AppSettings != null && AppSettings.EnableAutoCallForwarding && !string.IsNullOrWhiteSpace(AppSettings.ForwardPhoneNumber))
                        {
                            string randomFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                            if (!string.IsNullOrEmpty(randomFwd))
                            {
                                AddLog($"[{port.PortName}] Đang thiết lập tự động chuyển hướng đến {randomFwd}...");
                                string ccfcResult = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,1,\"{randomFwd}\",129", timeoutMs: 8000);
                                if (ccfcResult.Contains("OK"))
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        port.ForwardCount++;
                                        port.ForwardedTo = randomFwd; // #4: Lưu số đang chưa hướng để hiển thị lên bảng
                                    });
                                    AddLog($"[{port.PortName}] Chuyển hướng thành công → {randomFwd} (Tổng: {port.ForwardCount})", "SUCCESS");
                                }
                            }
                        }
                    });
                }
            }
            else if (e.Data.StartsWith("[PARSE_IMEI]"))
            {
                var match = Regex.Match(e.Data, @"\b(\d{14,17})\b");
                if (match.Success) port.Imei = match.Groups[1].Value;
            }
            else if (e.Data.StartsWith("[PARSE_CCID]"))
            {
                var match = Regex.Match(e.Data, @"\b([A-Za-z0-9]{18,22})\b");
                if (match.Success)
                {
                    port.Status = SimStatus.Active;
                    if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug).")
                    {
                        port.DeviceName = "Đã nhận SIM, đang khởi tạo...";
                    }

                    string ccid = NormalizeCcid(match.Groups[1].Value);
                    port.Serial = ccid;
                    if (_simCache.TryGetValue(ccid, out var cachedPhone))
                    {
                        port.PhoneNumber = cachedPhone;
                        UpdateSmsReceiverPhone(e.PortName, cachedPhone);
                        AddLog($"[{e.PortName}] Đã nạp SĐT từ cache: {cachedPhone}", "SUCCESS");
                    }

                    AddLog($"[{e.PortName}] [IMEI_MODE] Restore={AppSettings.EnableImeiRestore} BlockNew={AppSettings.BlockUnknownSims}");

                    // Thực hiện kiểm tra và khôi phục IMEI bất đồng bộ để tránh treo UI thread
                    _ = Task.Run(async () =>
                    {
                        string currentImei = NormalizeImei(port.Imei);
                        if (string.IsNullOrEmpty(currentImei))
                        {
                            string imeiResp = await _modemService.SendCommandAsync(port.PortName, "AT+CGSN", 30000, silent: true);
                            if (!imeiResp.Contains("ERROR"))
                            {
                                currentImei = NormalizeImei(imeiResp);
                                Application.Current.Dispatcher.Invoke(() => port.Imei = currentImei);
                            }
                        }

                        if (string.IsNullOrEmpty(currentImei))
                        {
                            AddLog($"[{e.PortName}] Không lấy được IMEI phần cứng để so sánh.", "WARNING");
                        }
                        else
                        {
                            var result = await _imeiManagementService.ProcessImeiAsync(
                                port,
                                ccid,
                                currentImei,
                                AppSettings,
                                (queryCcid) => 
                                {
                                    _imeiCache.TryGetValue(queryCcid, out var entry);
                                    return entry;
                                },
                                (newEntry) => AddNewImeiCacheEntry(newEntry),
                                (action) => Application.Current.Dispatcher.Invoke(action)
                            );

                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                if (result.Status == Services.ImeiProcessStatus.Matched)
                                {
                                    port.IsRebooting = false;
                                    port.Imei = result.FinalImei;
                                    MarkPortActiveAfterInit(e.PortName);
                                    // Bắt buộc khởi tạo lại cài đặt (AT+CMGF=1, CSCS...) 
                                    // vì nếu vừa chạy CFUN=1,1 xong modem sẽ mất hết cài đặt tạm thời.
                                    _ = _modemService.ReinitializeSettingsAsync(port.PortName);
                                }
                                else if (result.Status == Services.ImeiProcessStatus.Applied)
                                {
                                    port.IsRebooting = true;
                                    port.Imei = result.FinalImei;
                                    port.DeviceName = "Đang áp dụng IMEI, chờ Reset...";
                                    port.Status = SimStatus.Connecting;
                                    
                                    // [FIX] Handle modems with separate USB bridge chips that don't drop USB on AT+CFUN=1,1
                                    _ = Task.Run(async () =>
                                    {
                                        await Task.Delay(15000);
                                        if (port.IsRebooting)
                                        {
                                            Application.Current.Dispatcher.Invoke(() => 
                                            {
                                                port.IsRebooting = false;
                                                AddLog($"[{port.PortName}] Mạch không tự ngắt USB. Khởi động lại vòng lặp...", "INFO");
                                                _modemService.StartHotplugWaitLoop(port.PortName);
                                            });
                                        }
                                    });
                                }
                                else if (result.Status == Services.ImeiProcessStatus.SecurityBlocked)
                                {
                                    UpdateImeiCacheEntry(port.Serial, entry => entry.PhoneNumber = "Unknown");
                                    port.Status = SimStatus.SecurityBlocked;
                                    port.LastError = string.IsNullOrEmpty(result.ErrorMessage) ? SecurityErrors.WrongImei : result.ErrorMessage;
                                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                                    UpdateDashboard();
                                }
                                else
                                {
                                    port.Status = SimStatus.NoResponse;
                                    port.LastError = "Lỗi xử lý IMEI";
                                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                                    UpdateDashboard();
                                }
                            });
                        }
                    });
                }
                else
                {
                    AddLog($"[{e.PortName}] Không đọc được CCID hợp lệ để đối chiếu. Hủy kết nối để tránh lộ IMEI.", "ERROR");
                    _ = Task.Run(() =>
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            UpdateImeiCacheEntry(port.Serial, entry => entry.PhoneNumber = "Unknown");
                            port.Status = SimStatus.SecurityBlocked;
                            port.LastError = SecurityErrors.ReadCcidFailed;
                            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                            UpdateDashboard();
                        });
                        _modemService.Disconnect(e.PortName);
                    });
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
                    UpdateSmsReceiverPhone(e.PortName, rawNumber);
                    if (!string.IsNullOrWhiteSpace(port.Serial))
                    {
                        _simCache[port.Serial] = rawNumber;
                        SaveSimCache();

                        if (_imeiCache.TryGetValue(port.Serial, out var entry))
                        {
                            if (entry.PhoneNumber != rawNumber)
                            {
                                entry.PhoneNumber = rawNumber;
                                SaveImeiCache();
                            }
                        }
                    }
                }
            }
            else if (e.Data == "[STATUS_ACTIVE]")
            {
                port.Status = SimStatus.Active;
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
                foreach (var sms in SmsMessages.Where(s => s.PortName == e.PortName)) sms.Status = SimStatus.Active;
                
                // Xoá lỗi cũ trên Firebase (nếu có) khi cổng kết nối thành công
                _ = gsm.Services.FirebaseService.ClearWebStateAsync(e.PortName);
            }
            else if (e.Data == "[STATUS_NO_RESPONSE]")
            {
                port.Status = SimStatus.NoResponse;
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
            }
        });
    }

    private void MarkPortActiveAfterInit(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        port.Status = SimStatus.Active;
        port.TimeoutCount = 0;
        port.SmsErrorCount = 0;
        port.ReconnectCount = 0;
        port.LastError = string.Empty;
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        UpdateDashboard();

        foreach (var sms in SmsMessages.Where(s => s.PortName == portName))
        {
            sms.Status = SimStatus.Active;
        }

        _ = gsm.Services.FirebaseService.ClearWebStateAsync(portName);
    }

    private void ModemService_PortDisconnected(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port != null)
            {
                if (port.IsRebooting)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang khởi động lại mạch...";
                    AddLog($"[{e.PortName}] Đang khởi động lại mạch...", "INFO");
                }
                else
                {
                    Ports.Remove(port);
                    UpdateDashboard();
                    AddLog($"[{e.PortName}] {e.Data}", "ERROR");
                    SnackbarMessageQueue.Enqueue($"Cổng {e.PortName} bị ngắt kết nối!");
                }
            }
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
                var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                string senderPhone = "UNKNOWN";
                string extractedOtp = "N/A";
                string cleanContent = e.Data;

                // Nếu quá trình đọc tin nhắn gặp lỗi (VD: Lỗi Timeout Semaphore do đang kẹt gửi SMS)
                if (cleanContent.StartsWith("ERROR:"))
                {
                    AddLog($"[{e.PortName}] LỖI đọc tin nhắn: {cleanContent}. Đang bỏ qua và không xóa để tránh mất OTP.", "WARN");
                    return;
                }

                // 1. Tìm người gửi (Sender)
                var pduMatch = Regex.Match(e.Data, @"\+CMGR:\s*\d+,,(\d+)\r?\n([0-9A-Fa-f]+)");
                var senderMatch = Regex.Match(e.Data, @"\+CMGR:\s*""[^""]+"",""([^""]+)""");
                int concatRef = 0, concatTotal = 0, concatSeq = 0;
                if (pduMatch.Success)
                {
                    string pduHex = pduMatch.Groups[2].Value.Trim();
                    cleanContent = DecodePdu(pduHex, out senderPhone, out concatRef, out concatTotal, out concatSeq);
                    // Loại bỏ các ký tự thừa
                    cleanContent = cleanContent.Replace("\r", " ").Replace("\n", " ").Trim();
                    cleanContent = Regex.Replace(cleanContent, @"\s+", " ");
                }
                else if (senderMatch.Success)
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

                // 1b. Tin nhắn dài bị chia phần (concatenated SMS, theo UDH chuẩn 3GPP).
                // Chỉ xử lý tiếp khi đã gom ĐỦ tất cả các phần; nếu thiếu phần thì lưu vào buffer,
                // xóa tin ở SIM (đã đọc xong, tránh đầy bộ nhớ) và dừng lại chờ phần tiếp theo.
                if (concatTotal > 1)
                {
                    bool isComplete = TryBufferConcatenatedSms(e.PortName, senderPhone, concatRef, concatTotal, concatSeq, cleanContent, out string assembledContent);
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }

                    if (!isComplete)
                    {
                        AddLog($"[{e.PortName}] Đã nhận phần {concatSeq}/{concatTotal} của tin nhắn dài từ {senderPhone}, đang chờ ghép đủ...", "INFO");
                        return;
                    }

                    AddLog($"[{e.PortName}] Đã ghép đủ {concatTotal} phần tin nhắn dài từ {senderPhone}.", "INFO");
                    cleanContent = assembledContent;
                }

                // Tự động kiểm tra TKC khi nhận thông báo trừ tiền từ tổng đài:
                // 574848 = Vinaphone báo trừ tiền Zalo | 8068 = Viettel báo trừ tiền Zalo
                if (senderPhone == "574848" || senderPhone == "8068")
                {
                    AddLog($"[{e.PortName}] Phát hiện thông báo trừ tiền từ {senderPhone}, tự động cập nhật lại số dư...");
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(2000); // Đợi 2s cho hệ thống mạng ổn định
                        await CheckBalanceForPortAsync(e.PortName);
                    });
                }

                string cleanContentLower = cleanContent.ToLowerInvariant();

                // KIỂM TRA LỖI ZALO / HẾT TIỀN TRƯỚC KHI CHẶN SPAM
                bool isZaloError = false;
                if (cleanContentLower.Contains("sai dau so") || cleanContentLower.Contains("sai cú pháp") || cleanContentLower.Contains("sai cu phap"))
                {
                    AddLog($"[{e.PortName}] LỖI ZALO: Hệ thống Firebase đẩy lệnh gửi sai đầu số dịch vụ (Ví dụ: Zalo yêu cầu gửi 7539 nhưng lại gửi 8500)! Vui lòng sửa mã nguồn trên Web/Firebase.", "ERROR");
                    _ = gsm.Services.FirebaseService.SendErrorToWebAsync(e.PortName, "⚠️ Chọn sai đầu số rồi kìa");
                    isZaloError = true;
                }
                else if (cleanContentLower.Contains("khong thuc hien yeu cau") || cleanContentLower.Contains("không thực hiện yêu cầu"))
                {
                    AddLog($"[{e.PortName}] LỖI ZALO: SĐT đang không có yêu cầu mã xác thực Zalo.", "ERROR");
                    _ = gsm.Services.FirebaseService.SendErrorToWebAsync(e.PortName, "⚠️ SĐT đang không yêu cầu mã");
                    isZaloError = true;
                }
                else if (cleanContentLower.Contains("khong du tien") || cleanContentLower.Contains("không đủ tiền"))
                {
                    // Kiểm tra số dư thực tế trước khi kết luận "Hết tiền" (tránh false positive từ nhà mạng)
                    if (port != null && !string.IsNullOrWhiteSpace(port.Balance))
                    {
                        // Tìm số trong chuỗi Balance (VD: "123.456đ" -> 123456)
                        var balanceNum = System.Text.RegularExpressions.Regex.Replace(port.Balance, @"[^\d]", "");
                        if (int.TryParse(balanceNum, out var balanceValue) && balanceValue > 500)
                        {
                            AddLog($"[{e.PortName}] ⚠️ Nhà mạng báo hết tiền nhưng số dư vẫn còn ({port.Balance}). Đây có thể là lỗi tạm thời.", "WARNING");
                            // Không gửi lỗi "Hết tiền" lên web, chỉ ghi log
                            isZaloError = true;
                        }
                        else
                        {
                            AddLog($"[{e.PortName}] LỖI SIM: Tài khoản không đủ tiền để gửi SMS! Số dư: {port.Balance}", "ERROR");
                            _ = gsm.Services.FirebaseService.SendErrorToWebAsync(e.PortName, "⚠️ Hết tiền");
                            isZaloError = true;
                        }
                    }
                    else
                    {
                        // Nếu không biết số dư thì thực hiện kiểm tra số dư trước
                        AddLog($"[{e.PortName}] Nhà mạng báo hết tiền. Đang kiểm tra số dư thực tế...", "WARNING");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            await CheckBalanceForPortAsync(e.PortName);
                        });
                        isZaloError = true;
                    }
                }

                if (isZaloError)
                {
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                    return;
                }

                // Tự động xác nhận đăng ký ezCom từ tổng đài
                var ezMatch = Regex.Match(cleanContentLower, @"soan tin\s+(ez\s*\d+)", RegexOptions.IgnoreCase);
                if (ezMatch.Success)
                {
                    string confirmMsg = ezMatch.Groups[1].Value.ToUpper();
                    if (port != null) port.LastMessageContent = $"Nhận mã {confirmMsg}. Đang xác nhận...";
                    AddLog($"[{e.PortName}] Nhận yêu cầu xác nhận ezCom. Đang tự động gửi: {confirmMsg} đến 888", "INFO");
                    _ = Task.Run(async () =>
                    {
                        string result = await _modemService.SendSmsAsync(e.PortName, "888", confirmMsg);
                        if (result.Contains("ERROR") || result.Contains("TIMEOUT"))
                        {
                            Application.Current.Dispatcher.Invoke(() => {
                                if (port != null) port.LastMessageContent = $"Lỗi xác nhận EZ: {result}";
                            });
                            AddLog($"[{e.PortName}] Lỗi gửi xác nhận EZ: {result}", "ERROR");
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() => {
                                if (port != null) port.LastMessageContent = "Đã xác nhận EZ! Chờ KQ từ 888...";
                            });
                            AddLog($"[{e.PortName}] Đã xác nhận EZ thành công!", "SUCCESS");
                        }
                    });
                    
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                    return;
                }

                // LUÔN CHẶN cảnh báo ezCom bất kể cài đặt Nhận tất cả hay không
                if (cleanContentLower.Contains("thue bao ezcom chi duoc") || cleanContentLower.Contains("dich vu vinaphone khac"))
                {
                    AddLog($"[{e.PortName}] Đã chặn tin nhắn hệ thống ezCom.");
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                    return;
                }

                bool receiveAll = SettingsService.Current.ReceiveAllSms;

                if (!receiveAll)
                {
                    // Chặn tin nhắn rác từ nhà mạng / tổng đài hệ thống
                    // isTopUpSender: sender là tổng đài nhà mạng Viettel/Vinaphone (không bao giờ là OTP thực)
                    bool isTopUpSender = senderPhone == "8068"    // Viettel báo trừ tiền
                                      || senderPhone == "900"
                                      || senderPhone == "49515355"
                                      || senderPhone == "57515253"
                                      || senderPhone.StartsWith("VTT",      StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VNP",      StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VNPT",     StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VIETTEL",  StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VINAPHONE",StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("MOBIFONE", StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VIETNAMOBILE", StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("GMOBILE",  StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("WINTEL",   StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("ITELECOM", StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("ITEL",     StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("SKY",      StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("VNSKY",    StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("LOCAL",    StringComparison.OrdinalIgnoreCase)
                                      || senderPhone.StartsWith("FPT",      StringComparison.OrdinalIgnoreCase);

                    // isTopUpContent: nội dung mang dấu hiệu nạp tiền / cập nhật số dư
                    bool isTopUpContent = cleanContentLower.Contains("da duoc nap")
                                       || cleanContentLower.Contains("tai khoan cua quy khach")
                                       || cleanContentLower.Contains("nap tien thanh cong")
                                       || (cleanContentLower.Contains("so du hien tai") && !cleanContentLower.Contains("zalo"));

                    bool isSpamContent = cleanContentLower.Contains("khoan airtime")
                                      || cleanContentLower.Contains("ong su dung het")
                                      || cleanContentLower.Contains("ng su dung het")
                                      || cleanContentLower.Contains("chinh sach tai")
                                      || cleanContentLower.Contains("tu choi nhan loi moi");

                    if (isTopUpSender || isTopUpContent || isSpamContent)
                    {
                        AddLog($"[{e.PortName}] Đã chặn tin nhắn hệ thống/rác từ {senderPhone}");
                        
                        // Nếu là thông báo nạp tiền → tự động cập nhật lại TKC
                        if (isTopUpContent)
                        {
                            AddLog($"[{e.PortName}] Phát hiện tin nhắn nạp thẻ, tự động cập nhật lại số dư...");
                            _ = Task.Run(async () => 
                            {
                                await Task.Delay(2000);
                                await CheckBalanceForPortAsync(e.PortName);
                            });
                        }

                        if (!string.IsNullOrEmpty(e.MsgIndex))
                            await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                        return;
                    }

                    // --- WHITELIST / BLACKLIST ---
                var settings = SettingsService.Current;

                if (settings.EnableSenderBlacklist && !string.IsNullOrWhiteSpace(settings.SenderBlacklist))
                {
                    var blacklist = settings.SenderBlacklist
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToArray();

                    bool isBlocked = blacklist.Any(b =>
                        senderPhone.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isBlocked)
                    {
                        AddLog($"[{e.PortName}] Đã chặn (Blacklist): {senderPhone}", "WARN");
                        if (!string.IsNullOrEmpty(e.MsgIndex))
                            await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                        return;
                    }
                }

                if (settings.EnableSenderWhitelist && !string.IsNullOrWhiteSpace(settings.SenderWhitelist))
                {
                    var whitelist = settings.SenderWhitelist
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToArray();

                    bool isAllowed = whitelist.Any(w =>
                        senderPhone.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!isAllowed)
                    {
                        AddLog($"[{e.PortName}] Bỏ qua (không trong Whitelist): {senderPhone}");
                        if (!string.IsNullOrEmpty(e.MsgIndex))
                            await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                        return;
                    }
                }
                // --- END WHITELIST / BLACKLIST ---
                    bool isZalo = cleanContent.IndexOf("Zalo", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                  senderPhone.IndexOf("Zalo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  senderPhone.Contains("8500") || senderPhone.Contains("7539");
                    bool isWhatsApp = cleanContent.IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      senderPhone.IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      senderPhone.IndexOf("WA", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isTelegram = cleanContent.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      senderPhone.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!isZalo && !isWhatsApp && !isTelegram)
                    {
                        AddLog($"[{e.PortName}] Đã chặn và xóa tin nhắn không hợp lệ từ {senderPhone}");
                        if (!string.IsNullOrEmpty(e.MsgIndex))
                        {
                            await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                        }
                        return;
                    }
                }

                // 2. Tìm OTP
                extractedOtp = ExtractOtp(cleanContent);

                // 3. Tìm cổng tương ứng để lấy thông tin SIM (SĐT, Nhà mạng)
                string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

                if (extractedOtp == "N/A" && TryAppendToRecentMultipartSms(e.PortName, senderPhone, cleanContent, port, receiveAll))
                {
                    AddLog($"[{e.PortName}] Da ghep doan SMS tiep theo tu {senderPhone} vao tin truoc.", "INFO");
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                    return;
                }

                if (receiveAll || extractedOtp != "N/A")
                {
                    // Escape HTML characters for Telegram parse_mode = HTML
                    string safeContent = System.Net.WebUtility.HtmlEncode(cleanContent);
                    string safeSender = System.Net.WebUtility.HtmlEncode(senderPhone);
                    
                    // GỌI HÀM BẮN TELEGRAM (Toàn văn nếu receiveAll)
                    string teleMsg = receiveAll 
                        ? $"📩 <b>Tin Nhắn Từ {e.PortName}</b>\n📱 SĐT: {receiverPhone}\n👤 Từ: {safeSender}\n📝 Nội dung: <i>{safeContent}</i>"
                        : $"📩 <b>OTP Mới Từ {e.PortName}</b>\n📱 SĐT: {receiverPhone}\n👤 Từ: {safeSender}\n🔑 OTP: <code>{extractedOtp}</code>\n📝 Nội dung: <i>{safeContent}</i>";

                    _ = TelegramService.SendMessageAsync(teleMsg);

                    if (extractedOtp != "N/A")
                        OtpReceivedEvent?.Invoke(e.PortName, extractedOtp);
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
                    Status = port?.Status ?? SimStatus.Connecting,
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
                    AddLog($"[{e.PortName}] Đã bắt được OTP: {extractedOtp} từ {senderPhone}", "SUCCESS");
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");

                    // Lưu lịch sử OTP vào file CSV
                    OtpHistoryService.Append(e.PortName, receiverPhone, senderPhone, extractedOtp, cleanContent);
                    // Cập nhật live vào OtpHistoryList (nếu tab đang mở)
                    OtpHistoryList.Insert(0, new Services.OtpRecord
                    {
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Port      = e.PortName,
                        SimPhone  = receiverPhone,
                        Sender    = senderPhone,
                        Otp       = extractedOtp,
                        Content   = cleanContent
                    });
                    if (SelectedTabIndex != 3) IncrementUnreadOtp();
                    OnPropertyChanged(nameof(FilteredOtpHistory));
                    OnPropertyChanged(nameof(FilteredOtpHistoryCount));

                    // Phát âm thanh cảnh báo OTP
                    Services.SoundAlertService.PlayOtp();

                    // Tự động kiểm tra nếu là OTP MyVNPT thì đổi pass
                    if (cleanContent.Contains("ma xac thuc OTP tren MyVNPT") || cleanContent.Contains("MyVNPT"))
                    {
                        if (_pendingMyVnptPasswordPorts.TryRemove(e.PortName, out _))
                        {
                            AddLog($"[{e.PortName}] Phát hiện OTP MyVNPT, tiến hành đổi mật khẩu...", "INFO");
                            _ = Services.MyVnptService.SetPasswordAsync(e.PortName, receiverPhone, extractedOtp, (msg, type) => AddLog(msg, type), (isSuccess, message) => {
                                if (port != null)
                                {
                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        port.LastMessageContent = message;
                                        port.LastSmsResult = message;
                                        port.UpdateDisplayResult(CommandPanelTab);
                                    });
                                }
                            });
                        }
                        else
                        {
                            AddLog($"[{e.PortName}] Nhận OTP MyVNPT nhưng không có yêu cầu từ tool, bỏ qua đặt mật khẩu.", "INFO");
                        }
                    }

                    // Thông báo Toast Windows
                    ToastService.ShowOtp(e.PortName, receiverPhone, extractedOtp, senderPhone);

                    // Tự động forward OTP qua Webhook (nếu có rule được cấu hình)
                    var webhookRules = AppSettings?.WebhookRules ?? new System.Collections.Generic.List<Models.WebhookRule>();
                    foreach (var rule in webhookRules)
                    {
                        _ = Services.WebhookService.TriggerAsync(rule, e.PortName, receiverPhone, senderPhone, extractedOtp, cleanContent);
                    }

                    // Chỉ xóa tin nhắn sau khi đã trích xuất OTP thành công
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                }
                else
                {
                    AddLog($"[{e.PortName}] Tin nhắn mới từ {senderPhone}");
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Tin nhắn mới từ {senderPhone}");

                    // Phát âm thanh SMS thường
                    Services.SoundAlertService.PlaySms();

                    // Forward SMS (không có OTP) qua webhook nếu rule không yêu cầu OtpOnly
                    var webhookRules = AppSettings?.WebhookRules ?? new System.Collections.Generic.List<Models.WebhookRule>();
                    foreach (var rule in webhookRules)
                    {
                        _ = Services.WebhookService.TriggerAsync(rule, e.PortName, receiverPhone, senderPhone, "N/A", cleanContent);
                    }

                    // PHẢI XÓA SMS NGAY CẢ KHI KHÔNG CÓ OTP ĐỂ TRÁNH TRÀN BỘ NHỚ SIM (SIM FULL SẼ KHÔNG NHẬN ĐƯỢC SMS NỮA)
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"[{e.PortName}] Lỗi xử lý SMS: {ex.Message}", "ERROR");
            }
        });
    }

    private class MultipartSmsBuffer
    {
        public string PortName = "";
        public string SenderPhone = "";
        public int ConcatRef;
        public int ConcatTotal;
        public DateTime LastUpdated;
        public Dictionary<int, string> Parts = new();
    }

    // Bộ nhớ đệm ghép các phần SMS dài (concatenated SMS) đang chờ đủ theo ConcatRef+Sender+Port.
    private readonly List<MultipartSmsBuffer> _multipartSmsBuffers = new();
    private static readonly TimeSpan MultipartSmsBufferTimeout = TimeSpan.FromMinutes(3);

    // Gom một phần (part) của tin nhắn dài vào buffer theo đúng số thứ tự (seq) khai báo trong UDH.
    // Trả về true và xuất nội dung đã ghép đủ khi đã nhận được toàn bộ concatTotal phần.
    private bool TryBufferConcatenatedSms(string portName, string senderPhone, int concatRef, int concatTotal, int concatSeq, string partContent, out string assembledContent)
    {
        assembledContent = string.Empty;
        var now = DateTime.Now;

        // Dọn các buffer bị bỏ dở quá lâu (phần bị mất/lỗi mạng): hiển thị luôn phần đã có để tránh mất dữ liệu.
        for (int i = _multipartSmsBuffers.Count - 1; i >= 0; i--)
        {
            var stale = _multipartSmsBuffers[i];
            if (now - stale.LastUpdated > MultipartSmsBufferTimeout)
            {
                _multipartSmsBuffers.RemoveAt(i);
                AddLog($"[{stale.PortName}] Tin nhắn dài từ {stale.SenderPhone} bị thiếu phần (chỉ nhận {stale.Parts.Count}/{stale.ConcatTotal}) sau {MultipartSmsBufferTimeout.TotalMinutes:0} phút, hiển thị phần đã nhận được.", "WARN");
                string partial = string.Join("", stale.Parts.OrderBy(kv => kv.Key).Select(kv => kv.Value));
                DeliverAssembledSms(stale.PortName, stale.SenderPhone, partial);
            }
        }

        var buffer = _multipartSmsBuffers.FirstOrDefault(b =>
            b.PortName == portName && b.SenderPhone == senderPhone &&
            b.ConcatRef == concatRef && b.ConcatTotal == concatTotal);

        if (buffer == null)
        {
            buffer = new MultipartSmsBuffer
            {
                PortName = portName,
                SenderPhone = senderPhone,
                ConcatRef = concatRef,
                ConcatTotal = concatTotal
            };
            _multipartSmsBuffers.Add(buffer);
        }

        buffer.Parts[concatSeq] = partContent;
        buffer.LastUpdated = now;

        if (buffer.Parts.Count < concatTotal)
            return false;

        assembledContent = string.Join("", buffer.Parts.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        assembledContent = Regex.Replace(assembledContent, @"\s+", " ").Trim();
        _multipartSmsBuffers.Remove(buffer);
        return true;
    }

    // Xử lý một tin nhắn dài đã ghép đủ nhưng bị timeout khi đang gom dở (không đợi thêm được nữa):
    // vẫn cố trích OTP/hiển thị lên UI bằng đúng luồng xử lý chuẩn.
    private void DeliverAssembledSms(string portName, string senderPhone, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        string extractedOtp = ExtractOtp(content);
        string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

        if (extractedOtp != "N/A")
        {
            OtpHistoryService.Append(portName, receiverPhone, senderPhone, extractedOtp, content);
            OtpReceivedEvent?.Invoke(portName, extractedOtp);
            Services.SoundAlertService.PlayOtp();
            ToastService.ShowOtp(portName, receiverPhone, extractedOtp, senderPhone);
        }

        SmsMessages.Insert(0, new SmsMessage
        {
            PortName = portName,
            ReceivedTime = DateTime.Now.ToString("HH:mm:ss"),
            Content = content,
            Sender = senderPhone,
            Otp = extractedOtp,
            ReceiverPhone = port?.PhoneNumber ?? "",
            NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
            Status = port?.Status ?? SimStatus.Connecting,
            CallCount = "0",
            ForwardContent = "Không"
        });

        if (port != null)
        {
            port.Sender = senderPhone;
            port.Otp = extractedOtp;
            port.LastMessageContent = content;
            port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private bool TryAppendToRecentMultipartSms(string portName, string senderPhone, string content, SimPort? port, bool receiveAll = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var now = DateTime.Now;
        var existing = SmsMessages.FirstOrDefault(s =>
            s.PortName == portName &&
            s.Sender == senderPhone &&
            s.Otp == "N/A" &&
            IsRecentSmsTime(s.ReceivedTime, now));

        if (existing == null)
            return false;

        string previous = existing.Content?.TrimEnd() ?? string.Empty;
        string current = content.TrimStart();
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;

        existing.Content = Regex.Replace($"{previous} {current}", @"\s+", " ").Trim();
        existing.ReceivedTime = now.ToString("HH:mm:ss");

        string newOtp = ExtractOtp(existing.Content);
        if (newOtp != "N/A" && existing.Otp == "N/A")
        {
            existing.Otp = newOtp;
            
            // Xử lý gửi OTP khi ráp thành công
            string simPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";
            OtpHistoryService.Append(portName, simPhone, senderPhone, newOtp, existing.Content);

            OtpReceivedEvent?.Invoke(portName, newOtp);
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                var newRecord = new Services.OtpRecord
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Port = portName,
                    SimPhone = simPhone,
                    Sender = senderPhone,
                    Otp = newOtp,
                    Content = existing.Content
                };
                OtpHistoryList.Insert(0, newRecord);
                if (SelectedTabIndex != 3) IncrementUnreadOtp();
                if (OtpHistoryList.Count > 100) OtpHistoryList.RemoveAt(OtpHistoryList.Count - 1);
            });
            
            Services.SoundAlertService.PlayOtp();
            ToastService.ShowOtp(portName, simPhone, newOtp, senderPhone);

            _ = Task.Run(async () =>
            {
                if (SettingsService.Current.EnableTelegramNotification)
                {
                    await TelegramService.SendMessageAsync($"📩 <b>OTP MỚI TỪ GHÉP SMS</b>\n📱 Số SIM: {simPhone}\nCổng: {portName}\n👤 Từ: {senderPhone}\n🔑 OTP: <code>{newOtp}</code>\n📝 Nội dung: {existing.Content}");
                }
                foreach (var rule in SettingsService.Current.WebhookRules.Where(r => r.Enabled))
                {
                    await WebhookService.TriggerAsync(rule, portName, simPhone, senderPhone, newOtp, existing.Content);
                }
            });
        }
        else if (receiveAll)
        {
            string simPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";
            string safeContent = System.Net.WebUtility.HtmlEncode(existing.Content);
            string safeSender = System.Net.WebUtility.HtmlEncode(senderPhone);
            _ = Task.Run(async () =>
            {
                if (SettingsService.Current.EnableTelegramNotification)
                {
                    await TelegramService.SendMessageAsync($"📩 <b>Tin Nhắn Ghép (Toàn Văn) Từ {portName}</b>\n📱 SĐT: {simPhone}\n👤 Từ: {safeSender}\n📝 Nội dung: <i>{safeContent}</i>");
                }
            });
        }

        SmsMessages.Remove(existing);
        SmsMessages.Insert(0, existing);

        if (port != null)
        {
            port.Sender = senderPhone;
            if (newOtp != "N/A") port.Otp = newOtp;
            port.LastMessageContent = existing.Content;
            port.LastReceivedTime = existing.ReceivedTime;
        }

        OnPropertyChanged(nameof(FilteredSmsMessages));
        OnPropertyChanged(nameof(SmsReceivedCount));
        return true;
    }

    private string ExtractOtp(string content)
    {
        string textForOtp = Regex.Replace(content, @"\*+\d+", "");

        var otpMatch = Regex.Match(textForOtp, @"(?:mã|code|otp|là|la|zalo|whatsapp|viber|telegram|facebook|google|apple|tiktok|tinder|xac nhan|verification|verify|pin|mat khau)[^\d]{0,30}?(\d{3}\s*[- ]\s*\d{3}|\d{4,8})", RegexOptions.IgnoreCase);
        
        if (!otpMatch.Success)
        {
            // Mở lại Fallback để bắt các số OTP đứng độc lập
            otpMatch = Regex.Match(textForOtp, @"(?<![\w:/])(?!1900|1800)\b(\d{3}\s*[- ]\s*\d{3}|\d{4,8})\b(?![\w:/])", RegexOptions.IgnoreCase);
        }

        return otpMatch.Success && otpMatch.Groups.Count > 1 && !string.IsNullOrEmpty(otpMatch.Groups[1].Value) 
            ? Regex.Replace(otpMatch.Groups[1].Value, @"\D", "") 
            : (otpMatch.Success ? Regex.Replace(otpMatch.Value, @"\D", "") : "N/A");
    }

    private bool IsRecentSmsTime(string receivedTime, DateTime now)
    {
        if (!TimeSpan.TryParse(receivedTime, out var timeOfDay))
            return false;

        var receivedAt = now.Date.Add(timeOfDay);
        var delta = now - receivedAt;
        if (delta < TimeSpan.Zero)
            delta = delta.Duration();

        return delta <= TimeSpan.FromSeconds(60);
    }

    private void ModemService_CallIncoming(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";
            string callerDisplay = string.IsNullOrWhiteSpace(e.Data) ? "Số ẩn" : e.Data;
            _activeCallers[e.PortName] = callerDisplay;

            if (port != null)
            {
                port.CallCount++;
                port.Sender = callerDisplay;
                port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                port.LastMessageContent = "Cuộc gọi đến...";
                UpdateDashboard();
            }

            AddLog($"[{e.PortName}] Có cuộc gọi đến từ SĐT: {callerDisplay}", "INFO");
            SnackbarMessageQueue.Enqueue($"[{e.PortName}] Có cuộc gọi từ {callerDisplay}");

            // Phát âm thanh cảnh báo cuộc gọi đến
            Services.SoundAlertService.PlayCall();

            string safeCallerHtml = System.Net.WebUtility.HtmlEncode(callerDisplay);
            _ = TelegramService.SendMessageAsync(
                $"📞 <b>Cuộc gọi đến [{e.PortName}]</b>\n" +
                $"📱 SIM nhận: {receiverPhone}\n" +
                $"☎️ Người gọi: <code>{safeCallerHtml}</code>"
            );

            // Tự động nhận cuộc gọi và ghi âm
            if (IsAutoAnswerEnabled)
            {
                if (!_activeRamRecordings.ContainsKey(e.PortName))
                {
                    AddLog($"[{e.PortName}] Đang tự động bắt máy cuộc gọi đến...", "INFO");
                    await _modemService.SendCommandAsync(e.PortName, "ATA");
                    await Task.Delay(1500);

                    AddLog($"[{e.PortName}] Bắt đầu thu âm vào RAM của mạch Quectel...", "INFO");
                    await _modemService.SendCommandAsync(e.PortName, "AT+QAUDRD=1,\"call.wav\",13,0");
                    _activeRamRecordings[e.PortName] = true;
                }
            }
            else
            {
                AddLog($"[{e.PortName}] Có cuộc gọi đến nhưng tính năng Tự động bắt máy đang TẮT.", "INFO");
            }
        });
    }
    private void ModemService_CallEnded(object? sender, GsmDataEventArgs e)
    {
        if (e.Data == "NO CARRIER" || e.Data == "BUSY" || e.Data == "NO ANSWER")
        {
            _callFailures[e.PortName] = e.Data;
        }

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            AddLog($"[{e.PortName}] Cuộc gọi đã kết thúc. ({e.Data})");

            string callerDisplay = _activeCallers.TryRemove(e.PortName, out var caller) ? caller : "Số ẩn";
            string wavFilePath = string.Empty;
            string transcript = string.Empty;
            bool hadRecording = false;

            if (_activeRamRecordings.TryRemove(e.PortName, out _))
            {
                AddLog($"[{e.PortName}] Đang chốt file ghi âm RAM...");
                await _modemService.SendCommandAsync(e.PortName, "AT+QAUDRD=0"); // Dừng ghi âm

                AddLog($"[{e.PortName}] Đang tải file ghi âm qua cổng COM... (Vui lòng chờ)");
                
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                
                wavFilePath = Path.Combine(logDir, $"call_{e.PortName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                string downloadedFile = await _modemService.DownloadFileFromModemAsync(e.PortName, "call.wav", wavFilePath);

                hadRecording = File.Exists(downloadedFile) && new FileInfo(downloadedFile).Length > 0;

                if (hadRecording)
                {
                    AddLog($"[{e.PortName}] Đã tải xong file âm thanh từ mạch, đang phân tích...");
                    transcript = await Task.Run(() => _speechToTextService.RecognizeWavFile(downloadedFile));
                }
                else
                {
                    AddLog($"[{e.PortName}] Tải file âm thanh thất bại hoặc file trống.", "ERROR");
                }
            }

            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";
            string fileName = string.IsNullOrWhiteSpace(wavFilePath) ? "Không có file" : Path.GetFileName(wavFilePath);
            bool hasTranscript = !string.IsNullOrWhiteSpace(transcript) && !transcript.StartsWith("Lỗi:", StringComparison.OrdinalIgnoreCase);
            string content = hasTranscript
                ? transcript
                : hadRecording
                    ? "Không nhận diện được giọng nói trong cuộc gọi này."
                    : "Không có dữ liệu ghi âm cho cuộc gọi này.";

            SmsMessages.Insert(0, new SmsMessage
            {
                PortName = e.PortName,
                ReceivedTime = DateTime.Now.ToString("HH:mm:ss"),
                Content = content,
                Sender = callerDisplay,
                Otp = "",
                ReceiverPhone = receiverPhone,
                NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
                Status = port?.Status ?? SimStatus.Connecting,
                CallCount = port?.CallCount.ToString() ?? "1",
                ForwardContent = fileName
            });

            if (port != null)
            {
                port.LastMessageContent = content;
                port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                port.Otp = "";
                port.Sender = callerDisplay;
            }

            OnPropertyChanged(nameof(FilteredSmsMessages));
            OnPropertyChanged(nameof(SmsReceivedCount));

            if (hasTranscript)
            {
                AddLog($"[{e.PortName}] Nội dung cuộc gọi: {transcript}", "SUCCESS");
            }
            else if (!string.IsNullOrWhiteSpace(transcript))
            {
                AddLog($"[{e.PortName}] {transcript}", "WARN");
            }
            else
            {
                AddLog($"[{e.PortName}] {content}", "WARN");
            }

            string safeCallerHtml = System.Net.WebUtility.HtmlEncode(callerDisplay);
            string safeContent = System.Net.WebUtility.HtmlEncode(content);
            string safeFileName = System.Net.WebUtility.HtmlEncode(fileName);
            _ = TelegramService.SendMessageAsync(
                $"🎙 <b>Cuộc gọi đến [{e.PortName}]</b>\n" +
                $"📱 SIM nhận: {receiverPhone}\n" +
                $"☎️ Người gọi: <code>{safeCallerHtml}</code>\n" +
                $"📝 Nội dung: <i>{safeContent}</i>\n" +
                $"💾 File: <code>{safeFileName}</code>"
            );
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
    private void SetLogFilter(string filter)
    {
        LogFilter = filter ?? string.Empty;
    }

    [RelayCommand]
    private void ReloadImeiBackup()
    {
        LoadImeiCache();
        ImportCsvToImeiCache();

        int applied = 0;
        foreach (var port in Ports)
        {
            if (string.IsNullOrWhiteSpace(port.Serial)) continue;
            string ccid = NormalizeCcid(port.Serial);
            if (!_imeiCache.TryGetValue(ccid, out var entry) || entry == null) continue;

            if (!string.IsNullOrWhiteSpace(entry.PhoneNumber))
            {
                port.PhoneNumber = entry.PhoneNumber;
                UpdateSmsReceiverPhone(port.PortName, entry.PhoneNumber);
                _simCache[ccid] = entry.PhoneNumber;
            }

            port.CreatedAt = entry.CreatedAt;
            port.LicenseKeySuffix = entry.LicenseKeySuffix;
            port.KeyMismatch = entry.KeyMismatch;
            applied++;
        }

        if (applied > 0)
        {
            SaveSimCache();
        }

        AddLog($"[IMEI_SOURCE] Đã reload imei_backup.csv và áp dụng metadata cho {applied} cổng đang cắm.", "SUCCESS");
        SnackbarMessageQueue.Enqueue($"Đã reload imei_backup.csv ({applied} cổng được cập nhật).");
    }

    private string GetUssdCodeForProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "*101#";
        
        string upperProvider = provider.ToUpperInvariant();
        foreach (var kvp in BalanceUssdByProvider)
        {
            if (upperProvider.Contains(kvp.Key.ToUpperInvariant()))
            {
                return kvp.Value;
            }
        }
        
        // Mặc định chuẩn mạng VN là *101#
        return "*101#";
    }

    [RelayCommand]
    private async Task SweepSmsAsync(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) đang hoạt động để quét tin kẹt.");
                return;
            }
        }
        else
        {
            targetPorts = Ports.Where(IsActive).ToList();
        }

        SnackbarMessageQueue.Enqueue($"Đang tiến hành vét tin nhắn kẹt trên {targetPorts.Count} cổng...");
        
        foreach (var port in targetPorts)
        {
            if (SmsInProgressPorts.ContainsKey(port.PortName))
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _modemService.SweepUnreadSmsAsync(port.PortName);
                    Application.Current.Dispatcher.Invoke(() => port.LastSweepTime = DateTime.Now.ToString("HH:mm:ss"));
                }
                catch { }
            });
            await Task.Delay(200);
        }
    }

    [RelayCommand]
    private async Task CheckBalanceAsync(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) đang hoạt động để kiểm tra số dư.");
                return;
            }
            SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh kiểm tra TKC cho {targetPorts.Count} cổng ĐÃ CHỌN...");
            AddLog($"Bắt đầu kiểm tra số dư cho {targetPorts.Count} cổng đã chọn...");
        }
        else
        {
            targetPorts = Ports.Where(IsActive).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Không có cổng nào đang hoạt động để kiểm tra số dư.");
                return;
            }
            SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh kiểm tra TKC cho TOÀN BỘ {targetPorts.Count} cổng...");
            AddLog($"Bắt đầu kiểm tra số dư cho toàn bộ {targetPorts.Count} cổng...");
        }

        foreach (var port in targetPorts)
        {
            if (string.IsNullOrWhiteSpace(port.NetworkProvider))
            {
                AddLog($"[{port.PortName}] Bỏ qua kiểm tra TKC vì chưa xác định được nhà mạng.", "WARN");
                continue;
            }

            string ussdCode = GetUssdCodeForProvider(port.NetworkProvider);

            // Gọi bất đồng bộ không chờ (để throttler bên trong hàm tự động xếp hàng)
            _ = SendUssdThrottledAsync(port.PortName, ussdCode, "Kiểm tra số dư", maxAttempts: 3, logResult: true);
        }
    }

    [RelayCommand]
    private async Task RebootModemAsync(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) đang hoạt động để khởi động lại.");
                return;
            }
        }
        else
        {
            targetPorts = Ports.Where(IsActive).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Không có cổng nào đang hoạt động để khởi động lại.");
                return;
            }
        }

        if (System.Windows.MessageBox.Show($"Bạn có chắc muốn khởi động lại {targetPorts.Count} modem?\nThao tác này sẽ làm mất kết nối trong vài giây.", "Khởi động lại", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        SnackbarMessageQueue.Enqueue($"Đang gửi lệnh khởi động lại cho {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu khởi động lại {targetPorts.Count} cổng...");

        foreach (var port in targetPorts)
        {
            await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=1,1");
        }
    }

    [RelayCommand]
    private async Task PrepareSwapSim(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) để chuẩn bị đổi SIM.");
                return;
            }
        }
        else
        {
            targetPorts = Ports.ToList();
        }


        if (System.Windows.MessageBox.Show($"Bạn có chắc muốn ép ngắt sóng {targetPorts.Count} modem để chuẩn bị thay SIM?\nThao tác này sẽ tắt sóng vô tuyến để tránh rò rỉ IMEI.", "Chuẩn bị Đổi SIM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        SnackbarMessageQueue.Enqueue($"Đang ép ngắt sóng {targetPorts.Count} cổng để chờ thay SIM...");
        AddLog($"Bắt đầu ngắt sóng {targetPorts.Count} cổng...");

        foreach (var port in targetPorts)
        {
            Application.Current.Dispatcher.Invoke(() => port.Status = SimStatus.Connecting);
            _ = Task.Run(async () => 
            {
                await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=4");
                
                // [SHIELD IMEI] Tráng một lớp IMEI Fake ngẫu nhiên làm lá chắn.
                
                _modemService.StartHotplugWaitLoop(port.PortName);
            });
        }
        
        SnackbarMessageQueue.Enqueue("Đã ngắt sóng an toàn. Bạn có thể rút khay SIM ra và cắm SIM mới vào.");
    }


    [RelayCommand]
    private async Task ClearSmsAsync(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) đang hoạt động để xóa tin nhắn.");
                return;
            }
        }
        else
        {
            targetPorts = Ports.Where(IsActive).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Không có cổng nào đang hoạt động để xóa tin nhắn.");
                return;
            }
        }

        if (System.Windows.MessageBox.Show($"Bạn có chắc muốn xóa TOÀN BỘ tin nhắn trên {targetPorts.Count} SIM?\nThao tác này KHÔNG THỂ HOÀN TÁC.", "Xóa tin nhắn", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        SnackbarMessageQueue.Enqueue($"Đang gửi lệnh xóa SMS rác cho {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu xóa SMS rác cho {targetPorts.Count} cổng...");

        foreach (var port in targetPorts)
        {
            await _modemService.SendCommandAsync(port.PortName, "AT+CMGD=1,4");
        }
    }

    public async Task<string> CheckBalanceForPortAsync(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port != null && !string.IsNullOrWhiteSpace(port.NetworkProvider))
        {
            string ussdCode = GetUssdCodeForProvider(port.NetworkProvider);
            AddLog($"Tự động kiểm tra lại TKC cho {port.PortName} sau khi gửi SMS...");
            return await SendUssdThrottledAsync(port.PortName, ussdCode, "Tự động kiểm tra TKC", maxAttempts: 99999, logResult: true);
        }
        return "ERROR: Cổng không hợp lệ hoặc không có thông tin nhà mạng";
    }



    private async Task<string> SendUssdThrottledAsync(string portName, string ussdCode, string reason, bool logResult = false, int maxAttempts = 3)
    {
        string result = string.Empty;

        for (int i = 0; i < maxAttempts; i++)
        {
            if (IsPortCoolingDown(portName, out var remainingCooldown))
            {
                result = $"ERROR: Port cooling down for {remainingCooldown.TotalSeconds:0}s";
                AddLog($"[{portName}] Bỏ qua USSD vì cổng đang cooldown {remainingCooldown.TotalSeconds:0}s sau lỗi gần nhất.", "WARN");
                return result;
            }

            if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(ussdCode))
            {
                return "ERROR: Invalid USSD request";
            }

            var port = Ports.FirstOrDefault(p => p.PortName == portName);
            if (reason.Contains("lấy SĐT") && !reason.Contains("TKC") && port != null && !string.IsNullOrWhiteSpace(port.PhoneNumber))
            {
                return "SKIPPED: Đã có SĐT";
            }
            if (reason == "Tự động lấy SĐT và TKC" && port != null && !string.IsNullOrWhiteSpace(port.PhoneNumber) && !string.IsNullOrWhiteSpace(port.Balance))
            {
                return "SKIPPED: Đã đủ thông tin";
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
                        await Task.Delay(remaining, _lifetimeCts.Token);
                        now = DateTime.UtcNow;
                    }
                }

                var globalRemaining = UssdMinIntervalGlobal - (now - _lastUssdGlobalUtc);
                if (globalRemaining > TimeSpan.Zero)
                {
                    WarnUssdThrottle(portName, reason, globalRemaining, "GLOBAL");
                    await Task.Delay(globalRemaining, _lifetimeCts.Token);
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
            var preflightError = await PrepareUssdPortAsync(portName);
            if (!string.IsNullOrEmpty(preflightError))
            {
                result = preflightError;
            }
            else
            {
                await _modemService.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);

            // 2. Gửi lệnh USSD
                try
                {
                    result = await _modemService.SendCommandAsync(portName, $"AT+CUSD=1,\"{ussdCode}\",15");
                }
                finally
                {

            // 3. Chuyển lại UCS2
                    await _modemService.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);
                }
            }

            bool isFailed = result.Contains("ERROR") || result.Contains("Thao tac khong hop le") || result.Contains("he thong ban") || result.Contains("+CUSD: 2") || result.Contains("+CUSD: 4") || result.Contains("+CUSD: 5");

            if (!isFailed)
            {
                if (logResult) AddLog($"Kết quả từ {portName}: {result}", "SUCCESS");
                return result; // Thành công, thoát vòng lặp
            }

            RecordPortError(portName, result);
            MaybeCooldownPort(portName, result);

            if (i < maxAttempts - 1)
            {
                // Nếu đang có SMS chờ xử lý trên cổng này, dừng retry USSD lại ngay
                if (SmsInProgressPorts.ContainsKey(portName))
                {
                    AddLog($"[{portName}] Dừng retry USSD vì có lệnh SMS đang ưu tiên.", "INFO");
                    break;
                }
                int delaySecs = Math.Min(3 + i * 2, 30);
                AddLog($"[{portName}] Lỗi USSD ({result.Trim()}). Thử lại sau {delaySecs} giây... (Lần {i + 1}/{maxAttempts - 1})", "WARN");
                await Task.Delay(TimeSpan.FromSeconds(delaySecs), _lifetimeCts.Token);
            }
        }

        if (logResult) AddLog($"Kết quả từ {portName} (Đã thử {maxAttempts} lần): {result}", "ERROR");
        return result;
    }

    private void WarnUssdThrottle(string portName, string reason, TimeSpan remaining, string scope)
    {
        string message = $"USSD đang xếp hàng ({scope}) cho {portName} - {reason}. Đợi {remaining.TotalSeconds:0.#}s.";
        AddLog(message, "INFO");
    }

    private async Task<string?> PrepareUssdPortAsync(string portName)
    {
        string at = await _modemService.SendCommandAsync(portName, "AT", 3000, true);
        if (IsCommandError(at))
        {
            return $"ERROR: Modem not ready ({at.Trim()})";
        }

        string pinStatus = await _modemService.SendCommandAsync(portName, "AT+CPIN?", 5000, true);
        if (IsCommandError(pinStatus))
        {
            return $"ERROR: SIM status check failed ({pinStatus.Trim()})";
        }
        if (pinStatus.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase) ||
            pinStatus.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase) ||
            pinStatus.Contains("PH-NET PIN", StringComparison.OrdinalIgnoreCase))
        {
            return $"ERROR: SIM not ready ({pinStatus.Trim()})";
        }

        string registration = await _modemService.SendCommandAsync(portName, "AT+CREG?", 5000, true);
        if (IsCommandError(registration))
        {
            return $"ERROR: Network registration check failed ({registration.Trim()})";
        }
        if (!IsNetworkRegistered(registration))
        {
            return $"ERROR: SIM not registered on network ({registration.Trim()})";
        }

        string signal = await _modemService.SendCommandAsync(portName, "AT+CSQ", 5000, true);
        if (IsCommandError(signal))
        {
            return $"ERROR: Signal quality check failed ({signal.Trim()})";
        }
        if (!HasUsableSignal(signal))
        {
            return $"ERROR: Signal too weak for USSD ({signal.Trim()})";
        }

        await _modemService.SendCommandAsync(portName, "AT+CUSD=2", 5000, true);
        await Task.Delay(400, _lifetimeCts.Token);
        return null;
    }

    private static bool IsCommandError(string response)
    {
        return string.IsNullOrWhiteSpace(response) ||
               response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkRegistered(string response)
    {
        var match = Regex.Match(response, @"\+CREG:\s*\d+\s*,\s*(\d+)");
        if (!match.Success) return false;

        return match.Groups[1].Value is "1" or "5";
    }

    private static bool HasUsableSignal(string response)
    {
        var match = Regex.Match(response, @"\+CSQ:\s*(\d+)");
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, out int csq)) return false;
        if (csq == 99) return false;

        return csq >= 6;
    }

    private bool IsPortCoolingDown(string portName, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_portCooldownUntilUtc.TryGetValue(portName, out var untilUtc)) return false;

        remaining = untilUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _portCooldownUntilUtc.TryRemove(portName, out _);
            remaining = TimeSpan.Zero;
            return false;
        }

        return true;
    }

    private void MaybeCooldownPort(string portName, string result)
    {
        if (!ShouldCooldown(result)) return;

        var cooldown = result.Contains("Port not open", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(45);

        _portCooldownUntilUtc[portName] = DateTime.UtcNow.Add(cooldown);
    }

    private static bool ShouldCooldown(string result)
    {
        return result.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Port not open", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CMS ERROR: 350", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CME ERROR: 13", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetrySms(string result)
    {
        return result.Contains("Another command", StringComparison.OrdinalIgnoreCase)
            || result.Contains("waiting for lock", StringComparison.OrdinalIgnoreCase);
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

    public IEnumerable<string> AtCommandPortOptions
    {
        get
        {
            var list = new List<string> { "Tất cả cổng" };
            list.AddRange(Ports.Select(p => p.PortName));
            return list;
        }
    }

    public IEnumerable<string> CallManagerPortOptions => Ports.Select(p => p.PortName);

    [RelayCommand]
    private void SortPorts(string criteria)
    {
        if (string.IsNullOrEmpty(criteria)) return;

        var sorted = criteria switch
        {
            "Network" => Ports.OrderBy(p => string.IsNullOrEmpty(p.NetworkProvider) ? "ZZZ" : p.NetworkProvider).ThenBy(p => p.PortNumber).ToList(),
            "Status" => Ports.OrderByDescending(p => p.Status == "Active").ThenBy(p => p.PortNumber).ToList(),
            "Signal" => Ports.OrderByDescending(p => p.SignalStrength).ThenBy(p => p.PortNumber).ToList(),
            "Balance" => Ports.OrderByDescending(p => 
            {
                if (string.IsNullOrEmpty(p.Balance)) return 0d;
                var match = System.Text.RegularExpressions.Regex.Match(p.Balance, @"\d+");
                return match.Success ? double.Parse(match.Value) : 0d;
            }).ThenBy(p => p.PortNumber).ToList(),
            "COM" or _ => Ports.OrderBy(p => p.PhysicalIndex).ToList()
        };
        
        Ports.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            var port = sorted[i];
            port.Stt = i + 1;
            Ports.Add(port);
        }
        
        UpdateDashboard();
        
        var criteriaName = criteria switch {
            "Network" => "Nhà mạng",
            "Status" => "Trạng thái (Online)",
            "Signal" => "Cường độ sóng",
            "Balance" => "Số dư",
            _ => "Thứ tự COM"
        };
        SnackbarMessageQueue.Enqueue($"Đã sắp xếp theo: {criteriaName}");
    }

    [RelayCommand]
    private void DummyFeature(string featureName)
    {
        SnackbarMessageQueue.Enqueue($"Tính năng '{featureName}' đang được phát triển.");
    }

    [RelayCommand]
    private void OpenAtCommandDialog()
    {
        AtCommandSelectedPort = Ports.Count > 0 ? Ports.First().PortName : "Tất cả cổng";
        AtCommandOutput = string.Empty;
        AtCommandInput = "AT";
        IsAtCommandDialogOpen = true;
    }

    [RelayCommand]
    private async Task SendAtCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(AtCommandSelectedPort) || string.IsNullOrWhiteSpace(AtCommandInput)) return;

        AtCommandOutput += $"> {AtCommandInput}\n";
        
        if (AtCommandSelectedPort == "Tất cả cổng")
        {
            var targetPorts = Ports.Select(p => p.PortName).ToList();
            if (targetPorts.Count == 0)
            {
                AtCommandOutput += "[WARN] Không có cổng nào đang kết nối.\n";
                return;
            }
            
            var tasks = targetPorts.Select(async port => 
            {
                try
                {
                    string res = await _modemService.SendCommandAsync(port, AtCommandInput, timeoutMs: 5000);
                    return $"[{port}] {res.Trim()}";
                }
                catch (Exception ex)
                {
                    return $"[{port}] ERROR: {ex.Message}";
                }
            });
            
            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                AtCommandOutput += $"{r}\n";
            }
        }
        else
        {
            try
            {
                string result = await _modemService.SendCommandAsync(AtCommandSelectedPort, AtCommandInput, timeoutMs: 5000);
                AtCommandOutput += $"{result}\n";
            }
            catch (Exception ex)
            {
                AtCommandOutput += $"[ERROR] {ex.Message}\n";
            }
        }
    }

    [RelayCommand]
    private void OpenSettingsDialog()
    {
        var json = JsonSerializer.Serialize(SettingsService.Current);
        AppSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        IsSettingsDialogOpen = true;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsService.SaveSettings(AppSettings);
        IsSettingsDialogOpen = false;
        SnackbarMessageQueue.Enqueue("Đã lưu cấu hình thành công.");

        OnPropertyChanged(nameof(IsTelegramNotificationEnabled));
        OnPropertyChanged(nameof(IsWebNotificationEnabled));
        OnPropertyChanged(nameof(IsWatchdogEnabled));
        OnPropertyChanged(nameof(IsAutoAnswerEnabled));

        OnPropertyChanged(nameof(IsImeiRestoreEnabled));
        OnPropertyChanged(nameof(IsBlockUnknownSimsEnabled));
        OnPropertyChanged(nameof(IsNewSimIntakeModeEnabled));

        // Áp dụng tính năng chuyển hướng ngay lập tức cho tất cả các cổng
        if (AppSettings != null && AppSettings.EnableAutoCallForwarding && !string.IsNullOrWhiteSpace(AppSettings.ForwardPhoneNumber))
        {
            SnackbarMessageQueue.Enqueue($"Đang áp dụng chuyển hướng ngẫu nhiên cho các cổng...");
            
            Task.Run(async () =>
            {
                var activePorts = GetPortsSnapshot();
                foreach (var port in activePorts)
                {
                    string randomFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                    if (string.IsNullOrEmpty(randomFwd)) continue;
                    
                    AddLog($"[{port.PortName}] Đang thiết lập tự động chuyển hướng đến {randomFwd}...");
                    string res = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,1,\"{randomFwd}\",129", timeoutMs: 5000);
                    if (res.Contains("OK"))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            port.ForwardedTo = randomFwd; // #4: Cập nhật cột hiển thị
                            port.ForwardCount++;
                        });
                    }
                    await Task.Delay(500); // Tránh nghẽn lệnh
                }
            });
        }
        else if (AppSettings != null)
        {
            // Hủy chuyển hướng nếu người dùng tắt tính năng hoặc để trống số điện thoại
            Task.Run(async () =>
            {
                var activePorts = GetPortsSnapshot();
                foreach (var port in activePorts)
                {
                    await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,4", timeoutMs: 5000);
                    Application.Current.Dispatcher.Invoke(() => port.ForwardedTo = string.Empty);
                    await Task.Delay(500);
                }
            });
        }
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
    private void CopyOtpFromPort(SimPort? port)
    {
        if (port != null && !string.IsNullOrEmpty(port.Otp))
        {
            Clipboard.SetText(port.Otp);
            SnackbarMessageQueue.Enqueue("Đã sao chép OTP vào Clipboard.");
        }
    }

    [RelayCommand]
    private void CopyPhoneFromPort(SimPort? port)
    {
        if (port != null && !string.IsNullOrEmpty(port.PhoneNumber))
        {
            Clipboard.SetText(port.PhoneNumber);
            SnackbarMessageQueue.Enqueue("Đã sao chép SĐT vào Clipboard.");
        }
    }

    [RelayCommand]
    private void CopyPhone(SmsMessage? sms)
    {
        if (sms != null && !string.IsNullOrEmpty(sms.ReceiverPhone))
        {
            Clipboard.SetText(sms.ReceiverPhone);
            SnackbarMessageQueue.Enqueue("Đã sao chép SĐT vào Clipboard.");
        }
    }

    [RelayCommand]
    private void CopyAllPhones()
    {
        var phones = Ports
            .Where(p => !string.IsNullOrWhiteSpace(p.PhoneNumber))
            .Select(p => p.PhoneNumber!)
            .ToList();

        if (phones.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Đang có 0 số điện thoại, chưa có gì để copy!");
            return;
        }

        Clipboard.SetText(string.Join("\n", phones));
        SnackbarMessageQueue.Enqueue($"✅ Đã copy {phones.Count} số điện thoại vào clipboard!");
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
    private void ApplySmsFilter()
    {
        OnPropertyChanged(nameof(FilteredSmsMessages));
        SnackbarMessageQueue.Enqueue("Đã lọc dữ liệu SMS.");
    }

    [RelayCommand]
    private void MarkAllSmsRead()
    {
        foreach (var sms in SmsMessages)
        {
            sms.Status = "Đã đọc";
        }

        SnackbarMessageQueue.Enqueue($"Đã đánh dấu {SmsMessages.Count} tin nhắn là đã đọc.");
    }

    [RelayCommand]
    private void DeleteFilteredSms()
    {
        var filtered = FilteredSmsMessages.Cast<SmsMessage>().ToList();
        foreach (var sms in filtered)
        {
            SmsMessages.Remove(sms);
        }

        SnackbarMessageQueue.Enqueue($"Đã xóa {filtered.Count} tin nhắn.");
    }

    [RelayCommand]
    private void ExportSmsToExcel()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Xuất danh sách SMS",
            Filter = "Excel files (*.xlsx)|*.xlsx",
            FileName = $"sms_export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("SMS");
            var headers = new[] { "Cổng", "Người gửi", "SĐT", "Nhà mạng", "Nhận lúc", "OTP", "Trạng thái", "Nội dung" };
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.Cells[1, i + 1].Value = headers[i];
                sheet.Cells[1, i + 1].Style.Font.Bold = true;
            }

            var rows = FilteredSmsMessages.Cast<SmsMessage>().ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var sms = rows[i];
                int row = i + 2;
                sheet.Cells[row, 1].Value = sms.PortName;
                sheet.Cells[row, 2].Value = sms.Sender;
                sheet.Cells[row, 3].Value = sms.ReceiverPhone;
                sheet.Cells[row, 4].Value = sms.NetworkProvider;
                sheet.Cells[row, 5].Value = sms.ReceivedTime;
                sheet.Cells[row, 6].Value = sms.Otp;
                sheet.Cells[row, 7].Value = sms.Status;
                sheet.Cells[row, 8].Value = sms.Content;
            }

            if (sheet.Dimension != null)
            {
                sheet.Cells[sheet.Dimension.Address].AutoFitColumns();
            }

            package.SaveAs(new FileInfo(dialog.FileName));
            SnackbarMessageQueue.Enqueue($"Đã xuất {rows.Count} tin nhắn ra Excel.");
        }
        catch (Exception ex)
        {
            AddLog($"[SMS EXPORT] Lỗi xuất Excel: {ex.Message}", "ERROR");
            SnackbarMessageQueue.Enqueue($"Lỗi xuất Excel: {ex.Message}");
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
            targetPorts = Ports.Where(IsActive).ToList();
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

    // Custom USSD methods removed

    // ComposeSms methods removed



    [RelayCommand]
    private void OpenExportExcelDialog()
    {
        IsExportExcelDialogOpen = true;
    }

    [RelayCommand]
    private void ExecuteExportExcel()
    {
        IsExportExcelDialogOpen = false;
        var selectedColumns = ExportColumns.Where(c => c.IsSelected).ToList();
        if (selectedColumns.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cột để xuất.");
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx",
            FileName = $"DanhSachSIM_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            Title = "Lưu file Excel"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
                using var package = new OfficeOpenXml.ExcelPackage();
                var worksheet = package.Workbook.Worksheets.Add("Danh Sach SIM");

                // Headers
                for (int i = 0; i < selectedColumns.Count; i++)
                {
                    worksheet.Cells[1, i + 1].Value = selectedColumns[i].ColumnName;
                    worksheet.Cells[1, i + 1].Style.Font.Bold = true;
                }

                // Data
                var items = Ports.ToList(); // Export all currently held ports or FilteredPortsView? FilteredPortsView might be better, but we need to access items.
                // It's better to use FilteredPortsView.Cast<SimPort>().ToList() to match the UI!
                var viewItems = FilteredPortsView.Cast<SimPort>().ToList();
                for (int row = 0; row < viewItems.Count; row++)
                {
                    var item = viewItems[row];
                    for (int col = 0; col < selectedColumns.Count; col++)
                    {
                        var propInfo = typeof(SimPort).GetProperty(selectedColumns[col].BindingPath);
                        if (propInfo != null)
                        {
                            var value = propInfo.GetValue(item);
                            worksheet.Cells[row + 2, col + 1].Value = value?.ToString();
                        }
                    }
                }

                worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
                File.WriteAllBytes(saveFileDialog.FileName, package.GetAsByteArray());
                SnackbarMessageQueue.Enqueue($"Đã xuất file thành công: {Path.GetFileName(saveFileDialog.FileName)}");
            }
            catch (Exception ex)
            {
                AddLog($"Lỗi xuất Excel: {ex.Message}", "ERROR");
                SnackbarMessageQueue.Enqueue("Có lỗi xảy ra khi xuất Excel. Vui lòng xem log.");
            }
        }
    }

    public Task<string> QueueSmsAsync(string portName, string phoneNumber, string content)
    {
        return SendSmsThrottledAsync(portName, phoneNumber, content);
    }

    private async Task<string> SendSmsThrottledAsync(string portName, string phoneNumber, string content)
    {
        var sendLock = _smsSendLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(_lifetimeCts.Token);

        try
        {
            SmsInProgressPorts.TryAdd(portName, true);

            if (IsPortCoolingDown(portName, out var remainingCooldown))
            {
                string msg = $"ERROR: Port cooling down for {remainingCooldown.TotalSeconds:0}s";
                AddLog($"[{portName}] Bỏ qua gửi SMS vì cổng đang cooldown {remainingCooldown.TotalSeconds:0}s sau lỗi gần nhất.", "WARN");
                return msg;
            }

            // 1. Remove diacritics to send via GSM safely without UCS2 hex-encoding complexity
            string safeContent = RemoveDiacritics(content);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                // 2. Switch to GSM temporarily so that the raw text is accepted
                await _modemService.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);
                await _modemService.SendCommandAsync(portName, "AT+CSMP=17,167,0,0", 5000, true);

                // 3. Send the SMS
                string result = await _modemService.SendSmsAsync(portName, phoneNumber, safeContent, timeoutMs: 30000);
                
                if (result.Contains("OK") || result.Contains("+CMGS:"))
                {
                    RecordSmsSuccess(portName);
                    AddLog($"[{portName}] Gửi tin nhắn đến {phoneNumber} thành công.", "SUCCESS");
                    return "Gửi thành công";
                }

                RecordPortError(portName, result);
                MaybeCooldownPort(portName, result);

                if (attempt >= 3 || !ShouldRetrySms(result))
                {
                    if (result.Contains("Timeout"))
                    {
                        AddLog($"[{portName}] Lỗi Timeout SMS: Không retry để tránh gửi trùng. Vui lòng kiểm tra điện thoại người nhận xem đã có tin nhắn chưa!", "WARN");
                        Application.Current.Dispatcher.Invoke(() => SnackbarMessageQueue.Enqueue($"[{portName}] Timeout SMS: Không retry để tránh gửi trùng."));
                    }
                    else
                    {
                        AddLog($"[{portName}] Gửi tin nhắn thất bại sau {attempt} lần: {result}", "ERROR");
                    }
                    return result;
                }

                var delay = TimeSpan.FromSeconds(2 * attempt);
                AddLog($"[{portName}] Gửi SMS lỗi ({result}). Thử lại sau {delay.TotalSeconds:0}s... (Lần {attempt}/3)", "WARN");
                await Task.Delay(delay, _lifetimeCts.Token);

                if (IsPortCoolingDown(portName, out remainingCooldown))
                {
                    AddLog($"[{portName}] Dừng retry SMS vì cổng chuyển sang cooldown {remainingCooldown.TotalSeconds:0}s.", "WARN");
                    return $"ERROR: Cooldown {remainingCooldown.TotalSeconds:0}s";
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // 4. Always revert back to UCS2 so incoming SMS (Tiếng Việt) doesn't break!
            await _modemService.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);
            await _modemService.SendCommandAsync(portName, "AT+CSMP=17,167,0,8", 5000, true);
            SmsInProgressPorts.TryRemove(portName, out _);
            sendLock.Release();
        }
        return "ERROR: Hết thời gian chờ hoặc hủy bỏ";
    }

    public string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string normalizedString = text.Normalize(NormalizationForm.FormD);
        StringBuilder stringBuilder = new StringBuilder();
        foreach (char c in normalizedString)
        {
            System.Globalization.UnicodeCategory unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }
        return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace("đ", "d").Replace("Đ", "D");
    }

    private string GetRandomForwardNumber(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var numbers = input.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(n => !string.IsNullOrWhiteSpace(n))
                           .Select(n => n.Trim())
                           .ToArray();
        if (numbers.Length == 0) return string.Empty;
        // Fix #3: Dùng static _rng thay vì new Random() mỗi lần
        int index = _rng.Next(numbers.Length);
        return numbers[index];
    }

    [RelayCommand]
    private void OpenCallManagerDialog()
    {
        CallManagerSelectedPort = Ports.Count > 0 ? Ports.FirstOrDefault(p => p.IsSelected)?.PortName ?? Ports.First().PortName : string.Empty;
        CallPhoneNumber = string.Empty;
        DtmfTones = string.Empty;
        ForwardNumber = string.Empty;
        CallManagerOutput = string.Empty;
        IsCallManagerDialogOpen = true;
    }

    [RelayCommand]
    private async Task CallManagerActionAsync(string action)
    {
        if (string.IsNullOrWhiteSpace(CallManagerSelectedPort))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn cổng để thực hiện.");
            return;
        }

        string cmd = string.Empty;
        
        switch (action)
        {
            case "Dial":
                if (string.IsNullOrWhiteSpace(CallPhoneNumber)) return;
                cmd = $"ATD{CallPhoneNumber};";
                break;
            case "Answer":
                cmd = "ATA";
                break;
            case "HangUp":
                cmd = "ATH";
                break;
            case "EnableClip":
                cmd = "AT+CLIP=1";
                break;
            case "EnableClir":
                cmd = "AT+CLIR=1";
                break;
            case "SendDtmf":
                if (string.IsNullOrWhiteSpace(DtmfTones)) return;
                cmd = $"AT+VTS=\"{DtmfTones}\"";
                break;
            case "SetForwarding":
                if (string.IsNullOrWhiteSpace(ForwardNumber)) return;
                cmd = $"AT+CCFC=0,1,\"{ForwardNumber}\",129";
                break;
            case "Hold":
                cmd = "AT+CHLD=2";
                break;
            case "CallStatus":
                cmd = "AT+CLCC";
                break;
            case "CallWaiting":
                cmd = "AT+CCWA=1,1,1";
                break;
        }

        if (string.IsNullOrEmpty(cmd)) return;

        CallManagerOutput += $"> {cmd}\n";
        try
        {
            string result = await _modemService.SendCommandAsync(CallManagerSelectedPort, cmd, timeoutMs: 5000);
            CallManagerOutput += $"{result}\n";
        }
        catch (Exception ex)
        {
            CallManagerOutput += $"[ERROR] {ex.Message}\n";
        }
    }

    // Network & Sim methods removed



    // Phân tích User Data Header (UDH) để lấy thông tin ghép tin nhắn dài (concatenated SMS).
    // udHex: chuỗi hex của phần User Data (bắt đầu bằng UDHL nếu hasUdh = true).
    // Trả về udhTotalBytes = tổng số byte của UDH (kể cả byte độ dài) để bên gọi bỏ qua khi đọc nội dung.
    private void ParseUdh(string udHex, out int udhTotalBytes, out int concatRef, out int concatTotal, out int concatSeq)
    {
        udhTotalBytes = 0;
        concatRef = 0;
        concatTotal = 0;
        concatSeq = 0;

        if (string.IsNullOrEmpty(udHex) || udHex.Length < 2) return;

        int udhl = int.Parse(udHex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        int endPos = (udhl + 1) * 2; // vị trí kết thúc UDH tính theo ký tự hex
        if (udHex.Length < endPos) return; // UDH khai báo dài hơn dữ liệu thực có -> PDU lỗi, bỏ qua

        udhTotalBytes = udhl + 1;

        int pos = 2; // bỏ qua byte UDHL, bắt đầu đọc các Information Element (IE)
        while (pos + 4 <= endPos)
        {
            int iei = int.Parse(udHex.Substring(pos, 2), System.Globalization.NumberStyles.HexNumber);
            int iedl = int.Parse(udHex.Substring(pos + 2, 2), System.Globalization.NumberStyles.HexNumber);
            int dataStart = pos + 4;
            if (dataStart + iedl * 2 > endPos) break; // IE khai báo vượt quá UDH -> dừng đọc

            if (iei == 0x00 && iedl == 3)
            {
                // Concat SMS - tham chiếu 8-bit: [ref][total][seq]
                concatRef = int.Parse(udHex.Substring(dataStart, 2), System.Globalization.NumberStyles.HexNumber);
                concatTotal = int.Parse(udHex.Substring(dataStart + 2, 2), System.Globalization.NumberStyles.HexNumber);
                concatSeq = int.Parse(udHex.Substring(dataStart + 4, 2), System.Globalization.NumberStyles.HexNumber);
            }
            else if (iei == 0x08 && iedl == 4)
            {
                // Concat SMS - tham chiếu 16-bit: [refHi][refLo][total][seq]
                concatRef = int.Parse(udHex.Substring(dataStart, 4), System.Globalization.NumberStyles.HexNumber);
                concatTotal = int.Parse(udHex.Substring(dataStart + 4, 2), System.Globalization.NumberStyles.HexNumber);
                concatSeq = int.Parse(udHex.Substring(dataStart + 6, 2), System.Globalization.NumberStyles.HexNumber);
            }

            pos = dataStart + iedl * 2;
        }
    }

    private string DecodePdu(string pdu, out string senderPhone, out int concatRef, out int concatTotal, out int concatSeq)
    {
        senderPhone = "UNKNOWN";
        concatRef = 0;
        concatTotal = 0;
        concatSeq = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(pdu) || pdu.Length < 14) return "";

            int smscLen = int.Parse(pdu.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            int smscEnd = 2 + smscLen * 2;

            // first octet of SMS-DELIVER
            int firstOctet = int.Parse(pdu.Substring(smscEnd, 2), System.Globalization.NumberStyles.HexNumber);
            bool hasUdh = (firstOctet & 0x40) != 0;

            int senderLen = int.Parse(pdu.Substring(smscEnd + 2, 2), System.Globalization.NumberStyles.HexNumber);
            int toa = int.Parse(pdu.Substring(smscEnd + 4, 2), System.Globalization.NumberStyles.HexNumber);
            bool isAlphaNumeric = ((toa & 0x70) == 0x50);
            
            int senderBytes = (senderLen + 1) / 2;
            int senderStart = smscEnd + 6;
            int senderEnd = senderStart + senderBytes * 2;
            
            // decode sender
            string senderHex = pdu.Substring(senderStart, senderBytes * 2);
            if (isAlphaNumeric)
            {
                byte[] toaBytes = new byte[senderHex.Length / 2];
                for (int i = 0; i < toaBytes.Length; i++)
                    toaBytes[i] = Convert.ToByte(senderHex.Substring(i * 2, 2), 16);
                
                string bitString = "";
                foreach (byte b in toaBytes)
                {
                    string bin = Convert.ToString(b, 2).PadLeft(8, '0');
                    char[] binArray = bin.ToCharArray();
                    Array.Reverse(binArray);
                    bitString += new string(binArray);
                }

                StringBuilder senderSb = new StringBuilder();
                for (int i = 0; i < bitString.Length; i += 7)
                {
                    if (i + 7 > bitString.Length) break;
                    string charBits = bitString.Substring(i, 7);
                    char[] charArray = charBits.ToCharArray();
                    Array.Reverse(charArray);
                    int charVal = Convert.ToInt32(new string(charArray), 2);
                    senderSb.Append((char)(charVal != 0 ? charVal : 64)); 
                }
                
                int numChars = (senderLen * 4) / 7;
                if (senderSb.Length > numChars) senderSb.Length = numChars;
                senderPhone = senderSb.ToString();
            }
            else
            {
                StringBuilder senderSb = new StringBuilder();
                for (int i = 0; i < senderHex.Length; i += 2)
                {
                    senderSb.Append(senderHex[i + 1]);
                    senderSb.Append(senderHex[i]);
                }
                if (senderSb.Length > 0 && senderSb[senderSb.Length - 1] == 'F') senderSb.Length--;
                senderPhone = senderSb.ToString();
            }

            int pid = int.Parse(pdu.Substring(senderEnd, 2), System.Globalization.NumberStyles.HexNumber);
            int dcs = int.Parse(pdu.Substring(senderEnd + 2, 2), System.Globalization.NumberStyles.HexNumber);
            
            int udlIdx = senderEnd + 18;
            int udl = int.Parse(pdu.Substring(udlIdx, 2), System.Globalization.NumberStyles.HexNumber);
            string ud = pdu.Substring(udlIdx + 2);
            
            bool isUcs2 = false;
            if ((dcs & 0xF0) < 0xE0) 
            {
                if (((dcs >> 2) & 0x03) == 0x02) isUcs2 = true;
            }
            if (dcs == 0x08 || dcs == 0x19 || dcs == 0x18 || dcs == 0x11) isUcs2 = true;
            
            int udhTotalBytesShared = 0;
            if (hasUdh)
            {
                ParseUdh(ud, out udhTotalBytesShared, out concatRef, out concatTotal, out concatSeq);
            }

            if (isUcs2)
            {
                StringBuilder sb = new StringBuilder();
                int start = udhTotalBytesShared * 2;
                for (int i = start; i < ud.Length && i < udl * 2; i += 4)
                {
                    if (i + 4 <= ud.Length)
                    {
                        sb.Append((char)Convert.ToInt32(ud.Substring(i, 4), 16));
                    }
                }
                return sb.ToString();
            }
            else
            {
                byte[] udBytes = new byte[ud.Length / 2];
                for (int i = 0; i < udBytes.Length; i++)
                    udBytes[i] = Convert.ToByte(ud.Substring(i * 2, 2), 16);

                string bitString = "";
                foreach (byte b in udBytes)
                {
                    string bin = Convert.ToString(b, 2).PadLeft(8, '0');
                    char[] binArray = bin.ToCharArray();
                    Array.Reverse(binArray);
                    bitString += new string(binArray);
                }

                int startIndexBits = 0;
                if (hasUdh)
                {
                    int udhBits = udhTotalBytesShared * 8;
                    int fillBits = 7 - (udhBits % 7);
                    if (fillBits == 7) fillBits = 0;
                    startIndexBits = udhBits + fillBits;
                }

                StringBuilder sb = new StringBuilder();
                for (int i = startIndexBits; i < bitString.Length; i += 7)
                {
                    if (i + 7 > bitString.Length) break;
                    string charBits = bitString.Substring(i, 7);
                    char[] charArray = charBits.ToCharArray();
                    Array.Reverse(charArray);
                    int charVal = Convert.ToInt32(new string(charArray), 2);
                    sb.Append((char)(charVal != 0 ? charVal : 64)); 
                }
                
                int charsToRead = hasUdh ? (udl - ((startIndexBits) / 7)) : udl;
                if (charsToRead >= 0 && sb.Length > charsToRead) sb.Length = charsToRead;
                else if (charsToRead < 0) sb.Clear(); // or handle it somehow, maybe it's just invalid PDU. sb.Length = 0 is safe.
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            return $"Lỗi giải mã PDU: {ex.Message}";
        }
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

            string decoded = sb.ToString();
            if (Regex.IsMatch(hexString, @"^\d+$") && decoded.Any(c => c > 0x2E00))
            {
                return hexString;
            }
            return decoded;
        }
        catch
        {
            return hexString; // Trả về nguyên bản nếu lỗi
        }
    }

    private static readonly object _cacheLock = new object();

    // Property hiển thị địa chỉ API ở Status Bar
    public string ApiServerUrl =>
        SettingsService.Current.EnableApiServer
            ? $"API: http://localhost:{SettingsService.Current.ApiServerPort}/api"
            : string.Empty;


    // Import file Excel → gửi SMS hàng loạt
    [RelayCommand]
    private async Task ImportAndSendBulkSms()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Chọn file Excel chứa danh sách SMS",
            Filter = "Excel files (*.xlsx)|*.xlsx|Tất cả file|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var items = BulkSmsService.ReadFromExcel(dialog.FileName);
            if (items.Count == 0)
            {
                SnackbarMessageQueue.Enqueue("File Excel không có dữ liệu hợp lệ (cần từ dòng 2 trở đi, cột A = SĐT, cột B = Nội dung).");
                return;
            }

            // Lấy danh sách cổng đang hoạt động
            var activePorts = GetPortsSnapshot().Where(IsActive).Select(p => p.PortName).ToList();
            if (activePorts.Count == 0)
            {
                SnackbarMessageQueue.Enqueue("Không có cổng SIM nào đang hoạt động.");
                return;
            }

            SnackbarMessageQueue.Enqueue($"Đang gửi {items.Count} SMS... (mỗi tin cách nhau 2 giây)");
            AddLog($"[BULK SMS] Bắt đầu gửi {items.Count} tin nhắn từ file: {System.IO.Path.GetFileName(dialog.FileName)}");

            int portIdx = 0;
            int sent = 0, failed = 0;

            foreach (var (phone, content) in items)
            {
                // Phân phối luân phiên giữa các cổng SIM
                string sourcePort = activePorts[portIdx % activePorts.Count];
                portIdx++;

                try
                {
                    await _modemService.SendCommandAsync(sourcePort, "AT+CSCS=\"GSM\"", 5000, true);
                    await _modemService.SendCommandAsync(sourcePort, "AT+CSMP=17,167,0,0", 5000, true);

                    string safeContent = RemoveDiacritics(content);
                    string result = await _modemService.SendSmsAsync(sourcePort, phone, safeContent, timeoutMs: 30000);

                    var port = Ports.FirstOrDefault(p => p.PortName == sourcePort);
                    if (result.Contains("OK") || result.Contains("+CMGS:"))
                    {
                        AddLog($"[BULK SMS] [{sourcePort}] → {phone}: OK", "SUCCESS");
                        if (port != null) { port.LastSmsResult = "Gửi thành công"; port.UpdateDisplayResult(CommandPanelTab); }
                        sent++;
                    }
                    else
                    {
                        AddLog($"[BULK SMS] [{sourcePort}] → {phone}: FAIL — {result}", "ERROR");
                        if (port != null) { port.LastSmsResult = result; port.UpdateDisplayResult(CommandPanelTab); }
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"[BULK SMS] [{sourcePort}] → {phone}: FAIL — {ex.Message}", "ERROR");
                    failed++;
                }
                finally
                {
                    await _modemService.SendCommandAsync(sourcePort, "AT+CSCS=\"UCS2\"", 5000, true);
                    await _modemService.SendCommandAsync(sourcePort, "AT+CSMP=17,167,0,8", 5000, true);
                }

                await Task.Delay(2000); // 2 giây giữa các tin
            }

            AddLog($"[BULK SMS] Hoàn thành: {sent} thành công, {failed} thất bại.", sent > 0 ? "SUCCESS" : "ERROR");
            SnackbarMessageQueue.Enqueue($"Gửi xong: {sent}/{items.Count} tin nhắn thành công.");
        }
        catch (Exception ex)
        {
            AddLog($"[BULK SMS] Lỗi đọc file: {ex.Message}", "ERROR");
            SnackbarMessageQueue.Enqueue($"Lỗi: {ex.Message}");
        }
    }

    private void LoadSimCache()
    {
        lock (_cacheLock)
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
    }

    private void SaveSimCache()
    {
        lock (_cacheLock)
        {
            try
            {
                var dictToSave = new Dictionary<string, string>();
                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        string existingJson = File.ReadAllText(_cacheFilePath);
                        var diskDict = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson);
                        if (diskDict != null)
                        {
                            foreach (var kvp in diskDict)
                            {
                                dictToSave[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch { }
                }

                // Add or update with current session's cache
                foreach (var kvp in _simCache)
                {
                    dictToSave[kvp.Key] = kvp.Value;
                }

                var json = JsonSerializer.Serialize(dictToSave);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch { }
        }
    }

    private void LoadImeiCache()
    {
        lock (_imeiCacheLock)
        {
            if (File.Exists(_imeiCacheFilePath))
            {
                try
                {
                    var lines = File.ReadAllLines(_imeiCacheFilePath);
                    var newCache = new ConcurrentDictionary<string, SimBackupEntry>();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = ParseCsvLine(line);
                        if (parts.Length >= 2)
                        {
                            string ccid = NormalizeCcid(parts[0]);
                            string imei = NormalizeImei(parts[1]);
                            if (!string.IsNullOrEmpty(ccid) && !string.IsNullOrEmpty(imei))
                            {
                                var entry = new SimBackupEntry
                                {
                                    Ccid = ccid,
                                    Imei = imei,
                                    PhoneNumber = parts.Length >= 3 ? parts[2].Trim() : string.Empty,
                                    CreatedAt = parts.Length >= 4 ? parts[3].Trim() : string.Empty,
                                    LicenseKeySuffix = parts.Length >= 5 ? parts[4].Trim() : string.Empty,
                                    KeyMismatch = parts.Length >= 6 ? parts[5].Trim() : string.Empty,
                                    SourceFile = "imei_backup.csv"
                                };
                                newCache[ccid] = entry;
                                if (!string.IsNullOrWhiteSpace(entry.PhoneNumber))
                                {
                                    _simCache[ccid] = entry.PhoneNumber;
                                }
                            }
                        }
                    }
                    _imeiCache = newCache;
                    AddLog($"[IMEI_SOURCE] Đã nạp {newCache.Count} dòng từ imei_backup.csv.", "SUCCESS");
                }
                catch (Exception ex)
                {
                    AddLog($"Lỗi đọc imei_backup.csv: {ex.Message}", "ERROR");
                }
            }
        }
    }

    private void SaveImeiCache()
    {
        lock (_imeiCacheLock)
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("CCID,IMEI,PhoneNumber,CreatedAt,LicenseKeySuffix,KeyMismatch");
                foreach (var kvp in _imeiCache)
                {
                    var entry = kvp.Value;
                    builder.AppendLine(string.Join(",", new[]
                    {
                        EscapeCsv(entry.Ccid),
                        EscapeCsv(entry.Imei),
                        EscapeCsv(entry.PhoneNumber),
                        EscapeCsv(entry.CreatedAt),
                        EscapeCsv(entry.LicenseKeySuffix),
                        EscapeCsv(entry.KeyMismatch)
                    }));
                }

                string tempPath = _imeiCacheFilePath + ".tmp";
                File.WriteAllText(tempPath, builder.ToString());

                if (File.Exists(_imeiCacheFilePath))
                {
                    string backupPath = _imeiCacheFilePath.Replace(".csv", ".backup.csv");
                    File.Copy(_imeiCacheFilePath, backupPath, overwrite: true);
                }

                File.Move(tempPath, _imeiCacheFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AddLog($"Lỗi ghi file imei_backup.csv: {ex.Message}", "ERROR");
            }
        }
    }

    private void AddNewImeiCacheEntry(SimBackupEntry newEntry)
    {
        if (newEntry == null || string.IsNullOrEmpty(newEntry.Ccid)) return;
        lock (_imeiCacheLock)
        {
            _imeiCache[newEntry.Ccid] = newEntry;
            SaveImeiCache();
        }
    }

    private void UpdateImeiCacheEntry(string ccid, Action<SimBackupEntry> updateAction)
    {
        if (string.IsNullOrEmpty(ccid)) return;
        lock (_imeiCacheLock)
        {
            if (_imeiCache.TryGetValue(ccid, out var entry))
            {
                updateAction(entry);
                SaveImeiCache();
            }
        }
    }

    private static string NormalizeImei(string? imei)
    {
        if (string.IsNullOrWhiteSpace(imei)) return string.Empty;
        var match = Regex.Match(imei, @"\b(\d{14,17})\b");
        return match.Success ? match.Groups[1].Value : imei.Replace("OK", "").Replace("ERROR", "").Trim();
    }

    private static string NormalizeCcid(string? ccid)
    {
        if (string.IsNullOrWhiteSpace(ccid)) return string.Empty;
        string clean = ccid.Replace("+CCID:", "")
                           .Replace("OK", "")
                           .Replace("ERROR", "")
                           .Replace("\r", "")
                           .Replace("\n", "")
                           .Trim();
        clean = Regex.Replace(clean, @"\s+", "");
        return clean;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static string EscapeCsv(string? value)
    {
        string text = value ?? string.Empty;
        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\r') && !text.Contains('\n'))
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private void ImportCsvToImeiCache()
    {
        string directoryPath = AppPaths.RuntimeDirectory;
        if (!System.IO.Directory.Exists(directoryPath)) return;

        bool hasNewImei = false;
        bool hasNewSim = false;

        try
        {
            var csvFiles = System.IO.Directory.GetFiles(directoryPath, "imei-lookup-*.csv");
            foreach (var csvPath in csvFiles)
            {
                int importedRows = 0;
                string sourceFile = System.IO.Path.GetFileName(csvPath);
                string[] lines = System.IO.File.ReadAllLines(csvPath);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = ParseCsvLine(line);
                    if (parts.Length >= 2)
                    {
                        string serial = NormalizeCcid(parts[0]);
                        string imei = NormalizeImei(parts[1]);
                        string phone = parts.Length >= 3 ? parts[2].Trim() : "";
                        string createdAt = parts.Length >= 4 ? parts[3].Trim() : "";
                        string licenseKeySuffix = parts.Length >= 5 ? parts[4].Trim() : "";
                        string keyMismatch = parts.Length >= 6 ? parts[5].Trim() : "";

                        if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(imei))
                        {
                            if (_imeiCache.TryGetValue(serial, out var existingEntry))
                            {
                                string normExisting = NormalizeImei(existingEntry.Imei);
                                bool isChanged = normExisting != imei ||
                                                 existingEntry.PhoneNumber != phone ||
                                                 existingEntry.CreatedAt != createdAt ||
                                                 existingEntry.LicenseKeySuffix != licenseKeySuffix ||
                                                 existingEntry.KeyMismatch != keyMismatch;

                                if (isChanged)
                                {
                                    if (normExisting != imei)
                                    {
                                        AddLog($"[IMEI_CONFLICT] Keep imei_backup.csv value for SIM {serial}. Lookup source={sourceFile} is not allowed to overwrite existing backup.", "WARN");
                                        AddLog($"[IMEI_CONFLICT] Xung đột IMEI cho SIM {serial}: Cache={normExisting}, CSV={imei}. Chọn giá trị từ CSV.", "WARN");
                                    }
                                    if (normExisting == imei)
                                    {
                                        existingEntry.Imei = imei;
                                    }
                                    if (string.IsNullOrWhiteSpace(existingEntry.PhoneNumber))
                                    {
                                        existingEntry.PhoneNumber = phone;
                                    }
                                    if (string.IsNullOrWhiteSpace(existingEntry.CreatedAt))
                                    {
                                        existingEntry.CreatedAt = createdAt;
                                    }
                                    if (string.IsNullOrWhiteSpace(existingEntry.LicenseKeySuffix))
                                    {
                                        existingEntry.LicenseKeySuffix = licenseKeySuffix;
                                    }
                                    if (string.IsNullOrWhiteSpace(existingEntry.KeyMismatch))
                                    {
                                        existingEntry.KeyMismatch = keyMismatch;
                                    }
                                    hasNewImei = true;
                                }
                            }
                            else
                            {
                                var entry = new SimBackupEntry
                                {
                                    Ccid = serial,
                                    Imei = imei,
                                    PhoneNumber = phone,
                                    CreatedAt = createdAt,
                                    LicenseKeySuffix = licenseKeySuffix,
                                    KeyMismatch = keyMismatch,
                                    SourceFile = sourceFile
                                };
                                _imeiCache[serial] = entry;
                                hasNewImei = true;
                            }
                            importedRows++;
                        }

                        if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(phone))
                        {
                            if (phone.StartsWith("+84", StringComparison.Ordinal))
                            {
                                phone = "0" + phone.Substring(3);
                            }
                            else if (phone.StartsWith("84", StringComparison.Ordinal) && phone.Length >= 11)
                            {
                                phone = "0" + phone.Substring(2);
                            }

                            if (!_simCache.TryGetValue(serial, out var existingPhone) || existingPhone != phone)
                            {
                                _simCache[serial] = phone;
                                hasNewSim = true;
                            }
                        }
                    }
                }
                AddLog($"[IMEI_SOURCE] Đã nạp {importedRows} dòng từ {System.IO.Path.GetFileName(csvPath)}.", "SUCCESS");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Lỗi nạp CSV: {ex.Message}", "ERROR");
        }

        if (hasNewImei)
        {
            SaveImeiCache();
        }
        if (hasNewSim)
        {
            SaveSimCache();
        }
    }

    public void Dispose()
    {
        if (!_lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts.Cancel();
        }

        _firebaseService.Stop();
        _firebaseService.Dispose();
        _apiServerService?.Stop();
        _modemService.DisconnectAll();

        _activeRamRecordings.Clear();
        _activeCallers.Clear();

        foreach (var semaphore in _smsSendLocks.Values)
        {
            semaphore.Dispose();
        }

        _ussdSendLock.Dispose();
        _lifetimeCts.Dispose();
    }

    [RelayCommand]
    private void ToggleCommandPanel()
    {
        IsCommandPanelOpen = !IsCommandPanelOpen;
    }

    [RelayCommand]
    private void CloseCommandPanel()
    {
        IsCommandPanelOpen = false;
    }

    [RelayCommand]
    private void SelectCommandPanelTab(string type)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            CommandPanelTab = type;
            ClearCommandPanelErrors();
            
            // Cập nhật hiển thị kết quả cho tất cả các cổng
            foreach (var port in Ports)
            {
                port.UpdateDisplayResult(type);
            }
        }
    }

    [RelayCommand]
    private void AddCommandQueue()
    {
        ClearCommandPanelErrors();
        bool isValid = true;

        if (CommandPanelTab == "USSD" && string.IsNullOrWhiteSpace(CommandPanelUssdText))
        {
            HasUssdError = true; isValid = false;
        }
        else if (CommandPanelTab == "SMS")
        {
            if (string.IsNullOrWhiteSpace(CommandPanelSmsRecipient)) { HasSmsRecipientError = true; isValid = false; }
            if (string.IsNullOrWhiteSpace(CommandPanelSmsContent)) { HasSmsContentError = true; isValid = false; }
        }
        else if (CommandPanelTab == "Call" && string.IsNullOrWhiteSpace(CommandPanelCallNumber))
        {
            HasCallNumberError = true; isValid = false;
        }
        else if (CommandPanelTab == "Data" && CommandPanelDataAmount <= 0)
        {
            HasDataAmountError = true; isValid = false;
        }
        else if (CommandPanelTab == "Delay" && CommandPanelDelaySeconds <= 0)
        {
            HasDelaySecondsError = true; isValid = false;
        }

        if (!isValid) return;

        UpsertCommandQueue(
            Guid.NewGuid().ToString("N")[..8],
            "",
            CommandPanelTab,
            GetCommandPanelRecipient(),
            GetCommandPanelContent(),
            CurrentCommandPanelMode);
    }

    [RelayCommand]
    private async Task RunSingleCommandQueueAsync()
    {
        ClearCommandPanelErrors();
        bool isValid = true;

        if (CommandPanelTab == "USSD" && string.IsNullOrWhiteSpace(CommandPanelUssdText))
        {
            HasUssdError = true; isValid = false;
        }
        else if (CommandPanelTab == "SMS")
        {
            if (string.IsNullOrWhiteSpace(CommandPanelSmsRecipient)) { HasSmsRecipientError = true; isValid = false; }
            if (string.IsNullOrWhiteSpace(CommandPanelSmsContent)) { HasSmsContentError = true; isValid = false; }
        }
        else if (CommandPanelTab == "Call" && string.IsNullOrWhiteSpace(CommandPanelCallNumber))
        {
            HasCallNumberError = true; isValid = false;
        }
        else if (CommandPanelTab == "Data" && CommandPanelDataAmount <= 0)
        {
            HasDataAmountError = true; isValid = false;
        }
        else if (CommandPanelTab == "Delay" && CommandPanelDelaySeconds <= 0)
        {
            HasDelaySecondsError = true; isValid = false;
        }

        if (!isValid) return;

        var singleItem = new CommandQueueItem
        {
            CommandId = Guid.NewGuid().ToString("N")[..8],
            Recipient = GetCommandPanelRecipient(),
            Type = CommandPanelTab,
            Content = GetCommandPanelContent(),
            Status = "Chờ"
        };

        var targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
        if (!targetPorts.Any() && SelectedPort != null && IsActive(SelectedPort))
            targetPorts.Add(SelectedPort);

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn cổng để thực thi.");
            return;
        }

        foreach (var p in targetPorts)
        {
            if (SmsInProgressPorts.ContainsKey(p.PortName))
            {
                SnackbarMessageQueue.Enqueue("Đang có tác vụ chạy trên cổng " + p.PortName + ". Vui lòng đợi.");
                return;
            }
        }

        foreach (var p in targetPorts)
        {
            SmsInProgressPorts[p.PortName] = true;
        }

        SnackbarMessageQueue.Enqueue($"Bắt đầu chạy lệnh {CommandPanelTab}...");

        try
        {
            var tasks = new List<Task>();
            foreach (var p in targetPorts)
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (!_lifetimeCts.Token.IsCancellationRequested)
                    {
                        await ExecuteCommandQueueItemAsync(p.PortName, singleItem);
                    }
                }));
            }
            await Task.WhenAll(tasks);
            SnackbarMessageQueue.Enqueue($"Đã chạy xong lệnh {CommandPanelTab}.");
        }
        finally
        {
            foreach (var p in targetPorts)
                SmsInProgressPorts.TryRemove(p.PortName, out _);
        }
    }

    [RelayCommand]
    private async Task RunSingleWithErrorCommandQueueAsync()
    {
        await RunSingleCommandQueueAsync();
    }

    private string GetCommandPanelRecipient() => CommandPanelTab switch
    {
        "SMS" => CommandPanelSmsRecipient,
        "MMS" => CommandPanelMmsRecipients,
        "Call" => CommandPanelCallNumber,
        _ => ""
    };

    private string GetCommandPanelContent() => CommandPanelTab switch
    {
        "USSD" => CommandPanelUssdText,
        "SMS" => CommandPanelSmsContent,
        "MMS" => CommandPanelMmsTitle,
        "Call" => $"{CommandPanelCallDuration}|{CommandPanelCallDtmf}",
        "Data" => $"{CommandPanelDataAmount} KB",
        "IMEI" => CommandPanelImeiValue,
        "Delay" => $"{CommandPanelDelaySeconds}s",
        _ => ""
    };



    [RelayCommand]
    private void ClearCommandQueue()
    {
        CommandQueue.Clear();
        UpdateCommandCounts();
    }

    [RelayCommand]
    private async Task RunCommandQueueAsync()
    {
        var items = CommandQueue.Reverse().ToList();
        if (!items.Any())
        {
            SnackbarMessageQueue.Enqueue("Chưa có lệnh trong kịch bản.");
            return;
        }

        var targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
        if (!targetPorts.Any() && SelectedPort != null && IsActive(SelectedPort))
            targetPorts.Add(SelectedPort);

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn cổng để thực thi.");
            return;
        }

        foreach (var p in targetPorts)
        {
            if (SmsInProgressPorts.ContainsKey(p.PortName))
            {
                SnackbarMessageQueue.Enqueue("Đang có tác vụ chạy trên cổng " + p.PortName + ". Vui lòng đợi.");
                return;
            }
        }

        foreach (var p in targetPorts)
        {
            SmsInProgressPorts[p.PortName] = true;
        }

        SnackbarMessageQueue.Enqueue("Bắt đầu chạy kịch bản...");

        try
        {
            var tasks = new List<Task>();
            foreach (var p in targetPorts)
            {
                tasks.Add(Task.Run(async () =>
                {
                    foreach (var item in items)
                    {
                        if (_lifetimeCts.Token.IsCancellationRequested) break;
                        await ExecuteCommandQueueItemAsync(p.PortName, item);
                    }
                }));
            }
            await Task.WhenAll(tasks);
            SnackbarMessageQueue.Enqueue("Đã chạy xong kịch bản.");
        }
        finally
        {
            foreach (var p in targetPorts)
                SmsInProgressPorts.TryRemove(p.PortName, out _);
        }
    }

    private async Task ExecuteCommandQueueItemAsync(string portName, CommandQueueItem item)
    {
        Application.Current.Dispatcher.Invoke(() => item.Status = "Đang chạy");
        UpdateCommandCounts();

        try
        {
            string finalResult = "";
            var port = Ports.FirstOrDefault(p => p.PortName == portName);
            string cmdType = item.Type ?? "";

            if (port != null) 
            {
                if (cmdType == "USSD") port.LastUssdResult = "Đang chạy...";
                else if (cmdType == "SMS") port.LastSmsResult = "Đang chạy...";
                else if (cmdType == "Call") port.LastCallResult = "Đang chạy...";
                else if (cmdType == "MMS") port.LastMmsResult = "Đang chạy...";
                else if (cmdType == "IMEI") port.LastImeiResult = "Đang chạy...";
                else if (cmdType == "Data") port.LastDataResult = "Đang chạy...";
                else if (cmdType == "Delay") port.LastDelayResult = "Đang chạy...";
                port.UpdateDisplayResult(CommandPanelTab);
            }
            if (cmdType == "USSD")
            {
                finalResult = await SendUssdThrottledAsync(portName, item.Content, "Kịch bản", maxAttempts: CommandPanelRetryCount + 1);
                if (finalResult.Contains("OK")) finalResult = "Đang chờ nhà mạng phản hồi...";
            }
            else if (cmdType == "SMS")
            {
                finalResult = await SendSmsThrottledAsync(portName, item.Recipient, item.Content);
            }
            else if (cmdType == "Call")
            {
                string cleanNumber = (item.Recipient ?? "").Replace(" ", "").Replace("-", "");
                finalResult = await _modemService.SendCommandAsync(portName, "ATD" + cleanNumber + ";", timeoutMs: 15000);
                
                if (finalResult.Contains("OK"))
                {
                    finalResult = "Đang gọi...";
                    
                    // Parse duration and dtmf from Content: "duration|dtmf"
                    string[] parts = (item.Content ?? "").Split('|');
                    string durationStr = parts.Length > 0 ? parts[0] : "";
                    
                    if (int.TryParse(durationStr, out int duration) && duration > 0)
                    {
                        finalResult = $"Đang gọi (Tự tắt sau {duration}s)";
                        
                        // Cập nhật UI ngay lập tức để báo đang chờ
                        if (port != null) port.LastCallResult = finalResult;
                        port?.UpdateDisplayResult(CommandPanelTab);
                        
                        _callFailures.TryRemove(portName, out _);
                        bool failed = false;
                        
                        // Chờ đúng thời gian được cấu hình (kiểm tra mỗi 500ms xem cuộc gọi có bị từ chối sớm không)
                        for (int i = 0; i < duration * 2; i++)
                        {
                            await Task.Delay(500);
                            if (_callFailures.TryGetValue(portName, out string? failReason))
                            {
                                finalResult = $"Cuộc gọi thất bại ({failReason})";
                                failed = true;
                                break;
                            }
                        }
                        
                        if (!failed)
                        {
                            await _modemService.SendCommandAsync(portName, "ATH", timeoutMs: 5000);
                            finalResult = "Gọi thành công";
                        }
                    }
                }
            }
            else if (cmdType == "Delay")
            {
                if (int.TryParse(item.Content.Replace("s", ""), out int d))
                    await Task.Delay(d * 1000);
                finalResult = "Đã chờ xong";
            }
            else if (cmdType == "Data")
            {
                if (int.TryParse(item.Content.Replace(" KB", "").Replace(" ", ""), out int kb))
                {
                    await ConsumeDataQuectelAsync(portName, kb);
                    finalResult = "Đã tiêu thụ Data";
                }
            }
            else
            {
                finalResult = "Lệnh không hợp lệ";
                Application.Current.Dispatcher.Invoke(() => { item.Result = "Bỏ qua"; item.Error = "Chưa hỗ trợ"; });
            }

            if (port != null)
            {
                string currentRes = cmdType switch 
                {
                    "USSD" => port.LastUssdResult,
                    "SMS" => port.LastSmsResult,
                    "Call" => port.LastCallResult,
                    "MMS" => port.LastMmsResult,
                    "IMEI" => port.LastImeiResult,
                    "Data" => port.LastDataResult,
                    "Delay" => port.LastDelayResult,
                    _ => ""
                };

                // [FIX RACE CONDITION]: Nếu nhà mạng trả về kết quả (+CUSD) quá nhanh, 
                // sự kiện LogMessage đã cập nhật LastCommandResult thành kết quả thực sự.
                // Do đó, ta chỉ ghi đè "Đang chờ nhà mạng phản hồi..." nếu kết quả hiện tại vẫn là "Đang chạy..." hoặc "Đang khởi chạy...".
                if (finalResult == "Đang chờ nhà mạng phản hồi..." || finalResult == "Đang gọi...")
                {
                    if (currentRes == "Đang chạy..." || currentRes == "Đang khởi chạy...")
                    {
                        if (cmdType == "USSD") port.LastUssdResult = finalResult;
                        else if (cmdType == "SMS") port.LastSmsResult = finalResult;
                        else if (cmdType == "Call") port.LastCallResult = finalResult;
                    }
                }
                else
                {
                    if (cmdType == "USSD") port.LastUssdResult = finalResult;
                    else if (cmdType == "SMS") port.LastSmsResult = finalResult;
                    else if (cmdType == "Call") port.LastCallResult = finalResult;
                    else if (cmdType == "MMS") port.LastMmsResult = finalResult;
                    else if (cmdType == "IMEI") port.LastImeiResult = finalResult;
                    else if (cmdType == "Data") port.LastDataResult = finalResult;
                    else if (cmdType == "Delay") port.LastDelayResult = finalResult;
                }
                
                port.UpdateDisplayResult(CommandPanelTab);
            }

            Application.Current.Dispatcher.Invoke(() => item.Status = "Xong");
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => { item.Status = "Lỗi"; item.Error = ex.Message; });
        }
        
        UpdateCommandCounts();
    }

    private async Task ConsumeDataQuectelAsync(string portName, int kilobytes)
    {
        // 1. Kích hoạt mạng 4G/3G (PDP Context)
        await _modemService.SendCommandAsync(portName, "AT+QIACT=1", timeoutMs: 15000);
        
        // 2. Cấu hình HTTP (Context ID = 1)
        await _modemService.SendCommandAsync(portName, "AT+QHTTPCFG=\"contextid\",1", timeoutMs: 3000);
        await _modemService.SendCommandAsync(portName, "AT+QHTTPCFG=\"responseheader\",0", timeoutMs: 3000);
        
        // Link tải 1 file rác ~100KB để nuốt dung lượng Data
        string testUrl = "http://speedtest.ftp.otenet.gr/files/test100k.db"; 
        
        // Tính số lần tải cần thiết (ví dụ nhập 500 KB => tải 5 lần)
        int loops = kilobytes / 100;
        if (loops == 0) loops = 1;
        
        for (int i = 0; i < loops; i++)
        {
            // Báo độ dài URL cho Modem biết
            string resp = await _modemService.SendCommandAsync(portName, $"AT+QHTTPURL={testUrl.Length},80", timeoutMs: 10000);
            
            // Modem phản hồi chữ CONNECT nghĩa là nó đã sẵn sàng nhận link gốc
            if (resp.Contains("CONNECT"))
            {
                // Gửi Link dạng RAW (không kèm dấu enter \r\n ở đuôi, vì modem chỉ đọc đúng Length byte)
                await _modemService.SendRawAsync(portName, testUrl, timeoutMs: 10000);
                
                // Bắt đầu lệnh tải (Timeout 60s cho mạng chậm)
                await _modemService.SendCommandAsync(portName, "AT+QHTTPGET=80", timeoutMs: 60000);
            }
            
            await Task.Delay(1000); // Nghỉ 1 giây giữa các lần tải
        }
    }

    [RelayCommand]
    private async Task RunWithErrorCommandQueueAsync()
    {
        SnackbarMessageQueue.Enqueue("Tính năng chưa được hỗ trợ.");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenImeiManager()
    {
        var win = new ImeiManagerWindow();
        win.ShowDialog();
    }
}

public partial class ExportColumnItem : ObservableObject
{
    [ObservableProperty]
    private string _columnName;

    [ObservableProperty]
    private string _bindingPath;

    [ObservableProperty]
    private bool _isSelected;

    public ExportColumnItem(string columnName, string bindingPath, bool isSelected = true)
    {
        ColumnName = columnName;
        BindingPath = bindingPath;
        IsSelected = isSelected;
    }
}

