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
    public IGsmModemService ModemService => _modemService;

    private readonly SpeechToTextService _speechToTextService;
    private readonly FirebaseService _firebaseService;
    private readonly ApiServerService? _apiServerService;
    private readonly ConcurrentDictionary<string, AudioRecordingService> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, string> _activeCallers = new();
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

    [ObservableProperty]
    private string _composeSmsPhone = string.Empty;

    [ObservableProperty]
    private string _composeSmsContent = string.Empty;

    [ObservableProperty]
    private bool _isComposeSmsDialogOpen;

    [ObservableProperty]
    private string _composeSmsMode = "Selected";

    [ObservableProperty]
    private string _customUssdCode = string.Empty;
    
    [ObservableProperty]
    private string _customUssdOutput = string.Empty;

    [ObservableProperty]
    private bool _isCustomUssdDialogOpen;

    [ObservableProperty]
    private string _customUssdMode = "Selected";



    [ObservableProperty]
    private bool _isCallManagerDialogOpen;

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

    [ObservableProperty]
    private bool _isNetworkSimDialogOpen;

    [ObservableProperty]
    private string _networkSimSelectedPort = string.Empty;

    [ObservableProperty]
    private string _networkSimOutput = string.Empty;

    [ObservableProperty]
    private string _networkOperator = string.Empty;

    [ObservableProperty]
    private string _pinCode = string.Empty;

    [ObservableProperty]
    private string _phonebookIndex = string.Empty;

    [ObservableProperty]
    private string _phonebookNumber = string.Empty;

    [ObservableProperty]
    private string _phonebookName = string.Empty;

    [ObservableProperty]
    private string _ussdCommand = string.Empty;

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

    public System.Collections.IEnumerable FilteredSmsMessages =>
        SmsMessages.Where(s =>
            MatchesFilter(s.ReceiverPhone, SmsPhoneFilter) &&
            MatchesFilter(s.PortName, SmsPortFilter) &&
            MatchesFilter(s.Sender, SmsSenderFilter));

    public int TotalPortCount => Ports.Count;
    public int OnlinePortCount => Ports.Count(IsActive);
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

        LoadSimCache();
        LoadImeiCache();
        ImportCsvToImeiCache();
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
                        await SendUssdThrottledAsync(p.PortName, ussdCode, "Làm mới số dư tự động", maxRetries: 1);
                        await Task.Delay(2000, lifetimeToken);
                    }
                }
            }
        }, lifetimeToken);

        // Ping SIM định kỳ để phát hiện SIM mất kết nối
        _ = Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                int intervalMin = SettingsService.Current.SimPingIntervalMinutes > 0
                    ? SettingsService.Current.SimPingIntervalMinutes : 5;
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMin), lifetimeToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!SettingsService.Current.EnableSimPing) continue;

                var portsCopy = GetPortsSnapshot();
                foreach (var p in portsCopy)
                {
                    if (lifetimeToken.IsCancellationRequested) break;
                    try
                    {
                        string resp = await _modemService.SendCommandAsync(p.PortName, "AT", timeoutMs: 3000, silent: true);
                        if (!resp.Contains("OK"))
                        {
                            if (p.Status != SimStatus.NoResponse)
                            {
                                Application.Current.Dispatcher.Invoke(() => p.Status = SimStatus.NoResponse);
                                RecordPortError(p.PortName, "SIM ping timeout");
                                AddLog($"[{p.PortName}] SIM không phản hồi khi ping!", "WARN");
                                _ = TelegramService.SendMessageAsync($"⚠️ <b>SIM Không Phản Hồi</b>\nCổng: {p.PortName}\nSĐT: {p.PhoneNumber}");
                                ToastService.ShowSimOffline(p.PortName);
                            }
                        }
                        else if (p.Status == SimStatus.NoResponse)
                        {
                            Application.Current.Dispatcher.Invoke(() => p.Status = SimStatus.Active);
                            AddLog($"[{p.PortName}] SIM đã khôi phục kết nối.", "SUCCESS");
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordPortError(p.PortName, ex.Message);
                    }
                    await Task.Delay(500, lifetimeToken);
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
                        AddLog($"[WATCHDOG] Cổng {p.PortName} mất kết nối. Tự động gửi lệnh phục hồi (AT+CFUN=1,1)...", "WARN");
                        _ = _modemService.SendCommandAsync(p.PortName, "AT+CFUN=1,1", silent: true);
                    }
                }
            }
        }, lifetimeToken);
    }

    private void UpdateDashboard()
    {
        int activeCount = Ports.Count(IsActive);
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

            port.LastError = error;
            if (error.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                port.TimeoutCount++;
            }
            if (error.Contains("SMS", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
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

        Task.Run(() =>
        {
            _modemService.ConnectAll(115200);
        });
        
        StartAutoPortWatcher();
    }

    private void StartAutoPortWatcher()
    {
        var lifetimeToken = _lifetimeCts.Token;
        Task.Run(async () =>
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, lifetimeToken); // Quét 3 giây 1 lần
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                
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
            }
        }, lifetimeToken);
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
                await Task.Delay(1000);
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
                await Task.Delay(1000); 
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
            await Task.Delay(1000);
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
            await Task.Delay(1000); 
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
                if (e.Data.StartsWith("[PARSE_CCID]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+COPS:") || e.Data.StartsWith("+CUSD:"))
                {
                    port = new SimPort { PortName = e.PortName, Status = SimStatus.Active, SignalStrength = 0 };
                    port.ReconnectCount++;
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
                    var strictMatch = Regex.Match(ussdContent, @"(?:TK\s*goc|TKG|TK\s*chinh|TKC|Tai khoan chinh|Tài khoản chính|So du|Số dư)[^\d]*?(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ)", RegexOptions.IgnoreCase);
                    if (strictMatch.Success) 

                    {
                        port.Balance = strictMatch.Groups[1].Value + " " + strictMatch.Groups[2].Value;
                    }
                    else
                    {
                        // Fallback nếu nhà mạng trả về format lạ, nhưng phải tránh các từ khóa rác và tránh cước phí (vd: 1000d/ngay)
                        if (!Regex.IsMatch(ussdContent, @"khong du|chua du|cuoc|uu dai|tang|gia|khong lo|ho tro|phi", RegexOptions.IgnoreCase))
                        {
                            var fallback = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ)(?!/)", RegexOptions.IgnoreCase);
                            if (fallback.Success) port.Balance = fallback.Groups[1].Value + " " + fallback.Groups[2].Value;
                        }
                    }

                    var expiryMatch = Regex.Match(ussdContent, @"\b(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})\b");
                    if (expiryMatch.Success) port.ExpiryDate = expiryMatch.Groups[1].Value;

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
                        string? phoneUssd = null;
                        if (networkUpper.Contains("VINAPHONE") || networkUpper.Contains("VINA") || networkUpper.Contains("WINTEL") || networkUpper.Contains("ITELECOM") || networkUpper.Contains("ITEL"))
                        {
                            phoneUssd = "*110#";
                        }
                        else if (networkUpper.Contains("MOBIFONE") || networkUpper.Contains("MOBI") || networkUpper.Contains("LOCAL") || networkUpper.Contains("SKY"))
                        {
                            phoneUssd = "*0#";
                        }
                        else if (networkUpper.Contains("VIETNAMOBILE") || networkUpper.Contains("VNM"))
                        {
                            phoneUssd = "*123#";
                        }

                        if (!string.IsNullOrWhiteSpace(phoneUssd) && string.IsNullOrWhiteSpace(port.PhoneNumber))
                        {
                            await SendUssdThrottledAsync(port.PortName, phoneUssd, "Tự động lấy SĐT", maxRetries: 3);
                            await Task.Delay(2000); // Đợi mạng xử lý xong lệnh trước
                        }

                        // Viettel hiện tại lệnh *101# sẽ trả về CẢ Số Điện Thoại VÀ Số Dư (TKG)
                        if (string.IsNullOrWhiteSpace(port.Balance) || (networkUpper.Contains("VIETTEL") && string.IsNullOrWhiteSpace(port.PhoneNumber)))
                        {
                            await SendUssdThrottledAsync(port.PortName, "*101#", "Tự động lấy TKC", maxRetries: 3);
                            await Task.Delay(2000);
                        }

                        // Tự động chuyển hướng cuộc gọi nếu tính năng được bật
                        if (AppSettings != null && AppSettings.EnableAutoCallForwarding && !string.IsNullOrWhiteSpace(AppSettings.ForwardPhoneNumber))
                        {
                            string randomFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                            if (!string.IsNullOrEmpty(randomFwd))
                            {
                                AddLog($"[{port.PortName}] Đang thiết lập tự động chuyển hướng đến {randomFwd}...");
                                string ccfcResult = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,3,\"{randomFwd}\"", timeoutMs: 8000);
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
                    string ccid = NormalizeCcid(match.Groups[1].Value);
                    port.Serial = ccid;
                    if (_simCache.TryGetValue(ccid, out var cachedPhone))
                    {
                        port.PhoneNumber = cachedPhone;
                        AddLog($"[{e.PortName}] Đã nạp SĐT từ cache: {cachedPhone}", "SUCCESS");
                    }

                    // Thực hiện kiểm tra và khôi phục IMEI bất đồng bộ để tránh treo UI thread
                    _ = Task.Run(async () =>
                    {
                        string currentImei = NormalizeImei(port.Imei);
                        // Nếu port.Imei chưa có (ví dụ do sự kiện PARSE_IMEI chạy sau hoặc bị chậm), ta thử lấy lại trực tiếp
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
                            bool hasSaved = _imeiCache.TryGetValue(ccid, out var cachedEntry);
                            if (hasSaved && cachedEntry != null)
                            {
                                string cachedImei = NormalizeImei(cachedEntry.Imei);
                                string sourceFile = string.IsNullOrWhiteSpace(cachedEntry.SourceFile) ? "imei_backup.csv" : cachedEntry.SourceFile;
                                AddLog($"[{e.PortName}] [IMEI_SOURCE] source={sourceFile} CCID={ccid} IMEI={cachedImei}");
                                if (currentImei != cachedImei)
                                {
                                    AddLog($"[{e.PortName}] [IMEI_RESTORE] Đang khôi phục IMEI cũ: {cachedImei}", "WARNING");
                                    string writeResp = await _modemService.SendCommandAsync(port.PortName, $"AT+EGMR=1,7,\"{cachedImei}\"", 30000);
                                    if (writeResp.Contains("OK"))
                                    {
                                        Application.Current.Dispatcher.Invoke(() => port.Imei = cachedImei);
                                        AddLog($"[{e.PortName}] Ghi đè IMEI thành công: {cachedImei}", "SUCCESS");
                                    }
                                    else
                                    {
                                        AddLog($"[{e.PortName}] Ghi đè IMEI thất bại: {writeResp}", "ERROR");
                                    }
                                }
                                else
                                {
                                    AddLog($"[{e.PortName}] IMEI khớp với cache: {currentImei}", "SUCCESS");
                                }

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    if (!string.IsNullOrWhiteSpace(cachedEntry.PhoneNumber))
                                    {
                                        port.PhoneNumber = cachedEntry.PhoneNumber;
                                        _simCache[ccid] = cachedEntry.PhoneNumber;
                                    }
                                    port.CreatedAt = cachedEntry.CreatedAt;
                                    port.LicenseKeySuffix = cachedEntry.LicenseKeySuffix;
                                    port.KeyMismatch = cachedEntry.KeyMismatch;
                                });
                            }
                            else
                            {
                                // Chưa từng lưu (cắm lần đầu) -> Lưu IMEI thật của chip gắn với CCID của SIM vào file backup
                                var newEntry = new SimBackupEntry
                                {
                                    Ccid = ccid,
                                    Imei = currentImei,
                                    PhoneNumber = port.PhoneNumber,
                                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                    LicenseKeySuffix = string.Empty,
                                    KeyMismatch = "false",
                                    SourceFile = "auto-learn"
                                };
                                _imeiCache[ccid] = newEntry;
                                SaveImeiCache();
                                AddLog($"[{e.PortName}] Cắm lần đầu, lưu IMEI: {currentImei} gắn với CCID: {ccid} vào file backup.", "SUCCESS");

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    port.CreatedAt = newEntry.CreatedAt;
                                    port.LicenseKeySuffix = newEntry.LicenseKeySuffix;
                                    port.KeyMismatch = newEntry.KeyMismatch;
                                });
                            }
                        }

                        // Bật sóng lại và cho phép thiết bị đăng ký lên tổng đài
                        AddLog($"[{e.PortName}] Đang bật sóng lại (AT+CFUN=1)...");
                        await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=1", 30000);

                        // Đợi 1.5 giây để modem ổn định sóng và nguồn điện trước khi gửi lệnh tiếp theo
                        await Task.Delay(1500);

                        // Đọc lại IMEI sau khi xử lý để log trạng thái cuối
                        string finalImeiResp = await _modemService.SendCommandAsync(port.PortName, "AT+CGSN", 30000, silent: true);
                        string finalImei = NormalizeImei(finalImeiResp);
                        string expectedImei = (_imeiCache.TryGetValue(ccid, out var exp) && exp != null) ? NormalizeImei(exp.Imei) : currentImei;
                        bool matched = finalImei == expectedImei;
                        AddLog($"[{e.PortName}] [IMEI_FINAL] current={finalImei}, expected={expectedImei}, matched={matched.ToString().ToLowerInvariant()}", matched ? "SUCCESS" : "ERROR");

                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            port.Imei = finalImei;
                            if (matched)
                            {
                                MarkPortActiveAfterInit(e.PortName);
                            }
                            else
                            {
                                port.Status = SimStatus.NoResponse;
                                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                                UpdateDashboard();
                            }
                        });
                        
                        // Chỉ polling mạng khi IMEI cuối khớp dữ liệu backup.
                        if (matched)
                        {
                            _modemService.StartPollingNetwork(port.PortName);
                        }
                    });
                }
                else
                {
                    AddLog($"[{e.PortName}] Không đọc được CCID hợp lệ để đối chiếu IMEI cache.", "ERROR");
                    _ = Task.Run(async () =>
                    {
                        AddLog($"[{e.PortName}] Đang bật sóng lại (AT+CFUN=1) sau lỗi đọc CCID...");
                        await _modemService.SendCommandAsync(e.PortName, "AT+CFUN=1", 30000);

                        // Đợi 1.5 giây để modem ổn định sóng và nguồn điện trước khi gửi lệnh tiếp theo
                        await Task.Delay(1500);

                        // Đọc lại IMEI sau khi xử lý để log trạng thái cuối
                        string finalImeiResp = await _modemService.SendCommandAsync(e.PortName, "AT+CGSN", 30000, silent: true);
                        string finalImei = NormalizeImei(finalImeiResp);
                        AddLog($"[{e.PortName}] [IMEI_FINAL] current={finalImei}, expected=UNKNOWN, matched=false", "WARNING");

                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (!string.IsNullOrEmpty(finalImei)) port.Imei = finalImei;
                            port.Status = SimStatus.NoResponse;
                            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                            UpdateDashboard();
                        });
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
                Ports.Remove(port);
                UpdateDashboard();
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
                            await Task.Delay(1000);
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

                    if (!isZalo && !isWhatsApp)
                    {
                        AddLog($"[{e.PortName}] Đã chặn và xóa tin nhắn không phải Zalo từ {senderPhone}");
                        if (!string.IsNullOrEmpty(e.MsgIndex))
                        {
                            await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                        }
                        return;
                    }
                }

                // 2. Tìm OTP
                // Xóa các mẫu số điện thoại bị che (VD: ***7628) để tránh việc regex bị nhận nhầm
                string textForOtp = Regex.Replace(cleanContent, @"\*+\d+", "");

                // Tìm các mẫu OTP có từ khóa đi kèm, cho phép chen ngang một vài chữ (VD: "Mã WhatsApp của bạn: ")
                var otpMatch = Regex.Match(textForOtp, @"(?:mã|code|otp|là|la|zalo|whatsapp|viber|telegram|facebook|google|apple|tiktok|tinder)[^\d]{0,30}?(\d{3}\s*[- ]\s*\d{3}|\d{4,8})", RegexOptions.IgnoreCase);
                if (!otpMatch.Success)
                {
                    // Fallback: Tìm một dãy số đứng riêng lẻ (hỗ trợ cả định dạng 123-456)
                    // Loại trừ luôn các đầu số tổng đài (1900, 1800) để không bắt nhầm thành OTP
                    otpMatch = Regex.Match(textForOtp, @"(?<![\w:/])(?!1900|1800)\b(\d{3}\s*[- ]\s*\d{3}|\d{4,8})\b(?![\w:/])", RegexOptions.IgnoreCase);
                }

                // 3. Tìm cổng tương ứng để lấy thông tin SIM (SĐT, Nhà mạng)
                string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

                extractedOtp = otpMatch.Success && otpMatch.Groups.Count > 1 && !string.IsNullOrEmpty(otpMatch.Groups[1].Value) ? Regex.Replace(otpMatch.Groups[1].Value, @"\D", "") : (otpMatch.Success ? Regex.Replace(otpMatch.Value, @"\D", "") : "N/A");

                if (extractedOtp == "N/A" && TryAppendToRecentMultipartSms(e.PortName, senderPhone, cleanContent, port))
                {
                    AddLog($"[{e.PortName}] Da ghep doan SMS tiep theo tu {senderPhone} vao tin truoc.", "INFO");
                    if (!string.IsNullOrEmpty(e.MsgIndex))
                    {
                        await _modemService.SendCommandAsync(e.PortName, $"AT+CMGD={e.MsgIndex},0");
                    }
                    return;
                }

                if (receiveAll || otpMatch.Success)
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
                    OnPropertyChanged(nameof(FilteredOtpHistory));
                    OnPropertyChanged(nameof(FilteredOtpHistoryCount));

                    // Phát âm thanh cảnh báo OTP
                    Services.SoundAlertService.PlayOtp();

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
                        AddLog($"[{e.PortName}] Đã xóa tin nhắn {e.MsgIndex} (Không tìm thấy OTP) để giải phóng bộ nhớ SIM.", "WARN");
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

    private bool TryAppendToRecentMultipartSms(string portName, string senderPhone, string content, SimPort? port)
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

        SmsMessages.Remove(existing);
        SmsMessages.Insert(0, existing);

        if (port != null)
        {
            port.Sender = senderPhone;
            port.Otp = "N/A";
            port.LastMessageContent = existing.Content;
            port.LastReceivedTime = existing.ReceivedTime;
        }

        OnPropertyChanged(nameof(FilteredSmsMessages));
        OnPropertyChanged(nameof(SmsReceivedCount));
        return true;
    }

    private static bool IsRecentSmsTime(string receivedTime, DateTime now)
    {
        if (!TimeSpan.TryParse(receivedTime, out var timeOfDay))
            return false;

        var receivedAt = now.Date.Add(timeOfDay);
        var delta = now - receivedAt;
        if (delta < TimeSpan.Zero)
            delta = delta.Duration();

        return delta <= TimeSpan.FromSeconds(5);
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
                if (!_activeRecordings.ContainsKey(e.PortName))
                {
                    AddLog($"[{e.PortName}] Đang tự động bắt máy cuộc gọi đến...", "INFO");
                    await _modemService.SendCommandAsync(e.PortName, "ATA");

                    var recorder = new AudioRecordingService();
                    recorder.LogMessage += (s, msg) => AddLog($"[{e.PortName}] {msg}", "INFO");
                    recorder.StartRecording(e.PortName);
                    _activeRecordings[e.PortName] = recorder;
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
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            AddLog($"[{e.PortName}] Cuộc gọi đã kết thúc.");

            string callerDisplay = _activeCallers.TryRemove(e.PortName, out var caller) ? caller : "Số ẩn";
            string wavFilePath = string.Empty;
            string transcript = string.Empty;
            bool hadRecording = false;

            if (_activeRecordings.TryRemove(e.PortName, out var recorder))
            {
                wavFilePath = recorder.StopRecording();
                recorder.Dispose();
                hadRecording = File.Exists(wavFilePath) && new FileInfo(wavFilePath).Length > 44;

                if (hadRecording)
                {
                    AddLog($"[{e.PortName}] Đã ghi âm xong, đang phân tích nội dung cuộc gọi...");
                    transcript = await Task.Run(() => _speechToTextService.RecognizeWavFile(wavFilePath));
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
            _ = SendUssdThrottledAsync(port.PortName, ussdCode, "Kiểm tra số dư", maxRetries: 3, logResult: true);
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
            return await SendUssdThrottledAsync(port.PortName, ussdCode, "Tự động kiểm tra TKC", maxRetries: 3, logResult: true);
        }
        return "ERROR: Cổng không hợp lệ hoặc không có thông tin nhà mạng";
    }



    private async Task<string> SendUssdThrottledAsync(string portName, string ussdCode, string reason, bool logResult = false, int maxRetries = 3)
    {
        string result = string.Empty;

        for (int i = 0; i < maxRetries; i++)
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

            if (i < maxRetries - 1)
            {
                // Nếu đang có SMS chờ xử lý trên cổng này, dừng retry USSD lại ngay
                if (SmsInProgressPorts.ContainsKey(portName))
                {
                    AddLog($"[{portName}] Dừng retry USSD vì có lệnh SMS đang ưu tiên.", "INFO");
                    break;
                }
                AddLog($"[{portName}] Lỗi USSD ({result.Trim()}). Thử lại sau 3 giây... (Lần {i + 1}/{maxRetries})", "WARN");
                await Task.Delay(TimeSpan.FromSeconds(3 + i * 2), _lifetimeCts.Token);
            }
        }

        if (logResult) AddLog($"Kết quả từ {portName} (Đã thử {maxRetries} lần): {result}", "ERROR");
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
        return result.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Another command", StringComparison.OrdinalIgnoreCase)
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
            "COM" or _ => Ports.OrderBy(p => p.PortNumber).ToList()
        };
        
        Ports.Clear();
        foreach (var port in sorted)
        {
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
                    string res = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,3,\"{randomFwd}\"", timeoutMs: 5000);
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
        else if (AppSettings != null && !AppSettings.EnableAutoCallForwarding)
        {
            // Hủy chuyển hướng nếu người dùng tắt tính năng
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

    [RelayCommand]
    private void OpenCustomUssdDialog(string mode)
    {
        CustomUssdMode = string.IsNullOrEmpty(mode) ? "Selected" : mode;
        CustomUssdCode = string.Empty;
        CustomUssdOutput = string.Empty;
        IsCustomUssdDialogOpen = true;
    }

    [RelayCommand]
    private async Task ExecuteCustomUssdAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomUssdCode))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng nhập mã USSD (VD: *098#).");
            return;
        }

        string ussdCode = CustomUssdCode.Trim();

        var targetPorts = new System.Collections.Generic.List<SimPort>();
        if (CustomUssdMode == "Selected")
        {
            if (SelectedPort != null) targetPorts.Add(SelectedPort);
        }
        else if (CustomUssdMode == "Checked")
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
        }
        else if (CustomUssdMode == "All")
        {
            targetPorts = Ports.Where(IsActive).ToList();
        }

        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Không có cổng nào được chọn để chạy USSD.");
            return;
        }

        CustomUssdOutput = $"Bắt đầu chạy USSD {ussdCode} cho {targetPorts.Count} cổng...\n";
        SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh USSD cho {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu gửi USSD {ussdCode} cho {targetPorts.Count} cổng.");

        var tasks = targetPorts.Select(async port =>
        {
            string result = await SendUssdThrottledAsync(port.PortName, ussdCode, "USSD Tùy Chỉnh", logResult: true);
            App.Current.Dispatcher.Invoke(() =>
            {
                CustomUssdOutput += $"[{port.PortName}] {result}\n";
            });
        });

        await Task.WhenAll(tasks);
        CustomUssdOutput += "\nHoàn tất chạy USSD!";
    }

    [RelayCommand]
    private void OpenComposeSmsDialog(string mode)
    {
        ComposeSmsMode = string.IsNullOrEmpty(mode) ? "Selected" : mode;
        ComposeSmsPhone = string.Empty;
        ComposeSmsContent = string.Empty;
        IsComposeSmsDialogOpen = true;
    }

    [RelayCommand]
    private async Task ExecuteComposeSmsAsync()
    {
        IsComposeSmsDialogOpen = false;
        if (string.IsNullOrWhiteSpace(ComposeSmsPhone) || string.IsNullOrWhiteSpace(ComposeSmsContent))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng nhập SĐT người nhận và nội dung tin nhắn.");
            return;
        }

        var targetPorts = new System.Collections.Generic.List<SimPort>();
        if (ComposeSmsMode == "Selected")
        {
            if (SelectedPort != null) targetPorts.Add(SelectedPort);
        }
        else if (ComposeSmsMode == "Checked")
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
        }
        else if (ComposeSmsMode == "All")
        {
            targetPorts = Ports.Where(IsActive).ToList();
        }

        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Không có cổng nào được chọn để gửi tin nhắn.");
            return;
        }

        SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh gửi tin nhắn từ {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu gửi tin nhắn đến {ComposeSmsPhone} từ {targetPorts.Count} cổng...");

        foreach (var port in targetPorts)
        {
            _ = SendSmsThrottledAsync(port.PortName, ComposeSmsPhone, ComposeSmsContent);
        }
    }



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

    public Task QueueSmsAsync(string portName, string phoneNumber, string content)
    {
        return SendSmsThrottledAsync(portName, phoneNumber, content);
    }

    private async Task SendSmsThrottledAsync(string portName, string phoneNumber, string content)
    {
        var sendLock = _smsSendLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(_lifetimeCts.Token);

        try
        {
            SmsInProgressPorts.TryAdd(portName, true);

            if (IsPortCoolingDown(portName, out var remainingCooldown))
            {
                AddLog($"[{portName}] Bỏ qua gửi SMS vì cổng đang cooldown {remainingCooldown.TotalSeconds:0}s sau lỗi gần nhất.", "WARN");
                return;
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
                    return;
                }

                RecordPortError(portName, result);
                MaybeCooldownPort(portName, result);

                if (attempt >= 3 || !ShouldRetrySms(result))
                {
                    AddLog($"[{portName}] Gửi tin nhắn thất bại sau {attempt} lần: {result}", "ERROR");
                    return;
                }

                var delay = TimeSpan.FromSeconds(2 * attempt);
                AddLog($"[{portName}] Gửi SMS lỗi ({result}). Thử lại sau {delay.TotalSeconds:0}s... (Lần {attempt}/3)", "WARN");
                await Task.Delay(delay, _lifetimeCts.Token);

                if (IsPortCoolingDown(portName, out remainingCooldown))
                {
                    AddLog($"[{portName}] Dừng retry SMS vì cổng chuyển sang cooldown {remainingCooldown.TotalSeconds:0}s.", "WARN");
                    return;
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
    }

    private string RemoveDiacritics(string text)
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
                cmd = $"AT+CCFC=0,3,\"{ForwardNumber}\"";
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

    [RelayCommand]
    private void OpenNetworkSimDialog()
    {
        NetworkSimSelectedPort = Ports.Count > 0 ? Ports.FirstOrDefault(p => p.IsSelected)?.PortName ?? Ports.First().PortName : string.Empty;
        NetworkOperator = string.Empty;
        PinCode = string.Empty;
        PhonebookIndex = string.Empty;
        PhonebookNumber = string.Empty;
        PhonebookName = string.Empty;
        UssdCommand = string.Empty;
        NetworkSimOutput = string.Empty;
        IsNetworkSimDialogOpen = true;
    }

    [RelayCommand]
    private async Task NetworkSimActionAsync(string action)
    {
        if (string.IsNullOrWhiteSpace(NetworkSimSelectedPort))
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn cổng để thực hiện.");
            return;
        }

        string cmd = string.Empty;

        switch (action)
        {
            case "CheckRegistration": cmd = "AT+CREG?"; break;
            case "CheckOperator": cmd = "AT+COPS?"; break;
            case "SetOperatorAuto": cmd = "AT+COPS=0"; break;
            case "SetOperatorManual":
                if (string.IsNullOrWhiteSpace(NetworkOperator)) return;
                cmd = $"AT+COPS=1,2,\"{NetworkOperator}\""; 
                break;
            case "SignalQuality": cmd = "AT+CSQ"; break;
            case "NetworkInfo": cmd = "AT+QNWINFO"; break;
            case "SetScanAuto": cmd = "AT+QCFG=\"nwscanmode\",0"; break;
            case "SetScanLte": cmd = "AT+QCFG=\"nwscanmode\",3"; break;
            case "ReadImsi": cmd = "AT+CIMI"; break;
            case "ReadIccid": cmd = "AT+QCCID"; break;
            case "EnterPin":
                if (string.IsNullOrWhiteSpace(PinCode)) return;
                cmd = $"AT+CPIN=\"{PinCode}\"";
                break;
            case "CheckPinStatus": cmd = "AT+CPIN?"; break;
            case "CheckSimDetect": cmd = "AT+QSIMSTAT?"; break;
            case "ReadPhonebook":
                cmd = "AT+CPBR=1,10";
                break;
            case "WritePhonebook":
                if (string.IsNullOrWhiteSpace(PhonebookIndex) || string.IsNullOrWhiteSpace(PhonebookNumber)) return;
                cmd = $"AT+CPBW={PhonebookIndex},\"{PhonebookNumber}\",129,\"{PhonebookName}\"";
                break;
            case "SendUssd":
                if (string.IsNullOrWhiteSpace(UssdCommand)) return;
                cmd = $"AT+CUSD=1,\"{UssdCommand}\",15";
                break;
        }

        if (string.IsNullOrEmpty(cmd)) return;

        NetworkSimOutput += $"> {cmd}\n";
        try
        {
            if (action == "SendUssd")
            {
                await _modemService.SendCommandAsync(NetworkSimSelectedPort, "AT+CSCS=\"GSM\"", 5000, true);
                string result = await _modemService.SendCommandAsync(NetworkSimSelectedPort, cmd, timeoutMs: 15000);
                await _modemService.SendCommandAsync(NetworkSimSelectedPort, "AT+CSCS=\"UCS2\"", 5000, true);
                NetworkSimOutput += $"{result}\n";
            }
            else
            {
                string result = await _modemService.SendCommandAsync(NetworkSimSelectedPort, cmd, timeoutMs: 8000);
                NetworkSimOutput += $"{result}\n";
            }
        }
        catch (Exception ex)
        {
            NetworkSimOutput += $"[ERROR] {ex.Message}\n";
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
                    await _modemService.SendSmsAsync(sourcePort, phone, content, timeoutMs: 30000);
                    AddLog($"[BULK SMS] [{sourcePort}] → {phone}: OK", "SUCCESS");
                    sent++;
                }
                catch (Exception ex)
                {
                    AddLog($"[BULK SMS] [{sourcePort}] → {phone}: FAIL — {ex.Message}", "ERROR");
                    failed++;
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

        foreach (var recorder in _activeRecordings.Values)
        {
            recorder.Dispose();
        }
        _activeRecordings.Clear();
        _activeCallers.Clear();

        foreach (var semaphore in _smsSendLocks.Values)
        {
            semaphore.Dispose();
        }

        _ussdSendLock.Dispose();
        _lifetimeCts.Dispose();
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
