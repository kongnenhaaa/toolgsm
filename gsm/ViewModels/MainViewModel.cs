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
    private readonly IPortSessionRegistry _portSessions;
    private readonly IGsmSmsService _smsService;
    private readonly IGsmUssdService _ussdService;
    private readonly IGsmCallService _callService;
    private readonly IGsmBackgroundSupervisor _backgroundSupervisor;
    private readonly Services.ImeiManagementService _imeiManagementService;
    private readonly SpeechToTextService _speechToTextService;
    private readonly gsm.Services.INotifyService _notifyService = new gsm.Services.NotifyService();
    private readonly gsm.Services.IFirebaseOtpService _firebaseOtpService = new gsm.Services.FirebaseOtpService();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _sentConfirmations = new();
    public IGsmModemService ModemService => _modemService;

    private readonly FirebaseService _firebaseService;
    public ProxyManagerService ProxyManager { get; }
    private readonly ConcurrentDictionary<string, string> _callFailures = new();
    private readonly ConcurrentDictionary<string, bool> _activeRamRecordings = new();
    private readonly ConcurrentDictionary<string, string> _activeCallers = new();
    private sealed class PendingMyVnptPasswordOperation
    {
        private readonly object _otpClaimLock = new();
        private readonly HashSet<string> _usedOtps = new(StringComparer.Ordinal);
        private bool _otpClaimed;
        public required string PortName { get; init; }
        public required string Ccid { get; init; }
        public required long Epoch { get; init; }
        public required string LocalPhone { get; init; }
        public required string Password { get; init; }
        public required MyVnptOtpSession ApiSession { get; set; }
        public required CancellationToken CancellationToken { get; init; }
        public TaskCompletionSource<MyVnptPasswordResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryClaimOtp(string otp)
        {
            lock (_otpClaimLock)
            {
                if (_otpClaimed || _usedOtps.Contains(otp)) return false;
                _otpClaimed = true;
                _usedOtps.Add(otp);
                return true;
            }
        }

        public void UpdateApiSession(MyVnptOtpSession session)
        {
            lock (_otpClaimLock)
                ApiSession = session;
        }

        public void PrepareForNextOtp(MyVnptOtpSession session)
        {
            lock (_otpClaimLock)
            {
                ApiSession = session;
                _otpClaimed = false;
            }
        }
    }

    private readonly ConcurrentDictionary<string, PendingMyVnptPasswordOperation> _pendingMyVnptPasswordPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _vnptBatchGate = new(1, 1);
    // Khóa trọn giao dịch phát OTP của một COM: check account -> đăng ký pending
    // -> otp_send. Không để toàn bộ check_account chen lên trước toàn bộ otp_send.
    private readonly SemaphoreSlim _vnptOtpIssueWorkflowGate = new(1, 1);
    
    [ObservableProperty] private int _vnptTotalActiveCount = 0;
    [ObservableProperty] private int _vnptSuccessCount = 0;
    [ObservableProperty] private int _vnptFailCount = 0;
    private readonly object _vnptLock = new object();

    [ObservableProperty]
    private string _vnptSummaryText = string.Empty;

    private void DecrementVnptActiveCount(bool isSuccess)
    {
        lock (_vnptLock)
        {
            if (isSuccess) VnptSuccessCount++;
            else VnptFailCount++;

            VnptTotalActiveCount--;
            if (VnptTotalActiveCount < 0) VnptTotalActiveCount = 0;

            int success = VnptSuccessCount;
            int fail = VnptFailCount;
            int remaining = VnptTotalActiveCount;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (remaining > 0)
                {
                    VnptSummaryText = $"MyVNPT: Đang chạy (Thành công: {success}, Thất bại: {fail}, Còn lại: {remaining})";
                }
                else
                {
                    VnptSummaryText = $"MyVNPT: Hoàn tất! (Thành công: {success}, Thất bại: {fail})";
                    SnackbarMessageQueue.Enqueue($"Đã hoàn tất đặt pass MyVNPT! Thành công: {success}, Thất bại: {fail}");
                }
            });
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<gsm.Models.VnptResultItem> VnptResults { get; } = new();

    private void AddVnptResult(string port, string phone, string password, bool success, string response)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            VnptResults.Insert(0, new gsm.Models.VnptResultItem
            {
                Time = DateTime.Now,
                Port = port,
                Phone = phone,
                Password = password,
                Success = success,
                Response = response
            });
            if (VnptResults.Count > 300)
            {
                VnptResults.RemoveAt(VnptResults.Count - 1);
            }
        });
    }

    private async Task CompletePendingMyVnptPasswordAsync(
        PendingMyVnptPasswordOperation pending,
        string otp)
    {
        try
        {
            if (!IsSimSessionCurrent(pending.PortName, pending.Ccid, pending.Epoch))
            {
                pending.Completion.TrySetResult(new MyVnptPasswordResult(false, "SIM đã thay đổi trước khi nhận OTP"));
                return;
            }

            MyVnptPasswordResult result = await MyVnptService.SetPasswordAsync(
                pending.PortName,
                pending.ApiSession,
                otp,
                pending.Password,
                (message, type) => AddLog(message, type),
                pending.CancellationToken);

            if (result.NeedsRetryWithMissPassword)
            {
                if (!IsSimSessionCurrent(pending.PortName, pending.Ccid, pending.Epoch))
                {
                    pending.Completion.TrySetResult(new MyVnptPasswordResult(false, "SIM đã thay đổi trước khi gửi lại OTP"));
                    return;
                }

                MyVnptOtpSession recoverySession = pending.ApiSession with { AccountExists = true };
                await _vnptOtpIssueWorkflowGate.WaitAsync(pending.CancellationToken);
                try
                {
                    // Mở nhận OTP trước khi gọi API để không bỏ lỡ SMS về rất nhanh.
                    // OTP cũ nằm trong _usedOtps nên SMS lặp lại không thể bị dùng lần hai.
                    pending.PrepareForNextOtp(recoverySession);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var port = Ports.FirstOrDefault(p =>
                            string.Equals(p.PortName, pending.PortName, StringComparison.OrdinalIgnoreCase));
                        if (port != null)
                        {
                            port.VnptStatus = "Yêu cầu lại OTP...";
                            port.LastMessageContent = "Tài khoản đã tồn tại; đang gửi OTP quên mật khẩu mới...";
                        }
                        AddLog($"[{pending.PortName}] [VNPT_FLOW] Gửi OTP authen_miss_password mới...", "INFO");
                    });

                    recoverySession = await MyVnptService.SendOtpAsync(
                        recoverySession,
                        pending.CancellationToken,
                        (message, type) => AddLog($"[{pending.PortName}] {message}", type));
                    pending.UpdateApiSession(recoverySession);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var port = Ports.FirstOrDefault(p =>
                            string.Equals(p.PortName, pending.PortName, StringComparison.OrdinalIgnoreCase));
                        if (port != null)
                        {
                            port.VnptStatus = "Đợi tin nhắn...";
                            port.LastMessageContent = "Đang đợi OTP quên mật khẩu mới...";
                        }
                        AddLog($"[{pending.PortName}] [VNPT_FLOW] Đã gửi OTP quên mật khẩu; tiếp tục chờ SMS.", "INFO");
                    });
                }
                finally
                {
                    _vnptOtpIssueWorkflowGate.Release();
                }
                return;
            }

            pending.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetResult(new MyVnptPasswordResult(
                false,
                MyVnptService.GetFriendlyExceptionMessage(ex)));
        }
    }

    public void ClearVnptResults()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            VnptResults.Clear();
            VnptSuccessCount = 0;
            VnptFailCount = 0;
            VnptTotalActiveCount = 0;
            VnptSummaryText = string.Empty;
        });
    }

    private readonly object _logFileLock = new();
    
    public event Action<string, string>? OtpReceivedEvent;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _portCooldownUntilUtc = new();

    // Fix #3: Dùng static Random để tránh lỗi seed trùng khi gọi liên tiếp nhanh
    private static readonly Random _rng = new Random();

    // Đánh dấu cổng nào đang có SMS được gửi để USSD tự nhường đường (tránh tranh Semaphore)
    public ConcurrentDictionary<string, bool> SmsInProgressPorts => _smsService.InProgressPorts;

    // Đánh dấu cổng nào đang trong quá trình khởi tạo SIM/IMEI để tránh khởi tạo song song
    // Lease riêng cho từng lần khởi tạo. Dùng bool khiến tác vụ cũ bị hủy có thể để
    // lại khóa vĩnh viễn hoặc xóa nhầm khóa của phiên SIM mới.
    private readonly ConcurrentDictionary<string, Guid> _initializingPorts = new();
    private readonly ConcurrentDictionary<string, int> _portRecoveryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _portRecoveryInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _quarantinedPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<bool>> _ussdVoiceRecoveryTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ussdVoiceRecoveryAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ussdRecoveryRetryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _initialBalanceLookupOwners = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxAutomaticRecoveryAttempts = 3;
    // Mỗi lần cắm/rút SIM tạo một epoch mới. Mọi tác vụ IMEI/Accept phải giữ đúng
    // epoch + CCID; tác vụ của SIM cũ không được phép cập nhật SIM mới trên cùng COM.

    private readonly string _cacheFilePath = AppPaths.ForRuntimeFile("sim_cache.json");
    private ConcurrentDictionary<string, string> _simCache = new();

    // Bản publish nằm trong ...\win-x64\publish, trong khi kho backup thường nằm
    // ở thư mục Release cha. Luôn dùng kho gần nhất đã tồn tại để tránh
    // mỗi bản EXE tạo một imei_backup.xlsx rỗng khác nhau.
    private readonly string _imeiCacheFilePath =
        AppPaths.ResolveRuntimeOrAncestorFile("imei_backup.xlsx");
    private readonly string _pendingImeiCacheFilePath =
        AppPaths.ForResolvedFileSibling("imei_backup.xlsx", "imei_backup.pending.xlsx");
    private readonly string _legacyImeiCacheCsvPath =
        AppPaths.ForResolvedFileSibling("imei_backup.xlsx", "imei_backup.csv");
    private static readonly string[] ImeiBackupColumns =
    [
        "CCID", "IMEI", "PhoneNumber", "NetworkProvider", "Balance", "PromotionBalance",
        "ExpiryDate", "SimRegDate", "Lock1C", "Lock2C", "CreatedAt", "UpdatedAt",
        "LastPortName", "DeviceName", "HardwareName", "ModemManufacturer", "ModemModel",
        "ModemFirmware", "ModemCapabilities", "Status", "SignalStrength", "SourceFile"
    ];
    private ConcurrentDictionary<string, SimBackupEntry> _imeiCache = new();
    public IReadOnlyDictionary<string, SimBackupEntry> ImeiCache => _imeiCache;
    private readonly object _imeiCacheLock = new();
    private readonly ConcurrentDictionary<string, string> _imeiTargetReservations =
        new(StringComparer.Ordinal);

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
    public Task SetMyVnptPassword(object obj, CancellationToken cancellationToken = default)
    {
        string legacyPassword = "123456a@A";
        try
        {
            string path = AppPaths.ForRuntimeFile("dat_passvnpt.txt");
            if (File.Exists(path) && !string.IsNullOrWhiteSpace(File.ReadAllText(path)))
                legacyPassword = File.ReadAllText(path).Trim();
        }
        catch { }
        return SetMyVnptPassword(obj, legacyPassword, cancellationToken);
    }

    public async Task SetMyVnptPassword(
        object obj,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!await _vnptBatchGate.WaitAsync(0, cancellationToken))
        {
            SnackbarMessageQueue.Enqueue("Tiến trình MyVNPT đang chạy; không gửi lặp lại OTP.");
            return;
        }

        try
        {
        var targetPorts = new List<SimPort>();
        
        if (obj is System.Collections.Generic.IEnumerable<SimPort> portsList)
        {
            targetPorts = portsList.ToList();
        }
        else if (obj is string param)
        {
            if (param == "All")
            {
                targetPorts = Ports.ToList();
            }
            else if (param == "Selected")
            {
                targetPorts = Ports.Where(p => p.IsSelected).ToList();
            }
            else if (param == "Error")
            {
                targetPorts = Ports.Where(p => 
                    (p.LastMessageContent?.Contains("lỗi", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastMessageContent?.Contains("thất bại", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastMessageContent?.Contains("không thành công", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastSmsResult?.Contains("lỗi", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastSmsResult?.Contains("thất bại", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastSmsResult?.Contains("không thành công", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastCommandResult?.Contains("lỗi", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastCommandResult?.Contains("thất bại", StringComparison.OrdinalIgnoreCase) == true) ||
                    (p.LastCommandResult?.Contains("không thành công", StringComparison.OrdinalIgnoreCase) == true)
                ).ToList();
            }
        }
        else
        {
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
            if (obj is SimPort clickedPort && !targetPorts.Contains(clickedPort))
            {
                targetPorts.Add(clickedPort);
            }
        }

        targetPorts = targetPorts
            .Where(p => p.Status == SimStatus.Active && IsPortReadyForOperation(p.PortName))
            .DistinctBy(p => p.PortName)
            .ToList();

        // Mỗi SĐT chỉ tạo một yêu cầu trong một lượt, kể cả khi cache làm cùng
        // một SĐT tạm thời xuất hiện trên nhiều COM.
        var uniqueTargets = new List<SimPort>(targetPorts.Count);
        var seenPhones = new HashSet<string>(StringComparer.Ordinal);
        foreach (SimPort candidate in targetPorts)
        {
            string normalizedPhone = MyVnptService.NormalizePhone(candidate.PhoneNumber);
            if (string.IsNullOrEmpty(normalizedPhone) || seenPhones.Add(normalizedPhone))
            {
                uniqueTargets.Add(candidate);
            }
            else
            {
                AddLog($"[{candidate.PortName}] Bỏ qua yêu cầu MyVNPT trùng SĐT {candidate.PhoneNumber}.", "WARN");
            }
        }
        targetPorts = uniqueTargets;

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (hoặc không có cổng nào thỏa mãn điều kiện) để đặt mật khẩu MyVNPT.");
            return;
        }

        int count = 0;
        lock (_vnptLock)
        {
            VnptSuccessCount = 0;
            VnptFailCount = 0;
            VnptTotalActiveCount = targetPorts.Count(p =>
                !string.IsNullOrWhiteSpace(MyVnptService.NormalizePhone(p.PhoneNumber)));
            if (VnptTotalActiveCount > 0)
            {
                VnptSummaryText = $"MyVNPT: Đang chạy (Thành công: 0, Thất bại: 0, Còn lại: {VnptTotalActiveCount})";
            }
            else
            {
                VnptSummaryText = string.Empty;
            }
        }

        var requestTasks = new List<Task>();
        foreach (var port in targetPorts)
        {
            if (string.IsNullOrWhiteSpace(MyVnptService.NormalizePhone(port.PhoneNumber)))
            {
                AddLog($"[{port.PortName}] Bỏ qua vì chưa có số điện thoại.", "WARN");
                continue;
            }

            count++;
            requestTasks.Add(Task.Run(async () =>
            {
                bool resultRecorded = false;
                PendingMyVnptPasswordOperation? pending = null;
                if (!TryGetCurrentSimSession(port.PortName, out var vnptCcid, out var vnptEpoch, out var simToken))
                {
                    DecrementVnptActiveCount(false);
                    AddVnptResult(port.PortName, port.PhoneNumber, password, false, "Phiên SIM không còn hợp lệ");
                    return;
                }
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, simToken);
                var operationToken = linkedCts.Token;
                try
                {
                    operationToken.ThrowIfCancellationRequested();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        port.VnptStatus = "Xếp hàng OTP...";
                        port.LastMessageContent = "Đang chờ đến lượt gửi yêu cầu MyVNPT...";
                    });
                    await _vnptOtpIssueWorkflowGate.WaitAsync(operationToken);
                    try
                    {
                        operationToken.ThrowIfCancellationRequested();
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            port.VnptStatus = "Kiểm tra TK...";
                            port.LastMessageContent = "Đang kiểm tra tài khoản MyVNPT...";
                            AddLog($"[{port.PortName}] [VNPT_FLOW] Bắt đầu kiểm tra tài khoản {port.PhoneNumber}...");
                        });

                        MyVnptOtpSession apiSession = await MyVnptService.PreparePasswordRequestAsync(
                            port.PhoneNumber,
                            operationToken,
                            (message, type) => AddLog($"[{port.PortName}] {message}", type));
                        if (!IsSimSessionCurrent(port.PortName, vnptCcid, vnptEpoch)
                            || port.Status != SimStatus.Active)
                            throw new OperationCanceledException(operationToken);

                        string modeStr = apiSession.AccountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
                        pending = new PendingMyVnptPasswordOperation
                        {
                            PortName = port.PortName,
                            Ccid = vnptCcid,
                            Epoch = vnptEpoch,
                            LocalPhone = port.PhoneNumber,
                            Password = password,
                            ApiSession = apiSession,
                            CancellationToken = operationToken
                        };
                        if (!_pendingMyVnptPasswordPorts.TryAdd(port.PortName, pending))
                            throw new InvalidOperationException("COM đang có một yêu cầu MyVNPT khác");

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            port.VnptStatus = "Yêu cầu OTP...";
                            port.LastMessageContent = $"Đang yêu cầu gửi OTP ({modeStr})...";
                            AddLog($"[{port.PortName}] [VNPT_FLOW] {modeStr}; gửi OTP ngay sau bước kiểm tra...");
                        });

                        // Đăng ký pending trước otp_send để không bỏ lỡ SMS về cực nhanh.
                        // Giữ khóa workflow đến khi otp_send có phản hồi, sau đó COM kế tiếp mới chạy check.
                        // SendOtpAsync có thể trả về session mới nếu fallback register→miss_password.
                        apiSession = await MyVnptService.SendOtpAsync(
                            apiSession,
                            operationToken,
                            (message, type) => AddLog($"[{port.PortName}] {message}", type));
                        pending.UpdateApiSession(apiSession);
                        if (!IsSimSessionCurrent(port.PortName, vnptCcid, vnptEpoch)
                            || port.Status != SimStatus.Active)
                            throw new OperationCanceledException(operationToken);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (port.VnptStatus == "Yêu cầu OTP...")
                            {
                                port.VnptStatus = "Đợi tin nhắn...";
                                port.LastMessageContent = "Đang đợi tin nhắn OTP...";
                            }
                            AddLog($"[{port.PortName}] [VNPT_FLOW] otp_send thành công ({modeStr}); nhường lượt cho COM kế tiếp.", "INFO");
                        });
                    }
                    finally
                    {
                        _vnptOtpIssueWorkflowGate.Release();
                    }

                    PendingMyVnptPasswordOperation activePending = pending
                        ?? throw new InvalidOperationException("Không tạo được phiên chờ OTP MyVNPT");

                    using var otpTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                    otpTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                    MyVnptPasswordResult result;
                    try
                    {
                        result = await activePending.Completion.Task.WaitAsync(otpTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
                    {
                        result = new MyVnptPasswordResult(false, "Hết hạn OTP (Timeout)");
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        port.VnptStatus = result.Message;
                        port.LastSmsResult = result.Message;
                        port.LastMessageContent = result.Success ? result.Message : $"Lỗi: {result.Message}";
                        port.UpdateDisplayResult(CommandPanelTab);
                    });
                    DecrementVnptActiveCount(result.Success);
                    AddVnptResult(port.PortName, port.PhoneNumber, password, result.Success, result.Message);
                    resultRecorded = true;
                }
                catch (OperationCanceledException)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (port.VnptStatus is "Xếp hàng OTP..." or "Kiểm tra TK..." or "Yêu cầu OTP..." or "Đợi tin nhắn...")
                        {
                            port.VnptStatus = "Đã hủy";
                            port.LastMessageContent = "Đã hủy yêu cầu MyVNPT";
                        }
                    });
                    if (!resultRecorded)
                    {
                        DecrementVnptActiveCount(false);
                        AddVnptResult(port.PortName, port.PhoneNumber, password, false, "Đã hủy");
                        resultRecorded = true;
                    }
                }
                catch (Exception ex)
                {
                    string friendlyErr = Services.MyVnptService.GetFriendlyExceptionMessage(ex);
                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        port.VnptStatus = friendlyErr;
                        port.LastMessageContent = friendlyErr;
                        AddLog($"[{port.PortName}] Lỗi gửi yêu cầu OTP: {ex.Message}", "ERROR");
                    });
                    if (!resultRecorded)
                    {
                        DecrementVnptActiveCount(false);
                        AddVnptResult(port.PortName, port.PhoneNumber, password, false, friendlyErr);
                        resultRecorded = true;
                    }
                }
                finally
                {
                    if (pending != null)
                    {
                        ((ICollection<KeyValuePair<string, PendingMyVnptPasswordOperation>>)_pendingMyVnptPasswordPorts)
                            .Remove(new KeyValuePair<string, PendingMyVnptPasswordOperation>(port.PortName, pending));
                    }
                }
            }));
        }

        await Task.WhenAll(requestTasks);
        
        if (count > 0)
            SnackbarMessageQueue.Enqueue($"Đã hoàn tất xử lý MyVNPT cho {count} cổng.");
        }
        finally
        {
            _vnptBatchGate.Release();
        }
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
        get => true;
        set
        {
            // Watchdog is always enabled by default
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
    private bool _isDisplayColumnsDialogOpen;

    [ObservableProperty]
    private string _atCommandInput = "AT";

    public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> PredefinedAtCommands { get; } = new()
    {
        // 1. CƠ BẢN & THÔNG TIN THIẾT BỊ
        new("AT", "Kiểm tra kết nối modem"),
        new("ATI", "Xem thông tin Firmware/Version của Modem"),
        new("ATE1", "Bật tính năng Echo (hiển thị ký tự gõ)"),
        new("ATE0", "Tắt tính năng Echo"),
        new("AT+CMEE=2", "Bật báo lỗi chi tiết (Verbose Error)"),
        
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

    public MainViewModel(
        IGsmModemService modemService,
        IPortSessionRegistry portSessions,
        IGsmSmsService smsService,
        IGsmUssdService ussdService,
        IGsmCallService callService,
        IGsmBackgroundSupervisor backgroundSupervisor,
        ImeiManagementService imeiManagementService)
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
        ExportColumns.Add(new ExportColumnItem("Ngày ĐK SIM", "SimRegDate"));
        ExportColumns.Add(new ExportColumnItem("Khóa 1C", "Lock1C"));
        ExportColumns.Add(new ExportColumnItem("Khóa 2C", "Lock2C"));

        _modemService = modemService;
        _portSessions = portSessions;
        _smsService = smsService;
        _ussdService = ussdService;
        _callService = callService;
        _backgroundSupervisor = backgroundSupervisor;
        // Backend phải dùng cấu hình đã lưu ngay từ lúc khởi động. Trước đây AppSettings giữ
        // object mặc định cho tới khi người dùng mở/lưu dialog Settings, làm AutoAccept và
        // một số tùy chọn GSM không có hiệu lực trong lần startup đầu tiên.
        AppSettings = SettingsService.Current;
        _modemService.RequiresSimAcceptanceCheck = (ccid, imei) =>
        {
            string normCcid = NormalizeCcid(ccid);
            if (string.IsNullOrEmpty(normCcid)) return true;
            _imeiCache.TryGetValue(normCcid, out var entry);
            
            bool hasValidBackup = entry != null
                && Services.ImeiManagementService.IsValidImei(entry.Imei);
                                  
            bool isHardwareImeiValid = Services.ImeiManagementService.IsUsableObservedImei(imei)
                && !Services.ImeiManagementService.IsFakeImei(imei);
            
            bool treatAsNewSim = (entry == null) || (!isHardwareImeiValid && !hasValidBackup);
            return treatAsNewSim;
        };
        _imeiManagementService = imeiManagementService;
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        _modemService.CallIncoming += ModemService_CallIncoming;
        _modemService.CallEnded += ModemService_CallEnded;
        _modemService.DtmfReceived += ModemService_DtmfReceived;
        _modemService.IncomingCallRinging += ModemService_IncomingCallRinging;
        _modemService.IncomingCallAnswered += ModemService_IncomingCallAnswered;
        _modemService.IncomingCallEnded += ModemService_IncomingCallEnded;
        
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

        _backgroundSupervisor.Start(new GsmBackgroundSupervisorContext
        {
            GetPorts = GetPortsSnapshot,
            IsActive = IsActive,
            IsWatchdogEnabled = () => IsWatchdogEnabled,
            IsSmsInProgress = portName => _smsService.IsInProgress(portName),
            SendBalanceUssdAsync = async (port, reason) =>
            {
                string code = GetUssdCodeForProvider(port.NetworkProvider);
                await SendUssdThrottledAsync(port.PortName, code, reason, maxAttempts: 1);
            },
            SetSignalStrength = (port, value) => Application.Current.Dispatcher.Invoke(() => port.SignalStrength = value),
            MarkSmsSweep = port => Application.Current.Dispatcher.Invoke(() =>
                port.LastSweepTime = DateTime.Now.ToString("HH:mm:ss")),
            MarkConnectionTimeout = port => Application.Current.Dispatcher.Invoke(() =>
            {
                port.Status = SimStatus.NoResponse;
                port.LastError = "Kết nối quá hạn (Timeout 60s)";
                AddLog($"[{port.PortName}] Đang xử lý quá 60 giây; kích hoạt cứu sống cổng ngay.", "WARN");
                UpdateDashboard();
            }),
            InvalidateSession = InvalidateSimSession,
            RecoverFaultedPortAsync = AutoRecoverFaultedPortAsync,
            Log = AddLog
        }, _lifetimeCts.Token);
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

    public void UpsertCommandQueue(
        string commandId,
        string portId,
        string type,
        string recipient,
        string content,
        string status,
        string? result = null,
        string? error = null,
        string? source = null)
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
            if (!string.IsNullOrWhiteSpace(source))
                item.Source = source.Trim();
            else if (string.IsNullOrWhiteSpace(item.Source))
                item.Source = "Tool";
            item.Recipient = recipient;
            item.Content = content;
            item.Status = status;
            item.Result = result ?? item.Result;
            item.Error = error ?? string.Empty;
            item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");

            // Multi-port SMS batches can legitimately contain thousands of rows.
            // A 200-row cap silently discarded pending work.
            while (CommandQueue.Count > 10_000)
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

    public event Action<LogMessage>? LogAdded;

    public void AddLog(string message, string level = "INFO")
    {
        message = TextEncodingNormalizer.RepairMojibake(message);
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
            System.IO.File.AppendAllText(
                logFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch { }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var newLog = new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Message = message };
            SystemLogs.Insert(0, newLog);
            if (SystemLogs.Count > 500)
            {
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
            }
            // Cập nhật bộ lọc log sau mỗi lần thêm dòng mới
            OnPropertyChanged(nameof(FilteredLogs));
            OnPropertyChanged(nameof(FilteredLogCount));
            LogAdded?.Invoke(newLog);
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
    private async Task ReloadSimAsync(string portName)
    {
        if (string.IsNullOrEmpty(portName)) return;
        await ModemService.ReloadSimAsync(portName);
        SnackbarMessageQueue.Enqueue($"Đã gửi lệnh tải lại SIM cho cổng {portName}.");
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
                        InvalidateSimSession(p.PortName);
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

        var activePorts = targetPorts.Where(port => port.Status == SimStatus.Active).ToList();
        int skipped = targetPorts.Count - activePorts.Count;
        foreach (var port in targetPorts.Except(activePorts))
        {
            AddLog($"[{port.PortName}] Bỏ qua vì SIM không ở trạng thái Active (hiện tại: {port.Status}).", "WARN");
        }

        await Task.WhenAll(activePorts.Select(async port =>
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() => port.LastMessageContent = "Đang gửi DK EZ...");
                AddLog($"[{port.PortName}] Đang gửi lệnh DK EZ đến 888...", "INFO");
                string result = await _smsService.SendAsync(port.PortName, "888", "DK EZ", _lifetimeCts.Token);
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
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Lỗi đăng ký EZ COM: {ex.Message}", "ERROR");
            }
        }));
        
        if (skipped > 0)
        {
            SnackbarMessageQueue.Enqueue($"Đã bỏ qua {skipped} cổng do chưa kết nối xong (Status != Active).");
        }
    }

    [RelayCommand]
    private void ApproveUnknownSim(object obj)
    {
        List<SimPort> targetPorts;
        
        if (obj?.ToString() == "AllBlocked")
        {
            // Lấy cả WaitingAccept (SIM mới chờ duyệt) lẫn SecurityBlocked (bị chặn thực sự)
            targetPorts = Ports.Where(p => p.Status == SimStatus.SecurityBlocked || p.Status == SimStatus.WaitingAccept).ToList();
        }
        else if (obj is SimPort clickedPort)
        {
            // Bấm nút "Chấp nhận" trên 1 dòng đơn lẻ -> Chỉ duyệt dòng đó, phớt lờ Checkbox
            targetPorts = new List<SimPort> { clickedPort };
        }
        else
        {
            // Bấm nút "Chấp nhận" tổng (Global) -> Duyệt các dòng đang tích Checkbox
            targetPorts = Ports.Where(p => p.IsSelected).ToList();
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
            
            // Chấp nhận cả WaitingAccept (SIM mới chờ duyệt) lẫn SecurityBlocked (SIM bị chặn)
            if (port.Status != SimStatus.SecurityBlocked && port.Status != SimStatus.WaitingAccept)
            {
                continue;
            }

            if (string.IsNullOrEmpty(port.Serial))
            {
                AddLog($"[{port.PortName}] Lỗi: Không tìm thấy CCID.", "ERROR");
                continue;
            }

            successCount++;
            // Mọi cách Accept (đơn, hàng loạt, nút global) đều đi qua cùng một
            // state machine có xác minh CCID; không lưu backup trước khi ghi thành công.
            _ = Task.Run(() => AcceptNewSimAsync(port.PortName));
        }
        
        if (successCount > 0)
        {
            SnackbarMessageQueue.Enqueue($"Đang xác minh và chấp nhận {successCount} SIM...");
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
                    InvalidateSimSession(p.PortName);
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
                foreach (var name in Ports.Select(p => p.PortName).ToList()) InvalidateSimSession(name);
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

    public Task RefreshPortAsync(string portName) => RefreshPortsAsync([portName]);

    public void RefreshAllPorts() => _ = RefreshPortsAsync(GetPortsSnapshot().Select(p => p.PortName));

    public async Task RefreshPortsAsync(IEnumerable<string> portNames)
    {
        var names = portNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return;

        foreach (string name in names)
        {
            // Refresh thủ công đồng nghĩa người dùng muốn thử cứu lại cổng đã cách ly.
            _quarantinedPorts.TryRemove(name, out _);
            _portRecoveryAttempts.TryRemove(name, out _);
            InvalidateSimSession(name);
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var port in Ports.Where(p => names.Contains(p.PortName, StringComparer.OrdinalIgnoreCase)).ToList())
                Ports.Remove(port);
            AddLog($"Đang làm mới {names.Count} cổng...");
        });

        try
        {
            foreach (string name in names) _modemService.Disconnect(name);
            await Task.Delay(1500, _lifetimeCts.Token);
            _modemService.ConnectAll(115200);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"Làm mới cổng thất bại: {ex.Message}", "ERROR");
        }
    }

    private (long Epoch, CancellationToken Token) StartSimSession(string portName, string ccid)
    {
        var session = _portSessions.Begin(portName, ccid, _lifetimeCts.Token);
        return (session.Epoch, session.Token);
    }

    private void InvalidateSimSession(string portName)
    {
        _portSessions.Invalidate(portName);
        _initializingPorts.TryRemove(portName, out _);
    }

    private bool TryBeginPortInitialization(string portName, out Guid lease)
    {
        lease = Guid.NewGuid();
        return _initializingPorts.TryAdd(portName, lease);
    }

    private void EndPortInitialization(string portName, Guid lease)
    {
        // Remove theo cả key + lease để tác vụ cũ không xóa khóa của phiên mới.
        ((ICollection<KeyValuePair<string, Guid>>)_initializingPorts)
            .Remove(new KeyValuePair<string, Guid>(portName, lease));
    }

    private bool IsSimSessionCurrent(string portName, string ccid, long epoch)
    {
        return _portSessions.IsCurrent(portName, ccid, epoch);
    }

    private bool TryGetCurrentSimSession(
        string portName,
        out string ccid,
        out long epoch,
        out CancellationToken token)
    {
        ccid = string.Empty;
        epoch = 0;
        token = CancellationToken.None;

        if (!_portSessions.TryGet(portName, out var session)) return false;
        ccid = session.Ccid;
        epoch = session.Epoch;
        token = session.Token;
        return true;
    }

    public bool IsPortReadyForOperation(string portName)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        return port != null
            && port.Status == SimStatus.Active
            && TryGetCurrentSimSession(portName, out _, out _, out _);
    }

    public async Task<bool> RecoverActivePortAsync(string portName)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null
            || port.Status != SimStatus.Active
            || !TryGetCurrentSimSession(portName, out _, out _, out _))
        {
            return false;
        }

        return await ReloadPortSafelyAsync(portName, "Đang khôi phục modem an toàn...");
    }

    public async Task<bool> ReloadPortSafelyAsync(string portName, string progressText = "Đang tải lại SIM...")
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null) return false;

        // Recovery có reboot phải hủy phiên trước; modem chỉ được trở lại Active qua
        // đúng pipeline đọc CCID -> xác minh IMEI -> cấu hình -> bật radio.
        InvalidateSimSession(portName);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            port.IsRebooting = true;
            port.Status = SimStatus.Connecting;
            port.DeviceName = progressText;
            port.LastError = string.Empty;
        });

        try
        {
            bool resumed = await _modemService.ReloadAndResumeSimAsync(portName, _lifetimeCts.Token);
            if (!resumed)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => port.IsRebooting = false);
                AddLog($"[{portName}] Modem đã boot nhưng chưa xác nhận được SIM; chuyển sang dò hot-plug.", "WARN");
                _modemService.StartHotplugWaitLoop(portName);
            }
            return resumed;
        }
        catch (OperationCanceledException)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => port.IsRebooting = false);
            return false;
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => port.IsRebooting = false);
            AddLog($"[{portName}] Khôi phục modem thất bại: {ex.Message}", "ERROR");
            _modemService.StartHotplugWaitLoop(portName);
            return false;
        }
    }

    private async Task AutoRecoverFaultedPortAsync(SimPort faultedPort)
    {
        string portName = faultedPort.PortName;
        if (!AppSettings.AutoRecovery || _quarantinedPorts.ContainsKey(portName)) return;
        if (!_portRecoveryInProgress.TryAdd(portName, 0)) return;

        try
        {
            int attempt = _portRecoveryAttempts.AddOrUpdate(portName, 1, (_, old) => old + 1);
            if (attempt > MaxAutomaticRecoveryAttempts)
            {
                await QuarantinePortAsync(portName, MaxAutomaticRecoveryAttempts);
                return;
            }

            AddLog($"[AUTO-RECOVERY] {portName}: bắt đầu cứu cổng bước {attempt}/{MaxAutomaticRecoveryAttempts}.", "WARN");
            InvalidateSimSession(portName);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var current = Ports.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
                if (current == null) return;
                current.Status = SimStatus.Connecting;
                current.DeviceName = $"Đang cứu cổng – bước {attempt}/{MaxAutomaticRecoveryAttempts}";
                current.LastError = string.Empty;
                UpdateDashboard();
            });

            if (attempt == 1)
            {
                // Mức nhẹ: COM còn trả AT thì chỉ dựng lại state machine SIM.
                string ping = await _modemService.SendCommandAsync(portName, "AT", 4000, silent: true);
                if (ping.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    _modemService.StartHotplugWaitLoop(portName);
            }
            else if (attempt == 2)
            {
                // Mức vừa: reboot riêng module EC20, không ảnh hưởng COM khác.
                bool resumed = await _modemService.ReloadAndResumeSimAsync(portName, _lifetimeCts.Token);
                if (!resumed)
                    _modemService.StartHotplugWaitLoop(portName);
            }
            else
            {
                // Mức cuối: đóng/mở lại đúng SerialPort rồi để pipeline nhận dạng từ đầu.
                _modemService.Disconnect(portName);
                await Task.Delay(2000, _lifetimeCts.Token);
                _modemService.ConnectAll(AppSettings.BaudRate > 0 ? AppSettings.BaudRate : 115200);
            }

            bool recovered = false;
            for (int i = 0; i < 45 && !_lifetimeCts.IsCancellationRequested; i++)
            {
                await Task.Delay(2000, _lifetimeCts.Token);
                var current = GetPortsSnapshot().FirstOrDefault(p =>
                    p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
                if (current == null) continue;

                recovered = current.Status == SimStatus.Active
                    || current.Status == SimStatus.WaitingAccept
                    || current.Status == SimStatus.SecurityBlocked
                    || current.Status == "Chờ cắm SIM";
                if (recovered) break;
            }

            if (recovered)
            {
                _portRecoveryAttempts.TryRemove(portName, out _);
                _quarantinedPorts.TryRemove(portName, out _);
                AddLog($"[AUTO-RECOVERY] {portName}: cổng đã sống lại.", "SUCCESS");
            }
            else if (attempt >= MaxAutomaticRecoveryAttempts)
            {
                await QuarantinePortAsync(portName, attempt);
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var current = Ports.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
                    if (current == null) return;
                    current.Status = SimStatus.NoResponse;
                    current.LastError = $"Recovery bước {attempt} chưa thành công";
                    UpdateDashboard();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"[AUTO-RECOVERY] {portName}: {ex.Message}", "ERROR");
        }
        finally
        {
            _portRecoveryInProgress.TryRemove(portName, out _);
        }
    }

    private async Task QuarantinePortAsync(string portName, int attempts)
    {
        _quarantinedPorts[portName] = 0;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var current = Ports.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (current == null) return;
            current.Status = SimStatus.Quarantined;
            current.DeviceName = "Đã cách ly – không ảnh hưởng COM khác";
            current.LastError = $"Tự cứu thất bại sau {attempts} bước; bấm Refresh để thử lại";
            current.SignalStrength = 0;
            UpdateDashboard();
        });
        AddLog($"[AUTO-RECOVERY] {portName}: đã cách ly sau {attempts} bước thất bại.", "ERROR");
    }

    private async Task<string> ReadLiveCcidAsync(
        string portName, CancellationToken ct, int attempts = 3)
    {
        attempts = Math.Max(1, attempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string raw = await _modemService.SendCommandAsync(
                portName, "AT+QCCID", 3000, silent: true, ct: ct);
            string ccid = NormalizeCcid(raw);
            if (!string.IsNullOrWhiteSpace(ccid)) return ccid;

            raw = await _modemService.SendCommandAsync(
                portName, "AT+CCID", 3000, silent: true, ct: ct);
            ccid = NormalizeCcid(raw);
            if (!string.IsNullOrWhiteSpace(ccid)) return ccid;

            if (attempt < attempts) await Task.Delay(500, ct);
        }

        return string.Empty;
    }

    internal static bool IsRadioStackDisabled(string? cfunResponse) =>
        Regex.IsMatch(cfunResponse ?? string.Empty, @"\+CFUN:\s*(?:0|4)\b", RegexOptions.IgnoreCase);

    private async Task<bool> CompletePortInitializationAsync(
        SimPort port, string ccid, string expectedImei, long epoch, CancellationToken token)
    {
        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return false;
        bool radioMayBeOn = false;
        bool activationSucceeded = false;

        async Task ForceRadioOffBestEffortAsync()
        {
            try
            {
                await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=4", 8000, silent: true);
                string state = await _modemService.SendCommandAsync(port.PortName, "AT+CFUN?", 3000, silent: true);
                AddLog($"[{port.PortName}] [RADIO_FAILSAFE] {state.Trim()}",
                    IsRadioStackDisabled(state) ? "WARN" : "ERROR");
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] [RADIO_FAILSAFE_FAILED] {ex.Message}", "ERROR");
            }
        }

        async Task<(bool Valid, string Imei)> VerifyIdentityAsync(
            string phase, bool radioMustBeOff, int ccidAttempts)
        {
            // Đọc IMEI trước CCID để phát hiện sai danh tính sớm nhất sau CFUN=1.
            string cfun = await _modemService.SendCommandAsync(
                port.PortName, "AT+CFUN?", 3000, silent: true, ct: token);
            string rawImei = await _modemService.SendCommandAsync(
                port.PortName, "AT+CGSN", 8000, silent: true, ct: token);
            string liveImei = NormalizeImei(rawImei);
            string rawStoredImei = await _modemService.SendCommandAsync(
                port.PortName, "AT+EGMR=0,7", 8000, silent: true, ct: token);
            string storedImei = NormalizeImei(rawStoredImei);
            string rawStored2Imei = await _modemService.SendCommandAsync(
                port.PortName, "AT+EGMR=0,10", 8000, silent: true, ct: token);
            string stored2Imei = NormalizeImei(rawStored2Imei);
            string liveCcid = await ReadLiveCcidAsync(port.PortName, token, attempts: ccidAttempts);

            bool cfunMatches = radioMustBeOff
                ? IsRadioStackDisabled(cfun)
                : Regex.IsMatch(cfun, @"\+CFUN:\s*1\b", RegexOptions.IgnoreCase);
            bool valid = cfunMatches
                      && string.Equals(liveCcid, NormalizeCcid(ccid), StringComparison.OrdinalIgnoreCase)
                      && Services.ImeiManagementService.AreEquivalentImei(liveImei, expectedImei)
                      && Services.ImeiManagementService.StoredImeiMatchesOrUnavailable(rawStoredImei, expectedImei)
                      && Services.ImeiManagementService.StoredImeiMatchesOrUnavailable(rawStored2Imei, expectedImei)
                      && IsSimSessionCurrent(port.PortName, ccid, epoch);

            AddLog($"[{port.PortName}] [{(valid ? "IMEI_VERIFY_OK" : "IMEI_VERIFY")}] phase={phase}; CFUN={cfun.Trim()}; expected={expectedImei}; CGSN={liveImei}; EGMR_slot7={storedImei}; EGMR_slot10={stored2Imei}; CCID={liveCcid}",
                valid ? "SUCCESS" : "ERROR");
            return (valid, liveImei);
        }

        try
        {
            string cfunOff = await _modemService.SendCommandAsync(
                port.PortName, "AT+CFUN=4", 10000, silent: true, ct: token);
            string cfunOffState = await _modemService.SendCommandAsync(
                port.PortName, "AT+CFUN?", 3000, silent: true, ct: token);
            if (cfunOff.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || !IsRadioStackDisabled(cfunOffState))
                return false;

            // Không defer CCID. Firmware không đọc được CCID trong CFUN=4 phải fail-closed.
            var beforeRadio = await VerifyIdentityAsync("radio-off-before-config", radioMustBeOff: true, ccidAttempts: 4);
            if (!beforeRadio.Valid)
            {
                AddLog($"[{port.PortName}] Xác minh CCID/IMEI khi radio tắt thất bại. Không bật RF.", "ERROR");
                return false;
            }

            bool configured = await _modemService.ReinitializeSettingsAsync(port.PortName, token);
            if (!configured || !IsSimSessionCurrent(port.PortName, ccid, epoch)) return false;

            var afterConfig = await VerifyIdentityAsync("radio-off-after-config", radioMustBeOff: true, ccidAttempts: 4);
            if (!afterConfig.Valid) return false;

            // Từ dòng này, timeout cũng phải được coi là RF có thể đã bật.
            radioMayBeOn = true;
            string cfunOn = await _modemService.SendCommandAsync(
                port.PortName, "AT+CFUN=1", 15000, silent: true);
            if (cfunOn.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return false;

            // Không delay mù: kiểm tra CGSN/EGMR trước, rồi mới chờ CCID sẵn sàng.
            var afterRadio = await VerifyIdentityAsync("radio-on-final", radioMustBeOff: false, ccidAttempts: 6);
            if (!afterRadio.Valid)
            {
                AddLog($"[{port.PortName}] Danh tính sau CFUN=1 không khớp; cấm reboot NV và tắt RF ngay.", "ERROR");
                return false;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
                port.IsRebooting = false;
                port.Imei = afterRadio.Imei;
                MarkPortActiveAfterInit(port.PortName);
            });

            activationSucceeded = IsSimSessionCurrent(port.PortName, ccid, epoch)
                && port.Status == SimStatus.Active;
            if (activationSucceeded) _modemService.StartPollingNetwork(port.PortName);
            return activationSucceeded;
        }
        finally
        {
            // Cancellation/timeout/exception sau CFUN=1 không được để RF bật ngoài kiểm soát.
            if (radioMayBeOn && !activationSucceeded)
                await ForceRadioOffBestEffortAsync();
        }
    }

    public async Task<bool> ResetNetworkSafelyAsync(string portName)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null
            || port.Status != SimStatus.Active
            || string.IsNullOrWhiteSpace(port.Imei)
            || !TryGetCurrentSimSession(portName, out var ccid, out var epoch, out var token))
        {
            return false;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            port.Status = SimStatus.Connecting;
            port.DeviceName = "Đang làm mới kết nối mạng...";
        });

        // Dùng lại đúng cổng xác minh chuẩn thay vì tự CFUN=4 -> CFUN=1 trực tiếp.
        bool active = await CompletePortInitializationAsync(port, ccid, port.Imei, epoch, token);
        if (!active && IsSimSessionCurrent(portName, ccid, epoch))
        {
            await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.Status = SimStatus.NoResponse;
                port.LastError = "Không xác minh được SIM/IMEI sau khi làm mới mạng";
            });
        }
        return active;
    }

    private async Task<bool> ValidateSessionIdentityAsync(
        string portName, string ccid, long epoch, CancellationToken token)
    {
        if (!IsSimSessionCurrent(portName, ccid, epoch)) return false;
        string liveCcid = await ReadLiveCcidAsync(portName, token);
        if (!string.IsNullOrWhiteSpace(liveCcid))
        {
            bool matches = string.Equals(liveCcid, NormalizeCcid(ccid), StringComparison.OrdinalIgnoreCase)
                && IsSimSessionCurrent(portName, ccid, epoch);
            if (!matches)
                AddLog($"[{portName}] [SESSION_VERIFY_FAILED] expected_ccid={NormalizeCcid(ccid)} live_ccid={liveCcid} epoch={epoch}", "WARN");
            return matches;
        }

        // Không được defer sang sau CFUN=1: lúc đó EC20 đã có thể attach mạng.
        AddLog($"[{portName}] [SESSION_VERIFY_FAILED] Không đọc được CCID khi RF tắt; fail-closed và hủy xử lý IMEI.", "ERROR");
        return false;
    }

    private async Task ProcessCurrentSimSessionAsync(
        SimPort port, string ccid, bool forceAccept, long epoch, CancellationToken token,
        Guid initializationLease, string? explicitTargetImei = null)
    {
        string portName = port.PortName;
        using var initializationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        initializationCts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken initializationToken = initializationCts.Token;
        try
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

            string currentImei = NormalizeImei(port.Imei);
            if (string.IsNullOrEmpty(currentImei))
            {
                string imeiResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
                currentImei = NormalizeImei(imeiResp);
            }

            if (string.IsNullOrEmpty(currentImei) || !IsSimSessionCurrent(portName, ccid, epoch))
            {
                AddLog($"[{portName}] Không đọc được IMEI hoặc phiên SIM đã thay đổi.", "WARN");
                if (IsSimSessionCurrent(portName, ccid, epoch))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        port.Status = SimStatus.NoResponse;
                        port.LastError = "Không đọc được IMEI khi khởi tạo";
                        port.DeviceName = "Khởi tạo thất bại – chờ recovery";
                        UpdateDashboard();
                    });
                }
                return;
            }

            if (!await ValidateSessionIdentityAsync(portName, ccid, epoch, initializationToken))
            {
                AddLog($"[{portName}] Không xác minh được đúng CCID khi RF tắt; giữ sóng tắt và chặn xử lý IMEI.", "ERROR");
                await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Status = SimStatus.SecurityBlocked;
                    port.LastError = "Không xác minh được CCID khi radio tắt";
                    port.DeviceName = "Đã chặn – chưa xác minh được SIM/IMEI";
                    UpdateDashboard();
                });
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() => port.Imei = currentImei);

            var result = await _imeiManagementService.ProcessImeiAsync(
                port,
                ccid,
                currentImei,
                AppSettings,
                queryCcid => FindImeiBackupEntry(queryCcid),
                newEntry => AddNewImeiCacheEntry(newEntry),
                action => Application.Current.Dispatcher.Invoke(action),
                forceAccept,
                initializationToken,
                () => ValidateSessionIdentityAsync(portName, ccid, epoch, initializationToken),
                candidate => IsImeiAssignedOrReserved(candidate, ccid),
                explicitTargetImei);

            AddLog($"[{portName}] [IMEI_RESULT] status={result.Status} forceAccept={forceAccept} message={result.ErrorMessage}",
                result.Status is Services.ImeiProcessStatus.Matched or Services.ImeiProcessStatus.Applied ? "SUCCESS" : "INFO");

            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

            if (result.Status == Services.ImeiProcessStatus.Matched || result.Status == Services.ImeiProcessStatus.Applied)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Imei = result.FinalImei;
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang hoàn tất cấu hình modem...";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                });

                bool active = await CompletePortInitializationAsync(port, ccid, result.FinalImei, epoch, initializationToken);
                if (active && (result.Status == Services.ImeiProcessStatus.Applied
                               || Services.ImeiManagementService.IsValidImei(NormalizeImei(explicitTargetImei))))
                {
                    var existing = FindImeiBackupEntry(ccid);
                    AddNewImeiCacheEntry(new SimBackupEntry
                    {
                        Ccid = ccid,
                        Imei = result.FinalImei,
                        PhoneNumber = port.PhoneNumber,
                        CreatedAt = existing?.CreatedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceFile = string.IsNullOrWhiteSpace(result.TargetSource)
                            ? (existing?.SourceFile ?? "verified-live-accept")
                            : result.TargetSource,
                        SimRegDate = port.SimRegDate
                    });
                    AddLog($"[{portName}] [IMEI_BACKUP_COMMIT] CCID={ccid}; IMEI={result.FinalImei}; chỉ lưu sau xác minh radio-on.", "SUCCESS");
                }
                else if (!active && IsSimSessionCurrent(portName, ccid, epoch))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        port.Status = SimStatus.SecurityBlocked;
                        port.LastError = "Không hoàn tất được cấu hình/xác minh SIM";
                        port.DeviceName = "Đã chặn – xác minh CCID/IMEI sau bật sóng thất bại";
                        UpdateDashboard();
                    });
                }
            }
            else if (result.Status == Services.ImeiProcessStatus.WaitingAccept)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                    port.Status = SimStatus.WaitingAccept;
                    port.LastError = result.ErrorMessage;
                    port.DeviceName = "SIM mới – bấm ACCEPT để kích hoạt";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                });
            }
            else if (result.Status == Services.ImeiProcessStatus.SecurityBlocked)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                    port.Status = SimStatus.SecurityBlocked;
                    port.LastError = string.IsNullOrEmpty(result.ErrorMessage) ? SecurityErrors.WrongImei : result.ErrorMessage;
                    port.DeviceName = "Bị chặn bảo mật";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                });
            }
            else if (!initializationToken.IsCancellationRequested)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                    port.Status = forceAccept ? SimStatus.SecurityBlocked : SimStatus.NoResponse;
                    port.LastError = result.ErrorMessage;
                    if (forceAccept)
                        port.DeviceName = "Accept thất bại – giữ radio tắt để kiểm tra lại";
                    UpdateDashboard();
                });
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 8000, silent: true);
            AddLog($"[{portName}] Khởi tạo SIM quá hạn 2 phút; giải phóng khóa và chuyển recovery.", "WARN");
            if (IsSimSessionCurrent(portName, ccid, epoch))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Status = SimStatus.NoResponse;
                    port.LastError = "Khởi tạo SIM quá hạn 2 phút";
                    port.DeviceName = "Khởi tạo quá hạn – chờ recovery";
                    UpdateDashboard();
                });
            }
        }
        catch (OperationCanceledException)
        {
            await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 8000, silent: true);
        }
        catch (Exception ex)
        {
            await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 8000, silent: true);
            if (IsSimSessionCurrent(portName, ccid, epoch))
            {
                AddLog($"[{portName}] Lỗi xử lý phiên SIM: {ex.Message}", "ERROR");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Status = SimStatus.NoResponse;
                    port.LastError = ex.Message;
                    port.DeviceName = "Khởi tạo lỗi – chờ recovery";
                    UpdateDashboard();
                });
            }
        }
        finally
        {
            ReleaseImeiReservations(ccid);
            EndPortInitialization(portName, initializationLease);
        }
    }

    private bool IsImeiAssignedOrReserved(string candidate, string ccid)
    {
        string normalizedCandidate = Services.ImeiManagementService.ToCanonicalImei(candidate);
        string normalizedCcid = NormalizeCcid(ccid);
        if (!Services.ImeiManagementService.IsValidImei(normalizedCandidate)) return true;

        lock (_imeiCacheLock)
        {
            if (_imeiCache.Values.Any(entry =>
                !string.Equals(NormalizeCcid(entry.Ccid), normalizedCcid, StringComparison.OrdinalIgnoreCase)
                && Services.ImeiManagementService.AreEquivalentImei(entry.Imei, normalizedCandidate)))
                return true;
        }

        string owner = _imeiTargetReservations.GetOrAdd(normalizedCandidate, normalizedCcid);
        return !string.Equals(owner, normalizedCcid, StringComparison.OrdinalIgnoreCase);
    }

    private void ReleaseImeiReservations(string ccid)
    {
        string normalizedCcid = NormalizeCcid(ccid);
        foreach (var reservation in _imeiTargetReservations)
        {
            if (string.Equals(reservation.Value, normalizedCcid, StringComparison.OrdinalIgnoreCase))
            {
                ((ICollection<KeyValuePair<string, string>>)_imeiTargetReservations)
                    .Remove(reservation);
            }
        }
    }

    private static void ClearSimScopedState(SimPort port)
    {
        // Chỉ giữ thông tin vật lý của COM (PortName/HardwareName/STT và bộ đếm health).
        // Mọi dữ liệu dưới đây thuộc SIM cũ và tuyệt đối không được hiển thị sau khi rút/thay SIM.
        port.IsRebooting = false;
        port.PhoneNumber = string.Empty;
        port.NetworkProvider = string.Empty;
        port.Imei = string.Empty;
        port.Serial = string.Empty;
        port.Balance = string.Empty;
        port.IsBalanceLoading = false;
        port.PromotionBalance = string.Empty;
        port.ExpiryDate = string.Empty;
        port.CreatedAt = string.Empty;
        port.UpdatedAt = string.Empty;
        port.SimRegDate = string.Empty;
        port.Lock1C = string.Empty;
        port.Lock2C = string.Empty;
        port.ForwardedTo = string.Empty;
        port.ForwardCount = 0;
        port.CallCount = 0;
        port.SignalStrength = 0;
        port.Otp = string.Empty;
        port.Sender = string.Empty;
        port.LastMessageContent = string.Empty;
        port.LastReceivedTime = string.Empty;
        port.LastSweepTime = string.Empty;
        port.LastSmsSentAt = string.Empty;
        port.VnptStatus = string.Empty;
        port.LastCommandResult = string.Empty;
        port.LastUssdResult = string.Empty;
        port.LastSmsResult = string.Empty;
        port.LastCallResult = string.Empty;
        port.LastMmsResult = string.Empty;
        port.LastImeiResult = string.Empty;
        port.LastDataResult = string.Empty;
        port.LastDelayResult = string.Empty;
        port.LastError = string.Empty;
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
                if (e.Data == "[PORT_OPENED]" || e.Data.StartsWith("[STATUS_SIM_LOCKED]") || e.Data.StartsWith("[PARSE_CCID]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+COPS:") || e.Data.StartsWith("+CUSD:") || e.Data.StartsWith("[WAITING_FOR_SIM]") || e.Data.StartsWith("[PARSE_IMEI]") || e.Data.StartsWith("[STATUS_NO_RESPONSE]") || e.Data.StartsWith("[NETWORK_WAITING]") || e.Data.StartsWith("[NETWORK_RECOVERY]") || e.Data.StartsWith("[NETWORK_FAILED]") || e.Data.StartsWith("Lỗi kết nối"))
                {
                    port = new SimPort { PortName = e.PortName, Status = "Chờ cắm SIM", SignalStrength = 0 };
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

            if (e.Data.StartsWith("[MODEM_PROFILE]", StringComparison.Ordinal))
            {
                string Payload(string key)
                {
                    Match match = Regex.Match(e.Data, $@"(?:\[MODEM_PROFILE\]\s*|;\s*){Regex.Escape(key)}=([^;]*)", RegexOptions.IgnoreCase);
                    return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
                }
                port.ModemManufacturer = Payload("manufacturer");
                port.ModemModel = Payload("model");
                port.ModemFirmware = Payload("firmware");
                port.ModemCapabilities = Payload("capabilities");
            }

            if (e.Data == "[PORT_OPENED]")
            {
                ClearSimScopedState(port);
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang kiểm tra modem/SIM...";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_SIM_LOCKED]"))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(port);
                port.Status = e.Data.Contains("PUK", StringComparison.OrdinalIgnoreCase) ? "SIM yêu cầu PUK" : "SIM yêu cầu PIN";
                port.DeviceName = "SIM đang bị khóa";
                port.LastError = port.Status;
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[WAITING_FOR_SIM]"))
            {
                if (port.IsRebooting)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Modem đang khởi động lại và nhận diện SIM...";
                    UpdateDashboard();
                    return;
                }
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(port);
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("Lỗi kết nối"))
            {
                port.Status = SimStatus.NoResponse;
                port.DeviceName = "Lỗi kết nối";
                port.LastError = e.Data;
            }
            else if (e.Data.StartsWith("[NETWORK_WAITING]") && !_modemService.IsCallInProgress(e.PortName))
            {
                // Modem và phiên SIM vẫn hoạt động; chỉ chưa có COPS. Không đổi
                // Active thành NoResponse và không reset RF, để cổng tự bắt sóng.
                port.LastError = "Đang chờ đăng ký nhà mạng (không reset RF)";
                port.SignalStrength = 0;
            }
            else if (e.Data.StartsWith("[NETWORK_RECOVERY]") && !_modemService.IsCallInProgress(e.PortName))
            {
                // Mất đăng ký mạng không đồng nghĩa modem/SIM chưa khởi tạo. Nếu phiên SIM
                // vẫn hợp lệ thì giữ Active, chỉ cập nhật lỗi sóng để không treo UI.
                if (TryGetCurrentSimSession(e.PortName, out _, out _, out _))
                {
                    port.LastError = "Đang thử khôi phục đăng ký nhà mạng";
                    port.SignalStrength = 0;
                }
            }
            else if (e.Data.Contains("[NETWORK_FAILED]") && !_modemService.IsCallInProgress(e.PortName))
            {
                bool simInitialized = TryGetCurrentSimSession(e.PortName, out _, out _, out _)
                    && !string.IsNullOrWhiteSpace(port.Serial)
                    && !string.IsNullOrWhiteSpace(port.Imei);
                port.Status = simInitialized ? SimStatus.Active : SimStatus.NoResponse;
                port.LastError = simInitialized
                    ? "SIM đã sẵn sàng nhưng chưa đăng ký được nhà mạng"
                    : "Không đăng ký được mạng";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[SIM_CONTACT_ERROR]"))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(port);
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "COM sống – modem không đọc được chip SIM";
                port.LastError = "Kiểm tra chiều SIM, tiếp điểm hoặc thử SIM khác trên cùng khe";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (!_modemService.IsCallInProgress(e.PortName)
                && !port.IsRebooting
                && (e.Data.Contains("+CME ERROR: 10") || e.Data.Contains("+CME ERROR: 13") || e.Data.Contains("+CME ERROR: 11") || e.Data.Contains("+CPIN: NOT INSERTED") || e.Data.Contains("+CPIN: NOT READY") || e.Data.Contains("SIM not inserted")))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(port);
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                _modemService.StartHotplugWaitLoop(e.PortName);
                UpdateDashboard();
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
                port.IsBalanceLoading = false;
                var match = Regex.Match(e.Data, @"\+CUSD:.*?""(.*?)(?:""|$)", RegexOptions.Singleline);
                if (match.Success)
                {
                    string ussdContent = match.Groups[1].Value;
                    
                    // Giải mã UCS2 (Hex sang string UTF-8) để đọc được tiếng Việt
                    ussdContent = UssdResponseDecoder.DecodePayload(ussdContent);

                    // Lưu kết quả USSD vào LastUssdResult (dùng cho tab USSD trong CommandPanel)
                    port.LastUssdResult = ussdContent;

                    // Sửa lỗi Parse nhầm "1đ" từ tin nhắn báo không đủ tiền hoặc quảng cáo cước phí
                    // Cập nhật hỗ trợ TKG (Tài Khoản Gốc) của Viettel, VinaPhone
                    // Guard: bỏ qua nếu nội dung USSD chứa từ khoá quảng cáo/cước (tránh parse nhầm 1đ/900đ)
                    bool ussdHasAdKeywords = Regex.IsMatch(ussdContent,
                        @"cuoc|phi\s*dich\s*vu|uu\s*dai|goi\s*cuoc|tang\s*them|khuyen\s*mai|phi\s*truoc|phi\s*cuoc|khong\s*du|chua\s*du",
                        RegexOptions.IgnoreCase);
                    var strictMatch = Regex.Match(ussdContent, @"(?:TK\s*goc|TKG|TK\s*chinh|TKC|Tai khoan chinh|Tài khoản chính|Tai khoan|Tài khoản|So du|Số dư|TK|balance)[^\d]{0,20}(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)?", RegexOptions.IgnoreCase);
                    if (strictMatch.Success) 
                    {
                        string rawVal = strictMatch.Groups[1].Value.Replace(".", "").Replace(",", "");
                        // Reject số dư < 100 VND để tránh parse nhầm cước phí (vd: "1d/ngay", "900d cuoc")
                        if (int.TryParse(rawVal, out int parsedBal) && (parsedBal >= 100 || !ussdHasAdKeywords))
                        {
                            port.Balance = strictMatch.Groups[1].Value + "đ";
                        }
                    }
                    else
                    {
                        // Fallback nếu nhà mạng trả về format lạ, nhưng phải tránh các từ khóa rác và tránh cước phí (vd: 1000d/ngay)
                        if (!ussdHasAdKeywords && !Regex.IsMatch(ussdContent, @"khong du|chua du|cuoc|uu dai|tang|gia|khong lo|ho tro|phi|dang ky", RegexOptions.IgnoreCase))
                        {
                            var fallback = Regex.Match(ussdContent, @"(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)(?!/)", RegexOptions.IgnoreCase);
                            if (fallback.Success)
                            {
                                string fallbackRaw = fallback.Groups[1].Value.Replace(".", "").Replace(",", "");
                                if (int.TryParse(fallbackRaw, out int fallbackBal) && fallbackBal >= 100)
                                    port.Balance = fallback.Groups[1].Value + "đ";
                            }
                        }
                    }

                    // Hiển thị kết quả USSD trên cột "Nội dung" trong bảng COM ngay lập tức
                    port.LastMessageContent = "[USSD] " + ussdContent;
                    port.Sender = "USSD";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");

                    var phoneMatch = Regex.Match(ussdContent, @"(?:thuê bao|thue bao|so tb|số tb|msisdn|sim)[^\d]{0,15}(0\d{9,10}|84\d{9,10})", RegexOptions.IgnoreCase);
                    if (!phoneMatch.Success)
                    {
                        // Thử match đầu số Viettel (032-039, 086, 096, 097, 098) và Vinaphone (081-085, 088, 091, 094)
                        phoneMatch = Regex.Match(ussdContent, @"(?:84|0)(3[2-9]|8[1-9]|9[1-9])\d{7}");
                    }
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
                        string foundNumber = phoneMatch.Groups[1].Success ? phoneMatch.Groups[1].Value : phoneMatch.Value;
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
                                    UpdateImeiCacheEntry(port.Serial, value => value.PhoneNumber = foundNumber);
                                }
                            }
                        }
                    }

                    // 1. HSD (Hạn sử dụng)
                    var expiryMatch = Regex.Match(ussdContent, @"(?:HSD|han\s*sd|han\s*su\s*dung|ngay\s*het\s*han)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (expiryMatch.Success) 
                    {
                        port.ExpiryDate = expiryMatch.Groups[1].Value;
                    }
                    else
                    {
                        // Fallback: Lấy ngày đầu tiên xuất hiện trong USSD
                        var genericExpiryMatch = Regex.Match(ussdContent, @"\b(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})\b");
                        if (genericExpiryMatch.Success) port.ExpiryDate = genericExpiryMatch.Groups[1].Value;
                    }

                    // 2. Ngay KH (Ngày kích hoạt / Đăng ký SIM)
                    var khMatch = Regex.Match(ussdContent, @"(?:Ngay\s*KH|Ngay\s*kich\s*hoat|Ngay\s*DK|Ngay\s*dang\s*ky)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (khMatch.Success)
                    {
                        string regDate = khMatch.Groups[1].Value;
                        port.SimRegDate = regDate;
                        UpdateImeiCacheEntry(port.Serial, entry => entry.SimRegDate = regDate);
                    }

                    // 3. Khoa 1C (Khóa 1 chiều)
                    var lock1cMatch = Regex.Match(ussdContent, @"(?:Khoa\s*1C|Khoa\s*mot\s*chieu)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (lock1cMatch.Success)
                    {
                        string lock1cVal = lock1cMatch.Groups[1].Value;
                        port.Lock1C = lock1cVal;
                        UpdateImeiCacheEntry(port.Serial, entry => entry.Lock1C = lock1cVal);
                    }

                    // 4. Khoa 2C (Khóa 2 chiều)
                    var lock2cMatch = Regex.Match(ussdContent, @"(?:Khoa\s*2C|Khoa\s*hai\s*chieu)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (lock2cMatch.Success)
                    {
                        string lock2cVal = lock2cMatch.Groups[1].Value;
                        port.Lock2C = lock2cVal;
                        UpdateImeiCacheEntry(port.Serial, entry => entry.Lock2C = lock2cVal);
                    }

                    // Persist one complete snapshot after all USSD fields have been parsed.
                    UpdateImeiCacheEntry(port.Serial, _ => { });

                    UpdateDashboard(); // Refresh online/offline count when Balance is updated

                    // SnackbarMessageQueue.Enqueue($"[{e.PortName}] USSD: {ussdContent}");
                }
            }
            else if (e.Data.Contains("+COPS:"))
            {
                // Parse Network Provider from AT+COPS?
                // Example: +COPS: 0,0,"VIETTEL"
                var match = Regex.Match(e.Data, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)""");
                if (match.Success)
                {
                    string provider = match.Groups[1].Value.Trim();
                    if (provider == "45204") provider = "Viettel";
                    else if (provider == "45202") provider = "VinaPhone";
                    else if (provider == "45201") provider = "MobiFone";
                    else if (provider == "45205") provider = "Vietnamobile";
                    else if (provider == "45207") provider = "Gmobile";
                    else if (provider == "45208") provider = "iTel";
                    else if (provider == "45209") provider = "Wintel";
                    else
                    {
                        string pUpper = provider.ToUpperInvariant();
                        if (pUpper.Contains("VINAPHONE") || pUpper.Contains("VINA")) provider = "VinaPhone";
                        else if (pUpper.Contains("VIETTEL")) provider = "Viettel";
                        else if (pUpper.Contains("MOBIFONE") || pUpper.Contains("MOBI")) provider = "MobiFone";
                        else if (pUpper.Contains("VIETNAMOBILE") || pUpper.Contains("VNM")) provider = "Vietnamobile";
                        else if (pUpper.Contains("GMOBILE")) provider = "Gmobile";
                        else if (pUpper.Contains("WINTEL")) provider = "Wintel";
                        else if (pUpper.Contains("ITELECOM") || pUpper.Contains("ITEL")) provider = "iTel";
                    }

                    port.NetworkProvider = provider;
                    // COPS chỉ chứng minh modem thấy mạng, không chứng minh CCID/IMEI của
                    // phiên hiện tại đã được xác minh. Chỉ state machine mới được set Active.

                    // Chỉ hiển thị SĐT & TKC sau khi đã hiện Nhà mạng thành công
                    string? cachedPhone = null;
                    if (!string.IsNullOrEmpty(port.Serial))
                    {
                        _simCache.TryGetValue(port.Serial, out cachedPhone);
                    }

                    if (!string.IsNullOrEmpty(cachedPhone))
                    {
                        port.PhoneNumber = cachedPhone;
                        UpdateSmsReceiverPhone(port.PortName, cachedPhone);
                        AddLog($"[{port.PortName}] Đã hiển thị SĐT từ cache: {cachedPhone}", "SUCCESS");
                    }

                    string networkUpper = port.NetworkProvider.ToUpperInvariant();

                    // Mọi tác vụ hậu đăng ký mạng phải thuộc đúng phiên CCID hiện tại.
                    // Khi rút/đổi SIM, token bị hủy và không lệnh USSD/chuyển tiếp nào được chạy tiếp.
                    if (!TryGetCurrentSimSession(port.PortName, out var activeCcid, out var activeEpoch, out var activeToken)
                        || port.Status != SimStatus.Active)
                    {
                        return;
                    }

                    _ = Task.Run(async () => 
                    {
                        try
                        {
                        // Đợi 1 giây để bảo đảm UI cập nhật xong tên nhà mạng trước
                        await Task.Delay(1000, activeToken);
                        if (!IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch)
                            || port.Status != SimStatus.Active) return;

                        // Chạy độc lập để các tác vụ hậu mạng khác không phải chờ. Hàm này sở hữu
                        // loading-state và luôn kết thúc spinner kể cả nhà mạng không trả +CUSD.
                        _ = RunInitialBalanceLookupAsync(port, activeCcid, activeEpoch, activeToken);

                        if (!IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch)
                            || port.Status != SimStatus.Active) return;

                        // Tự động chuyển hướng cuộc gọi nếu tính năng được bật
                        if (AppSettings != null && AppSettings.EnableAutoCallForwarding && !string.IsNullOrWhiteSpace(AppSettings.ForwardPhoneNumber))
                        {
                            string randomFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                            if (!string.IsNullOrEmpty(randomFwd))
                            {
                                string fwdDialType = randomFwd.StartsWith("+") ? "145" : "129";
                                AddLog($"[{port.PortName}] Đang thiết lập tự động chuyển hướng đến {randomFwd}...");
                                
                                // Retry tối đa 3 lần (mạng vừa đăng ký có thể chưa sẵn sàng ngay)
                                bool fwdOk = false;
                                for (int attempt = 1; attempt <= 3 && !fwdOk; attempt++)
                                {
                                    string ccfcResult = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,1,\"{randomFwd}\",{fwdDialType}", timeoutMs: 8000);
                                    if (ccfcResult.Contains("OK"))
                                    {
                                        fwdOk = true;
                                        Application.Current.Dispatcher.Invoke(() =>
                                        {
                                            port.ForwardCount++;
                                            port.ForwardedTo = randomFwd;
                                        });
                                        AddLog($"[{port.PortName}] Chuyển hướng thành công → {randomFwd} (lần {attempt}, Tổng: {port.ForwardCount})", "SUCCESS");
                                    }
                                    else if (attempt < 3)
                                    {
                                        AddLog($"[{port.PortName}] Chuyển hướng thất bại lần {attempt}, thử lại sau 5s...", "WARN");
                                        await Task.Delay(5000, activeToken);
                                        if (!IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch)
                                            || port.Status != SimStatus.Active) return;
                                    }
                                    else
                                    {
                                        AddLog($"[{port.PortName}] Thiết lập chuyển hướng đến {randomFwd} thất bại sau 3 lần thử! (Lỗi từ mạng/SIM)", "ERROR");
                                    }
                                }
                            }
                        }

                        // Truy vấn trạng thái chuyển hướng thực tế từ nhà mạng để đồng bộ UI
                        if (!IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch)
                            || port.Status != SimStatus.Active) return;

                        string ccfcStatus = await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,2", timeoutMs: 8000);
                        var ccfcMatch = Regex.Match(ccfcStatus, @"\+CCFC:\s*1,\s*1,\s*""([^""]+)""");
                        if (ccfcMatch.Success)
                        {
                            string activeFwd = ccfcMatch.Groups[1].Value;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                port.ForwardedTo = activeFwd;
                            });
                        }
                        else if (AppSettings == null || !AppSettings.EnableAutoCallForwarding)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                port.ForwardedTo = string.Empty;
                            });
                        }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            if (IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch))
                                AddLog($"[{port.PortName}] Lỗi tác vụ hậu đăng ký mạng: {ex.Message}", "WARN");
                        }
                    }, activeToken);
                }
            }
            else if (e.Data.StartsWith("[PARSE_IMEI]"))
            {
                var match = Regex.Match(e.Data, @"\b(\d{14,17})\b");
                if (match.Success) port.Imei = match.Groups[1].Value;
            }
            else if (e.Data.StartsWith("[PARSE_CCID]"))
            {
                var match = Regex.Match(e.Data, @"\b(\d{18,22})\b");
                if (match.Success)
                {
                    string ccid = NormalizeCcid(match.Groups[1].Value);

                    // Chống trùng lặp: CNUM thường rỗng trên SIM mới, vì vậy chỉ cần
                    // cùng CCID và đã Active là đủ để bỏ qua event CCID lặp.
                    if (string.Equals(NormalizeCcid(port.Serial), ccid, StringComparison.OrdinalIgnoreCase)
                        && (port.Status == SimStatus.Active
                            || port.Status == SimStatus.WaitingAccept
                            || port.Status == SimStatus.SecurityBlocked))
                    {
                        return;
                    }

                    if (!TryBeginPortInitialization(e.PortName, out Guid initializationLease))
                    {
                        return; // Đang chạy khởi tạo rồi, bỏ qua
                    }

                    // Set Connecting (không phải Active) khi nhận CCID – chưa verify IMEI
                    port.Status = SimStatus.Connecting;
                    if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug)." || string.IsNullOrWhiteSpace(port.DeviceName))
                    {
                        port.DeviceName = "Đã nhận SIM, đang kiểm tra IMEI...";
                    }

                    port.Serial = ccid;
                    if (_simCache.TryGetValue(ccid, out var cachedPhone))
                    {
                        AddLog($"[{e.PortName}] Tìm thấy SĐT trong cache: {cachedPhone} (chờ đăng ký mạng)", "SUCCESS");
                    }

                    if (_imeiCache.TryGetValue(ccid, out var entry) && entry != null)
                    {
                        ApplyBackupMetadata(port, entry);
                    }

                    AddLog($"[{e.PortName}] [IMEI_MODE] Restore={AppSettings.EnableImeiRestore} BlockNew={AppSettings.BlockUnknownSims}");
                    var session = StartSimSession(e.PortName, ccid);
                    bool autoAccept = AppSettings.AutoAccept;
                    _ = Task.Run(() => ProcessCurrentSimSessionAsync(
                        port, ccid, forceAccept: autoAccept, session.Epoch, session.Token, initializationLease));
                }
                else
                {
                    AddLog($"[{e.PortName}] Chưa đọc được CCID hợp lệ; giữ radio tắt và tiếp tục chờ SIM.", "WARN");
                    InvalidateSimSession(e.PortName);
                    ClearSimScopedState(port);
                    port.Status = "Chờ cắm SIM";
                    port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                    port.LastError = SecurityErrors.ReadCcidFailed;
                    _modemService.StartHotplugWaitLoop(e.PortName);
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
                                UpdateImeiCacheEntry(port.Serial, value => value.PhoneNumber = rawNumber);
                            }
                        }
                    }
                }
            }
            else if (e.Data == "[STATUS_NO_RESPONSE]")
            {
                port.Status = SimStatus.NoResponse;
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_WAITING_ACCEPT]"))
            {
                // Trạng thái riêng biệt: SIM mới đang CHỜ user chấp nhận thủ công
                // KHÔNG dùng SecurityBlocked để tránh nhầm lẫn với SIM bị chặn bảo mật thực sự
                port.Status = SimStatus.WaitingAccept;
                port.LastError = "SIM mới – chờ user chấp nhận";
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_HOTPLUG_SIM_DETECTED]"))
            {
                // [PARSE_CCID] là nguồn duy nhất khởi chạy state machine IMEI.
                // Event này chỉ cập nhật UI, tránh chạy ProcessImeiAsync lần thứ hai.
                if (port.Status != SimStatus.WaitingAccept && port.Status != SimStatus.Active)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang cấu hình SIM mới...";
                    UpdateDashboard();
                }
            }
        });
    }

    private void MarkPortActiveAfterInit(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        _portRecoveryAttempts.TryRemove(portName, out _);
        _quarantinedPorts.TryRemove(portName, out _);

        // Không phát lệnh AT tại đây. CompletePortInitializationAsync đã cấu hình,
        // xác minh hai lần và bật radio trước khi gọi hàm cập nhật UI này.

        port.Status = SimStatus.Active;
        port.TimeoutCount = 0;
        port.SmsErrorCount = 0;
        port.ReconnectCount = 0;
        port.LastError = string.Empty;
        
        // Cập nhật tên thiết bị thực tế dựa trên IMEI
        // Liệt kê đầy đủ mọi chuỗi trạng thái tạm thời có thể được set trước đó
        if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug)."
            || port.DeviceName == "Đã nhận SIM, đang khởi tạo..."
            || port.DeviceName == "Đã nhận SIM, đang kiểm tra IMEI..."
            || port.DeviceName == "Đang xử lý chấp nhận..."
            || port.DeviceName == "Đang cấu hình SIM mới..."
            || port.DeviceName == "SIM mới – bấm ACCEPT để kích hoạt"
            || port.DeviceName == "Đang tráng IMEI Fake..."
            || string.IsNullOrWhiteSpace(port.DeviceName))
        {
            port.DeviceName = Services.ImeiManagementService.GetDeviceNameFromImei(port.Imei);
        }

        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        UpdateDashboard();

        foreach (var sms in SmsMessages.Where(s => s.PortName == portName))
        {
            sms.Status = SimStatus.Active;
        }

        _ = gsm.Services.FirebaseService.ClearWebStateAsync(portName);
    }

    // ---------------------------------------------------------------------
    // THAO TÁC ACCEPT SIM MỚI TỪ UI
    // ---------------------------------------------------------------------
    public async Task<bool> PaintImeiForCurrentSimAsync(string portName, string targetImei)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        string ccid = NormalizeCcid(port?.Serial);
        string target = NormalizeImei(targetImei);
        if (port == null || string.IsNullOrWhiteSpace(ccid) || target.Length != 15) return false;
        if (!TryBeginPortInitialization(portName, out Guid initializationLease)) return false;

        (long Epoch, CancellationToken Token) session;
        if (_portSessions.TryGet(portName, out var existingSession)
            && string.Equals(existingSession.Ccid, ccid, StringComparison.OrdinalIgnoreCase))
            session = (existingSession.Epoch, existingSession.Token);
        else
            session = StartSimSession(portName, ccid);

        try
        {
            string liveCcid = await ReadLiveCcidAsync(portName, session.Token);
            if (!string.Equals(liveCcid, ccid, StringComparison.OrdinalIgnoreCase)
                || !IsSimSessionCurrent(portName, ccid, session.Epoch))
                return false;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang tráng IMEI...";
                UpdateDashboard();
            });

            await ProcessCurrentSimSessionAsync(
                port, ccid, forceAccept: true, session.Epoch, session.Token,
                initializationLease, explicitTargetImei: target);

            return IsSimSessionCurrent(portName, ccid, session.Epoch)
                && port.Status == SimStatus.Active
                && Services.ImeiManagementService.AreEquivalentImei(port.Imei, target);
        }
        finally
        {
            EndPortInitialization(portName, initializationLease);
        }
    }

    public async Task AcceptNewSimAsync(string portName)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null || string.IsNullOrEmpty(port.Serial)) return;

        string ccid = NormalizeCcid(port.Serial);
        if (port.Status != SimStatus.WaitingAccept && port.Status != SimStatus.SecurityBlocked)
        {
            AddLog($"[{portName}] Bỏ qua ACCEPT vì cổng không còn ở trạng thái chờ chấp nhận.", "WARN");
            return;
        }

        if (!TryBeginPortInitialization(portName, out Guid initializationLease))
        {
            AddLog($"[{portName}] Cổng đang có một tác vụ SIM khác; không chạy ACCEPT song song.", "WARN");
            return;
        }

        (long Epoch, CancellationToken Token) session;
        if (_portSessions.TryGet(portName, out var existingSession)
            && string.Equals(existingSession.Ccid, ccid, StringComparison.OrdinalIgnoreCase))
        {
            session = (existingSession.Epoch, existingSession.Token);
        }
        else
        {
            session = StartSimSession(portName, ccid);
        }

        try
        {
            // Xác minh lại CCID vật lý ngay tại thời điểm người dùng bấm ACCEPT.
            string liveCcid = await ReadLiveCcidAsync(portName, session.Token);
            if (!string.Equals(liveCcid, ccid, StringComparison.OrdinalIgnoreCase)
                || !IsSimSessionCurrent(portName, ccid, session.Epoch))
            {
                AddLog($"[{portName}] Hủy ACCEPT: SIM đã bị rút/thay đổi (expected={ccid}, actual={liveCcid}).", "ERROR");
                await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    port.Status = SimStatus.SecurityBlocked;
                    port.LastError = string.IsNullOrWhiteSpace(liveCcid)
                        ? "Không đọc được CCID khi radio tắt"
                        : "SIM đã thay đổi trong lúc Accept";
                    port.DeviceName = "Accept bị chặn – chưa xác minh được CCID";
                    UpdateDashboard();
                });
                return;
            }

            AddLog($"[{portName}] Bắt đầu chấp nhận SIM đã xác minh (CCID: {ccid})...");
            Application.Current.Dispatcher.Invoke(() =>
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang xử lý chấp nhận...";
                UpdateDashboard();
            });

            await ProcessCurrentSimSessionAsync(
                port, ccid, forceAccept: true, session.Epoch, session.Token, initializationLease);
        }
        finally
        {
            EndPortInitialization(portName, initializationLease);
        }
    }

    public async Task AcceptSelectedAsync(IEnumerable<string> portNames)
    {
        var tasks = portNames.Distinct().Select(async p =>
        {
            try
            {
                await AcceptNewSimAsync(p);
            }
            catch (Exception ex)
            {
                AddLog($"[{p}] Lỗi khi accept hàng loạt: {ex.Message}", "ERROR");
            }
        });
        await Task.WhenAll(tasks);
    }

    private void ModemService_PortDisconnected(object? sender, GsmDataEventArgs e)
    {
        InvalidateSimSession(e.PortName);
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

        // Process the decoded SMS synchronously on the UI dispatcher. The modem
        // service deletes the recyclable SIM index only after this handler has
        // returned, so the UI has taken ownership before CMGD is issued.
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                string senderPhone = "UNKNOWN";
                string extractedOtp = "N/A";
                string cleanContent = TextEncodingNormalizer.RepairMojibake(e.Data);

                // Nếu quá trình đọc tin nhắn gặp lỗi (VD: Lỗi Timeout Semaphore do đang kẹt gửi SMS)
                if (cleanContent.StartsWith("ERROR:"))
                {
                    AddLog($"[{e.PortName}] LỖI đọc tin nhắn: {cleanContent}. Đang bỏ qua và không xóa để tránh mất OTP.", "WARN");
                    return;
                }

                // 1. Lấy thông tin từ sự kiện (Đã được xử lý 100% bên GsmModemService)
                if (!string.IsNullOrEmpty(e.Sender))
                {
                    senderPhone = e.Sender;
                    extractedOtp = string.IsNullOrEmpty(e.Otp) ? "N/A" : e.Otp;
                    // cleanContent đã là nội dung text sạch (fullContent)
                }
                else
                {
                    // Fallback an toàn (nếu có message nào chưa qua xử lý)
                    var pduMatch = Regex.Match(e.Data, @"\+CMGR:\s*\d+,,(\d+)\r?\n([0-9A-Fa-f]+)");
                    var senderMatch = Regex.Match(e.Data, @"\+(?:CMGR|CMGL):\s*""[^""]*"",""([^""]+)""");
                    if (pduMatch.Success)
                    {
                        string pduHex = pduMatch.Groups[2].Value.Trim();
                        cleanContent = DecodePdu(pduHex, out senderPhone, out int _, out int _, out int _);
                    }
                    else if (senderMatch.Success)
                    {
                        senderPhone = DecodeUcs2(senderMatch.Groups[1].Value);
                        cleanContent = Regex.Replace(e.Data, @"\+(?:CMGR|CMGL):.*?\r\n", "").Trim();
                        cleanContent = Regex.Replace(cleanContent, @"\r?\nOK\r?\n?$", "").Trim();
                        cleanContent = DecodeUcs2(cleanContent);
                    }
                    cleanContent = cleanContent.Replace("\r", " ").Replace("\n", " ").Trim();
                    cleanContent = Regex.Replace(cleanContent, @"\s+", " ");
                    extractedOtp = gsm.Services.GsmModemService.ExtractOtp(cleanContent) ?? "N/A";
                }

                if (port != null)
                {
                    bool simMetadataChanged = false;

                    // 1. Parse SĐT từ SMS (nếu có)
                    var phoneMatch = Regex.Match(cleanContent, @"(?:thuê bao|thue bao|so tb|số tb|msisdn|sim)[^\d]{0,15}(0\d{9,10}|84\d{9,10})", RegexOptions.IgnoreCase);
                    if (phoneMatch.Success)
                    {
                        string foundNumber = phoneMatch.Groups[1].Value;
                        if (foundNumber.StartsWith("84")) foundNumber = "0" + foundNumber.Substring(2);
                        else if (!foundNumber.StartsWith("0")) foundNumber = "0" + foundNumber;

                        if (string.IsNullOrEmpty(port.PhoneNumber) || port.PhoneNumber != foundNumber)
                        {
                            port.PhoneNumber = foundNumber;
                            UpdateSmsReceiverPhone(port.PortName, foundNumber);
                            AddLog($"[{e.PortName}] Đã cập nhật SĐT từ SMS: {foundNumber}", "SUCCESS");
                            simMetadataChanged = true;
                            if (!string.IsNullOrWhiteSpace(port.Serial))
                            {
                                _simCache[NormalizeCcid(port.Serial)] = foundNumber;
                                SaveSimCache();
                            }
                        }
                    }

                    // 2. Parse TKC từ SMS (nếu có)
                    // Guard: bỏ qua SMS chứa từ khóa quảng cáo/cước để tránh ghi đè TKC thật bằng số nhỏ (1đ, 900đ)
                    bool smsHasAdKeywords = Regex.IsMatch(cleanContent,
                        @"cuoc|phi\s*dich\s*vu|uu\s*dai|goi\s*cuoc|tang\s*them|khuyen\s*mai|phi\s*truoc|phi\s*cuoc|khong\s*du|chua\s*du",
                        RegexOptions.IgnoreCase);
                    if (!smsHasAdKeywords)
                    {
                        var strictMatch = Regex.Match(cleanContent, @"(?:TK\s*goc|TKG|TK\s*chinh|TKC|Tai khoan chinh|Tài khoản chính|Tai khoan|Tài khoản|So du|Số dư|TK|balance)[^\d]{0,20}(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)?", RegexOptions.IgnoreCase);
                        if (strictMatch.Success)
                        {
                            string rawSmsVal = strictMatch.Groups[1].Value.Replace(".", "").Replace(",", "");
                            // Reject số dư < 100 VND để tránh parse nhầm cước phí từ SMS
                            if (int.TryParse(rawSmsVal, out int parsedSmsBalance) && parsedSmsBalance >= 100)
                            {
                                string bal = strictMatch.Groups[1].Value + "đ";
                                if (port.Balance != bal)
                                {
                                    port.Balance = bal;
                                    AddLog($"[{e.PortName}] Đã cập nhật số dư từ SMS: {bal}", "SUCCESS");
                                    simMetadataChanged = true;
                                }
                            }
                        }
                    }

                    // Refresh/reconnect xóa dữ liệu tạm trên UI. Ghi snapshot ngay theo
                    // CCID để SĐT/TKC vừa đọc từ SMS được phục hồi ở phiên kế tiếp.
                    if (simMetadataChanged && !string.IsNullOrWhiteSpace(port.Serial))
                        UpdateImeiCacheEntry(port.Serial, _ => { });
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
                    _ = _firebaseService.MarkPendingCommandFailedAsync(
                        e.PortName, "⚠️ Chọn sai đầu số rồi kìa", cleanContent);
                    isZaloError = true;
                }
                else if (cleanContentLower.Contains("khong thuc hien yeu cau") || cleanContentLower.Contains("không thực hiện yêu cầu"))
                {
                    AddLog($"[{e.PortName}] LỖI ZALO: SĐT đang không có yêu cầu mã xác thực Zalo.", "ERROR");
                    _ = _firebaseService.MarkPendingCommandFailedAsync(
                        e.PortName, "⚠️ SĐT đang không yêu cầu mã", cleanContent);
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
                            _ = _firebaseService.MarkPendingCommandFailedAsync(
                                e.PortName,
                                $"Nhà mạng từ chối tiện ích vì không đủ tiền; TKC đang hiển thị {port.Balance}",
                                cleanContent);
                            isZaloError = true;
                        }
                        else
                        {
                            AddLog($"[{e.PortName}] LỖI SIM: Tài khoản không đủ tiền để gửi SMS! Số dư: {port.Balance}", "ERROR");
                            _ = _firebaseService.MarkPendingCommandFailedAsync(
                                e.PortName, "⚠️ Hết tiền", cleanContent);
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
                        _ = _firebaseService.MarkPendingCommandFailedAsync(
                            e.PortName, "⚠️ Nhà mạng báo không đủ tiền", cleanContent);
                        isZaloError = true;
                    }
                }

                if (isZaloError)
                {
                    // Keep processing and record the carrier/error SMS. Receiving
                    // all SMS means business-rule errors must not disappear from
                    // the inbox. GsmModemService exclusively owns CMGD.
                }

                // Tự động xác nhận đăng ký ezCom từ tổng đài (Hỗ trợ cả Y [mã] và EZ [mã])
                var ezMatch = Regex.Match(cleanContentLower, @"soan\s+(?:tin\s+)?((?:ez|y)\s*[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
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
                    
                    // The automatic reply is independent from inbox delivery;
                    // continue below so the original SMS is still recorded.
                }

                // LUÔN CHẶN cảnh báo ezCom bất kể cài đặt Nhận tất cả hay không
                if (cleanContentLower.Contains("thue bao ezcom chi duoc") || cleanContentLower.Contains("dich vu vinaphone khac"))
                {
                    AddLog($"[{e.PortName}] Đã chặn tin nhắn hệ thống ezCom.");
                    // Continue to the common receive path; never hide or delete
                    // a real network SMS because of its content.
                }

                // Every decoded network SMS now continues through the common
                // receive path. Whitelist/blacklist settings may control optional
                // forwarding elsewhere, but must never suppress the local inbox.

                // 2. Tìm OTP
                extractedOtp = ExtractOtp(cleanContent);

                // 3. Tìm cổng tương ứng để lấy thông tin SIM (SĐT, Nhà mạng)
                string receiverPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

                // ---------- LOAD SETTINGS ----------
                // Inbox delivery must not depend on settings being loaded. Use
                // safe defaults for optional notifications while retaining the
                // decoded SMS locally.
                var cfg = gsm.Services.SettingsService.Current ?? new AppSettings();

                // ---------- 0. FIREBASE (toolweb) ----------
                // A pending web SMS command must always receive its correlated OTP result.
                // WriteOtpToFirebase only controls the general port snapshot, not command replies.
                if (port != null && extractedOtp != "N/A")
                {
                    _ = _firebaseService.PublishOtpForPendingCommandAsync(
                        port.PortName, extractedOtp, cleanContent, senderPhone);
                }

                if (cfg.WriteOtpToFirebase && port != null)
                {
                    string machineId = FirebaseService.MachineId;
                    
                    if (extractedOtp != "N/A")
                    {
                        _ = _firebaseOtpService.WritePortOtpAsync(machineId, port.PortName, extractedOtp, cleanContent, senderPhone);
                    }

                    var dto = new gsm.Models.ApiPortDto
                    {
                        PortId = port.PortName,
                        PortName = port.PortName,
                        Status = port.Status.ToString(),
                        Phone = port.PhoneNumber,
                        Otp = extractedOtp != "N/A" ? extractedOtp : port.Otp,
                        LastContent = cleanContent,
                        UpdatedAt = DateTime.Now.ToString("HH:mm:ss")
                    };
                    _ = _firebaseOtpService.WritePortSnapshotAsync(machineId, dto);
                }

                // ---------- 1. TELEGRAM ----------
                bool hasToken = !string.IsNullOrWhiteSpace(cfg.TelegramBotToken) &&
                                !string.IsNullOrWhiteSpace(cfg.TelegramChatId);

                if (hasToken)
                {
                    // OTP
                    // Telegram: gửi OTP nếu TelegramOnOtp bật
                    if (cfg.TelegramOnOtp && extractedOtp != "N/A")
                    {
                        var text =
                            $"🔐 OTP mới\n" +
                            $"Port: {e.PortName}\n" +
                            $"SĐT: {receiverPhone}\n" +
                            $"Từ: {System.Net.WebUtility.HtmlEncode(senderPhone)}\n" +
                            $"OTP: <b>{extractedOtp}</b>\n" +
                            $"Nội dung: {System.Net.WebUtility.HtmlEncode(TrimStr(cleanContent, 200))}\n" +
                            $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                        _ = _notifyService.SendTelegramAsync(cfg.TelegramBotToken, cfg.TelegramChatId, text);
                    }
                    // Full SMS (kể cả không OTP)
                    // Telegram: gửi SMS thường nếu TelegramOnSms bật (không phụ thuộc receiveAll)
                    else if (cfg.TelegramOnSms)
                    {
                        var text =
                            $"📩 SMS mới\n" +
                            $"Port: {e.PortName}\n" +
                            $"SĐT: {receiverPhone}\n" +
                            $"Từ: {System.Net.WebUtility.HtmlEncode(senderPhone)}\n" +
                            $"Nội dung: {System.Net.WebUtility.HtmlEncode(TrimStr(cleanContent, 500))}\n" +
                            $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                        _ = _notifyService.SendTelegramAsync(cfg.TelegramBotToken, cfg.TelegramChatId, text);
                    }
                }

                // ---------- 2. WEBHOOK / TOOLWEB ----------
                // PushOtpToWeb = true  → chỉ đẩy khi có OTP
                // PushOtpToWeb = false → đẩy tất cả SMS (cả không OTP) nếu URL đã cấu hình
                if (!string.IsNullOrWhiteSpace(cfg.OtpWebhookUrl))
                {
                    bool shouldPush = cfg.PushOtpToWeb
                        ? extractedOtp != "N/A"          // OTP-only mode: chỉ khi có OTP
                        : !string.IsNullOrWhiteSpace(cleanContent); // All-SMS mode: khi có nội dung

                    if (shouldPush)
                    {
                        var payload = new
                        {
                            event_type = extractedOtp == "N/A" ? "sms" : "otp",
                            port = e.PortName,
                            phone = receiverPhone,
                            sender = senderPhone,
                            otp = extractedOtp == "N/A" ? "" : extractedOtp,
                            content = cleanContent,
                            imei = port?.Imei ?? "",
                            ccid = port?.Serial ?? "",
                            time = DateTime.Now.ToString("o"),
                            timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()
                        };

                        _ = _notifyService.PushWebhookAsync(cfg.OtpWebhookUrl, payload);
                    }
                }

                if (extractedOtp != "N/A")
                    OtpReceivedEvent?.Invoke(e.PortName, extractedOtp);

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
                    // SMS thường không có OTP không được phép ghi "N/A" đè mã
                    // đã nhận trước đó trên COM.
                    if (extractedOtp != "N/A")
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

                    // OTP MyVNPT chỉ được ghép với đúng tác vụ COM + phiên SIM + SĐT.
                    // Không xóa pending tại đây: task gốc chỉ hoàn tất sau khi API đặt pass trả kết quả.
                    if (MyVnptService.IsMyVnptOtpMessage(cleanContent))
                    {
                        if (_pendingMyVnptPasswordPorts.TryGetValue(e.PortName, out var pending)
                            && IsSimSessionCurrent(e.PortName, pending.Ccid, pending.Epoch)
                            && string.Equals(
                                MyVnptService.NormalizePhone(receiverPhone),
                                pending.ApiSession.Phone,
                                StringComparison.Ordinal)
                            && pending.TryClaimOtp(extractedOtp))
                        {
                            AddLog($"[{e.PortName}] Phát hiện OTP MyVNPT, tiến hành đổi mật khẩu...", "INFO");
                            _ = CompletePendingMyVnptPasswordAsync(pending, extractedOtp);
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

                    // GsmModemService deletes the SIM record after this handler returns.
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

                    // GsmModemService owns CMGD for ordinary SMS as well.
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
    private static readonly TimeSpan MultipartSmsBufferTimeout = TimeSpan.FromMinutes(10);

    // Debounce timer cho việc gửi Telegram khi SMS đa phần được nối — key = "port|sender"
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.CancellationTokenSource> _multipartTelegramDebounce = new();

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
            // Trả OTP về Web Firebase (cho multipart SMS đã được gộm và timeout)
            if (port != null)
                _ = _firebaseService.PublishOtpForPendingCommandAsync(
                    port.PortName, extractedOtp, content, senderPhone);
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

            // Trả OTP về Web Firebase (multipart SMS đã gộm đủ)
            if (port != null)
                _ = _firebaseService.PublishOtpForPendingCommandAsync(
                    port.PortName, newOtp, existing.Content, senderPhone);
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
                // Dùng _notifyService (với token đã lưu trong Settings) thay vì TelegramService static cũ
                var cfg2 = SettingsService.Current;
                bool hasTgToken = !string.IsNullOrWhiteSpace(cfg2.TelegramBotToken) &&
                                  !string.IsNullOrWhiteSpace(cfg2.TelegramChatId);

                if (hasTgToken && cfg2.TelegramOnOtp)
                {
                    string tgText =
                        $"🔐 <b>OTP MỚI (ghép SMS)</b>\n" +
                        $"Port: {portName}\n" +
                        $"SĐT: {simPhone}\n" +
                        $"Từ: {System.Net.WebUtility.HtmlEncode(senderPhone)}\n" +
                        $"OTP: <b>{newOtp}</b>\n" +
                        $"Nội dung: {System.Net.WebUtility.HtmlEncode(TrimStr(existing.Content, 300))}\n" +
                        $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                    await _notifyService.SendTelegramAsync(cfg2.TelegramBotToken, cfg2.TelegramChatId, tgText);
                }

                // Webhook rules
                await Task.WhenAll(cfg2.WebhookRules
                    .Where(r => r.Enabled)
                    .Select(rule => WebhookService.TriggerAsync(
                        rule, portName, simPhone, senderPhone, newOtp, existing.Content)));

                // Global webhook URL (PushOtpToWeb)
                if (!string.IsNullOrWhiteSpace(cfg2.OtpWebhookUrl))
                {
                    bool shouldPush2 = cfg2.PushOtpToWeb ? (newOtp != "N/A") : true;
                    if (shouldPush2)
                    {
                        var wPayload = new
                        {
                            event_type = "otp",
                            port = portName,
                            phone = simPhone,
                            sender = senderPhone,
                            otp = newOtp,
                            content = existing.Content,
                            time = DateTime.Now.ToString("o"),
                            timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()
                        };
                        await _notifyService.PushWebhookAsync(cfg2.OtpWebhookUrl, wPayload);
                    }
                }
            });
        }
        else if (receiveAll)
        {
            // Debounce: chờ 3 giây sau đoạn cuối cùng rồi mới gửi Telegram 1 lần duy nhất
            string debounceKey = $"{portName}|{senderPhone}";
            string simPhone = !string.IsNullOrWhiteSpace(port?.PhoneNumber) ? port.PhoneNumber : "Chưa lấy được số";

            // Hủy timer cũ (nếu được đặt từ đoạn trước)
            if (_multipartTelegramDebounce.TryRemove(debounceKey, out var oldCts))
                oldCts.Cancel();

            var cts = new System.Threading.CancellationTokenSource();
            _multipartTelegramDebounce[debounceKey] = cts;
            string capturedContent = existing.Content;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Chờ 3 giây — nếu có đoạn mới đến, timer này sẽ bị hủy
                    await Task.Delay(3000, cts.Token);
                    _multipartTelegramDebounce.TryRemove(debounceKey, out _);

                    var cfg2 = SettingsService.Current;
                    bool hasTgToken = !string.IsNullOrWhiteSpace(cfg2.TelegramBotToken) &&
                                      !string.IsNullOrWhiteSpace(cfg2.TelegramChatId);

                    if (hasTgToken && cfg2.TelegramOnSms)
                    {
                        string safeContent = System.Net.WebUtility.HtmlEncode(TrimStr(capturedContent, 500));
                        string safeSender = System.Net.WebUtility.HtmlEncode(senderPhone);
                        string tgText =
                            $"📩 <b>Tin nhắn ghép từ {portName}</b>\n" +
                            $"SĐT: {simPhone}\n" +
                            $"Từ: {safeSender}\n" +
                            $"Nội dung: <i>{safeContent}</i>\n" +
                            $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                        await _notifyService.SendTelegramAsync(cfg2.TelegramBotToken, cfg2.TelegramChatId, tgText);
                    }
                }
                catch (TaskCanceledException) { } // Bị hủy vì có đoạn mới đến — bình thường
            }, cts.Token);
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
        // Dùng chung một implementation thống nhất từ GsmModemService
        return gsm.Services.GsmModemService.ExtractOtp(content) ?? "N/A";
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
            // Thông báo Telegram cuộc gọi đến (kiểm tra TelegramOnCall trước khi gửi)
            var clipCfg = SettingsService.Current;
            if (clipCfg != null &&
                !string.IsNullOrWhiteSpace(clipCfg.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(clipCfg.TelegramChatId) &&
                clipCfg.TelegramOnCall)
            {
                string callText =
                    $"📞 <b>Cuộc gọi đến [{e.PortName}]</b>\n" +
                    $"📱 SIM nhận: {receiverPhone}\n" +
                    $"☎️ Người gọi: <code>{safeCallerHtml}</code>\n" +
                    $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                _ = _notifyService.SendTelegramAsync(clipCfg.TelegramBotToken, clipCfg.TelegramChatId, callText);
            }

            // Khôi phục luồng nhánh dev: +CLIP báo lên UI trước, sau đó chính handler
            // này gửi ATA và bắt đầu ghi âm trên đúng COM nhận cuộc gọi.
            if (IsAutoAnswerEnabled)
            {
                if (!_activeRamRecordings.ContainsKey(e.PortName))
                {
                    AddLog($"[{e.PortName}] Đang tự động bắt máy cuộc gọi đến...", "INFO");
                    string answer = await _modemService.SendCommandAsync(e.PortName, "ATA", 8000);
                    if (answer.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        AddLog($"[{e.PortName}] ATA lỗi: {answer.Trim()}", "ERROR");
                        return;
                    }

                    await Task.Delay(1500);
                    if (_modemService.GetModemProfile(e.PortName)?.Supports(ModemCapability.AudioRecord) == true)
                    {
                        AddLog($"[{e.PortName}] Bắt đầu thu âm vào RAM của mạch Quectel...", "INFO");
                        string recordResult = await _modemService.SendCommandAsync(
                            e.PortName, "AT+QAUDRD=1,\"call.wav\",13,0", 5000);
                        if (!recordResult.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                            _activeRamRecordings[e.PortName] = true;
                        else
                            AddLog($"[{e.PortName}] Không thể bắt đầu ghi âm: {recordResult.Trim()}", "WARN");
                    }
                }
            }
            else
            {
                AddLog($"[{e.PortName}] Có cuộc gọi đến nhưng tính năng Tự động bắt máy đang TẮT.", "INFO");
            }
        });
    }

    private async Task MonitorAndPlayAudioDuringCallAsync(string portName, int durationSeconds, string? customWavPath = null)
    {
        bool audioPlayed = false;
        string wavPath = customWavPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(wavPath))
            wavPath = AppSettings?.SoundCallOutPath ?? "";

        string fileName = System.IO.Path.GetFileName(wavPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "otp.wav";

        for (int i = 0; i < durationSeconds * 2; i++)
        {
            await Task.Delay(500);

            // Nếu cuộc gọi đã bị dập hoặc lỗi
            if (_callFailures.TryGetValue(portName, out string? failReason))
            {
                break;
            }

            if (!audioPlayed && File.Exists(wavPath))
            {
                // Kiểm tra trạng thái cuộc gọi
                string clcc = await _modemService.SendCommandAsync(portName, "AT+CLCC", 2000, silent: true);
                if (clcc.Contains("+CLCC:"))
                {
                    var lines = clcc.Split(new[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("+CLCC:"))
                        {
                            var parts = line.Replace("+CLCC:", "").Trim().Split(',');
                            if (parts.Length > 2)
                            {
                                string callState = parts[2].Trim();
                                if (callState == "0") // 0 = Active (Người nghe nhấc máy)
                                {
                                    audioPlayed = true;
                                    Application.Current.Dispatcher.Invoke(() => 
                                    {
                                        AddLog($"[{portName}] Đối phương đã nhấc máy! Chuẩn bị phát âm thanh...", "SUCCESS");
                                    });

                                    // Upload file lên modem nếu chưa có
                                    string flst = await _modemService.SendCommandAsync(portName, "AT+QFLST", 3000, silent: true);
                                    
                                    bool exists = false;
                                    if (flst.Contains(fileName))
                                    {
                                        exists = true;
                                    }

                                    if (!exists)
                                    {
                                        Application.Current.Dispatcher.Invoke(() => 
                                        {
                                            AddLog($"[{portName}] Đang tải file âm thanh lên modem (lần đầu)...", "INFO");
                                        });
                                        bool uploadOk = await _modemService.UploadFileToModemAsync(portName, wavPath, fileName);
                                        if (uploadOk)
                                        {
                                            Application.Current.Dispatcher.Invoke(() => 
                                            {
                                                AddLog($"[{portName}] Tải file {fileName} lên modem thành công.", "SUCCESS");
                                            });
                                            exists = true;
                                        }
                                        else
                                        {
                                            Application.Current.Dispatcher.Invoke(() => 
                                            {
                                                AddLog($"[{portName}] Lỗi khi tải file {fileName} lên modem.", "ERROR");
                                            });
                                        }
                                    }

                                    if (exists)
                                    {
                                        Application.Current.Dispatcher.Invoke(() => 
                                        {
                                            AddLog($"[{portName}] Đang phát file âm thanh cuộc gọi...", "INFO");
                                        });
                                        // Phát file âm thanh cuộc gọi thông qua AT+QPSND
                                        await _modemService.SendCommandAsync(portName, $"AT+QPSND=1,\"ufs:{fileName}\",0", 5000);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void ModemService_DtmfReceived(object? sender, GsmDataEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";
            
            AddLog($"[{e.PortName}] Nhận được phím DTMF: {e.Data}", "INFO");
            
            if (port != null)
            {
                port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                port.LastMessageContent = $"[DTMF] Phím: {e.Data}";
                UpdateDashboard();
            }

            // Gửi thông báo Telegram qua _notifyService (dùng token đã lưu trong Settings)
            var dtmfCfg = SettingsService.Current;
            if (dtmfCfg != null &&
                !string.IsNullOrWhiteSpace(dtmfCfg.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(dtmfCfg.TelegramChatId) &&
                dtmfCfg.TelegramOnCall)
            {
                string dtmfText =
                    $"🎹 <b>Phím DTMF [{e.PortName}]</b>\n" +
                    $"📱 SIM nhận: {receiverPhone}\n" +
                    $"Pressed: <b>{e.Data}</b>\n" +
                    $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                _ = _notifyService.SendTelegramAsync(dtmfCfg.TelegramBotToken, dtmfCfg.TelegramChatId, dtmfText);
            }
        });
    }

    private void ModemService_IncomingCallRinging(object? sender, gsm.Models.IncomingCallSession session)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == session.Port);
            if (port != null)
            {
                port.LastCallResult = $"Ringing: {session.Caller}";
                port.UpdateDisplayResult("Call");
                AddLog($"[{session.Port}] Đang đổ chuông từ {session.Caller}", "INFO");
            }
        });
    }

    private void ModemService_IncomingCallAnswered(object? sender, gsm.Models.IncomingCallSession session)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == session.Port);
            if (port != null)
            {
                port.LastCallResult = $"Answered: {session.Caller}";
                port.UpdateDisplayResult("Call");
                AddLog($"[{session.Port}] Đã bắt máy cuộc gọi từ {session.Caller}", "INFO");
            }
        });
    }

    private void ModemService_IncomingCallEnded(object? sender, gsm.Models.IncomingCallSession session)
    {
        Application.Current.Dispatcher.Invoke(async () =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == session.Port);
            if (port != null)
            {
                port.LastCallResult = $"Ended: {session.Caller}";
                port.UpdateDisplayResult("Call");
                
                if (!string.IsNullOrEmpty(session.Otp))
                {
                    port.Otp = session.Otp;
                    port.LastMessageContent = session.Transcript ?? "";
                    AddLog($"[{session.Port}] Lấy được OTP từ cuộc gọi: {session.Otp}", "SUCCESS");
                }
            }

            // Tự động cập nhật TKC sau khi kết thúc cuộc gọi đến
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckBalanceForPortAsync(session.Port);
            });

            await NotifyFromIncomingCallAsync(session, port);
        });
    }

    private async Task NotifyFromIncomingCallAsync(gsm.Models.IncomingCallSession session, gsm.Models.SimPort? port)
    {
        var cfg = gsm.Services.SettingsService.Current;
        if (cfg == null) return;

        string otp = session.Otp ?? "";
        string content = session.Transcript ?? "";
        string portName = session.Port;
        string caller = session.Caller;

        if (_notifyService != null)
        {
            // Telegram
            if (!string.IsNullOrWhiteSpace(cfg.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(cfg.TelegramChatId))
            {
                if (cfg.TelegramOnOtp && !string.IsNullOrEmpty(otp))
                {
                    var text =
                        $"📞 OTP từ cuộc gọi đến\n" +
                        $"Port: {portName}\n" +
                        $"Gọi từ: {caller}\n" +
                        $"OTP: <b>{otp}</b>\n" +
                        $"STT: {TrimStr(content, 300)}\n" +
                        $"File: {Path.GetFileName(session.LocalWavPath)}\n" +
                        $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                    await _notifyService.SendTelegramAsync(cfg.TelegramBotToken, cfg.TelegramChatId, text);
                }
                // Cuộc gọi đến (không có OTP)
                else if (cfg.TelegramOnCall)
                {
                    var text =
                        $"📞 Cuộc gọi đến\n" +
                        $"Port: {portName}\n" +
                        $"Từ: {caller}\n" +
                        $"STT: {TrimStr(content, 400)}\n" +
                        $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                    await _notifyService.SendTelegramAsync(cfg.TelegramBotToken, cfg.TelegramChatId, text);
                }
            }

            // Webhook / toolweb
            if (cfg.PushOtpToWeb && !string.IsNullOrWhiteSpace(cfg.OtpWebhookUrl))
            {
                var payload = new
                {
                    event_type = string.IsNullOrEmpty(otp) ? "incoming_call" : "otp_call",
                    port = portName,
                    phone = port?.PhoneNumber ?? "", 
                    sender = caller,
                    otp = otp,
                    content = content,
                    wav = session.LocalWavPath,
                    imei = port?.Imei ?? "",
                    ccid = port?.Serial ?? "",
                    time = DateTime.Now.ToString("o"),
                    timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()
                };
                await _notifyService.PushWebhookAsync(cfg.OtpWebhookUrl, payload);
            }
        }
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
            // Thông báo Telegram khi cuộc gọi kết thúc (check TelegramOnCall)
            var callEndCfg = SettingsService.Current;
            if (callEndCfg != null &&
                !string.IsNullOrWhiteSpace(callEndCfg.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(callEndCfg.TelegramChatId) &&
                callEndCfg.TelegramOnCall)
            {
                string endText =
                    $"🎙 <b>Cuộc gọi kết thúc [{e.PortName}]</b>\n" +
                    $"📱 SIM nhận: {receiverPhone}\n" +
                    $"☎️ Người gọi: <code>{safeCallerHtml}</code>\n" +
                    $"📝 Nội dung: <i>{safeContent}</i>\n" +
                    $"💾 File: <code>{safeFileName}</code>\n" +
                    $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                _ = _notifyService.SendTelegramAsync(callEndCfg.TelegramBotToken, callEndCfg.TelegramChatId, endText);
            }

            // Tự động cập nhật TKC sau khi kết thúc cuộc gọi
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckBalanceForPortAsync(e.PortName);
            });
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

            ApplyBackupMetadata(port, entry);
            if (!string.IsNullOrWhiteSpace(entry.PhoneNumber))
            {
                UpdateSmsReceiverPhone(port.PortName, entry.PhoneNumber);
                _simCache[ccid] = entry.PhoneNumber;
            }
            applied++;
        }

        if (applied > 0)
        {
            SaveSimCache();
        }

        AddLog($"[IMEI_SOURCE] Đã reload imei_backup.xlsx và áp dụng metadata cho {applied} cổng đang cắm.", "SUCCESS");
        SnackbarMessageQueue.Enqueue($"Đã reload imei_backup.xlsx ({applied} cổng được cập nhật).");
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
        
        BackendConcurrency.ConfigureThreadPool(targetPorts.Count);
        var sweepTasks = targetPorts.Select(async port =>
        {
            if (SmsInProgressPorts.ContainsKey(port.PortName) || !IsPortReadyForOperation(port.PortName))
                return;

            try
            {
                if (!TryGetCurrentSimSession(port.PortName, out var ccid, out var epoch, out _)) return;
                await _modemService.SweepUnreadSmsAsync(port.PortName);
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
                Application.Current.Dispatcher.Invoke(() => port.LastSweepTime = DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AddLog($"[{port.PortName}] Quét SMS lỗi: {ex.Message}", "ERROR"); }
        }).ToList();
        await Task.WhenAll(sweepTasks);
    }

    [RelayCommand]
    private async Task CheckBalanceAsync(string mode)
    {
        List<Models.SimPort> targetPorts;
        var balanceTasks = new List<Task>();
        
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
            balanceTasks.Add(RunBalanceLookupAsync(
                port, ussdCode, "Kiểm tra số dư", maxAttempts: 3, logResult: true));
        }
        await Task.WhenAll(balanceTasks);
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

        await Task.WhenAll(targetPorts.Select(async port =>
        {
            try
            {
                bool started = await ReloadPortSafelyAsync(port.PortName, "Đang khởi động lại và xác minh SIM...");
                AddLog($"[{port.PortName}] {(started ? "Đã bắt đầu reload an toàn" : "Không thể reload")}",
                    started ? "SUCCESS" : "ERROR");
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Khởi động lại lỗi: {ex.Message}", "ERROR");
            }
        }));
    }

    [RelayCommand]
    private async Task FixEc20Async(string mode)
    {
        List<Models.SimPort> targetPorts;
        
        if (mode == "Selected")
        {
            targetPorts = Ports.Where(p => p.IsSelected && IsActive(p)).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (đánh dấu ☑) để Fix EC20.");
                return;
            }
        }
        else
        {
            targetPorts = Ports.Where(IsActive).ToList();
            if (!targetPorts.Any())
            {
                SnackbarMessageQueue.Enqueue("Không có cổng nào để Fix EC20.");
                return;
            }
        }

        if (System.Windows.MessageBox.Show($"Bạn có chắc muốn chạy lệnh Fix Modem cho {targetPorts.Count} modem?\nThao tác này sẽ thiết lập lại cấu hình và khởi tạo SIM stack trong chế độ khóa RF.", "Fix Modem", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        SnackbarMessageQueue.Enqueue($"Đang gửi lệnh Fix Modem cho {targetPorts.Count} cổng...");
        AddLog($"Bắt đầu Fix Modem cho {targetPorts.Count} cổng...");

        BackendConcurrency.ConfigureThreadPool(targetPorts.Count);
        var fixTasks = targetPorts.Select(async port =>
        {
            try
            {
                if (!IsPortReadyForOperation(port.PortName)
                    || !TryGetCurrentSimSession(port.PortName, out var ccid, out var epoch, out _))
                    return;

                Application.Current.Dispatcher.Invoke(() => AddLog($"[{port.PortName}] Đang cấu hình lại EC20..."));
                QuectelModemProfile? profile = _modemService.GetModemProfile(port.PortName);
                var commands = new List<string> { "AT+CUSD=1" };
                if (profile?.Supports(ModemCapability.UrcPortRouting) == true)
                    commands.Add("AT+QURCCFG=\"urcport\",\"uart1\"");
                if (profile?.Supports(ModemCapability.NetworkScanConfig) == true)
                    commands.Add("AT+QCFG=\"nwscanmode\",0,1");
                // Không tự ghi QSIMDET polarity. Mức insert phụ thuộc cách đấu
                // USIM_PRESENCE của từng board và chỉ có hiệu lực sau reboot.
                if (profile?.Supports(ModemCapability.SimStatusUrc) == true)
                    commands.Add("AT+QSIMSTAT=1");

                foreach (string command in commands)
                {
                    if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
                    string response = await _modemService.SendCommandAsync(port.PortName, command, 5000);
                    if (response.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{command}: {response.Trim()}");
                }

                bool reloading = await ReloadPortSafelyAsync(port.PortName, "Đang nạp lại cấu hình EC20 và xác minh SIM...");
                AddLog($"[{port.PortName}] {(reloading ? "Fix EC20 hoàn tất; đang xác minh lại" : "Fix EC20 không thể reload")}",
                    reloading ? "SUCCESS" : "ERROR");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Lỗi Fix EC20: {ex.Message}", "ERROR");
            }
        }).ToList();
        await Task.WhenAll(fixTasks);
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

        var swapTasks = targetPorts.Select(async port =>
        {
            InvalidateSimSession(port.PortName);
            Application.Current.Dispatcher.Invoke(() => port.Status = SimStatus.Connecting);
            try
            {
                await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=4");
                _modemService.StartHotplugWaitLoop(port.PortName);
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Không thể tắt sóng để thay SIM: {ex.Message}", "ERROR");
            }
        }).ToList();
        await Task.WhenAll(swapTasks);
        
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

        await Task.WhenAll(targetPorts.Select(async port =>
        {
            if (!IsPortReadyForOperation(port.PortName)
                || !TryGetCurrentSimSession(port.PortName, out var ccid, out var epoch, out _))
                return;
            try
            {
                string result = await _modemService.SendCommandAsync(port.PortName, "AT+CMGD=1,4");
                if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                    && result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    AddLog($"[{port.PortName}] Xóa SMS thất bại: {result.Trim()}", "ERROR");
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Xóa SMS lỗi: {ex.Message}", "ERROR");
            }
        }));
    }

    public async Task<string> CheckBalanceForPortAsync(string portName)
    {
        if (!IsPortReadyForOperation(portName))
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";

        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port != null && !string.IsNullOrWhiteSpace(port.NetworkProvider))
        {
            string ussdCode = GetUssdCodeForProvider(port.NetworkProvider);
            AddLog($"Tự động kiểm tra lại TKC cho {port.PortName} sau khi gửi SMS...");
            return await RunBalanceLookupAsync(port, ussdCode, "Kiểm tra TKC", maxAttempts: 3, logResult: true);
        }
        return "ERROR: Cổng không hợp lệ hoặc không có thông tin nhà mạng";
    }

    /// <summary>
    /// Public wrapper: gửi USSD tùy chỉnh qua hệ thống throttle + decode UCS2 + set LastMessageContent.
    /// Dùng cho Ussd.razor và các UI component khác cần gọi USSD ngoài CheckBalance.
    /// </summary>
    public async Task<string> SendUssdForPortAsync(string portName, string ussdCode)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(ussdCode))
            return "ERROR: Thiếu tham số";

        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return "ERROR: Cổng không tìm thấy";
        if (!IsPortReadyForOperation(portName))
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";

        // Hiển thị trạng thái đang gửi lên cột Nội dung ngay lập tức
        Application.Current.Dispatcher.Invoke(() =>
        {
            port.LastMessageContent = $"[USSD] Đang gửi {ussdCode}...";
            port.Sender = "USSD";
        });

        string result;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(110));
        try
        {
            result = await SendUssdThrottledAsync(
                portName, ussdCode, "Manual USSD", maxAttempts: 2, logResult: true,
                cancellationToken: timeoutCts.Token);
            result = UssdResponseDecoder.Normalize(result);
        }
        catch (OperationCanceledException)
        {
            result = "ERROR: USSD timeout after 110 seconds";
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            bool failed = result.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || result.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
            port.LastUssdResult = result;
            if (failed)
            {
                port.LastMessageContent = "[USSD][THẤT BẠI] " + result;
                port.LastError = result;
            }
            else if (port.LastMessageContent.Contains("Đang gửi", StringComparison.OrdinalIgnoreCase))
            {
                port.LastMessageContent = result;
            }
            port.Sender = "USSD";
            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        });
        return result;
    }


    private async Task RunInitialBalanceLookupAsync(
        SimPort port, string ccid, long epoch, CancellationToken token)
    {
        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
            || port.Status != SimStatus.Active
            || (!string.IsNullOrWhiteSpace(port.PhoneNumber)
                && !string.IsNullOrWhiteSpace(port.Balance)
                && !string.IsNullOrWhiteSpace(port.ExpiryDate)))
            return;

        // Phiên trước đang sở hữu lần retry sau phục hồi IMS. Không tạo thêm một
        // yêu cầu *101# song song khi ReloadPortSafelyAsync vừa dựng phiên mới.
        string recoveryRetryKey = $"{port.PortName}|{NormalizeCcid(ccid)}";
        if (_ussdRecoveryRetryOwners.ContainsKey(recoveryRetryKey)) return;

        string lookupKey = $"{port.PortName}|{NormalizeCcid(ccid)}|{epoch}";
        if (!_initialBalanceLookupOwners.TryAdd(lookupKey, 0)) return;

        await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = true);
        try
        {
            int round = 0;
            while (IsSimSessionCurrent(port.PortName, ccid, epoch)
                && port.Status == SimStatus.Active
                && (string.IsNullOrWhiteSpace(port.PhoneNumber)
                    || string.IsNullOrWhiteSpace(port.Balance)
                    || string.IsNullOrWhiteSpace(port.ExpiryDate)))
            {
                token.ThrowIfCancellationRequested();
                round++;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.LastMessageContent = $"[USSD][ĐANG CHẠY] Tự dò SĐT/TKC/HSD – vòng {round}";
                    port.Sender = "USSD";
                    port.IsBalanceLoading = true;
                });

                string result = await SendUssdThrottledAsync(
                    port.PortName, "*101#", "Tự động lấy SĐT & TKC", maxAttempts: 3,
                    cancellationToken: token);

                // Give a late +CUSD URC time to reach the parser before retrying.
                await Task.Delay(10000, token);
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                    || port.Status != SimStatus.Active)
                    break;

                if (!string.IsNullOrWhiteSpace(port.PhoneNumber)
                    && !string.IsNullOrWhiteSpace(port.Balance)
                    && !string.IsNullOrWhiteSpace(port.ExpiryDate))
                {
                    AddLog($"[{port.PortName}] [USSD_AUTO_COMPLETE] Đã lấy đủ SĐT={port.PhoneNumber}, TKC={port.Balance}, HSD={port.ExpiryDate}; dừng retry.", "SUCCESS");
                    break;
                }

                int retrySeconds = Math.Clamp(AppSettings.UssdRetrySeconds, 10, 300);
                AddLog($"[{port.PortName}] [USSD_AUTO_RETRY] Vòng {round} chưa đủ SĐT/TKC/HSD ({result}); thử lại sau {retrySeconds} giây.", "WARN");
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"[{port.PortName}] Lấy SĐT/TKC tự động lỗi: {ex.Message}", "WARN");
        }
        finally
        {
            _initialBalanceLookupOwners.TryRemove(lookupKey, out _);
            if (IsSimSessionCurrent(port.PortName, ccid, epoch))
                await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = false);
        }
    }

    private async Task ContinueInitialBalanceLookupAfterRecoveryAsync(string portName)
    {
        try
        {
            if (!TryGetCurrentSimSession(portName, out string ccid, out long epoch, out CancellationToken token))
                return;

            // Cho +CUSD muộn đi qua parser trước. Nếu lần retry sau reboot đã lấy đủ
            // dữ liệu thì không tạo thêm lệnh; nếu chưa đủ, phiên SIM mới tự tiếp quản.
            await Task.Delay(10000, token);
            var port = GetPortsSnapshot().FirstOrDefault(p =>
                p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port == null
                || port.Status != SimStatus.Active
                || !IsSimSessionCurrent(portName, ccid, epoch)
                || (!string.IsNullOrWhiteSpace(port.PhoneNumber)
                    && !string.IsNullOrWhiteSpace(port.Balance)
                    && !string.IsNullOrWhiteSpace(port.ExpiryDate)))
                return;

            AddLog($"[{portName}] [USSD_SESSION_CONTINUE] Phiên SIM mới tiếp tục dò SĐT/TKC/HSD sau recovery.", "INFO");
            await RunInitialBalanceLookupAsync(port, ccid, epoch, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"[{portName}] Không thể nối lại vòng USSD sau recovery: {ex.Message}", "WARN");
        }
    }

    private async Task<string> RunBalanceLookupAsync(
        SimPort port, string ussdCode, string reason, int maxAttempts, bool logResult)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = true);
        try
        {
            string result = await SendUssdThrottledAsync(
                port.PortName, ussdCode, reason, maxAttempts: maxAttempts, logResult: logResult);
            // Chờ ngắn cho +CUSD bất đồng bộ; quá hạn UI sẽ hiện dấu —.
            if (!result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(10000, _lifetimeCts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            return "ERROR: USSD operation cancelled";
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = false);
        }
    }

    private async Task<string> SendUssdThrottledAsync(
        string portName,
        string ussdCode,
        string reason,
        bool logResult = false,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        if (!IsPortReadyForOperation(portName))
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";

        var currentPort = Ports.FirstOrDefault(p => p.PortName == portName);
        if (reason.Contains("lấy SĐT") && !reason.Contains("TKC")
            && currentPort != null && !string.IsNullOrWhiteSpace(currentPort.PhoneNumber))
            return "SKIPPED: Đã có SĐT";
        if (reason.Contains("Tự động lấy SĐT", StringComparison.OrdinalIgnoreCase) && currentPort != null
            && !string.IsNullOrWhiteSpace(currentPort.PhoneNumber)
            && !string.IsNullOrWhiteSpace(currentPort.Balance)
            && !string.IsNullOrWhiteSpace(currentPort.ExpiryDate))
            return "SKIPPED: Đã đủ thông tin";

        CancellationToken effectiveToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _lifetimeCts.Token;

        bool voiceReady = await EnsureUssdVoiceDomainAsync(portName, effectiveToken);
        if (!voiceReady || !IsPortReadyForOperation(portName))
        {
            string recoveryError = "ERROR: LTE registered but CS/IMS recovery did not complete";
            RecordPortError(portName, recoveryError);
            return recoveryError;
        }

        string result = await _ussdService.SendAsync(portName, ussdCode, maxAttempts, effectiveToken);

        // EC20F có thể đang CREG=1, nhận CUSD bằng OK, rồi rớt riêng miền CS trong
        // khi LTE/CEREG vẫn còn. Khi đó preflight ban đầu đã qua nên phải kiểm tra
        // lại sau lỗi, phục hồi IMS/reboot và retry đúng một chủ sở hữu cho COM+SIM.
        bool recoveredForAutomaticContinuation = false;
        if (IsMissingUssdPayload(result)
            && TryGetCurrentSimSession(portName, out string recoveryCcid, out _, out _))
        {
            string retryKey = $"{portName}|{NormalizeCcid(recoveryCcid)}";
            if (_ussdRecoveryRetryOwners.TryAdd(retryKey, 0))
            {
                try
                {
                    bool recovered = await TryRecoverCsAfterUssdFailureAsync(
                        portName, recoveryCcid, _lifetimeCts.Token);
                    if (recovered
                        && TryGetCurrentSimSession(portName, out string liveCcid, out _, out _)
                        && string.Equals(NormalizeCcid(liveCcid), NormalizeCcid(recoveryCcid), StringComparison.OrdinalIgnoreCase)
                        && IsPortReadyForOperation(portName))
                    {
                        recoveredForAutomaticContinuation = reason.Contains(
                            "Tự động lấy SĐT", StringComparison.OrdinalIgnoreCase);
                        AddLog($"[{portName}] [USSD_RETRY_AFTER_IMS] Gửi lại {ussdCode} sau khi CS đã phục hồi.", "INFO");
                        result = await _ussdService.SendAsync(
                            portName, ussdCode, maxAttempts, _lifetimeCts.Token);
                    }
                }
                finally
                {
                    _ussdRecoveryRetryOwners.TryRemove(retryKey, out _);
                }
            }
        }

        if (recoveredForAutomaticContinuation)
            _ = ContinueInitialBalanceLookupAfterRecoveryAsync(portName);

        if (result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            // USSD cancelled do session thay đổi (hot-swap SIM) hoặc shutdown — không phải lỗi thật
            bool isCancelledNotError = result.Contains("USSD operation cancelled", StringComparison.OrdinalIgnoreCase)
                || result.Contains("SIM session changed", StringComparison.OrdinalIgnoreCase);
            RecordPortError(portName, result);
            MaybeCooldownPort(portName, result);
            if (logResult)
            {
                string logLevel = isCancelledNotError ? "WARN" : "ERROR";
                AddLog($"Kết quả từ {portName}: {result}", logLevel);
            }
        }
        else if (logResult)
        {
            AddLog($"Kết quả từ {portName}: {result}", "SUCCESS");
        }
        return result;
    }

    private static bool IsMissingUssdPayload(string result) =>
        result.Contains("network returned no +CUSD", StringComparison.OrdinalIgnoreCase)
        // Lần đầu có thể mất +CUSD; các retry sau ghi đè kết quả bằng lỗi CREG.
        // Hàm phục hồi vẫn xác minh CREG mất nhưng CEREG còn trước khi reboot.
        || result.Contains("SIM not registered on CS network", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> TryRecoverCsAfterUssdFailureAsync(
        string portName, string expectedCcid, CancellationToken token)
    {
        static bool Registered(string response, string type) =>
            Regex.IsMatch(response, $@"\+{type}:\s*\d+\s*,\s*[15]\b", RegexOptions.IgnoreCase);

        string creg = await _modemService.SendCommandAsync(
            portName, "AT+CREG?", 5000, silent: true, ct: token);
        if (Registered(creg, "CREG")) return false;

        string cereg = await _modemService.SendCommandAsync(
            portName, "AT+CEREG?", 5000, silent: true, ct: token);
        if (!Registered(cereg, "CEREG")) return false;
        if (!TryGetCurrentSimSession(portName, out string liveCcid, out _, out _)
            || !string.Equals(NormalizeCcid(liveCcid), NormalizeCcid(expectedCcid), StringComparison.OrdinalIgnoreCase))
            return false;

        AddLog($"[{portName}] [USSD_CS_LOST_AFTER_SEND] Modem chỉ trả OK, không có +CUSD và đã rớt CS; bắt đầu phục hồi IMS.", "WARN");
        return await EnsureUssdVoiceDomainAsync(portName, token);
    }

    private async Task<bool> EnsureUssdVoiceDomainAsync(string portName, CancellationToken token)
    {
        static bool Registered(string response, string type) =>
            Regex.IsMatch(response, $@"\+{type}:\s*\d+\s*,\s*[15]\b", RegexOptions.IgnoreCase);

        string creg = await _modemService.SendCommandAsync(
            portName, "AT+CREG?", 5000, silent: true, ct: token);
        if (Registered(creg, "CREG")) return true;

        string cereg = await _modemService.SendCommandAsync(
            portName, "AT+CEREG?", 5000, silent: true, ct: token);
        QuectelModemProfile? profile = _modemService.GetModemProfile(portName);
        if (!Registered(cereg, "CEREG")
            || profile?.Supports(ModemCapability.ImsConfig) != true)
        {
            // Không phải đúng lỗi LTE-only đã xác minh trên EC20F; để preflight USSD
            // trả lỗi mạng thật thay vì tự thay đổi cấu hình modem.
            return true;
        }

        if (!TryGetCurrentSimSession(portName, out string ccid, out _, out _)) return false;
        string recoveryKey = $"{portName}|{ccid}";

        // Nhiều thao tác USSD đồng thời trên cùng SIM phải chờ chung đúng một lần
        // phục hồi; không để yêu cầu thứ hai vượt qua trong khi modem đang reboot.
        if (_ussdVoiceRecoveryTasks.TryGetValue(recoveryKey, out Task<bool>? activeRecovery))
            return await activeRecovery.WaitAsync(token);

        // Một SIM chỉ tự reboot một lần. Nếu lần đó thất bại, trả lỗi rõ ràng thay
        // vì gây vòng lặp reboot. SIM mới trên cùng COM có CCID khác nên vẫn được thử.
        if (_ussdVoiceRecoveryAttempted.ContainsKey(recoveryKey)) return false;

        Task<bool> recovery = _ussdVoiceRecoveryTasks.GetOrAdd(
            recoveryKey, _ => RecoverUssdVoiceDomainCoreAsync(portName, recoveryKey));
        _ = recovery.ContinueWith(
            _ => ((ICollection<KeyValuePair<string, Task<bool>>>)_ussdVoiceRecoveryTasks)
                .Remove(new KeyValuePair<string, Task<bool>>(recoveryKey, recovery)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await recovery.WaitAsync(token);
    }

    private async Task<bool> RecoverUssdVoiceDomainCoreAsync(string portName, string recoveryKey)
    {
        if (!_ussdVoiceRecoveryAttempted.TryAdd(recoveryKey, 0)) return false;

        try
        {
            AddLog($"[{portName}] [USSD_IMS_RECOVERY] LTE đã đăng ký nhưng chưa có CS; bật IMS và reboot riêng COM.", "WARN");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var port = Ports.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
                if (port != null)
                {
                    port.LastMessageContent = "[USSD][ĐANG CHẠY] Bật IMS và đăng ký lại dịch vụ thoại...";
                    port.Sender = "USSD";
                }
            });

            string ims = await _modemService.SendCommandAsync(
                portName, "AT+QCFG=\"ims\"", 5000, silent: true, ct: _lifetimeCts.Token);
            if (!Regex.IsMatch(ims, @"""ims""\s*,\s*1\b", RegexOptions.IgnoreCase))
            {
                string setIms = await _modemService.SendCommandAsync(
                    portName, "AT+QCFG=\"ims\",1", 5000, silent: true, ct: _lifetimeCts.Token);
                if (setIms.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"[{portName}] [USSD_IMS_RECOVERY] Modem từ chối bật IMS: {setIms.Trim()}", "ERROR");
                    return false;
                }
            }

            if (!await ReloadPortSafelyAsync(portName, "Đang bật IMS và xác minh lại SIM/IMEI..."))
                return false;

            for (int probe = 0; probe < 45; probe++)
            {
                await Task.Delay(2000, _lifetimeCts.Token);
                if (!IsPortReadyForOperation(portName)) continue;

                string creg = await _modemService.SendCommandAsync(
                    portName, "AT+CREG?", 5000, silent: true, ct: _lifetimeCts.Token);
                if (Regex.IsMatch(creg, @"\+CREG:\s*\d+\s*,\s*[15]\b", RegexOptions.IgnoreCase))
                {
                    AddLog($"[{portName}] [USSD_IMS_RECOVERY] Đã đăng ký CS sau reboot; tiếp tục USSD.", "SUCCESS");
                    return true;
                }
            }

            AddLog($"[{portName}] [USSD_IMS_RECOVERY] Hết 90 giây nhưng chưa đăng ký được CS.", "ERROR");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AddLog($"[{portName}] [USSD_IMS_RECOVERY] {ex.Message}", "ERROR");
            return false;
        }
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
    private void OpenDisplayColumnsDialog()
    {
        IsDisplayColumnsDialogOpen = true;
    }

    [RelayCommand]
    private void CloseDisplayColumnsDialog()
    {
        IsDisplayColumnsDialogOpen = false;
        SettingsService.SaveSettings(AppSettings);
        SnackbarMessageQueue.Enqueue("Đã lưu cấu hình hiển thị cột.");
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

        string normalizedCommand = AtCommandInput.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        if (normalizedCommand.StartsWith("AT+CFUN=", StringComparison.Ordinal)
            || normalizedCommand == "ATZ"
            || normalizedCommand.StartsWith("AT+QPOWD", StringComparison.Ordinal))
        {
            AtCommandOutput += "[BLOCKED] Lệnh thay đổi nguồn/radio phải chạy qua nút Recovery, Restart hoặc Fix EC20 để không bỏ qua xác minh SIM/IMEI.\n";
            return;
        }
        
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
                // Chỉ gửi lệnh cho các cổng đang Active — bỏ qua cổng lỗi/chờ SIM
                var activePorts = GetPortsSnapshot().Where(p => p.Status == Models.SimStatus.Active).ToList();
                await Task.WhenAll(activePorts.Select(async port =>
                {
                    string randomFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                    if (string.IsNullOrEmpty(randomFwd)) return;
                    
                    string fwdDialType = randomFwd.StartsWith("+") ? "145" : "129";
                    AddLog($"[{port.PortName}] Đang thiết lập tự động chuyển hướng đến {randomFwd}...");
                    string res = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,1,\"{randomFwd}\",{fwdDialType}", timeoutMs: 5000);
                    if (res.Contains("OK"))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            port.ForwardedTo = randomFwd;
                            port.ForwardCount++;
                        });
                        AddLog($"[{port.PortName}] Chuyển hướng → {randomFwd} OK", "SUCCESS");
                    }
                    else
                    {
                        AddLog($"[{port.PortName}] Thiết lập chuyển hướng thất bại: {res.Trim()}", "ERROR");
                    }
                }));
            });
        }
        else if (AppSettings != null)
        {
            // Hủy chuyển hướng nếu người dùng tắt tính năng hoặc để trống số điện thoại
            Task.Run(async () =>
            {
                var activePorts = GetPortsSnapshot();
                await Task.WhenAll(activePorts.Select(async port =>
                {
                    await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,4", timeoutMs: 5000);
                    Application.Current.Dispatcher.Invoke(() => port.ForwardedTo = string.Empty);
                }));
            });
        }
    }

    /// <summary>
    /// Áp dụng cài đặt mới (từ Settings.razor) ngay lập tức:
    /// sync AppSettings + apply call forwarding.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        var saved = SettingsService.Current;
        if (saved != null) AppSettings = saved;

        if (AppSettings != null && AppSettings.EnableAutoCallForwarding && !string.IsNullOrWhiteSpace(AppSettings.ForwardPhoneNumber))
        {
            AddLog("[Settings] Đang áp dụng chuyển hướng cuộc gọi...", "INFO");
            var activePorts = GetPortsSnapshot().Where(p => p.Status == Models.SimStatus.Active).ToList();
            await Task.WhenAll(activePorts.Select(async port =>
            {
                if (!IsPortReadyForOperation(port.PortName)) return;
                string rndFwd = GetRandomForwardNumber(AppSettings.ForwardPhoneNumber);
                if (string.IsNullOrEmpty(rndFwd)) return;
                string dialType = rndFwd.StartsWith("+") ? "145" : "129";
                string res = await _modemService.SendCommandAsync(port.PortName, $"AT+CCFC=0,1,\"{rndFwd}\",{dialType}", timeoutMs: 5000);
                if (res.Contains("OK"))
                {
                    Application.Current.Dispatcher.Invoke(() => { port.ForwardedTo = rndFwd; port.ForwardCount++; });
                    AddLog($"[{port.PortName}] Chuyển hướng → {rndFwd} OK", "SUCCESS");
                }
            }));
        }
        else if (AppSettings != null && !AppSettings.EnableAutoCallForwarding)
        {
            var activePorts = GetPortsSnapshot().Where(p => p.Status == Models.SimStatus.Active).ToList();
            await Task.WhenAll(activePorts.Select(async port =>
            {
                if (!IsPortReadyForOperation(port.PortName)) return;
                await _modemService.SendCommandAsync(port.PortName, "AT+CCFC=0,4", timeoutMs: 5000);
                Application.Current.Dispatcher.Invoke(() => port.ForwardedTo = string.Empty);
            }));
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

    public Task<string> QueueSmsAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct = default)
    {
        return SendSmsViaServiceAsync(portName, phoneNumber, content, ct);
    }

    /// <summary>
    /// SMS từ ToolWeb luôn đi vào pipeline chung theo từng COM và không bị chặn
    /// bởi cooldown của thao tác UI.
    /// </summary>
    public async Task<string> QueueSmsFromWebAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct = default)
    {
        string result = await _smsService.SendAsync(portName, phoneNumber, content, ct);
        if (result.Contains("thành công", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase))
        {
            RecordSmsSuccess(portName);
            AddLog($"[{portName}] [WEB_SMS_SENT] Đã gửi đến {phoneNumber}; đang chờ OTP.", "SUCCESS");
            // Tự động cập nhật TKC sau khi gửi SMS (delay 3s để modem ổn định)
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await CheckBalanceForPortAsync(portName);
            });
        }
        else
        {
            RecordPortError(portName, result);
            AddLog($"[{portName}] [WEB_SMS_FAILED] {result}", "ERROR");
        }
        return result;
    }

    private async Task<string> SendSmsViaServiceAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct)
    {
        if (!IsPortReadyForOperation(portName))
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";
        if (IsPortCoolingDown(portName, out var remaining))
            return $"ERROR: Port cooling down for {remaining.TotalSeconds:0}s";

        string result = await _smsService.SendAsync(portName, phoneNumber, content, ct);
        if (result.Contains("thành công", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase))
        {
            RecordSmsSuccess(portName);
            AddLog($"[{portName}] Gửi tin nhắn đến {phoneNumber} thành công.", "SUCCESS");
            // Tự động cập nhật TKC sau khi gửi SMS
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await CheckBalanceForPortAsync(portName);
            });
        }
        else
        {
            RecordPortError(portName, result);
            MaybeCooldownPort(portName, result);
            AddLog($"[{portName}] Gửi SMS thất bại: {result}", "ERROR");
        }
        return result;
    }

    public string RemoveDiacritics(string text)
    {
        return GsmSmsService.RemoveDiacritics(text);
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
                string fwType = ForwardNumber.StartsWith("+") ? "145" : "129";
                cmd = $"AT+CCFC=0,1,\"{ForwardNumber}\",{fwType}";
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
            
            // Bắt đầu giám sát cuộc gọi và phát âm thanh khi có người nhấc máy
            if (action == "Dial" && result.Contains("OK"))
            {
                _callFailures.TryRemove(CallManagerSelectedPort, out _);
                string portName = CallManagerSelectedPort;
                _ = Task.Run(async () =>
                {
                    await MonitorAndPlayAudioDuringCallAsync(portName, 60);
                });
            }
            
            // Cập nhật hiển thị lên bảng Dashboard
            if (action == "SetForwarding" && result.Contains("OK"))
            {
                var port = Ports.FirstOrDefault(p => p.PortName == CallManagerSelectedPort);
                if (port != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        port.ForwardedTo = ForwardNumber;
                        port.ForwardCount++;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            CallManagerOutput += $"[ERROR] {ex.Message}\n";
        }
    }

    // Network & Sim methods removed
    
    public async Task<bool> ExecuteCallFromUiAsync(
        string port,
        string phone,
        string wavPath,
        int duration,
        bool record = false,
        Action<string>? onStatusUpdate = null,
        CancellationToken ct = default)
    {
        if (!IsPortReadyForOperation(port)
            || !TryGetCurrentSimSession(port, out var callCcid, out var callEpoch, out var simToken))
            return false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, simToken);
        var operationToken = linkedCts.Token;

        // Đăng ký lắng nghe LogMessage của modem để truyền trạng thái realtime lên UI
        void OnLog(object? s, GsmDataEventArgs e)
        {
            if (e.PortName == port && e.Data != null)
                onStatusUpdate?.Invoke(e.Data);
        }

        if (onStatusUpdate != null)
            _modemService.LogMessage += OnLog;

        try
        {
            bool result = await _callService.CallAsync(
                port,
                phone,
                string.IsNullOrWhiteSpace(wavPath) ? null : wavPath,
                duration,
                record,
                operationToken);

            return result
                && IsSimSessionCurrent(port, callCcid, callEpoch)
                && IsPortReadyForOperation(port);
        }
        finally
        {
            if (onStatusUpdate != null)
                _modemService.LogMessage -= OnLog;
        }
    }
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
            // Loại bỏ User Data Header (UDH) nếu đây là SMS ghép nối bị lỗi mode Text
            if (hexString.StartsWith("050003", StringComparison.OrdinalIgnoreCase) && hexString.Length >= 12)
            {
                hexString = hexString.Substring(12);
            }
            else if (hexString.StartsWith("060804", StringComparison.OrdinalIgnoreCase) && hexString.Length >= 14)
            {
                hexString = hexString.Substring(hexString.Length % 4 == 2 ? 14 : 16);
            }

            // Kiểm tra xem có phải chuỗi HEX không và độ dài phải chia hết cho 4
            if (!Regex.IsMatch(hexString, @"^[0-9A-Fa-f]+$") || hexString.Length % 4 != 0) { return hexString; }
            if (Regex.IsMatch(hexString, @"^\d+$") && !Regex.IsMatch(hexString, @"^(00[2-7][0-9])+$")) { return hexString; }

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
    public string ApiServerUrl => $"Firebase: {FirebaseService.DatabaseUrl}";


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

            SnackbarMessageQueue.Enqueue($"Đang gửi {items.Count} SMS song song trên {activePorts.Count} cổng...");
            AddLog($"[BULK SMS] Bắt đầu gửi {items.Count} tin nhắn từ file: {System.IO.Path.GetFileName(dialog.FileName)}");

            int sent = 0, failed = 0;
            var portQueues = items
                .Select((item, index) => new
                {
                    Item = item,
                    PortName = activePorts[index % activePorts.Count]
                })
                .GroupBy(x => x.PortName, StringComparer.OrdinalIgnoreCase);

            await Task.WhenAll(portQueues.Select(async queue =>
            {
                // Một hàng đợi riêng cho mỗi COM: tuần tự trong cùng modem, song song giữa các modem.
                foreach (var assignment in queue)
                {
                    string sourcePort = assignment.PortName;
                    var (phone, content) = assignment.Item;
                    try
                    {
                        string result = await _smsService.SendAsync(
                            sourcePort, phone, content, _lifetimeCts.Token);
                        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == sourcePort);
                        if (!result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                        {
                            AddLog($"[BULK SMS] [{sourcePort}] → {phone}: OK", "SUCCESS");
                            if (port != null)
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    port.LastSmsResult = "Gửi thành công";
                                    port.UpdateDisplayResult(CommandPanelTab);
                                });
                            Interlocked.Increment(ref sent);
                        }
                        else
                        {
                            AddLog($"[BULK SMS] [{sourcePort}] → {phone}: FAIL — {result}", "ERROR");
                            if (port != null)
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    port.LastSmsResult = result;
                                    port.UpdateDisplayResult(CommandPanelTab);
                                });
                            Interlocked.Increment(ref failed);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[BULK SMS] [{sourcePort}] → {phone}: FAIL — {ex.Message}", "ERROR");
                        Interlocked.Increment(ref failed);
                    }

                    await Task.Delay(2000, _lifetimeCts.Token);
                }
            }));

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
            if (File.Exists(_imeiCacheFilePath) || File.Exists(_pendingImeiCacheFilePath))
            {
                LoadImeiCacheWorkbook();
                return;
            }

            // One-time migration path for older ToolGSM installations.
            if (File.Exists(_legacyImeiCacheCsvPath))
            {
                try
                {
                    var lines = File.ReadAllLines(_legacyImeiCacheCsvPath);
                    var newCache = new ConcurrentDictionary<string, SimBackupEntry>();
                    if (lines.Length > 0)
                    {
                        int headerLineIndex = lines[0].TrimStart('\uFEFF')
                            .StartsWith("sep=", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        if (headerLineIndex >= lines.Length)
                            throw new InvalidDataException("File imei_backup.csv thiếu dòng tiêu đề.");

                        var header = ParseCsvLine(lines[headerLineIndex])
                            .Select(value => value.Trim().TrimStart('\uFEFF'))
                            .ToArray();
                        int idxCcid = Array.IndexOf(header, "CCID");
                        int idxImei = Array.IndexOf(header, "IMEI");
                        int idxPhone = Array.IndexOf(header, "PhoneNumber");
                        int idxNetwork = Array.IndexOf(header, "NetworkProvider");
                        int idxBalance = Array.IndexOf(header, "Balance");
                        int idxPromotion = Array.IndexOf(header, "PromotionBalance");
                        int idxExpiry = Array.IndexOf(header, "ExpiryDate");
                        int idxCreated = Array.IndexOf(header, "CreatedAt");
                        int idxUpdated = Array.IndexOf(header, "UpdatedAt");
                        int idxRegDate = Array.IndexOf(header, "SimRegDate");
                        int idxLock1C = Array.IndexOf(header, "Lock1C");
                        int idxLock2C = Array.IndexOf(header, "Lock2C");
                        int idxPort = Array.IndexOf(header, "LastPortName");
                        int idxDevice = Array.IndexOf(header, "DeviceName");
                        int idxHardware = Array.IndexOf(header, "HardwareName");
                        int idxManufacturer = Array.IndexOf(header, "ModemManufacturer");
                        int idxModel = Array.IndexOf(header, "ModemModel");
                        int idxFirmware = Array.IndexOf(header, "ModemFirmware");
                        int idxCapabilities = Array.IndexOf(header, "ModemCapabilities");
                        int idxStatus = Array.IndexOf(header, "Status");
                        int idxSignal = Array.IndexOf(header, "SignalStrength");
                        int idxSource = Array.IndexOf(header, "SourceFile");

                        if (idxCcid < 0) idxCcid = 0;
                        if (idxImei < 0) idxImei = 1;
                        if (idxPhone < 0) idxPhone = 2;
                        if (idxCreated < 0) idxCreated = 3;
                        // Legacy seven-column schema:
                        // CCID,IMEI,PhoneNumber,CreatedAt,SimRegDate,Lock1C,Lock2C
                        if (header.Length == 7)
                        {
                            if (idxRegDate < 0) idxRegDate = 4;
                            if (idxLock1C < 0) idxLock1C = 5;
                            if (idxLock2C < 0) idxLock2C = 6;
                        }

                        // Heuristic detection based on first data row if headers were corrupted by old bugs
                        int firstDataLineIndex = headerLineIndex + 1;
                        if (lines.Length > firstDataLineIndex)
                        {
                            var firstDataParts = ParseCsvLine(lines[firstDataLineIndex]);
                            if (firstDataParts.Length >= 2)
                            {
                                string colCcid = firstDataParts[idxCcid].Trim();
                                string colImei = firstDataParts[idxImei].Trim();
                                if (colCcid.Length >= 14 && colCcid.Length <= 16 && colCcid.All(char.IsDigit) && 
                                    (colImei.StartsWith("89") || colImei.Length >= 18))
                                {
                                    int temp = idxCcid;
                                    idxCcid = idxImei;
                                    idxImei = temp;
                                }
                            }
                        }

                        static string Field(string[] values, int index) =>
                            index >= 0 && index < values.Length ? values[index].Trim() : string.Empty;

                        for (int i = firstDataLineIndex; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var parts = ParseCsvLine(line);
                            if (parts.Length > Math.Max(idxCcid, idxImei))
                            {
                                string ccid = NormalizeCcid(parts[idxCcid]);
                                string imei = NormalizeImei(parts[idxImei]);
                                if (!string.IsNullOrEmpty(ccid) && !string.IsNullOrEmpty(imei))
                                {
                                    string phone = parts.Length > idxPhone ? parts[idxPhone].Trim() : string.Empty;
                                    if (phone.Contains("So TB") || phone.Contains("so tb"))
                                    {
                                        var numMatch = Regex.Match(phone, @"\d{9,11}");
                                        if (numMatch.Success) phone = numMatch.Value;
                                    }
                                    var entry = new SimBackupEntry
                                    {
                                        Ccid = ccid,
                                        Imei = imei,
                                        PhoneNumber = phone,
                                        NetworkProvider = Field(parts, idxNetwork),
                                        Balance = Field(parts, idxBalance),
                                        PromotionBalance = Field(parts, idxPromotion),
                                        ExpiryDate = Field(parts, idxExpiry),
                                        CreatedAt = Field(parts, idxCreated),
                                        UpdatedAt = Field(parts, idxUpdated),
                                        SourceFile = string.IsNullOrWhiteSpace(Field(parts, idxSource))
                                            ? "imei_backup.csv" : Field(parts, idxSource),
                                        SimRegDate = Field(parts, idxRegDate),
                                        Lock1C = Field(parts, idxLock1C),
                                        Lock2C = Field(parts, idxLock2C),
                                        LastPortName = Field(parts, idxPort),
                                        DeviceName = Field(parts, idxDevice),
                                        HardwareName = Field(parts, idxHardware),
                                        ModemManufacturer = Field(parts, idxManufacturer),
                                        ModemModel = Field(parts, idxModel),
                                        ModemFirmware = Field(parts, idxFirmware),
                                        ModemCapabilities = Field(parts, idxCapabilities),
                                        Status = Field(parts, idxStatus),
                                        SignalStrength = int.TryParse(Field(parts, idxSignal), out int signal) ? signal : 0
                                    };
                                    newCache[ccid] = entry;
                                    if (!string.IsNullOrWhiteSpace(entry.PhoneNumber))
                                    {
                                        _simCache[ccid] = entry.PhoneNumber;
                                    }
                                }
                            }
                        }
                    }
                    _imeiCache = newCache;
                    AddLog($"[IMEI_SOURCE] Đã nạp {newCache.Count} dòng từ imei_backup.csv và chuyển sang XLSX.", "SUCCESS");
                    SaveImeiCache();
                }
                catch (Exception ex)
                {
                    AddLog($"Lỗi đọc imei_backup.csv: {ex.Message}", "ERROR");
                }
            }
            else
            {
                // File backup đã bị xóa trong lúc tool đang chạy: không giữ cache
                // cũ trong RAM, nếu không SIM chưa backup vẫn có thể bị nhận là đã duyệt.
                _imeiCache = new ConcurrentDictionary<string, SimBackupEntry>();
                AddLog($"[IMEI_SOURCE] Không tìm thấy {_imeiCacheFilePath}; mọi SIM sẽ chờ ACCEPT.", "WARN");
            }
        }
    }

    private void LoadImeiCacheWorkbook()
    {
        try
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var newCache = new ConcurrentDictionary<string, SimBackupEntry>();
            int ReadWorkbook(string path)
            {
                if (!File.Exists(path)) return 0;
                using var package = new ExcelPackage(new FileInfo(path));
                var worksheet = package.Workbook.Worksheets["IMEI Backup"]
                    ?? package.Workbook.Worksheets.FirstOrDefault();
                if (worksheet?.Dimension == null)
                    throw new InvalidDataException($"File {Path.GetFileName(path)} không có dữ liệu.");

                var headerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int column = 1; column <= worksheet.Dimension.End.Column; column++)
                {
                    string name = worksheet.Cells[1, column].Text.Trim().TrimStart('\uFEFF');
                    if (!string.IsNullOrWhiteSpace(name)) headerIndexes[name] = column;
                }

                if (!headerIndexes.TryGetValue("CCID", out int ccidColumn)
                    || !headerIndexes.TryGetValue("IMEI", out int imeiColumn))
                    throw new InvalidDataException($"File {Path.GetFileName(path)} thiếu cột CCID hoặc IMEI.");

                string Cell(int row, string name) => headerIndexes.TryGetValue(name, out int column)
                    ? worksheet.Cells[row, column].Text.Trim()
                    : string.Empty;

                int loaded = 0;
                for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
                {
                    string ccid = NormalizeCcid(worksheet.Cells[row, ccidColumn].Text);
                    string imei = NormalizeImei(worksheet.Cells[row, imeiColumn].Text);
                    if (string.IsNullOrWhiteSpace(ccid) || string.IsNullOrWhiteSpace(imei)) continue;

                    var entry = new SimBackupEntry
                    {
                        Ccid = ccid, Imei = imei, PhoneNumber = Cell(row, "PhoneNumber"),
                        NetworkProvider = Cell(row, "NetworkProvider"), Balance = Cell(row, "Balance"),
                        PromotionBalance = Cell(row, "PromotionBalance"), ExpiryDate = Cell(row, "ExpiryDate"),
                        SimRegDate = Cell(row, "SimRegDate"), Lock1C = Cell(row, "Lock1C"),
                        Lock2C = Cell(row, "Lock2C"), CreatedAt = Cell(row, "CreatedAt"),
                        UpdatedAt = Cell(row, "UpdatedAt"), LastPortName = Cell(row, "LastPortName"),
                        DeviceName = Cell(row, "DeviceName"), HardwareName = Cell(row, "HardwareName"),
                        ModemManufacturer = Cell(row, "ModemManufacturer"), ModemModel = Cell(row, "ModemModel"),
                        ModemFirmware = Cell(row, "ModemFirmware"), ModemCapabilities = Cell(row, "ModemCapabilities"),
                        Status = Cell(row, "Status"),
                        SignalStrength = int.TryParse(Cell(row, "SignalStrength"), out int signal) ? signal : 0,
                        SourceFile = string.IsNullOrWhiteSpace(Cell(row, "SourceFile"))
                            ? Path.GetFileName(path) : Cell(row, "SourceFile")
                    };
                    newCache[ccid] = entry;
                    loaded++;
                }
                return loaded;
            }

            int canonicalCount = ReadWorkbook(_imeiCacheFilePath);
            int pendingCount = ReadWorkbook(_pendingImeiCacheFilePath);
            _imeiCache = newCache;
            foreach (var entry in newCache.Values)
            {
                if (!string.IsNullOrWhiteSpace(entry.PhoneNumber)) _simCache[entry.Ccid] = entry.PhoneNumber;
            }
            AddLog($"[IMEI_SOURCE] Đã nạp {newCache.Count} dòng từ XLSX (chính={canonicalCount}, chờ hợp nhất={pendingCount}).", "SUCCESS");

            // A pending workbook is a complete snapshot saved while the main XLSX was locked.
            if (pendingCount > 0) SaveImeiCache();
        }
        catch (Exception ex)
        {
            AddLog($"Lỗi đọc imei_backup.xlsx: {ex.Message}", "ERROR");
        }
    }

    private void SaveImeiCache()
    {
        lock (_imeiCacheLock)
        {
            try
            {
                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
                using var package = new ExcelPackage();
                package.Workbook.Properties.Title = "ToolGSM IMEI Backup";
                package.Workbook.Properties.Subject = "CCID to IMEI and SIM metadata mapping";
                var worksheet = package.Workbook.Worksheets.Add("IMEI Backup");

                for (int column = 0; column < ImeiBackupColumns.Length; column++)
                    worksheet.Cells[1, column + 1].Value = ImeiBackupColumns[column];

                int row = 2;
                foreach (var entry in _imeiCache.Values.OrderBy(value => value.Ccid, StringComparer.OrdinalIgnoreCase))
                {
                    object?[] values =
                    [
                        entry.Ccid, entry.Imei, entry.PhoneNumber, entry.NetworkProvider, entry.Balance,
                        entry.PromotionBalance, entry.ExpiryDate, entry.SimRegDate, entry.Lock1C,
                        entry.Lock2C, entry.CreatedAt, entry.UpdatedAt, entry.LastPortName,
                        entry.DeviceName, entry.HardwareName, entry.ModemManufacturer, entry.ModemModel,
                        entry.ModemFirmware, entry.ModemCapabilities, entry.Status, entry.SignalStrength,
                        entry.SourceFile
                    ];
                    for (int column = 0; column < values.Length; column++)
                        worksheet.Cells[row, column + 1].Value = values[column];
                    row++;
                }

                int lastRow = Math.Max(1, row - 1);
                int lastColumn = ImeiBackupColumns.Length;
                using (var header = worksheet.Cells[1, 1, 1, lastColumn])
                {
                    header.Style.Font.Bold = true;
                    header.Style.Font.Color.SetColor(System.Drawing.Color.White);
                    header.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    header.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(30, 58, 138));
                    header.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                    header.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;
                }

                worksheet.Row(1).Height = 26;
                worksheet.View.FreezePanes(2, 1);
                worksheet.Cells[1, 1, lastRow, lastColumn].Style.VerticalAlignment =
                    OfficeOpenXml.Style.ExcelVerticalAlignment.Center;

                double[] widths = [24, 18, 16, 17, 15, 18, 14, 14, 14, 14, 20, 20, 13, 22, 24, 20, 18, 24, 28, 18, 13, 22];
                for (int column = 1; column <= lastColumn; column++)
                    worksheet.Column(column).Width = widths[column - 1];

                if (lastRow >= 2)
                {
                    // Keep identifiers exact (including a leading zero in phone numbers).
                    worksheet.Cells[2, 1, lastRow, 3].Style.Numberformat.Format = "@";
                    worksheet.Cells[2, 1, lastRow, 3].Style.QuotePrefix = true;
                    worksheet.Cells[2, 21, lastRow, 21].Style.Numberformat.Format = "0";
                    // Excel Table tự tạo AutoFilter trong table1.xml. Không đặt thêm
                    // worksheet AutoFilter trên cùng vùng vì Excel sẽ coi hai filter
                    // chồng nhau là nội dung không đọc được và xóa cả Table khi mở file.
                    var table = worksheet.Tables.Add(worksheet.Cells[1, 1, lastRow, lastColumn], "ImeiBackupTable");
                    table.TableStyle = OfficeOpenXml.Table.TableStyles.Medium2;
                    table.ShowFilter = true;
                }

                string directory = Path.GetDirectoryName(_imeiCacheFilePath) ?? AppPaths.RuntimeDirectory;
                string tempPath = Path.Combine(directory, "imei_backup.tmp.xlsx");
                if (File.Exists(tempPath)) File.Delete(tempPath);
                package.SaveAs(new FileInfo(tempPath));

                if (File.Exists(_imeiCacheFilePath))
                {
                    string backupPath = Path.Combine(directory, "imei_backup.backup.xlsx");
                    File.Copy(_imeiCacheFilePath, backupPath, overwrite: true);
                }

                File.Move(tempPath, _imeiCacheFilePath, overwrite: true);
                if (File.Exists(_pendingImeiCacheFilePath)) File.Delete(_pendingImeiCacheFilePath);
            }
            catch (Exception ex)
            {
                // Excel may lock the main workbook while the user is viewing it. Keep the
                // complete snapshot separately so accepted SIMs survive a restart and are
                // merged automatically on the next successful save.
                try
                {
                    string tempPath = AppPaths.ForRuntimeFile("imei_backup.tmp.xlsx");
                    if (File.Exists(tempPath))
                        File.Move(tempPath, _pendingImeiCacheFilePath, overwrite: true);
                }
                catch (Exception pendingEx)
                {
                    AddLog($"Lỗi lưu snapshot IMEI dự phòng: {pendingEx.Message}", "ERROR");
                }
                AddLog($"Lỗi ghi file imei_backup.xlsx: {ex.Message}", "ERROR");
            }
        }
    }

    public void ExportImeiBackupWorkbook(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        lock (_imeiCacheLock)
        {
            foreach (var port in GetPortsSnapshot())
            {
                string ccid = NormalizeCcid(port.Serial);
                if (!string.IsNullOrWhiteSpace(ccid) && _imeiCache.TryGetValue(ccid, out var entry))
                    EnrichBackupEntry(entry, port);
            }
        }

        SaveImeiCache();
        string sourcePath = File.Exists(_pendingImeiCacheFilePath)
            ? _pendingImeiCacheFilePath
            : _imeiCacheFilePath;
        if (!File.Exists(sourcePath))
            throw new IOException("Không tạo được file backup XLSX.");

        string fullSource = Path.GetFullPath(sourcePath);
        string fullTarget = Path.GetFullPath(filePath);
        if (!string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase))
            File.Copy(fullSource, fullTarget, overwrite: true);
    }

    public int ImportImeiBackupWorkbook(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return 0;

        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        int validRows = 0;
        using (var package = new ExcelPackage(new FileInfo(filePath)))
        {
            var worksheet = package.Workbook.Worksheets["IMEI Backup"]
                ?? package.Workbook.Worksheets.FirstOrDefault();
            if (worksheet?.Dimension == null)
                throw new InvalidDataException("File XLSX không có dữ liệu.");

            int ccidColumn = 0;
            int imeiColumn = 0;
            for (int column = 1; column <= worksheet.Dimension.End.Column; column++)
            {
                string header = worksheet.Cells[1, column].Text.Trim();
                if (header.Equals("CCID", StringComparison.OrdinalIgnoreCase)) ccidColumn = column;
                if (header.Equals("IMEI", StringComparison.OrdinalIgnoreCase)) imeiColumn = column;
            }
            if (ccidColumn == 0 || imeiColumn == 0)
                throw new InvalidDataException("File XLSX thiếu cột CCID hoặc IMEI.");

            for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
            {
                if (!string.IsNullOrWhiteSpace(NormalizeCcid(worksheet.Cells[row, ccidColumn].Text))
                    && !string.IsNullOrWhiteSpace(NormalizeImei(worksheet.Cells[row, imeiColumn].Text)))
                    validRows++;
            }
        }

        if (validRows == 0) return 0;
        File.Copy(filePath, _pendingImeiCacheFilePath, overwrite: true);
        lock (_imeiCacheLock)
        {
            LoadImeiCacheWorkbook();
        }
        return validRows;
    }

    public void AddNewImeiCacheEntry(SimBackupEntry newEntry)
    {
        if (newEntry == null) return;
        string normalizedCcid = NormalizeCcid(newEntry.Ccid);
        if (string.IsNullOrEmpty(normalizedCcid)) return;
        newEntry.Ccid = normalizedCcid;
        var currentPort = GetPortsSnapshot().FirstOrDefault(port =>
            string.Equals(NormalizeCcid(port.Serial), normalizedCcid, StringComparison.OrdinalIgnoreCase));
        if (currentPort != null) EnrichBackupEntry(newEntry, currentPort);
        lock (_imeiCacheLock)
        {
            _imeiCache[normalizedCcid] = newEntry;
            SaveImeiCache();
        }
    }

    private void UpdateImeiCacheEntry(string ccid, Action<SimBackupEntry> updateAction)
    {
        string normalizedCcid = NormalizeCcid(ccid);
        if (string.IsNullOrEmpty(normalizedCcid)) return;
        var currentPort = GetPortsSnapshot().FirstOrDefault(port =>
            string.Equals(NormalizeCcid(port.Serial), normalizedCcid, StringComparison.OrdinalIgnoreCase));
        lock (_imeiCacheLock)
        {
            if (_imeiCache.TryGetValue(normalizedCcid, out var entry))
            {
                updateAction(entry);
                if (currentPort != null) EnrichBackupEntry(entry, currentPort);
                SaveImeiCache();
            }
        }
    }

    private static void EnrichBackupEntry(SimBackupEntry entry, SimPort port)
    {
        static void CopyIfPresent(string value, Action<string> assign)
        {
            if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
        }

        CopyIfPresent(port.PhoneNumber, value => entry.PhoneNumber = value);
        CopyIfPresent(port.NetworkProvider, value => entry.NetworkProvider = value);
        CopyIfPresent(port.Balance, value => entry.Balance = value);
        CopyIfPresent(port.PromotionBalance, value => entry.PromotionBalance = value);
        CopyIfPresent(port.ExpiryDate, value => entry.ExpiryDate = value);
        CopyIfPresent(port.SimRegDate, value => entry.SimRegDate = value);
        CopyIfPresent(port.Lock1C, value => entry.Lock1C = value);
        CopyIfPresent(port.Lock2C, value => entry.Lock2C = value);
        CopyIfPresent(port.CreatedAt, value => entry.CreatedAt = value);
        CopyIfPresent(port.DeviceName, value => entry.DeviceName = value);
        CopyIfPresent(port.HardwareName, value => entry.HardwareName = value);
        CopyIfPresent(port.ModemManufacturer, value => entry.ModemManufacturer = value);
        CopyIfPresent(port.ModemModel, value => entry.ModemModel = value);
        CopyIfPresent(port.ModemFirmware, value => entry.ModemFirmware = value);
        CopyIfPresent(port.ModemCapabilities, value => entry.ModemCapabilities = value);
        CopyIfPresent(port.Status, value => entry.Status = value);

        entry.LastPortName = port.PortName;
        entry.SignalStrength = port.SignalStrength;
        entry.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (string.IsNullOrWhiteSpace(entry.CreatedAt))
            entry.CreatedAt = entry.UpdatedAt;
    }

    private static void ApplyBackupMetadata(SimPort port, SimBackupEntry entry)
    {
        // Khi khởi động: chỉ khôi phục SĐT từ backup (CCID → SĐT).
        // Các trường động (Balance, NetworkProvider, ExpiryDate, Lock, SimRegDate...)
        // sẽ được fetch mới khi tool đang chạy (USSD/AT+COPS/SMS) và
        // tự lưu ngược vào file backup qua UpdateImeiCacheEntry.
        if (!string.IsNullOrWhiteSpace(entry.PhoneNumber))
            port.PhoneNumber = entry.PhoneNumber;

        // CreatedAt là metadata tĩnh, không thay đổi — giữ lại.
        if (!string.IsNullOrWhiteSpace(entry.CreatedAt))
            port.CreatedAt = entry.CreatedAt;
    }

    public void RemoveImeiCacheEntry(string ccid)
    {
        if (string.IsNullOrEmpty(ccid)) return;
        lock (_imeiCacheLock)
        {
            _imeiCache.TryRemove(ccid, out _);
            SaveImeiCache();
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
        // ICCID is numeric. Accepting arbitrary alphanumeric text here caused modem errors such
        // as "+CME ERROR: SIM failure" to become a fake non-empty CCID ("+CME:SIMfailure"),
        // skipping the CFUN=1 retry and invalidating a SIM that was physically still inserted.
        var match = Regex.Match(ccid, @"\b(\d{18,22})\b");
        if (match.Success) return match.Groups[1].Value;
        return string.Empty;
    }

    private SimBackupEntry? FindImeiBackupEntry(string? rawCcid)
    {
        string ccid = NormalizeCcid(rawCcid);
        if (string.IsNullOrWhiteSpace(ccid)) return null;
        if (_imeiCache.TryGetValue(ccid, out var exact)) return exact;

        // Some legacy exports dropped the final ICCID check digit (19 instead of 20 digits).
        // Accept only a unique one-digit prefix match, then migrate it to the full live ICCID.
        var prefixMatches = _imeiCache
            .Where(pair =>
            {
                string cachedCcid = NormalizeCcid(pair.Key);
                return Math.Abs(cachedCcid.Length - ccid.Length) == 1
                    && (cachedCcid.StartsWith(ccid, StringComparison.Ordinal)
                        || ccid.StartsWith(cachedCcid, StringComparison.Ordinal));
            })
            .ToList();

        if (prefixMatches.Count != 1) return null;

        var legacy = prefixMatches[0];
        lock (_imeiCacheLock)
        {
            if (_imeiCache.TryRemove(legacy.Key, out var migrated))
            {
                migrated.Ccid = ccid;
                migrated.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _imeiCache[ccid] = migrated;
                SaveImeiCache();
                AddLog($"[IMEI_SOURCE] Tự nâng cấp CCID backup thiếu số kiểm tra: {legacy.Key} -> {ccid}.", "SUCCESS");
                return migrated;
            }
        }
        return null;
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
                if (lines.Length <= 1) continue;

                var header = lines[0].Split(',');
                int idxCcid = Array.FindIndex(header, s => s.Trim().Equals("CCID", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("Serial", StringComparison.OrdinalIgnoreCase));
                int idxImei = Array.FindIndex(header, s => s.Trim().Equals("IMEI", StringComparison.OrdinalIgnoreCase));
                int idxPhone = Array.FindIndex(header, s => s.Trim().Equals("PhoneNumber", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("Phone", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("SĐT", StringComparison.OrdinalIgnoreCase));
                int idxCreated = Array.FindIndex(header, s => s.Trim().Equals("CreatedAt", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("Created", StringComparison.OrdinalIgnoreCase));
                int idxRegDate = Array.FindIndex(header, s => s.Trim().Equals("SimRegDate", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("NgayDK", StringComparison.OrdinalIgnoreCase));

                // Fallback to defaults if headers are not found or invalid
                if (idxCcid < 0) idxCcid = 0;
                if (idxImei < 0) idxImei = 1;
                if (idxPhone < 0) idxPhone = 2;
                if (idxCreated < 0) idxCreated = 3;
                if (idxRegDate < 0) idxRegDate = 6;

                // Heuristic detection based on first data row if headers aren't clear or header row is missing/data-like
                var firstDataParts = ParseCsvLine(lines[1]);
                if (firstDataParts.Length >= 2)
                {
                    string col0 = firstDataParts[0].Trim();
                    string col1 = firstDataParts[1].Trim();
                    if (col0.Length >= 14 && col0.Length <= 16 && col0.All(char.IsDigit) && 
                        (col1.StartsWith("89") || col1.Length >= 18))
                    {
                        idxImei = 0;
                        idxCcid = 1;
                    }
                    else if (col1.Length >= 14 && col1.Length <= 16 && col1.All(char.IsDigit) && 
                             (col0.StartsWith("89") || col0.Length >= 18))
                    {
                        idxCcid = 0;
                        idxImei = 1;
                    }
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = ParseCsvLine(line);
                    if (parts.Length > Math.Max(idxCcid, idxImei))
                    {
                        string serial = NormalizeCcid(parts[idxCcid]);
                        string imei = NormalizeImei(parts[idxImei]);
                        string phone = parts.Length > idxPhone ? parts[idxPhone].Trim() : "";
                        string createdAt = parts.Length > idxCreated ? parts[idxCreated].Trim() : "";
                        string simRegDate = (idxRegDate >= 0 && parts.Length > idxRegDate) ? parts[idxRegDate].Trim() : "";

                        if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(imei))
                        {
                            if (_imeiCache.TryGetValue(serial, out var existingEntry))
                            {
                                string normExisting = NormalizeImei(existingEntry.Imei);
                                bool isChanged = normExisting != imei ||
                                                 existingEntry.PhoneNumber != phone ||
                                                 existingEntry.CreatedAt != createdAt ||
                                                 existingEntry.SimRegDate != simRegDate;

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
                                    if (string.IsNullOrWhiteSpace(existingEntry.SimRegDate))
                                    {
                                        existingEntry.SimRegDate = simRegDate;
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
                                    SourceFile = sourceFile,
                                    SimRegDate = simRegDate
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

        _portSessions.InvalidateAll();

        _firebaseService.Stop();
        _firebaseService.Dispose();
        _modemService.DisconnectAll();

        _activeRamRecordings.Clear();
        _activeCallers.Clear();

        _smsService.Dispose();
        _ussdService.Dispose();
        _backgroundSupervisor.Dispose();
        _portSessions.Dispose();
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

        var busyPorts = targetPorts.Where(p => SmsInProgressPorts.ContainsKey(p.PortName)).ToList();
        foreach (var p in busyPorts)
        {
            AddLog($"[{p.PortName}] Bỏ qua lệnh {CommandPanelTab} vì COM đang bận.", "WARN");
        }
        targetPorts = targetPorts.Except(busyPorts).ToList();
        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Tất cả cổng được chọn đang bận.");
            return;
        }

        foreach (var p in targetPorts)
        {
            SmsInProgressPorts[p.PortName] = true;
        }

        SnackbarMessageQueue.Enqueue($"Bắt đầu chạy lệnh {CommandPanelTab}...");

        try
        {
            await Task.WhenAll(targetPorts.Select(async p =>
            {
                if (!_lifetimeCts.Token.IsCancellationRequested)
                {
                    await ExecuteCommandQueueItemAsync(p.PortName, singleItem);
                }
            }));
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

        var busyPorts = targetPorts.Where(p => SmsInProgressPorts.ContainsKey(p.PortName)).ToList();
        foreach (var p in busyPorts)
        {
            AddLog($"[{p.PortName}] Bỏ qua kịch bản vì COM đang bận.", "WARN");
        }
        targetPorts = targetPorts.Except(busyPorts).ToList();
        if (targetPorts.Count == 0)
        {
            SnackbarMessageQueue.Enqueue("Tất cả cổng được chọn đang bận.");
            return;
        }

        foreach (var p in targetPorts)
        {
            SmsInProgressPorts[p.PortName] = true;
        }

        SnackbarMessageQueue.Enqueue("Bắt đầu chạy kịch bản...");

        try
        {
            await Task.WhenAll(targetPorts.Select(async p =>
            {
                foreach (var item in items)
                {
                    if (_lifetimeCts.Token.IsCancellationRequested) break;
                    await ExecuteCommandQueueItemAsync(p.PortName, item);
                }
            }));
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
                port.UpdateDisplayResult(cmdType);
            }
            if (cmdType == "USSD")
            {
                finalResult = await SendUssdThrottledAsync(portName, item.Content, "Kịch bản", maxAttempts: CommandPanelRetryCount + 1);
                if (finalResult.Contains("OK")) finalResult = "Đang chờ nhà mạng phản hồi...";
            }
            else if (cmdType == "SMS")
            {
                finalResult = await QueueSmsAsync(portName, item.Recipient, item.Content, _lifetimeCts.Token);
            }
            else if (cmdType == "Call")
            {
                if (!GsmDestination.TryNormalizeDial(item.Recipient, out string cleanNumber))
                    throw new InvalidOperationException("Địa chỉ gọi không hợp lệ");
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
                        port?.UpdateDisplayResult(cmdType);
                        
                        _callFailures.TryRemove(portName, out _);
                        
                        // Chạy giám sát và phát âm thanh
                        await MonitorAndPlayAudioDuringCallAsync(portName, duration);
                        
                        // Dập máy
                        await _modemService.SendCommandAsync(portName, "ATH", timeoutMs: 5000);
                        
                        if (_callFailures.TryGetValue(portName, out string? failReason))
                        {
                            finalResult = $"Cuộc gọi thất bại ({failReason})";
                        }
                        else
                        {
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
                    if (cmdType == "USSD")
                    {
                        port.LastUssdResult = finalResult;
                        // Hiển thị kết quả USSD từ CommandPanel lên cột "Nội dung"
                        // (kết quả từ +CUSD URC đã được set bởi handler bên trên, trường hợp này là fallback khi finalResult có nội dung)
                        if (!finalResult.Contains("Đang") && !string.IsNullOrWhiteSpace(finalResult))
                        {
                            port.LastMessageContent = "[USSD] " + finalResult;
                            port.Sender = "USSD";
                            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                        }
                    }
                    else if (cmdType == "SMS") port.LastSmsResult = finalResult;
                    else if (cmdType == "Call") port.LastCallResult = finalResult;
                    else if (cmdType == "MMS") port.LastMmsResult = finalResult;
                    else if (cmdType == "IMEI") port.LastImeiResult = finalResult;
                    else if (cmdType == "Data") port.LastDataResult = finalResult;
                    else if (cmdType == "Delay") port.LastDelayResult = finalResult;
                }
                
                port.UpdateDisplayResult(cmdType);
            }

            bool commandFailed = finalResult.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || finalResult.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || finalResult.Contains("Lỗi", StringComparison.OrdinalIgnoreCase)
                || finalResult.Contains("thất bại", StringComparison.OrdinalIgnoreCase);
            Application.Current.Dispatcher.Invoke(() =>
            {
                item.Result = finalResult;
                item.Error = commandFailed ? finalResult : string.Empty;
                item.Status = commandFailed ? "Lỗi" : "Xong";
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                item.Status = "Lỗi";
                item.Error = ex.Message;
                item.Result = ex.Message;
                var failedPort = Ports.FirstOrDefault(p => p.PortName == portName);
                if (failedPort != null && string.Equals(item.Type, "USSD", StringComparison.OrdinalIgnoreCase))
                {
                    failedPort.LastUssdResult = "ERROR: " + ex.Message;
                    failedPort.LastCommandResult = failedPort.LastUssdResult;
                    failedPort.LastMessageContent = "[USSD][THẤT BẠI] " + ex.Message;
                    failedPort.Sender = "USSD";
                    failedPort.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                }
            });
        }
        
        UpdateCommandCounts();
    }

    private async Task ConsumeDataQuectelAsync(string portName, int kilobytes)
    {
        if (_modemService.GetModemProfile(portName)?.Supports(ModemCapability.HttpData) != true)
            throw new NotSupportedException($"Model on {portName} does not support the Quectel HTTP command set.");
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

    private static string TrimStr(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
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


