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

    private readonly SpeechToTextService _speechToTextService;
    private readonly ConcurrentDictionary<string, AudioRecordingService> _activeRecordings = new();

    public event Action<string, string>? OtpReceivedEvent;

    private static readonly TimeSpan UssdMinIntervalPerPort = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UssdMinIntervalGlobal = TimeSpan.FromMilliseconds(10);
    private readonly ConcurrentDictionary<string, DateTime> _lastUssdByPort = new();
    private readonly SemaphoreSlim _ussdSendLock = new SemaphoreSlim(100, 100);
    private DateTime _lastUssdGlobalUtc = DateTime.MinValue;

    // Fix #3: Dùng static Random để tránh lỗi seed trùng khi gọi liên tiếp nhanh
    private static readonly Random _rng = new Random();

    // Đánh dấu cổng nào đang có SMS được gửi để USSD tự nhường đường (tránh tranh Semaphore)
    public readonly ConcurrentDictionary<string, bool> SmsInProgressPorts = new();

    private readonly string _cacheFilePath = "sim_cache.json";
    private ConcurrentDictionary<string, string> _simCache = new();

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
    private bool _isRegisterEzDialogOpen;

    [ObservableProperty]
    private string _registerEzMode = "Selected";

    [ObservableProperty]
    private bool _isCallManagerDialogOpen;

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

    [ObservableProperty]
    private string _atCommandOutput = string.Empty;

    [ObservableProperty]
    private string _atCommandSelectedPort = string.Empty;

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
            : SystemLogs.Where(l => l.Message.Contains(_logFilter, StringComparison.OrdinalIgnoreCase));

    public int FilteredLogCount =>
        string.IsNullOrWhiteSpace(_logFilter)
            ? SystemLogs.Count
            : SystemLogs.Count(l => l.Message.Contains(_logFilter, StringComparison.OrdinalIgnoreCase));

    public ISeries[] ConnectionSeries { get; set; }
    public ISeries[] SmsSeries { get; set; }

    public MainViewModel()
    {
        LoadSimCache();
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
        SmsMessages.CollectionChanged += (s, e) => UpdateDashboard();

        // Khởi động Firebase Service chạy ngầm
        new FirebaseService(this).Start();

        // API Server (port 8080)
        if (SettingsService.Current.EnableApiServer)
        {
            var apiServer = new ApiServerService(this);
            apiServer.Start(SettingsService.Current.ApiServerPort);
            AddLog($"[API] REST API server đang chạy tại http://localhost:{SettingsService.Current.ApiServerPort}/api/");
        }

        // #7: Tự động làm mới số dư mỗi 30 phút
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(30));
                var activePorts = Ports.Where(p => p.Status == "Đang hoạt động").ToList();
                if (activePorts.Count > 0)
                {
                    AddLog("[HỆ THỐNG] Tự động kiểm tra số dư định kỳ (30 phút/lần)...");
                    foreach (var p in activePorts)
                    {
                        string ussdCode = GetUssdCodeForProvider(p.NetworkProvider);
                        await SendUssdThrottledAsync(p.PortName, ussdCode, "Làm mới số dư tự động", maxRetries: 1);
                        await Task.Delay(2000);
                    }
                }
            }
        });

        // Ping SIM định kỳ để phát hiện SIM mất kết nối
        _ = Task.Run(async () =>
        {
            while (true)
            {
                int intervalMin = SettingsService.Current.SimPingIntervalMinutes > 0
                    ? SettingsService.Current.SimPingIntervalMinutes : 5;
                await Task.Delay(TimeSpan.FromMinutes(intervalMin));

                if (!SettingsService.Current.EnableSimPing) continue;

                var portsCopy = Ports.ToList();
                foreach (var p in portsCopy)
                {
                    try
                    {
                        string resp = await _modemService.SendCommandAsync(p.PortName, "AT", timeoutMs: 3000, silent: true);
                        if (!resp.Contains("OK"))
                        {
                            if (p.Status != "Không phản hồi")
                            {
                                Application.Current.Dispatcher.Invoke(() => p.Status = "Không phản hồi");
                                AddLog($"[{p.PortName}] SIM không phản hồi khi ping!", "WARN");
                                _ = TelegramService.SendMessageAsync($"⚠️ <b>SIM Không Phản Hồi</b>\nCổng: {p.PortName}\nSĐT: {p.PhoneNumber}");
                                ToastService.ShowSimOffline(p.PortName);
                            }
                        }
                        else if (p.Status == "Không phản hồi")
                        {
                            Application.Current.Dispatcher.Invoke(() => p.Status = "Đang hoạt động");
                            AddLog($"[{p.PortName}] SIM đã khôi phục kết nối.", "SUCCESS");
                        }
                    }
                    catch { }
                    await Task.Delay(500);
                }
            }
        });
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
        OnPropertyChanged(nameof(AtCommandPortOptions));
        OnPropertyChanged(nameof(CallManagerPortOptions));
    }

    private void AddLog(string message, string level = "INFO")
    {
        try 
        {
            const string logFile = "system_log.txt";
            // Fix #2: Giới hạn log file tối đa 5MB, tự động xoay vòng
            var fi = new System.IO.FileInfo(logFile);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024) // 5MB
            {
                string archive = $"system_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                System.IO.File.Move(logFile, archive);
            }
            System.IO.File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}\n");
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
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(3000); // Quét 3 giây 1 lần
                
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
                if (availablePorts.Any(p => !Ports.Any(port => port.PortName == p)))
                {
                    hasChanges = true;
                    _modemService.ConnectAll(115200);
                }

                if (hasChanges)
                {
                    Application.Current.Dispatcher.Invoke(() => UpdateDashboard());
                }
            }
        });
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
                    port.Serial = match.Groups[1].Value;
                    if (_simCache.TryGetValue(port.Serial, out var cachedPhone))
                    {
                        port.PhoneNumber = cachedPhone;
                        AddLog($"[{e.PortName}] Đã nạp SĐT từ cache: {cachedPhone}", "SUCCESS");
                    }
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
                else if (cleanContentLower.Contains("khong du tien") || cleanContentLower.Contains("không đủ tiền"))
                {
                    AddLog($"[{e.PortName}] LỖI SIM: Tài khoản không đủ tiền để gửi SMS đến tổng đài Zalo! Vui lòng nạp thêm tiền.", "ERROR");
                    _ = gsm.Services.FirebaseService.SendErrorToWebAsync(e.PortName, "⚠️ Hết tiền");
                    isZaloError = true;
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
                                  senderPhone.IndexOf("Zalo", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!isZalo)
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

                // Tìm các mẫu OTP có từ khóa đi kèm (Đã thêm mẫu Zalo cụ thể)
                var otpMatch = Regex.Match(textForOtp, @"(?:mã|code|otp|là|la|zalo|viber|telegram|facebook|google|apple|tiktok|tinder)\s*(?:cho\s+sdt\s*(?:\(\))?)?\s*[:\-]?\s*(\d{4,8})", RegexOptions.IgnoreCase);
                if (!otpMatch.Success)
                {
                    // Fallback: Tìm một dãy số đứng riêng lẻ (không liền kề chữ cái)
                    // Loại trừ luôn các đầu số tổng đài (1900, 1800) để không bắt nhầm thành OTP
                    otpMatch = Regex.Match(textForOtp, @"(?<![\w:/])(?!1900|1800)\b(\d{4,8})\b(?![\w:/])", RegexOptions.IgnoreCase);
                }

                // 3. Tìm cổng tương ứng để lấy thông tin SIM (SĐT, Nhà mạng)
                var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

                if (receiveAll || otpMatch.Success)
                {
                    extractedOtp = otpMatch.Success && otpMatch.Groups.Count > 1 && !string.IsNullOrEmpty(otpMatch.Groups[1].Value) ? otpMatch.Groups[1].Value : (otpMatch.Success ? otpMatch.Value : "N/A");
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
                    AddLog($"[{e.PortName}] Đã bắt được OTP: {extractedOtp} từ {senderPhone}", "SUCCESS");
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");

                    // Lưu lịch sử OTP vào file CSV
                    OtpHistoryService.Append(e.PortName, receiverPhone, senderPhone, extractedOtp, cleanContent);

                    // Thông báo Toast Windows
                    ToastService.ShowOtp(e.PortName, receiverPhone, extractedOtp, senderPhone);

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

    private void ModemService_CallIncoming(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";

            AddLog($"[{e.PortName}] Có cuộc gọi đến từ SĐT: {e.Data}. Đang tự động nhấc máy...", "INFO");
            SnackbarMessageQueue.Enqueue($"[{e.PortName}] Có cuộc gọi từ {e.Data}");

            // Fix #4: Gửi thông báo Telegram khi có cuộc gọi đến
            string callerDisplay = string.IsNullOrWhiteSpace(e.Data) ? "Số ẩn" : e.Data;
            string safeCallerHtml = System.Net.WebUtility.HtmlEncode(callerDisplay);
            _ = TelegramService.SendMessageAsync(
                $"📞 <b>Cuộc gọi đến [{e.PortName}]</b>\n" +
                $"📱 SIM nhận: {receiverPhone}\n" +
                $"☎️ Người gọi: <code>{safeCallerHtml}</code>"
            );

            // Gửi lệnh nhận cuộc gọi
            await _modemService.SendCommandAsync(e.PortName, "ATA");

            // Bắt đầu ghi âm
            var recorder = new AudioRecordingService();
            recorder.LogMessage += (s, msg) => AddLog($"[{e.PortName}] {msg}");
            
            if (_activeRecordings.TryAdd(e.PortName, recorder))
            {
                recorder.StartRecording(e.PortName);
            }
        });
    }

    private void ModemService_CallEnded(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            AddLog($"[{e.PortName}] Cuộc gọi đã kết thúc.");

            if (_activeRecordings.TryRemove(e.PortName, out var recorder))
            {
                string wavFilePath = recorder.StopRecording();
                recorder.Dispose();

                if (File.Exists(wavFilePath))
                {
                    AddLog($"[{e.PortName}] Đã ghi âm xong, đang phân tích nội dung cuộc gọi...");
                    
                    // Chạy dịch trên luồng riêng để không block UI
                    string text = await Task.Run(() => _speechToTextService.RecognizeWavFile(wavFilePath));
                    
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AddLog($"[{e.PortName}] Nội dung cuộc gọi: {text}", "SUCCESS");
                        
                        // Gửi qua Telegram
                        var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                        string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";
                        _ = TelegramService.SendMessageAsync($"🎙 <b>Cuộc gọi trên {e.PortName}</b>\n📱 SIM nhận: {receiverPhone}\n📝 Nội dung: <i>{text}</i>");
                    }
                    else
                    {
                        AddLog($"[{e.PortName}] Không có giọng nói trong cuộc gọi này (hoặc âm lượng quá nhỏ).", "WARN");
                    }
                }
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
    private async Task CheckBalanceAsync()
    {
        // Luôn kiểm tra toàn bộ các cổng đang hoạt động, bỏ qua việc người dùng có chọn hay không
        var targetPorts = Ports.Where(p => p.Status == "Đang hoạt động").ToList();
        
        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Không có cổng nào đang hoạt động để kiểm tra số dư.");
            return;
        }

        SnackbarMessageQueue.Enqueue($"Đang đẩy lệnh kiểm tra TKC cho TOÀN BỘ {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu kiểm tra số dư cho toàn bộ {targetPorts.Count} cổng...");

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

    public async Task CheckBalanceForPortAsync(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port != null && !string.IsNullOrWhiteSpace(port.NetworkProvider))
        {
            string ussdCode = GetUssdCodeForProvider(port.NetworkProvider);
            AddLog($"Tự động kiểm tra lại TKC cho {port.PortName} sau khi gửi SMS...");
            await SendUssdThrottledAsync(port.PortName, ussdCode, "Tự động kiểm tra TKC", maxRetries: 3, logResult: true);
        }
    }



    private async Task<string> SendUssdThrottledAsync(string portName, string ussdCode, string reason, bool logResult = false, int maxRetries = 1)
    {
        string result = string.Empty;

        for (int i = 0; i < maxRetries; i++)
        {
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
            await _modemService.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);

            // 2. Gửi lệnh USSD
            result = await _modemService.SendCommandAsync(portName, $"AT+CUSD=1,\"{ussdCode}\",15");

            // 3. Chuyển lại UCS2
            await _modemService.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);

            bool isFailed = result.Contains("ERROR") || result.Contains("Thao tac khong hop le") || result.Contains("he thong ban") || result.Contains("+CUSD: 2") || result.Contains("+CUSD: 4") || result.Contains("+CUSD: 5");

            if (!isFailed)
            {
                if (logResult) AddLog($"Kết quả từ {portName}: {result}", "SUCCESS");
                return result; // Thành công, thoát vòng lặp
            }

            if (i < maxRetries - 1)
            {
                // Nếu đang có SMS chờ xử lý trên cổng này, dừng retry USSD lại ngay
                if (SmsInProgressPorts.ContainsKey(portName))
                {
                    AddLog($"[{portName}] Dừng retry USSD vì có lệnh SMS đang ưu tiên.", "INFO");
                    break;
                }
                AddLog($"[{portName}] Lỗi USSD ({result.Trim()}). Thử lại sau 3 giây... (Lần {i + 1}/{maxRetries})", "WARN");
                await Task.Delay(3000);
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
    private void SortPorts()
    {
        var sorted = Ports.OrderBy(p => 
        {
            var match = Regex.Match(p.PortName, @"\d+");
            return match.Success ? int.Parse(match.Value) : 0;
        }).ToList();
        
        Ports.Clear();
        foreach (var port in sorted)
        {
            Ports.Add(port);
        }
        UpdateDashboard();
        SnackbarMessageQueue.Enqueue("Đã sắp xếp các cổng theo thứ tự.");
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
                var activePorts = Ports.ToList();
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
                var activePorts = Ports.ToList();
                foreach (var port in activePorts)
                {
                    await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,4", timeoutMs: 5000);
                    Application.Current.Dispatcher.Invoke(() => port.ForwardedTo = string.Empty);
                    await Task.Delay(500);
                }
            });
        }
    }

    // #5: Xoá chuyển hướng ngay lập tức cho tất cả SIM
    [RelayCommand]
    private void ClearForwardingAll()
    {
        SnackbarMessageQueue.Enqueue("Đang xóa chuyển hướng cho tất cả cổng...");
        Task.Run(async () =>
        {
            var activePorts = Ports.ToList();
            foreach (var port in activePorts)
            {
                string res = await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,4", timeoutMs: 5000);
                if (res.Contains("OK"))
                    Application.Current.Dispatcher.Invoke(() => port.ForwardedTo = string.Empty);
                await Task.Delay(300);
            }
            AddLog("[Đã xóa chuyển hướng] Hoàn thành cho tất cả cổng.", "SUCCESS");
        });
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
            targetPorts = Ports.Where(p => p.Status == "Đang hoạt động").ToList();
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
    private void OpenRegisterEzDialog(string mode)
    {
        RegisterEzMode = string.IsNullOrEmpty(mode) ? "Selected" : mode;
        IsRegisterEzDialogOpen = true;
    }

    [RelayCommand]
    private void ExecuteRegisterEz()
    {
        IsRegisterEzDialogOpen = false;
        
        List<SimPort> targetPorts = new();
        if (RegisterEzMode == "Selected")
        {
            if (SelectedPort != null) targetPorts.Add(SelectedPort);
        }
        else if (RegisterEzMode == "Checked")
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
        }
        else if (RegisterEzMode == "All")
        {
            targetPorts = Ports.Where(p => p.Status == "Đang hoạt động").ToList();
        }

        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Không có cổng nào được chọn để đăng ký EZ.");
            return;
        }

        SnackbarMessageQueue.Enqueue($"Đang tiến hành đăng ký EZ cho {targetPorts.Count} cổng...");
        
        foreach (var port in targetPorts)
        {
            string content = port.LastMessageContent ?? string.Empty;
            var match = Regex.Match(content, @"EZ\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string code = match.Groups[1].Value;
                string smsBody = $"EZ {code}";
                AddLog($"Bắt đầu tự đăng ký EZ ({smsBody}) cho cổng {port.PortName}...");
                _ = SendSmsThrottledAsync(port.PortName, "888", smsBody);
            }
            else
            {
                AddLog($"Bỏ qua {port.PortName}: Không tìm thấy mã EZ trong tin nhắn cuối.", "WARNING");
            }
        }
    }

    private async Task SendSmsThrottledAsync(string portName, string phoneNumber, string content)
    {
        try
        {
            SmsInProgressPorts.TryAdd(portName, true);
            
            // 1. Remove diacritics to send via GSM safely without UCS2 hex-encoding complexity
            string safeContent = RemoveDiacritics(content);

            // 2. Switch to GSM temporarily so that the raw text is accepted
            await _modemService.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);
            await _modemService.SendCommandAsync(portName, "AT+CSMP=17,167,0,0", 5000, true);

            // 3. Send the SMS
            string result = await _modemService.SendSmsAsync(portName, phoneNumber, safeContent);
            
            if (result.Contains("OK") || result.Contains("+CMGS:"))
            {
                AddLog($"[{portName}] Gửi tin nhắn đến {phoneNumber} thành công.", "SUCCESS");
            }
            else
            {
                if (result.Contains("Timeout sending SMS payload"))
                {
                    AddLog($"[{portName}] Sim không gửi tin nhắn đi được hoặc không nhận được tin nhắn phản hồi", "ERROR");
                }
                else
                {
                    AddLog($"[{portName}] Sim không gửi tin nhắn đi được ({result})", "ERROR");
                }
            }
        }
        finally
        {
            // 4. Always revert back to UCS2 so incoming SMS (Tiếng Việt) doesn't break!
            await _modemService.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);
            await _modemService.SendCommandAsync(portName, "AT+CSMP=17,167,0,8", 5000, true);
            SmsInProgressPorts.TryRemove(portName, out _);
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
            case "EnableAutoAnswer":
                cmd = "ATS0=1";
                break;
            case "DisableAutoAnswer":
                cmd = "ATS0=0";
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
            var activePorts = Ports.Where(p => p.Status == "Đang hoạt động").Select(p => p.PortName).ToList();
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
}
