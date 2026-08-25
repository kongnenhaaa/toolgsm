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
using System.Threading.Channels;
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
    private GsmBackgroundSupervisorContext? _backgroundSupervisorContext;
    private readonly gsm.Services.INotifyService _notifyService = new gsm.Services.NotifyService();
    private readonly gsm.Services.IFirebaseOtpService _firebaseOtpService = new gsm.Services.FirebaseOtpService();
    // SMS history is session-only. It is displayed while ToolGSM is open and
    // is never restored from or written to a local inbox file.
    private readonly SmsInboxStore _smsInboxStore =
        SmsInboxStore.CreateInMemory();
    private const int MaxSmsMessagesInMemory = 5000;
    private const int MaxOtpHistoryInMemory = 2000;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _sentConfirmations = new();
    public IGsmModemService ModemService => _modemService;

    private readonly FirebaseService _firebaseService;
    public ProxyManagerService ProxyManager { get; }
    private readonly ConcurrentDictionary<string, string> _callFailures = new();
    private readonly ConcurrentDictionary<string, string> _activeCallers = new();
    private readonly ConcurrentDictionary<string, SimPort> _stateTrackedPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastLoggedPortStatuses =
        new(StringComparer.OrdinalIgnoreCase);
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
        // SMS có thể về ngay sau khi otp_send được server nhận nhưng trước
        // khi request otp_send trả response về tool. Không được gọi set-pass
        // trong khoảng race này, nếu không VNPT có thể trả "Chưa yêu cầu
        // otp/pin cho dịch vụ này".
        public TaskCompletionSource<bool> OtpSendCompleted { get; } =
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

    }

    private readonly ConcurrentDictionary<string, PendingMyVnptPasswordOperation> _pendingMyVnptPasswordPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private sealed record MyVnptCleanupReadyPort(
        SimPort Port,
        string Ccid,
        long Epoch,
        CancellationToken SimToken);
    private readonly SimSessionSmsCleanupBarrier _initialSmsCleanupBarrier = new();
    private readonly SemaphoreSlim _vnptBatchGate = new(1, 1);
    private const int MaxConcurrentEzSms = 4;
    private readonly SemaphoreSlim _ezSmsGate =
        new(MaxConcurrentEzSms, MaxConcurrentEzSms);
    // Mỗi COM có một workflow và một pending OTP riêng; không dùng khóa toàn cục.

    [ObservableProperty] private int _vnptTotalActiveCount = 0;
    [ObservableProperty] private int _vnptSuccessCount = 0;
    [ObservableProperty] private int _vnptFailCount = 0;
    private readonly object _vnptLock = new object();

    [ObservableProperty]
    private string _vnptSummaryText = string.Empty;

    private void DecrementVnptActiveCount(bool isSuccess)
    {
        int success;
        int fail;
        int remaining;

        lock (_vnptLock)
        {
            if (isSuccess) VnptSuccessCount++;
            else VnptFailCount++;

            VnptTotalActiveCount--;
            if (VnptTotalActiveCount < 0) VnptTotalActiveCount = 0;

            success = VnptSuccessCount;
            fail = VnptFailCount;
            remaining = VnptTotalActiveCount;
        }

        // Never wait for the UI dispatcher while holding _vnptLock. The UI can
        // start/clear another VNPT batch and need the same lock.
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
            bool otpRequestAccepted = await pending.OtpSendCompleted.Task.WaitAsync(
                pending.CancellationToken);
            if (!otpRequestAccepted)
            {
                pending.Completion.TrySetResult(new MyVnptPasswordResult(
                    false,
                    "Không xác nhận được yêu cầu OTP MyVNPT"));
                return;
            }

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

    private async Task<MyVnptCleanupReadyPort?> PrepareMyVnptPortAfterCleanupAsync(
        SimPort port,
        string password,
        CancellationToken batchCancellationToken)
    {
        if (!TryGetCurrentSimSession(
                port.PortName,
                out string ccid,
                out long epoch,
                out CancellationToken simToken))
        {
            RecordMyVnptCleanupFailure(
                port,
                password,
                "Phiên SIM không còn hợp lệ");
            return null;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            batchCancellationToken,
            simToken);
        CancellationToken operationToken = linkedCts.Token;

        try
        {
            if (AppSettings?.AutoClearSmsAfterUssd != false)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    port.VnptStatus = "Đang dọn SMS...";
                    port.LastMessageContent =
                        "Đang chờ xóa sạch SMS trước khi chạy MyVNPT...";
                });
            }

            (bool success, string message) =
                await EnsureInitialSmsCleanupCompletedAsync(
                    port.PortName,
                    ccid,
                    epoch,
                    simToken,
                    operationToken).ConfigureAwait(false);

            operationToken.ThrowIfCancellationRequested();
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                || port.Status != SimStatus.Active)
            {
                throw new OperationCanceledException(operationToken);
            }

            if (AppSettings?.AutoClearSmsAfterUssd != false)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    port.VnptStatus = success
                        ? "Đã dọn SMS"
                        : "Dọn SMS có cảnh báo";
                    port.LastMessageContent = success
                        ? "Đã xác minh SMS = 0; sẵn sàng chạy MyVNPT."
                        : $"Xóa SMS đã kết thúc nhưng chưa xác minh sạch ({message}); vẫn tiếp tục MyVNPT.";
                });

                if (!success)
                {
                    AddLog(
                        $"[{port.PortName}] [VNPT_CLEANUP_WARNING] {message}; tác vụ xóa đã kết thúc, vẫn tiếp tục yêu cầu OTP.",
                        "WARN");
                }
            }

            return new MyVnptCleanupReadyPort(
                port,
                ccid,
                epoch,
                simToken);
        }
        catch (OperationCanceledException)
        {
            string message = IsSimSessionCurrent(port.PortName, ccid, epoch)
                ? "Đã hủy trước khi dọn SMS hoàn tất"
                : "SIM đã thay đổi trong khi dọn SMS";
            RecordMyVnptCleanupFailure(port, password, message);
            return null;
        }
        catch (Exception ex)
        {
            RecordMyVnptCleanupFailure(
                port,
                password,
                $"Lỗi dọn SMS: {ex.Message}");
            return null;
        }
    }

    private void RecordMyVnptCleanupFailure(
        SimPort port,
        string password,
        string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            port.VnptStatus = message;
            port.LastMessageContent = $"Lỗi: {message}";
        });
        AddLog(
            $"[{port.PortName}] [VNPT_CLEANUP_BLOCKED] {message}; không gửi OTP MyVNPT.",
            "ERROR");
        DecrementVnptActiveCount(false);
        AddVnptResult(
            port.PortName,
            port.PhoneNumber,
            password,
            false,
            message);
    }

    private sealed record FileLogEntry(DateTime Timestamp, string Level, string Message);

    private const int MaxUiLogsPerFlush = 64;
    private const int MaxPendingUiLogs = 2048;
    private static readonly TimeSpan SmsUiDispatchTimeout =
        TimeSpan.FromSeconds(15);
    private readonly object _logFileLock = new();
    private readonly Channel<FileLogEntry> _fileLogChannel =
        Channel.CreateBounded<FileLogEntry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly ConcurrentQueue<LogMessage> _pendingUiLogs = new();
    private readonly CancellationTokenSource _logWriterCts = new();
    private Task? _logFileWriterTask;
    private int _uiLogFlushScheduled;
    private int _shutdownStarted;
    private int _modemsDisconnected;
    private bool _disposed;
    
    public event Action<string, string>? OtpReceivedEvent;
    public event Action<string, MudBlazor.Severity>? SnackbarRequested;

    public void ShowToast(string message, MudBlazor.Severity severity = MudBlazor.Severity.Info)
    {
        try
        {
            SnackbarRequested?.Invoke(message, severity);
        }
        catch { }
    }

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly PortCooldownGate _portCooldown = new();

    // Fix #3: Dùng static Random để tránh lỗi seed trùng khi gọi liên tiếp nhanh
    private static readonly Random _rng = new Random();

    // Đánh dấu cổng nào đang có SMS được gửi để USSD tự nhường đường (tránh tranh Semaphore)
    public ConcurrentDictionary<string, bool> SmsInProgressPorts => _smsService.InProgressPorts;

    // Đánh dấu cổng đang khởi tạo SIM để tránh chạy song song trên cùng UART.
    // Lease riêng cho từng lần khởi tạo. Dùng bool khiến tác vụ cũ bị hủy có thể để
    // lại khóa vĩnh viễn hoặc xóa nhầm khóa của phiên SIM mới.
    private readonly ConcurrentDictionary<string, Guid> _initializingPorts = new();
    // Mỗi lần cắm/rút SIM tạo một epoch mới. Mọi tác vụ nhận SIM phải giữ đúng
    // epoch + CCID; tác vụ của SIM cũ không được phép cập nhật SIM mới trên cùng COM.

    // Dữ liệu backup cũ chỉ còn phục vụ màn hình tra cứu/import/export thủ công.
    // Kích hoạt SIM nofake không đọc mapping này và build/publish không tạo file mới.
    private readonly string _imeiCacheFilePath =
        AppPaths.ResolveRuntimeOrAncestorFile("imei_backup.xlsx");
    private readonly string _pendingImeiCacheFilePath =
        AppPaths.ForResolvedFileSibling("imei_backup.xlsx", "imei_backup.pending.xlsx");
    private readonly string _legacyImeiCacheCsvPath =
        AppPaths.ForResolvedFileSibling("imei_backup.xlsx", "imei_backup.csv");
    private static readonly string[] ImeiBackupColumns =
    [
        "CCID", "IMEI"
    ];
    private static readonly string[] ModemImeiBackupColumns =
    [
        "PortName", "IMEI"
    ];
    private ConcurrentDictionary<string, SimBackupEntry> _imeiCache = new();
    public IReadOnlyDictionary<string, SimBackupEntry> ImeiCache => _imeiCache;
    private ConcurrentDictionary<string, ModemImeiBackupEntry> _modemImeiCache =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, ModemImeiBackupEntry> ModemImeiCache => _modemImeiCache;

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

        foreach (SimPort invalidPhonePort in targetPorts.Where(port =>
                     string.IsNullOrWhiteSpace(
                         MyVnptService.NormalizePhone(port.PhoneNumber))))
        {
            AddLog(
                $"[{invalidPhonePort.PortName}] Bỏ qua vì chưa có số điện thoại.",
                "WARN");
        }
        targetPorts = targetPorts
            .Where(port => !string.IsNullOrWhiteSpace(
                MyVnptService.NormalizePhone(port.PhoneNumber)))
            .ToList();

        if (!targetPorts.Any())
        {
            SnackbarMessageQueue.Enqueue("Vui lòng chọn ít nhất 1 cổng (hoặc không có cổng nào thỏa mãn điều kiện) để đặt mật khẩu MyVNPT.");
            return;
        }

        int count = targetPorts.Count;
        lock (_vnptLock)
        {
            VnptSuccessCount = 0;
            VnptFailCount = 0;
            VnptTotalActiveCount = targetPorts.Count;
            if (VnptTotalActiveCount > 0)
            {
                VnptSummaryText = $"MyVNPT: Đang chạy (Thành công: 0, Thất bại: 0, Còn lại: {VnptTotalActiveCount})";
            }
            else
            {
                VnptSummaryText = string.Empty;
            }
        }

        // Global preflight barrier: no selected COM may call any MyVNPT API
        // until every selected COM has finished its initial SMS cleanup attempt.
        // A cleanup warning does not block OTP, but the completed task is retained
        // so delayed post-USSD cleanup cannot wake up later and delete the new OTP.
        AddLog(
            $"[VNPT_CLEANUP_BARRIER] Đang chờ thao tác xóa SMS kết thúc trên {targetPorts.Count} cổng trước khi gọi MyVNPT.",
            "INFO");
        MyVnptCleanupReadyPort?[] cleanupResults = await Task.WhenAll(
            targetPorts.Select(port => PrepareMyVnptPortAfterCleanupAsync(
                port,
                password,
                cancellationToken)));
        List<MyVnptCleanupReadyPort> readyPorts = cleanupResults
            .OfType<MyVnptCleanupReadyPort>()
            .ToList();

        if (readyPorts.Count > 0)
        {
            AddLog(
                $"[VNPT_CLEANUP_BARRIER] Mọi thao tác xóa SMS đã kết thúc trên {readyPorts.Count} cổng hợp lệ; bắt đầu MyVNPT.",
                "SUCCESS");
        }

        var requestTasks = new List<Task>();
        foreach (MyVnptCleanupReadyPort readyPort in readyPorts)
        {
            SimPort port = readyPort.Port;
            string vnptCcid = readyPort.Ccid;
            long vnptEpoch = readyPort.Epoch;
            CancellationToken simToken = readyPort.SimToken;

            requestTasks.Add(Task.Run(async () =>
            {
                bool resultRecorded = false;
                PendingMyVnptPasswordOperation? pending = null;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, simToken);
                var operationToken = linkedCts.Token;
                try
                {
                    operationToken.ThrowIfCancellationRequested();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        port.VnptStatus = "Đang chạy...";
                        port.LastMessageContent = "Đang bắt đầu luồng MyVNPT độc lập...";
                    });
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
                    // Workflow này chạy độc lập; COM khác không phải chờ COM hiện tại.
                    await MyVnptService.SendOtpAsync(
                        apiSession,
                        operationToken,
                        (message, type) => AddLog($"[{port.PortName}] {message}", type));
                    pending.OtpSendCompleted.TrySetResult(true);
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
                        AddLog($"[{port.PortName}] [VNPT_FLOW] otp_send thành công ({modeStr}); COM tiếp tục độc lập.", "INFO");
                    });

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
                        AddLog($"[{port.PortName}] [VNPT_FLOW] Hết hạn chờ OTP sau 3 phút.", "WARN");
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
                        if (port.VnptStatus is "Đang chạy..." or "Kiểm tra TK..." or "Yêu cầu OTP..." or "Đợi tin nhắn...")
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
                    // Unblock a very early SMS if the otp_send workflow fails
                    // or is cancelled before it can publish its success gate.
                    pending?.OtpSendCompleted.TrySetResult(false);
                    if (pending != null)
                    {
                        ((ICollection<KeyValuePair<string, PendingMyVnptPasswordOperation>>)_pendingMyVnptPasswordPorts)
                            .Remove(new KeyValuePair<string, PendingMyVnptPasswordOperation>(port.PortName, pending));
                    }
                }
            }));

            // Keep every COM workflow independent, but stagger the initial
            // VNPT requests like cuibap. Starting 30+ authen_check_account /
            // otp_send calls in the same millisecond causes burst throttling
            // and unstable account-branch responses.
            // Giãn nhịp khởi động giữa các COM để authen_check_account/otp_send
            // không dồn thành burst lên cùng API VNPT. MyVnptService còn có
            // pacing trung tâm cho từng HTTP request.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
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
    public int CooldownPortCount => _portCooldown.ActiveCount;
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
        IGsmBackgroundSupervisor backgroundSupervisor)
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

        LoadImeiCache();
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
        _logFileWriterTask = Task.Run(
            () => RunLogFileWriterAsync(_logWriterCts.Token));
        AppSettings = SettingsService.Current;
        _notifyService.TelegramStatus += NotifyService_TelegramStatus;
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        _modemService.CallIncoming += ModemService_CallIncoming;
        _modemService.CallEnded += ModemService_CallEnded;
        _modemService.DtmfReceived += ModemService_DtmfReceived;
        _modemService.IncomingCallRinging += ModemService_IncomingCallRinging;
        _modemService.IncomingCallEnded += ModemService_IncomingCallEnded;
        _modemService.CallRecordingSaved += ModemService_CallRecordingSaved;

        InitializeHardware();
        OtpHistoryList.Clear();
        
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
        AddLog($"[AT_TRACE] Nhật ký TX/RX UART: {AtCommandTraceLogger.CurrentLogPath}");
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

        _backgroundSupervisorContext = new GsmBackgroundSupervisorContext
        {
            GetPorts = GetPortsSnapshot,
            IsActive = IsActive,
            GetSignalScanIntervalSeconds = () =>
                Math.Clamp(SettingsService.Current.SignalScanIntervalSeconds, 5, 300),
            IsSmsInProgress = portName => _smsService.IsInProgress(portName),
            SetSignalReading = (port, rssi, percent) => Application.Current.Dispatcher.Invoke(() =>
            {
                port.SignalRssi = rssi;
                port.SignalStrength = percent;
                port.LastSignalScanAt = DateTime.Now;
            }),
            MarkSmsSweep = port => Application.Current.Dispatcher.Invoke(() =>
                port.LastSweepTime = DateTime.Now.ToString("HH:mm:ss")),
            Log = AddLog
        };
        // SAuto chỉ có một vòng DataPort sở hữu CPIN/CSQ/COPS. Không khởi động
        // supervisor thứ hai vì nó sẽ bắn thêm CSQ/CMGL ngoài log tham chiếu.
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

    /// <summary>
    /// Updates the operation result shown in the dashboard status column.
    /// The modem state (Active/Chặn SIM/...) remains in <see cref="SimPort.Status"/>;
    /// this short-lived label reports the last USSD, SMS or Call operation.
    /// </summary>
    public void SetOperationStatus(string portName, string operation, bool success)
    {
        string normalizedOperation = operation?.Trim() ?? string.Empty;
        string? status = normalizedOperation.ToUpperInvariant() switch
        {
            "USSD" => success ? "USSD OK" : "USSD Fail",
            "SMS" => success ? "SMS OK" : "SMS Fail",
            "CALL" => success ? "Call OK" : "Call Fail",
            _ => null
        };
        if (status == null) return;

        var dispatcher = Application.Current?.Dispatcher;
        void Update()
        {
            var port = Ports.FirstOrDefault(p =>
                p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port == null) return;
            port.SetOperationStatus(normalizedOperation, success);
        }

        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else _ = dispatcher.InvokeAsync(Update);
    }

    private static bool IsOperationFailureResult(string? result)
    {
        return string.IsNullOrWhiteSpace(result)
            || result.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Lỗi", StringComparison.OrdinalIgnoreCase)
            || result.Contains("thất bại", StringComparison.OrdinalIgnoreCase)
            || result.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || result.Contains("FAIL", StringComparison.OrdinalIgnoreCase);
    }

    private void RecordPortError(string portName, string error, string? operation = null)
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
            if (!string.IsNullOrWhiteSpace(operation))
            {
                port.SetOperationStatus(operation, false);
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
            port.SetOperationStatus("SMS", true);
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

    internal IReadOnlyList<SmsInboxRecord> GetRecentSmsSnapshot(int count) =>
        _smsInboxStore.GetRecent(count);

    public event Action<LogMessage>? LogAdded;

    public void AddLog(string message, string level = "INFO")
    {
        message = TextEncodingNormalizer.RepairMojibake(message);
        DateTime timestamp = DateTime.Now;

        // Never perform file I/O on the WPF dispatcher. A bounded queue keeps
        // a noisy modem from growing memory without making the UI wait.
        if (!ContainsSmsSensitiveLogData(message))
        {
            _fileLogChannel.Writer.TryWrite(
                new FileLogEntry(timestamp, level, message));
        }

        var newLog = new LogMessage
        {
            Time = timestamp.ToString("HH:mm:ss"),
            Level = level,
            Message = message
        };
        _pendingUiLogs.Enqueue(newLog);
        while (_pendingUiLogs.Count > MaxPendingUiLogs)
            _pendingUiLogs.TryDequeue(out _);
        ScheduleUiLogFlush();
    }

    internal static bool ContainsSmsSensitiveLogData(string? message)
    {
        string value = message ?? string.Empty;
        return value.Contains("SMS", StringComparison.OrdinalIgnoreCase)
            || value.Contains("OTP", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ZALO", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TIN NHẮN", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TIN NHAN", StringComparison.OrdinalIgnoreCase)
            || value.Contains("+CMGR", StringComparison.OrdinalIgnoreCase)
            || value.Contains("+CMGL", StringComparison.OrdinalIgnoreCase)
            || value.Contains("+CMT", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleUiLogFlush()
    {
        if (_disposed) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (Interlocked.Exchange(ref _uiLogFlushScheduled, 1) != 0) return;

        try
        {
            _ = dispatcher.InvokeAsync(
                FlushPendingUiLogs,
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch
        {
            Interlocked.Exchange(ref _uiLogFlushScheduled, 0);
        }
    }

    private void FlushPendingUiLogs()
    {
        int flushed = 0;
        while (flushed < MaxUiLogsPerFlush
               && _pendingUiLogs.TryDequeue(out LogMessage? newLog))
        {
            SystemLogs.Insert(0, newLog);
            if (SystemLogs.Count > 500)
                SystemLogs.RemoveAt(SystemLogs.Count - 1);

            LogAdded?.Invoke(newLog);
            flushed++;
        }

        if (flushed > 0)
        {
            OnPropertyChanged(nameof(FilteredLogs));
            OnPropertyChanged(nameof(FilteredLogCount));
        }

        Interlocked.Exchange(ref _uiLogFlushScheduled, 0);
        if (!_pendingUiLogs.IsEmpty)
            ScheduleUiLogFlush();
    }

    private async Task RunLogFileWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _fileLogChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                var batch = new List<FileLogEntry>(128);
                while (batch.Count < 256
                       && _fileLogChannel.Reader.TryRead(out FileLogEntry? entry))
                {
                    batch.Add(entry);
                }

                AppendLogBatch(batch);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is bounded; any remaining entries are best-effort.
        }
        catch
        {
            // Logging must never take down the modem/UI pipeline.
        }
    }

    private void AppendLogBatch(IReadOnlyCollection<FileLogEntry> entries)
    {
        if (entries.Count == 0) return;

        try
        {
            lock (_logFileLock)
            {
                string logFile = AppPaths.ForRuntimeFile("system_log.txt");
                var fi = new FileInfo(logFile);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    string archive = AppPaths.ForRuntimeFile(
                        $"system_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.Move(logFile, archive, overwrite: true);

                    try
                    {
                        var dirInfo = new DirectoryInfo(
                            Path.GetDirectoryName(logFile) ?? string.Empty);
                        foreach (FileInfo oldLog in dirInfo
                                     .GetFiles("system_log_*.txt")
                                     .OrderByDescending(f => f.CreationTime)
                                     .Skip(5)
                                     .ToList())
                        {
                            oldLog.Delete();
                        }
                    }
                    catch { }
                }

                var content = new StringBuilder();
                foreach (FileLogEntry entry in entries)
                {
                    content.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                        .Append(" [")
                        .Append(entry.Level)
                        .Append("] ")
                        .Append(entry.Message)
                        .AppendLine();
                }

                File.AppendAllText(
                    logFile,
                    content.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // A locked/unavailable log file must not block the application.
        }
    }

    private void AttachPortStateLogging(SimPort port)
    {
        if (_stateTrackedPorts.TryGetValue(
                port.PortName, out SimPort? previousPort))
        {
            if (ReferenceEquals(previousPort, port)) return;
            previousPort.PropertyChanged -= PortState_PropertyChanged;
        }

        _stateTrackedPorts[port.PortName] = port;
        _lastLoggedPortStatuses[port.PortName] = port.Status;
        port.PropertyChanged += PortState_PropertyChanged;
        AddLog(
            $"[{port.PortName}] [UI_STATE] initial=\"{StateLogValue(port.Status)}\"; ccid={NormalizeCcid(port.Serial)}; imei={NormalizeImei(port.Imei)}",
            "STATE");
    }

    private void PortState_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (sender is not SimPort port
            || !string.Equals(
                eventArgs.PropertyName,
                nameof(SimPort.Status),
                StringComparison.Ordinal))
        {
            return;
        }

        string current = port.Status ?? string.Empty;
        string previous = _lastLoggedPortStatuses.TryGetValue(
            port.PortName, out string? oldStatus)
                ? oldStatus
                : string.Empty;
        _lastLoggedPortStatuses[port.PortName] = current;
        if (string.Equals(previous, current, StringComparison.Ordinal)) return;

        AddLog(
            $"[{port.PortName}] [UI_STATE] old=\"{StateLogValue(previous)}\"; new=\"{StateLogValue(current)}\"; ccid={NormalizeCcid(port.Serial)}; imei={NormalizeImei(port.Imei)}; detail=\"{StateLogValue(port.DeviceName)}\"",
            "STATE");
    }

    private static string StateLogValue(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal)
            .Trim();

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

    // Add the decoded SMS to the current session before acknowledging the
    // modem. Nothing in this path is persisted to an SMS history file.
    private bool TryAddSmsToSession(
        SmsInboxRecord record,
        out SmsMessage? message)
    {
        message = null;

        _smsInboxStore.Append(record);

        // A retry during this process must not duplicate the UI row or repeat
        // sounds, webhooks, OTP history or carrier-state side effects.
        if (SmsMessages.Any(existing =>
                string.Equals(existing.DeliveryId, record.DeliveryId, StringComparison.Ordinal)))
            return true;

        message = ToSmsMessage(record);
        InsertSmsMessageBounded(message);

        return true;
    }

    private void InsertSmsMessageBounded(SmsMessage message)
    {
        DateTimeOffset messageTime = GetSmsDisplayTime(message);
        int insertAt = 0;
        while (insertAt < SmsMessages.Count
               && GetSmsDisplayTime(SmsMessages[insertAt]) >= messageTime)
        {
            insertAt++;
        }
        SmsMessages.Insert(insertAt, message);
        while (SmsMessages.Count > MaxSmsMessagesInMemory)
            SmsMessages.RemoveAt(SmsMessages.Count - 1);
    }

    internal static DateTimeOffset GetSmsDisplayTime(SmsMessage message) =>
        message.ReceivedAtUtc != default
            ? message.ReceivedAtUtc
            : message.SmsTimestampUtc ?? DateTimeOffset.MinValue;

    internal static void ApplyReceivedSmsToPort(
        SimPort? port,
        string senderPhone,
        string extractedOtp,
        string displayContent,
        DateTimeOffset receivedAtUtc)
    {
        if (port == null) return;

        port.Sender = senderPhone;
        port.LastSmsSender = senderPhone;
        // An ordinary SMS must not replace the most recent OTP with "N/A".
        if (!string.IsNullOrWhiteSpace(extractedOtp)
            && !string.Equals(extractedOtp, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            port.Otp = extractedOtp;
        }
        port.LastMessageContent = displayContent;
        port.LastReceivedTime = receivedAtUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    internal static bool CanAcknowledgeSmsDelivery(
        bool inboxRecorded,
        string deliveryId,
        IEnumerable<SmsMessage> messages) =>
        inboxRecorded
        && !string.IsNullOrWhiteSpace(deliveryId)
        && messages.Any(message => string.Equals(
            message.DeliveryId,
            deliveryId,
            StringComparison.Ordinal));

    private void InsertOtpHistoryBounded(Services.OtpRecord record)
    {
        OtpHistoryList.Insert(0, record);
        while (OtpHistoryList.Count > MaxOtpHistoryInMemory)
            OtpHistoryList.RemoveAt(OtpHistoryList.Count - 1);
    }

    private static SmsMessage ToSmsMessage(SmsInboxRecord record) => new()
    {
        DeliveryId = record.DeliveryId,
        ReceivedAtUtc = record.ReceivedAtUtc,
        SmsTimestampUtc = record.SmsTimestampUtc,
        PortName = record.PortName,
        ReceivedTime = (record.SmsTimestampUtc ?? record.ReceivedAtUtc)
            .ToLocalTime().ToString("HH:mm:ss"),
        Content = record.Content,
        Sender = record.Sender,
        Otp = record.Otp,
        ReceiverPhone = record.ReceiverPhone,
        NetworkProvider = record.NetworkProvider,
        Status = record.Status,
        CallCount = record.CallCount,
        ForwardContent = record.ForwardContent
    };

    // ========== OTP HISTORY COMMANDS ==========

    [RelayCommand]
    private void LoadOtpHistory()
    {
        OnPropertyChanged(nameof(FilteredOtpHistory));
        OnPropertyChanged(nameof(FilteredOtpHistoryCount));
        SnackbarMessageQueue.Enqueue("Lịch sử OTP chỉ tồn tại trong phiên hiện tại.");
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
                    var removedPorts = Ports.Where(p =>
                        !availablePorts.Contains(p.PortName)
                        && p.PortName != "COM_VIRTUAL").ToList();
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
                // A PORT_BUSY event creates a diagnostic row even though the
                // service did not acquire the COM handle. Checking UI rows here
                // made that port permanently ineligible for automatic retry and
                // forced the user to reload the app. Retry every unopened,
                // physically present port; GsmModemService applies per-port
                // backoff so a busy driver is not hammered.
                if (availablePorts.Any(portName =>
                        !_modemService.IsPortOpen(portName)))
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
                string result = await SendEzSmsBoundedAsync(
                    port.PortName,
                    "DK EZ",
                    _lifetimeCts.Token);
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

    private async Task<string> SendEzSmsBoundedAsync(
        string portName,
        string message,
        CancellationToken ct)
    {
        await _ezSmsGate.WaitAsync(ct);
        try
        {
            return await _smsService.SendAsync(portName, "888", message, ct);
        }
        finally
        {
            _ezSmsGate.Release();
        }
    }

    private async Task SendEzConfirmationAsync(
        string portName,
        SimPort? port,
        string confirmMessage)
    {
        try
        {
            string result = await SendEzSmsBoundedAsync(
                portName,
                confirmMessage,
                _lifetimeCts.Token);
            if (result.Contains("ERROR") || result.Contains("TIMEOUT"))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (port != null) port.LastMessageContent = $"Lỗi xác nhận EZ: {result}";
                });
                AddLog($"[{portName}] Lỗi gửi xác nhận EZ: {result}", "ERROR");
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (port != null) port.LastMessageContent = "Đã xác nhận EZ! Chờ KQ từ 888...";
                });
                AddLog($"[{portName}] Đã xác nhận EZ thành công!", "SUCCESS");
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown or workflow cancellation; no SMS was retried.
        }
        catch (Exception ex)
        {
            AddLog($"[{portName}] Lỗi gửi xác nhận EZ: {ex.Message}", "ERROR");
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
            _ = RefreshPortsAsync(targetPorts.Select(p => p.PortName));
        }
        else
        {
            SnackbarMessageQueue.Enqueue("Đang làm mới toàn bộ thiết bị...");
            AddLog("Đọc lại dữ liệu toàn bộ thiết bị; giữ nguyên COM, SIM và sóng...");
            RefreshAllPorts();
        }
    }

    public Task RefreshPortAsync(
        string portName,
        CancellationToken cancellationToken = default) =>
        RefreshPortsAsync([portName], cancellationToken);

    public void RefreshAllPorts() => _ = RefreshPortsAsync(GetPortsSnapshot().Select(p => p.PortName));

    public async Task RefreshPortsAsync(
        IEnumerable<string> portNames,
        CancellationToken cancellationToken = default)
    {
        var names = portNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return;

        AddLog($"Đang đọc lại trạng thái trực tiếp của {names.Count} cổng; giữ nguyên COM và phiên SIM...");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            cancellationToken);
        try
        {
            bool[] refreshed = await Task.WhenAll(names.Select(name =>
                RefreshLivePortStateAsync(name, linkedCts.Token)));
            var failedPorts = names.Where((_, index) => !refreshed[index]).ToList();
            if (failedPorts.Count > 0)
            {
                AddLog(
                    $"Chưa đọc được trạng thái mới: {string.Join(", ", failedPorts)}; giữ nguyên trạng thái hiện tại.",
                    "WARN");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"Làm mới cổng thất bại: {ex.Message}", "ERROR");
        }
    }

    private async Task<bool> RefreshLivePortStateAsync(
        string portName,
        CancellationToken token)
    {
        // Refresh is read-only for modem lifetime: never close/reopen COM,
        // invalidate the active SIM session, reset RF, or clear the current UI.
        using IDisposable backgroundLease =
            _modemService.SuspendPortBackgroundOperations(portName);

        string cpin = await _modemService.SendCommandAsync(
            portName,
            "AT+CPIN?",
            5000,
            silent: true,
            ct: token);
        if (!cpin.Contains(
                "+CPIN: READY",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentPort = GetPortsSnapshot().FirstOrDefault(p =>
            p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
        if (currentPort != null
            && string.IsNullOrWhiteSpace(ImeiProbe.ExtractImei(currentPort.Imei)))
        {
            string liveImei = await ReadLiveImeiAsync(
                portName,
                token,
                attempts: 3);
            if (!string.IsNullOrWhiteSpace(liveImei))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var livePort = Ports.FirstOrDefault(p =>
                        p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
                    if (livePort == null) return;
                    livePort.Imei = liveImei;
                    if (string.IsNullOrWhiteSpace(livePort.DeviceName)
                        || livePort.DeviceName.Contains(
                            "GSM Modem",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        livePort.DeviceName =
                            Services.ImeiManagementService.GetDeviceNameFromImei(liveImei);
                    }
                });
                AddLog(
                    $"[{portName}] [IMEI_REFRESHED] Đã đọc lại IMEI {liveImei}.",
                    "SUCCESS");
            }
            else
            {
                AddLog(
                    $"[{portName}] [IMEI_READ_FAILED] AT+CGSN/AT+GSN không trả IMEI hợp lệ sau 3 lần; giữ nguyên kết nối SIM.",
                    "WARN");
            }
        }

        await _modemService.SendCommandAsync(
            portName,
            "AT+ICCID",
            5000,
            silent: true,
            ct: token);
        await _modemService.SendCommandAsync(
            portName,
            "AT+CSQ",
            5000,
            silent: true,
            ct: token);
        await _modemService.SendCommandAsync(
            portName,
            "AT+COPS?",
            5000,
            silent: true,
            ct: token);
        return true;
    }

    private Task<string> ReadLiveImeiAsync(
        string portName,
        CancellationToken ct,
        int attempts = 3) =>
        ImeiProbe.ReadAsync(
            (command, token) => _modemService.SendCommandAsync(
                portName,
                command,
                4000,
                silent: true,
                ct: token),
            attempts,
            TimeSpan.FromMilliseconds(350),
            ct);

    private (long Epoch, CancellationToken Token) StartSimSession(string portName, string ccid)
    {
        var session = _portSessions.Begin(portName, ccid, _lifetimeCts.Token);
        return (session.Epoch, session.Token);
    }

    private void InvalidateSimSession(string portName)
    {
        // Tắt cờ của phiên cũ khi SIM bị mất/thay; cờ sẽ được bật lại ngay khi
        // pipeline đọc được CCID của SIM mới.
        _portSessions.Invalidate(portName);
        _initializingPorts.TryRemove(portName, out _);
        _initialSmsCleanupBarrier.RemovePort(portName);
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

    public bool TryGetCurrentSimSessionIdentity(
        string portName,
        string expectedCcid,
        out long epoch)
    {
        epoch = 0;
        return TryGetCurrentSimSession(
                portName,
                out string ccid,
                out epoch,
                out _)
            && string.Equals(
                NormalizeCcid(ccid),
                NormalizeCcid(expectedCcid),
                StringComparison.Ordinal);
    }

    public async Task<bool> VerifyPhysicalCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken cancellationToken = default)
    {
        string expected = NormalizeCcid(expectedCcid);
        if (expected.Length != 20) return false;
        using IDisposable backgroundLease =
            _modemService.SuspendPortBackgroundOperations(portName);
        try
        {
            string live = await ReadLiveCcidAsync(
                portName, cancellationToken, attempts: 3);
            bool matches = HasExactLiveCcidEvidence(live, expected);
            if (!matches)
            {
                AddLog(
                    $"[{portName}] [BULK_PHYSICAL_CCID_FAILED] expected={expected}; live={NormalizeCcid(live)}",
                    "ERROR");
            }
            return matches;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog(
                $"[{portName}] [BULK_PHYSICAL_CCID_FAILED] expected={expected}; error={ex.Message}",
                "ERROR");
            return false;
        }
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

        return await RefreshLivePortStateAsync(portName, _lifetimeCts.Token);
    }

    public async Task<bool> ReloadPortSafelyAsync(string portName, string progressText = "Đang tải lại SIM...")
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null) return false;

        try
        {
            // Nofake never reloads/reboots an active modem. Keep its session and
            // radio registration, then refresh only live read-only values.
            return await RefreshLivePortStateAsync(
                portName,
                _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AddLog($"[{portName}] Đọc lại trạng thái modem thất bại: {ex.Message}", "ERROR");
            return false;
        }
    }


    private async Task<string> ReadLiveCcidAsync(
        string portName, CancellationToken ct, int attempts = 3)
    {
        attempts = Math.Max(1, attempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (string command in new[] { "AT+QCCID", "AT+ICCID" })
            {
                string raw = await _modemService.SendCommandAsync(
                    portName, command, 4000, silent: true, ct: ct);
                string ccid = NormalizeCcid(raw);
                if (!string.IsNullOrWhiteSpace(ccid)) return ccid;
            }

            if (attempt < attempts) await Task.Delay(500, ct);
        }

        return string.Empty;
    }

    private async Task<bool> ActivateDetectedSimWithoutImeiAsync(
        SimPort port,
        string ccid,
        long epoch,
        CancellationToken token,
        Guid initializationLease)
    {
        string portName = port.PortName;
        string currentImei = string.Empty;
        string displayImei = string.Empty;
        bool pollingReady = false;
        if (!IsSimSessionCurrent(portName, ccid, epoch))
        {
            EndPortInitialization(portName, initializationLease);
            return false;
        }

        IDisposable backgroundLease =
            _modemService.SuspendPortBackgroundOperations(portName);

        try
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return false;

            displayImei = ImeiProbe.ExtractImei(
                _modemService.GetObservedImei(portName));
            if (string.IsNullOrWhiteSpace(displayImei))
                displayImei = ImeiProbe.ExtractImei(port.Imei);
            if (string.IsNullOrWhiteSpace(displayImei))
            {
                displayImei = await ReadLiveImeiAsync(
                    portName,
                    token,
                    attempts: 3);
            }

            // Always display the exact 15 digits reported by the modem. Only a
            // Luhn-usable value is used as a network-recovery expectation.
            currentImei = Services.ImeiManagementService.IsUsableObservedImei(displayImei)
                ? displayImei
                : string.Empty;

            if (!IsSimSessionCurrent(portName, ccid, epoch)) return false;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                port.IsRebooting = false;
                port.Serial = NormalizeCcid(ccid);
                if (!string.IsNullOrWhiteSpace(displayImei))
                    port.Imei = displayImei;
                MarkPortReadyForNetwork(portName);
            });
            pollingReady = IsSimSessionCurrent(portName, ccid, epoch);
            if (pollingReady)
            {
                AddLog(
                    $"[{portName}] [SIM_AUTO_ACCEPT] CCID={NormalizeCcid(ccid)}; IMEI hiện có={(string.IsNullOrWhiteSpace(displayImei) ? "không đọc được" : displayImei)}; không tạo/khôi phục IMEI.",
                    "SUCCESS");
            }
            return pollingReady;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AddLog(
                $"[{portName}] [SIM_AUTO_ACCEPT] Lỗi khởi tạo: {ex.Message}",
                "WARN");
            await SetSimAutoAcceptFailureAsync(port, ccid, epoch, ex.Message);
            return false;
        }
        finally
        {
            EndPortInitialization(portName, initializationLease);
            backgroundLease.Dispose();
            if (pollingReady)
                _modemService.StartPollingNetwork(portName, ccid, currentImei);
        }
    }

    private async Task SetSimAutoAcceptFailureAsync(
        SimPort port,
        string ccid,
        long epoch,
        string message)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
            port.IsRebooting = false;
            port.Status = SimStatus.NoResponse;
            port.DeviceName = "SIM đã được nhận, đang chờ kết nối mạng...";
            port.LastError = message;
            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            UpdateDashboard();
        });
    }

    internal static void ClearSimScopedState(
        SimPort port,
        string? currentModemImei = null)
    {
        string preservedImei = ImeiProbe.ExtractImei(currentModemImei);

        // Chỉ giữ thông tin vật lý của COM (PortName/HardwareName/STT và bộ đếm health).
        // Mọi dữ liệu dưới đây thuộc SIM cũ và tuyệt đối không được hiển thị sau khi rút/thay SIM.
        port.IsRebooting = false;
        port.SautoStatus = string.Empty;
        port.UssdStatus = string.Empty;
        port.SmsStatus = string.Empty;
        port.CallStatus = string.Empty;
        port.PhoneNumber = string.Empty;
        port.NetworkProvider = string.Empty;
        port.NetworkType = string.Empty;
        // IMEI thuộc modem, không thuộc SIM. Nhánh NoSIM truyền vào giá trị vừa
        // đọc từ slot 7 để UI vẫn hiển thị đúng IMEI hiện tại khi CCID đã bị xóa.
        port.Imei = preservedImei;
        port.Serial = string.Empty;
        port.Balance = string.Empty;
        port.IsBalanceLoading = false;
        port.PromotionBalance = string.Empty;
        port.ExpiryDate = string.Empty;
        port.CreatedAt = string.Empty;
        port.UpdatedAt = string.Empty;
        port.SimRegDate = string.Empty;
        port.SimType = string.Empty;
        port.Lock1C = string.Empty;
        port.Lock2C = string.Empty;
        port.ForwardedTo = string.Empty;
        port.ForwardCount = 0;
        port.CallCount = 0;
        port.SignalStrength = 0;
        port.SignalRssi = 99;
        port.LastSignalScanAt = null;
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
            bool isInternalEvent = e.Data.StartsWith("[PARSE_")
                || e.Data == "[STATUS_ACTIVE]";
            bool isHighFrequencyTelemetry = e.Data.StartsWith(
                "+CSQ:",
                StringComparison.OrdinalIgnoreCase);
            // Keep the live signal row updated below without flooding the UI
            // and log table with every five-second RSSI sample. The raw UART
            // trace still records the complete command cadence.
            if (!isInternalEvent && !isHighFrequencyTelemetry)
                AddLog($"[{e.PortName}] {e.Data}");

            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);

            if (port == null)
            {
                if (e.Data == "[PORT_OPENED]" || e.Data.StartsWith("[PORT_BUSY]") || e.Data.StartsWith("[STATUS_SIM_LOCKED]") || e.Data.StartsWith("[STATUS_SIM_READY]") || e.Data.StartsWith("[PARSE_CCID]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+COPS:") || e.Data.StartsWith("+CUSD:") || e.Data.StartsWith("[NO_SIM_READY]") || e.Data.StartsWith("[WAITING_FOR_SIM]") || e.Data.StartsWith("[PARSE_IMEI]") || e.Data.StartsWith("[STATUS_NO_RESPONSE]") || e.Data.StartsWith("[NETWORK_WAITING]") || e.Data.StartsWith("Lỗi kết nối"))
                {
                    port = new SimPort { PortName = e.PortName, Status = "Chờ cắm SIM", SignalStrength = 0 };
                    port.PhysicalIndex = _modemService.GetAvailablePorts().IndexOf(e.PortName);
                    if (port.PhysicalIndex < 0) port.PhysicalIndex = int.MaxValue;
                    port.ReconnectCount++;
                    AttachPortStateLogging(port);

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
                ClearSimScopedState(
                    port,
                    _modemService.GetObservedImei(e.PortName));
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang kiểm tra modem/SIM...";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_SIM_LOCKED]"))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(
                    port,
                    _modemService.GetObservedImei(e.PortName));
                port.Status = e.Data.Contains("PUK", StringComparison.OrdinalIgnoreCase) ? "SIM yêu cầu PUK" : "SIM yêu cầu PIN";
                port.DeviceName = "SIM đang bị khóa";
                port.LastError = port.Status;
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_SIM_READY]"))
            {
                if (port.Status != SimStatus.Active)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đã nhận SIM, đang đọc CCID...";
                    port.LastError = string.Empty;
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                }
            }
            else if (e.Data.StartsWith("[NO_SIM_READY]"))
            {
                string observedImei = Regex.Match(
                    e.Data, @"(?<!\d)\d{15}(?!\d)").Value;
                if (!string.IsNullOrWhiteSpace(observedImei))
                    port.Imei = observedImei;
            }
            else if (e.Data.StartsWith("[WAITING_FOR_SIM]"))
            {
                // Polling progress is not proof that a live SIM was removed.
                if (port.Status != SimStatus.Active
                    && string.IsNullOrWhiteSpace(port.Serial))
                {
                    port.Status = "Chờ cắm SIM";
                    port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                    UpdateDashboard();
                }
            }
            else if (e.Data.StartsWith("[PORT_BUSY]", StringComparison.Ordinal))
            {
                // PORT_BUSY is a transient transport detail, not a fourth SIM
                // state. Keep it in diagnostics and let the watcher retry the
                // unopened handle automatically.
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Windows chưa cấp quyền mở COM; đang tự thử lại...";
                port.LastError = e.Data["[PORT_BUSY]".Length..].Trim();
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("Lỗi kết nối"))
            {
                port.Status = SimStatus.NoResponse;
                port.DeviceName = "Lỗi kết nối";
                port.LastError = e.Data;
            }
            else if (e.Data.StartsWith("[STATUS_SIM_REMOVED]"))
            {
                InvalidateSimSession(e.PortName);
                _modemService.SetSmsSimIdentity(e.PortName, null);
                ClearSimScopedState(
                    port,
                    _modemService.GetObservedImei(e.PortName));
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[SIM_CONTACT_ERROR]"))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(
                    port,
                    _modemService.GetObservedImei(e.PortName));
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "COM sống – modem không đọc được chip SIM";
                port.LastError = "Kiểm tra chiều SIM, tiếp điểm hoặc thử SIM khác trên cùng khe";
                port.SignalStrength = 0;
                UpdateDashboard();
            }
            else if (!_modemService.IsCallInProgress(e.PortName)
                // NOT READY/CME 10/13/11 are transient on this modem family.
                // Only an explicit NOT INSERTED indication clears the UI.
                && (e.Data.Contains("+CPIN: NOT INSERTED") || e.Data.Contains("SIM not inserted")))
            {
                InvalidateSimSession(e.PortName);
                ClearSimScopedState(
                    port,
                    _modemService.GetObservedImei(e.PortName));
                port.Status = "Chờ cắm SIM";
                port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                _modemService.StartHotplugWaitLoop(e.PortName);
                UpdateDashboard();
            }
            else if (e.Data.Contains("+CSQ:"))
            {
                if (TryParseCsqResponse(e.Data, out int rssi, out int percent))
                {
                    port.SignalRssi = rssi;
                    port.SignalStrength = percent;
                    port.LastSignalScanAt = DateTime.Now;
                }
            }
            else if (e.Data.StartsWith("[NETWORK_TYPE]", StringComparison.Ordinal))
            {
                if (!TryGetCurrentSimSession(e.PortName, out _, out _, out _))
                    return;
                port.NetworkType = e.Data.Replace("[NETWORK_TYPE]", string.Empty).Trim();
            }
            else if (e.Data.StartsWith("[NETWORK_FALLBACK]", StringComparison.Ordinal))
            {
                bool sessionCurrent = TryGetCurrentSimSession(
                    e.PortName, out _, out _, out _);
                if (!sessionCurrent) return;

                Match typeMatch = Regex.Match(e.Data, @"\btype=([^;\s]+)", RegexOptions.IgnoreCase);
                string provider = ResolveNetworkProviderFromCcid(port.Serial);
                if (!string.IsNullOrWhiteSpace(provider))
                    port.NetworkProvider = provider;
                // Some EC20 firmware returns a valid registered operator
                // without the optional access-technology field. COPS is
                // still sufficient to start the SAuto lookup; keep a neutral
                // network label so the missing ACT does not strand this COM.
                if (string.IsNullOrWhiteSpace(port.NetworkType))
                    port.NetworkType = "Mạng";
                if (typeMatch.Success)
                    port.NetworkType = typeMatch.Groups[1].Value.Trim();
                // The fallback response is a valid network registration result.
                // Promote a port that was waiting for COPS back to Active before
                // starting the initial live lookup.
                // A late fallback URC cannot race the SIM activation lease.
                if (CanPromoteNetworkRegistration(
                    port,
                    _initializingPorts.ContainsKey(e.PortName),
                    sessionCurrent))
                {
                    MarkPortNetworkActive(e.PortName);
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
                    bool isMenu = Regex.IsMatch(ussdContent, @"1\..*?2\.|bam so|chon", RegexOptions.IgnoreCase);

                    var strictMatch = Regex.Match(ussdContent, @"(?:TK\s*goc|TKG|TK\s*chinh|TKC|Tai khoan chinh|Tài khoản chính|Tai khoan|Tài khoản|So du|Số dư|TK|balance)[^\d]{0,20}(\d+[\.\,]\d+|\d+)\s*(d|đ|vnd|vnđ|dong|đồng)?", RegexOptions.IgnoreCase);
                    if (strictMatch.Success) 
                    {
                        string rawVal = strictMatch.Groups[1].Value.Replace(".", "").Replace(",", "");
                        // Reject số dư < 100 VND để tránh parse nhầm cước phí hoặc menu (vd: "1.TK 2.Goi cuoc")
                        if (int.TryParse(rawVal, out int parsedBal) && (parsedBal >= 100 || (!ussdHasAdKeywords && !isMenu)))
                        {
                            port.Balance = strictMatch.Groups[1].Value;
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
                                    port.Balance = fallback.Groups[1].Value;
                            }
                        }
                    }

                    string promotionBalance = ExtractPromotionBalanceFromUssd(ussdContent);
                    if (!string.IsNullOrWhiteSpace(promotionBalance))
                        port.PromotionBalance = promotionBalance;

                    // Hiển thị kết quả USSD trên cột "Nội dung" trong bảng COM ngay lập tức
                    port.LastMessageContent = "[USSD] " + ussdContent;
                    port.Sender = "USSD";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");

                    string foundNumber = ExtractPhoneNumberFromUssd(ussdContent);
                    if (!string.IsNullOrWhiteSpace(foundNumber))
                    {
                        port.PhoneNumber = foundNumber;
                        UpdateSmsReceiverPhone(port.PortName, foundNumber);
                        string networkLabel = string.IsNullOrWhiteSpace(port.NetworkProvider) ? "UNKNOWN" : port.NetworkProvider;
                        AddLog($"[{e.PortName}] SĐT chuẩn: {foundNumber} ({networkLabel})", "SUCCESS");
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
                        if (genericExpiryMatch.Success) 
                        {
                            string matchedDate = genericExpiryMatch.Groups[1].Value;
                            // Tránh lấy nhầm Ngay KH làm HSD
                            if (!Regex.IsMatch(ussdContent, @"(?i)(?:Ngay\s*KH|Ngay\s*kich\s*hoat|Ngay\s*DK|Ngay\s*dang\s*ky)[^\d]{0,15}" + Regex.Escape(matchedDate)))
                            {
                                port.ExpiryDate = matchedDate;
                            }
                        }
                    }

                    // 2. Ngay KH (Ngày kích hoạt / Đăng ký SIM)
                    string regDate = ExtractSimRegDateFromUssd(ussdContent);
                    if (!string.IsNullOrWhiteSpace(regDate))
                    {
                        port.SimRegDate = regDate;
                    }

                    // Lấy loại SIM (ví dụ: VINA690, VINACARD)
                    var simTypeMatch = Regex.Match(ussdContent, @"So\s*TB\s*\d+\s*\(\s*([A-Za-z0-9]+)\s*\)", RegexOptions.IgnoreCase);
                    if (simTypeMatch.Success)
                    {
                        port.SimType = simTypeMatch.Groups[1].Value.Trim().ToUpper();
                    }

                    // 3. Khoa 1C (Khóa 1 chiều)
                    var lock1cMatch = Regex.Match(ussdContent, @"(?:Khoa\s*1C|Khoa\s*mot\s*chieu)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (lock1cMatch.Success)
                    {
                        string lock1cVal = lock1cMatch.Groups[1].Value;
                        port.Lock1C = lock1cVal;
                    }

                    // 4. Khoa 2C (Khóa 2 chiều)
                    var lock2cMatch = Regex.Match(ussdContent, @"(?:Khoa\s*2C|Khoa\s*hai\s*chieu)[^\d]{0,15}(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})", RegexOptions.IgnoreCase);
                    if (lock2cMatch.Success)
                    {
                        string lock2cVal = lock2cMatch.Groups[1].Value;
                        port.Lock2C = lock2cVal;
                    }

                    UpdateDashboard(); // Refresh online/offline count when Balance is updated

                    TriggerAutoClearSmsAfterUssd(e.PortName);

                    // SnackbarMessageQueue.Enqueue($"[{e.PortName}] USSD: {ussdContent}");
                }
            }
            else if (e.Data.Contains("+COPS:"))
            {
                // Reject a stale COPS response before it can mutate provider/type
                // on a row that already belongs to a different SIM session.
                if (!TryGetCurrentSimSession(
                    port.PortName,
                    out var activeCcid,
                    out var activeEpoch,
                    out var activeToken))
                {
                    return;
                }

                string sautoCarrier = GsmModemService.ResolveSautoCarrier(e.Data);
                if (!string.Equals(sautoCarrier, "No Signal", StringComparison.Ordinal))
                {
                    port.NetworkProvider = NormalizeNetworkProvider(sautoCarrier);

                    // COPS is the point at which radio registration is complete.
                    // A port may have been downgraded to Connecting while waiting
                    // for COPS; promote it here so the configured startup USSD can start.
                    // Do not let a stale COPS response race the foreground
                    // SIM activation lease and make the UI look online early.
                    if (CanPromoteNetworkRegistration(
                        port,
                        _initializingPorts.ContainsKey(e.PortName),
                        sessionCurrent: true))
                    {
                        MarkPortNetworkActive(e.PortName);
                    }
                    if (port.Status != SimStatus.Active) return;

                    _ = Task.Run(async () => 
                    {
                        try
                        {
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

                        // The SAuto activation path does not query CCFC. Only touch call
                        // forwarding when that independent feature is explicitly enabled;
                        // otherwise every periodic COPS response would inject AT+CCFC=0,2
                        // into the activation/USSD flow and contend with startup USSD.
                        if (AppSettings != null
                            && AppSettings.EnableAutoCallForwarding
                            && IsSimSessionCurrent(port.PortName, activeCcid, activeEpoch)
                            && port.Status == SimStatus.Active)
                        {
                            string ccfcStatus = await _modemService.SendCommandAsync(
                                port.PortName, "AT+CCFC=0,2", timeoutMs: 8000);
                            var ccfcMatch = Regex.Match(ccfcStatus, @"\+CCFC:\s*1,\s*1,\s*""([^""]+)""");
                            if (ccfcMatch.Success)
                            {
                                string activeFwd = ccfcMatch.Groups[1].Value;
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    port.ForwardedTo = activeFwd;
                                });
                            }
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
                else
                {
                    port.NetworkProvider = "No Signal";
                }
            }
            else if (e.Data.StartsWith("[SAUTO_AUTO_USSD_RESULT]", StringComparison.Ordinal))
            {
                if (e.Data.Contains("completed=True", StringComparison.OrdinalIgnoreCase))
                {
                    TriggerAutoClearSmsAfterUssd(e.PortName);
                }
            }
            else if (e.Data.StartsWith("[PARSE_IMEI]"))
            {
                string imei = ImeiProbe.ExtractImei(e.Data);
                if (!string.IsNullOrWhiteSpace(imei)) port.Imei = imei;
            }
            else if (e.Data.StartsWith("[PARSE_CCID]"))
            {
                var match = Regex.Match(e.Data, @"\b(\d{18,22})\b");
                if (match.Success)
                {
                    string ccid = NormalizeCcid(match.Groups[1].Value);
                    bool currentSessionMatches =
                        TryGetCurrentSimSession(
                            e.PortName,
                            out string sessionCcid,
                            out _,
                            out _)
                        && string.Equals(
                            NormalizeCcid(sessionCcid),
                            ccid,
                            StringComparison.Ordinal);

                    if (ShouldIgnoreDetectedCcid(
                            port.Serial,
                            ccid,
                            port.Status,
                            currentSessionMatches))
                    {
                        _modemService.SetSmsSimIdentity(e.PortName, ccid);
                        return;
                    }

                    string previousCcid = NormalizeCcid(port.Serial);
                    if (!string.IsNullOrWhiteSpace(previousCcid)
                        && !string.Equals(
                            previousCcid,
                            ccid,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        InvalidateSimSession(e.PortName);
                        ClearSimScopedState(
                            port,
                            _modemService.GetObservedImei(e.PortName));
                    }

                    if (!TryBeginPortInitialization(e.PortName, out Guid initializationLease))
                    {
                        AddLog(
                            $"[{e.PortName}] [SAUTO_RX_OWNED] CCID={ccid}; chuỗi AT hiện tại đã nhận trực tiếp phản hồi này.",
                            "STATE");
                        return;
                    }

                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đã nhận SIM, đang bật kết nối mạng...";
                    port.Serial = ccid;
                    var detectedSession = StartSimSession(e.PortName, ccid);
                    _modemService.SetSmsSimIdentity(e.PortName, ccid);
                    string observedImei = NormalizeImei(
                        _modemService.GetObservedImei(e.PortName));
                    if (Services.ImeiManagementService.IsUsableObservedImei(observedImei))
                        port.Imei = observedImei;
                    port.LastError = string.Empty;
                    AddLog(
                        $"[{e.PortName}] [SIM_AUTO_ACCEPT] CCID={ccid}; không kiểm tra backup và không ghi IMEI.",
                        "INFO");
                    UpdateDashboard();

                    _ = Task.Run(() => ActivateDetectedSimWithoutImeiAsync(
                        port,
                        ccid,
                        detectedSession.Epoch,
                        detectedSession.Token,
                        initializationLease));
                    return;
                }
                else
                {
                    AddLog($"[{e.PortName}] Chưa đọc được CCID hợp lệ; tiếp tục chờ SIM.", "WARN");
                    InvalidateSimSession(e.PortName);
                    ClearSimScopedState(
                        port,
                        _modemService.GetObservedImei(e.PortName));
                    port.Status = "Chờ cắm SIM";
                    port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                    port.LastError = SecurityErrors.ReadCcidFailed;
                    _modemService.StartHotplugWaitLoop(e.PortName);
                }
            }
            // Chỉ lỗi đọc UART thực sự mới được phép đổi sang mất phản hồi.
            else if (e.Data.StartsWith("[STATUS_NO_RESPONSE]", StringComparison.OrdinalIgnoreCase))
            {
                port.Status = SimStatus.NoResponse;
                port.DeviceName = "Modem không phản hồi";
                port.LastError = e.Data["[STATUS_NO_RESPONSE]".Length..].Trim();
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[STATUS_HOTPLUG_SIM_DETECTED]"))
            {
                // [PARSE_CCID] là nguồn duy nhất khởi chạy state machine nhận SIM.
                // Event này chỉ cập nhật UI, tránh chạy state machine lần thứ hai.
                if (port.Status != SimStatus.Active)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang cấu hình SIM mới...";
                    UpdateDashboard();
                }
            }
        });
    }

    private void MarkPortReadyForNetwork(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        // SIM identity and RF are ready, but CSQ alone is not network registration.
        // Keep the COM pending until a current-session COPS response or a
        // registered CREG fallback explicitly promotes it to Active.
        port.Status = SimStatus.Connecting;
        port.NetworkProvider = string.Empty;
        port.NetworkType = string.Empty;
        port.TimeoutCount = 0;
        port.SmsErrorCount = 0;
        port.ReconnectCount = 0;
        port.LastError = "SIM đã được nhận; đang chờ đăng ký nhà mạng";
        
        // Cập nhật tên thiết bị thực tế dựa trên IMEI
        if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug)."
            || port.DeviceName == "Đang cấu hình SIM mới..."
            || port.DeviceName == "Đã nhận SIM, đang bật kết nối mạng..."
            || port.DeviceName == "SIM đã được nhận, đang chờ kết nối mạng..."
            || string.IsNullOrWhiteSpace(port.DeviceName))
        {
            port.DeviceName = Services.ImeiManagementService.GetDeviceNameFromImei(port.Imei);
        }

        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        UpdateDashboard();
    }

    private void MarkPortNetworkActive(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        port.Status = SimStatus.Active;
        port.LastError = string.Empty;
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        UpdateDashboard();

        foreach (var sms in SmsMessages.Where(s => s.PortName == portName))
        {
            sms.Status = SimStatus.Active;
        }

        _ = gsm.Services.FirebaseService.ClearWebStateAsync(portName);
    }

    internal static bool CanPromoteNetworkRegistration(
        SimPort port,
        bool initializationInProgress,
        bool sessionCurrent) =>
        sessionCurrent
        && !initializationInProgress
        && port.Status == SimStatus.Connecting
        && !string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial));

    internal static bool HasExactLiveCcidEvidence(
        string? liveCcid,
        string? expectedCcid)
    {
        string live = NormalizeCcid(liveCcid);
        string expected = NormalizeCcid(expectedCcid);
        return !string.IsNullOrWhiteSpace(live)
            && !string.IsNullOrWhiteSpace(expected)
            && string.Equals(live, expected, StringComparison.Ordinal);
    }

    internal static bool ShouldIgnoreDetectedCcid(
        string? currentCcid,
        string? detectedCcid,
        string? currentStatus,
        bool currentSessionMatches) =>
        string.Equals(
            NormalizeCcid(currentCcid),
            NormalizeCcid(detectedCcid),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            currentStatus,
            SimStatus.Active,
            StringComparison.Ordinal)
        && currentSessionMatches;

    private void ModemService_PortDisconnected(object? sender, GsmDataEventArgs e)
    {
        InvalidateSimSession(e.PortName);
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port != null)
            {
                Ports.Remove(port);
                UpdateDashboard();
                AddLog($"[{e.PortName}] {e.Data}", "ERROR");
                SnackbarMessageQueue.Enqueue($"Cổng {e.PortName} bị ngắt kết nối!");
            }
        });
    }

    private void ModemService_SmsReceived(object? sender, GsmDataEventArgs e)
    {
        // Raw Data trả về thường có dạng:
        // +CMGR: "REC UNREAD","+84999999999",,"26/05/01,10:00:00+28"
        // Ma xac nhan Zalo cua ban la 123456

        // The modem service deletes the recyclable SIM index only after this
        // handler returns. Keep the acknowledgement ordering, but cap how long a
        // blocked WPF/Blazor dispatcher can hold the serial receive pipeline.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        void ProcessOnUiThread()
        {
            try
            {
                var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
                string senderPhone = "UNKNOWN";
                string extractedOtp = "N/A";
                string cleanContent = TextEncodingNormalizer.RepairMojibake(e.Data);
                bool inboxRecorded = false;
                string uiDeliveryId = string.Empty;

                // Nếu quá trình đọc tin nhắn gặp lỗi (VD: Lỗi Timeout Semaphore do đang kẹt gửi SMS)
                if (cleanContent.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(e.DeliveryId)
                    && string.IsNullOrWhiteSpace(e.Sender))
                {
                    AddLog($"[{e.PortName}] LỖI đọc tin nhắn: {cleanContent}. Đang bỏ qua và không xóa để tránh mất OTP.", "WARN");
                    return;
                }

                // 1. Lấy thông tin từ sự kiện (Đã được xử lý 100% bên GsmModemService)
                if (!string.IsNullOrEmpty(e.Sender))
                {
                    senderPhone = e.Sender;
                    extractedOtp = string.IsNullOrWhiteSpace(e.Otp)
                        ? (gsm.Services.GsmModemService.ExtractOtp(cleanContent) ?? "N/A")
                        : e.Otp;
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

                string displayContent =
                    VietnameseCarrierTextNormalizer.RestoreForDisplay(cleanContent);
                if (!string.Equals(displayContent, cleanContent, StringComparison.Ordinal))
                {
                    AddLog(
                        $"[{e.PortName}] [SMS_DIACRITICS_RESTORED] sender={senderPhone} chars={displayContent.Length}",
                        "INFO");
                }

                // Commit every complete decoded SMS before GsmModemService may
                // release its exact SIM slot.
                if (!string.IsNullOrWhiteSpace(cleanContent))
                {
                    DateTimeOffset receivedAtUtc = DateTimeOffset.UtcNow;
                    string deliveryId = string.IsNullOrWhiteSpace(e.DeliveryId)
                        ? SmsInboxStore.CreateDeliveryId(
                            e.PortName,
                            e.MsgIndex,
                            senderPhone,
                            cleanContent)
                        : e.DeliveryId;
                    uiDeliveryId = deliveryId;
                    var smsRecord = new SmsInboxRecord
                    {
                        DeliveryId = deliveryId,
                        ReceivedAtUtc = receivedAtUtc,
                        SmsTimestampUtc = e.SmsTimestampUtc,
                        PortName = e.PortName,
                        // Preserve the exact carrier payload for OTP, webhook and
                        // resend. SmsMessage.DisplayContent restores only known
                        // ASCII carrier templates for the inbox/copy/export UI.
                        Content = cleanContent,
                        Sender = senderPhone,
                        Otp = extractedOtp,
                        ReceiverPhone = port?.PhoneNumber ?? "",
                        NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
                        Status = port?.Status ?? SimStatus.Connecting,
                        CallCount = "0",
                        ForwardContent = string.Empty
                    };
                    if (!TryAddSmsToSession(
                            smsRecord,
                            out SmsMessage? newlyAddedMessage))
                        return;

                    inboxRecorded = true;
                    if (newlyAddedMessage == null)
                    {
                        string replayReceiver = !string.IsNullOrWhiteSpace(
                            port?.PhoneNumber)
                            ? port.PhoneNumber
                            : "Chưa lấy được số";
                        QueueTelegramSmsNotification(
                            SettingsService.Current ?? new AppSettings(),
                            e.PortName,
                            replayReceiver,
                            senderPhone,
                            extractedOtp,
                            cleanContent);
                        e.DeliveryAccepted = true;
                        AtCommandTraceLogger.State(
                            e.PortName,
                            $"SMS_UI_COMMIT_ACCEPTED;delivery={deliveryId};session=true;ui=true;replay=true");
                        AddLog(
                            $"[{e.PortName}] [SMS_REPLAY_ACK] delivery={deliveryId}; SMS đã có trong phiên hiện tại.",
                            "INFO");
                        return;
                    }
                    AddLog(
                        $"[{e.PortName}] [SMS_UI_RECEIVED] delivery={deliveryId} sender={senderPhone} chars={cleanContent.Length} otp={extractedOtp}",
                        "INFO");
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
                        AddLog($"[{e.PortName}] Nhà mạng báo hết tiền nhưng chưa có số dư đã kiểm tra thủ công.", "WARNING");
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
                    _ = SendEzConfirmationAsync(e.PortName, port, confirmMsg);
                    
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

                // Commit the per-port summary before Telegram, webhook, Firebase
                // and other optional subscribers. A failure in an integration
                // must never leave ToolGSM showing stale SMS/OTP data.
                ApplyReceivedSmsToPort(
                    port,
                    senderPhone,
                    extractedOtp,
                    displayContent,
                    DateTimeOffset.UtcNow);

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
                // This call queues the notification in RAM synchronously. The
                // SIM is acknowledged only afterwards.
                QueueTelegramSmsNotification(
                    cfg,
                    e.PortName,
                    receiverPhone,
                    senderPhone,
                    extractedOtp,
                    cleanContent);

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

                // Acknowledge only after the record is present in the live UI
                // collection. The history remains session-only.
                bool visibleInUi = CanAcknowledgeSmsDelivery(
                    inboxRecorded,
                    uiDeliveryId,
                    SmsMessages);
                if (visibleInUi)
                {
                    e.DeliveryAccepted = true;
                    AtCommandTraceLogger.State(
                        e.PortName,
                        $"SMS_UI_COMMIT_ACCEPTED;delivery={uiDeliveryId};session=true;ui=true;replay=false");
                    AddLog(
                        $"[{e.PortName}] [SMS_DELIVERY_ACCEPTED] delivery={uiDeliveryId}; session=true; ui=true; SIM slot có thể giải phóng.",
                        "INFO");
                }
                else if (inboxRecorded)
                {
                    AtCommandTraceLogger.Error(
                        e.PortName,
                        $"SMS_UI_ACK_BLOCKED;delivery={uiDeliveryId};session=true;ui=false;source_retained=true");
                    AddLog(
                        $"[{e.PortName}] [SMS_UI_ACK_BLOCKED] delivery={uiDeliveryId}; chưa xác nhận có hàng trên UI, giữ nguyên SMS ở SIM để thử lại.",
                        "ERROR");
                }
                
                if (extractedOtp != "N/A")
                {
                    AddLog($"[{e.PortName}] Đã bắt được OTP: {extractedOtp} từ {senderPhone}", "SUCCESS");
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");

                    // Chỉ cập nhật lịch sử OTP trong RAM của phiên hiện tại.
                    InsertOtpHistoryBounded(new Services.OtpRecord
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
                AtCommandTraceLogger.Error(
                    e.PortName,
                    $"SMS_UI_PROCESSING_FAILED;error={ex.GetType().Name};source_retained=true");
                AddLog($"[{e.PortName}] Lỗi xử lý SMS: {ex.Message}", "ERROR");
            }
        }

        if (dispatcher.CheckAccess())
        {
            ProcessOnUiThread();
            return;
        }

        try
        {
            var operation = dispatcher.InvokeAsync(ProcessOnUiThread);
            if (!operation.Task.Wait(SmsUiDispatchTimeout))
            {
                // Do not acknowledge here. GsmModemService keeps the exact SIM
                // record and its retry/sweep path will deliver it later.
                AtCommandTraceLogger.Error(
                    e.PortName,
                    $"SMS_UI_DISPATCH_TIMEOUT;seconds={SmsUiDispatchTimeout.TotalSeconds:0};source_retained=true");
                AddLog(
                    $"[{e.PortName}] [SMS_UI_DISPATCH_TIMEOUT] UI bận quá {SmsUiDispatchTimeout.TotalSeconds:0}s; giữ SMS trên SIM để tự thử lại.",
                    "ERROR");
                return;
            }

            operation.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AtCommandTraceLogger.Error(
                e.PortName,
                $"SMS_UI_DISPATCH_FAILED;error={ex.GetType().Name};source_retained=true");
            AddLog(
                $"[{e.PortName}] [SMS_UI_DISPATCH_FAILED] Không đưa được SMS vào UI: {ex.Message}; giữ SMS trên SIM.",
                "ERROR");
        }
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
        DateTimeOffset receivedAtUtc = DateTimeOffset.UtcNow;
        string deliveryId = SmsInboxStore.CreateDeliveryId(
            "assembled-timeout",
            portName,
            senderPhone,
            content);
        var smsRecord = new SmsInboxRecord
        {
            DeliveryId = deliveryId,
            ReceivedAtUtc = receivedAtUtc,
            PortName = portName,
            ReceiverPhone = port?.PhoneNumber ?? string.Empty,
            Sender = senderPhone,
            Content = content,
            Otp = extractedOtp,
            NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
            Status = port?.Status ?? SimStatus.Connecting,
            CallCount = "0",
            ForwardContent = "Không"
        };
        if (!TryAddSmsToSession(smsRecord, out SmsMessage? newlyAddedMessage)
            || newlyAddedMessage == null)
            return;

        if (extractedOtp != "N/A")
        {
            OtpReceivedEvent?.Invoke(portName, extractedOtp);
            Services.SoundAlertService.PlayOtp();
            ToastService.ShowOtp(portName, receiverPhone, extractedOtp, senderPhone);
            // Trả OTP về Web Firebase (cho multipart SMS đã được gộm và timeout)
            if (port != null)
                _ = _firebaseService.PublishOtpForPendingCommandAsync(
                    port.PortName, extractedOtp, content, senderPhone);
        }

        if (port != null)
        {
            port.Sender = senderPhone;
            port.LastSmsSender = senderPhone;
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
                InsertOtpHistoryBounded(newRecord);
                if (SelectedTabIndex != 3) IncrementUnreadOtp();
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
                        $"Nội dung: {System.Net.WebUtility.HtmlEncode(existing.Content)}\n" +
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
                        string safeContent = System.Net.WebUtility.HtmlEncode(capturedContent);
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
            port.LastSmsSender = senderPhone;
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
        Application.Current.Dispatcher.InvokeAsync(() =>
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
            ShowToast($"[{e.PortName}] 📞 Có cuộc gọi từ {callerDisplay}", MudBlazor.Severity.Warning);
            Services.ToastService.Show($"📞 Cuộc gọi đến [{e.PortName}]", $"Người gọi: {callerDisplay}\nSIM: {receiverPhone}");

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

            // GsmModemService owns the ATA + QAUDRD workflow for voice-capable
            // profiles. This event is only a notification hook; the old message
            // claimed that auto-answer was disabled even while the modem service
            // had already answered and started recording.
            AddLog($"[{e.PortName}] Đã nhận cuộc gọi; đang tự động nghe máy và ghi âm.", "INFO");
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
                        // Ignore the permanent IMS/data CLCC row exposed by EC20
                        // (dir=1, mode=1). Only an active outgoing voice row may
                        // start call audio.
                        if (GsmModemService.HasActiveOutgoingVoiceSession(line))
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
                                        await _modemService.SendCommandAsync(
                                            portName,
                                            $"AT+QPSND=1,\"{fileName}\",0,1,1",
                                            5000);
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
            string displayCaller = string.IsNullOrWhiteSpace(session.Caller) || string.Equals(session.Caller, "Unknown", StringComparison.OrdinalIgnoreCase) ? "Số ẩn" : session.Caller;
            if (port != null)
            {
                port.LastCallResult = $"Ringing: {displayCaller}";
                port.UpdateDisplayResult("Call");
                AddLog($"[{session.Port}] Đang đổ chuông từ {displayCaller}", "INFO");
                ShowToast($"[{session.Port}] 📞 Đang đổ chuông: {displayCaller}", MudBlazor.Severity.Info);
            }
        });
    }

    private void ModemService_IncomingCallEnded(object? sender, gsm.Models.IncomingCallSession session)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == session.Port);
            if (port != null)
            {
                port.LastCallResult = $"Ended: {session.Caller}";
                port.UpdateDisplayResult("Call");
            }
        });
    }

    private void ModemService_CallEnded(object? sender, GsmDataEventArgs e)
    {
        if (e.Data == "NO CARRIER" || e.Data == "BUSY" || e.Data == "NO ANSWER")
        {
            _callFailures[e.PortName] = e.Data;
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // NO CARRIER còn có thể xuất hiện ở các lệnh mạng khác. Chỉ tạo bản ghi
            // cuộc gọi đến khi trước đó thật sự đã nhận được +CLIP cho cùng COM.
            if (!_activeCallers.TryRemove(e.PortName, out var callerDisplay))
                return;

            AddLog($"[{e.PortName}] Cuộc gọi đã kết thúc. ({e.Data})");

            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";
            const string content = "Cuộc gọi đến đã kết thúc.";
            DateTimeOffset receivedAtUtc = DateTimeOffset.UtcNow;

            InsertSmsMessageBounded(new SmsMessage
            {
                PortName = e.PortName,
                ReceivedAtUtc = receivedAtUtc,
                ReceivedTime = receivedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                Content = content,
                Sender = callerDisplay,
                Otp = "",
                ReceiverPhone = receiverPhone,
                NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
                Status = port?.Status ?? SimStatus.Connecting,
                CallCount = port?.CallCount.ToString() ?? "1",
                ForwardContent = ""
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

            string safeCallerHtml = System.Net.WebUtility.HtmlEncode(callerDisplay);
            // Thông báo Telegram khi cuộc gọi kết thúc (check TelegramOnCall)
            var callEndCfg = SettingsService.Current;
            if (callEndCfg != null &&
                !string.IsNullOrWhiteSpace(callEndCfg.TelegramBotToken) &&
                !string.IsNullOrWhiteSpace(callEndCfg.TelegramChatId) &&
                callEndCfg.TelegramOnCall)
            {
                string endText =
                    $"📞 <b>Cuộc gọi kết thúc [{e.PortName}]</b>\n" +
                    $"📱 SIM nhận: {receiverPhone}\n" +
                    $"☎️ Người gọi: <code>{safeCallerHtml}</code>\n" +
                    $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                _ = _notifyService.SendTelegramAsync(callEndCfg.TelegramBotToken, callEndCfg.TelegramChatId, endText);
            }
        });
    }

    private void ModemService_CallRecordingSaved(object? sender, GsmDataEventArgs e)
    {
        string localWav = e.Data;
        string portName = e.PortName;

        if (string.IsNullOrWhiteSpace(localWav) || !File.Exists(localWav))
            return;

        // Chạy tiến trình STT và bóc tách Voice OTP trên luồng background
        _ = Task.Run(async () =>
        {
            AddLog($"[{portName}] 🎧 Whisper đang dịch file ghi âm ({Path.GetFileName(localWav)})...", "INFO");

            var result = await Services.VoiceTranscriptionService.TranscribeAudioAsync(localWav);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AddLog($"[{portName}] [VOICE_STT_ERROR] {result.Error}", "WARN");
                return;
            }

            string text = result.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                AddLog($"[{portName}] [VOICE_STT] File ghi âm không có giọng nói hoặc quá ngắn.", "INFO");
                return;
            }

            var port = Ports.FirstOrDefault(p => p.PortName == portName);
            string receiverPhone = port?.PhoneNumber ?? "Chưa lấy được số";

            Application.Current.Dispatcher.Invoke(() =>
            {
                AddLog($"[{portName}] 📝 Voice STT: \"{text}\"", "INFO");

                // Tìm bản ghi tin nhắn cuộc gọi vừa kết thúc
                var existingMsg = SmsMessages.FirstOrDefault(m => m.PortName == portName && (m.Content == "Cuộc gọi đến đã kết thúc." || m.Content.StartsWith("[VOICE]")));

                // Ưu tiên giữ lại số người gọi thực tế nếu có
                string senderPhone = !string.IsNullOrWhiteSpace(e.Sender) && e.Sender != "Unknown" && e.Sender != "Ẩn số"
                    ? e.Sender
                    : (existingMsg != null && !string.IsNullOrWhiteSpace(existingMsg.Sender) && existingMsg.Sender != "Ẩn số" && existingMsg.Sender != "Unknown"
                        ? existingMsg.Sender
                        : (port != null && !string.IsNullOrWhiteSpace(port.Sender) && port.Sender != "Ẩn số" && port.Sender != "Unknown" ? port.Sender : "Ẩn số"));

                // Cập nhật hoặc chèn bản ghi vào danh sách Tin nhắn tới (SmsMessages)
                if (existingMsg != null)
                {
                    int idx = SmsMessages.IndexOf(existingMsg);
                    if (idx >= 0)
                    {
                        SmsMessages[idx] = new SmsMessage
                        {
                            PortName = portName,
                            ReceivedTime = existingMsg.ReceivedTime,
                            ReceivedAtUtc = existingMsg.ReceivedAtUtc,
                            SmsTimestampUtc = existingMsg.SmsTimestampUtc,
                            Content = $"[VOICE] {text}",
                            Sender = senderPhone,
                            Otp = result.Otp ?? string.Empty,
                            ReceiverPhone = receiverPhone,
                            NetworkProvider = port?.NetworkProvider ?? existingMsg.NetworkProvider,
                            Status = port?.Status ?? existingMsg.Status,
                            CallCount = port?.CallCount.ToString() ?? existingMsg.CallCount,
                            ForwardContent = existingMsg.ForwardContent
                        };
                    }
                }
                else
                {
                    DateTimeOffset receivedAtUtc = DateTimeOffset.UtcNow;
                    InsertSmsMessageBounded(new SmsMessage
                    {
                        PortName = portName,
                        ReceivedAtUtc = receivedAtUtc,
                        ReceivedTime = receivedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                        Content = $"[VOICE] {text}",
                        Sender = senderPhone,
                        Otp = result.Otp ?? string.Empty,
                        ReceiverPhone = receiverPhone,
                        NetworkProvider = port?.NetworkProvider ?? "UNKNOWN",
                        Status = port?.Status ?? SimStatus.Connecting,
                        CallCount = port?.CallCount.ToString() ?? "1",
                        ForwardContent = ""
                    });
                }

                if (port != null)
                {
                    port.LastMessageContent = $"[VOICE] {text}";
                    port.Otp = result.Otp ?? string.Empty;
                    port.LastCallResult = $"STT: {text}";
                    port.Sender = senderPhone;
                    port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                    port.UpdateDisplayResult("Call");
                    UpdateDashboard();
                }

                OnPropertyChanged(nameof(SmsMessages));
                OnPropertyChanged(nameof(FilteredSmsMessages));
                OnPropertyChanged(nameof(SmsReceivedCount));

                // Nếu là thông báo SIM bị khóa từ tổng đài
                if (result.Locked)
                {
                    AddLog($"[{portName}] 🔒 SIM BỊ KHÓA: Tổng đài thông báo thuê bao/SIM bị khóa.", "ERROR");
                    ShowToast($"[{portName}] ⚠️ SIM bị khóa!", MudBlazor.Severity.Error);
                }

                // Nếu bóc tách được mã OTP
                if (!string.IsNullOrWhiteSpace(result.Otp))
                {
                    AddLog($"[{portName}] 🔑 ĐÃ BẮT ĐƯỢC VOICE OTP: {result.Otp} từ {senderPhone}", "SUCCESS");

                    InsertOtpHistoryBounded(new Services.OtpRecord
                    {
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Port = portName,
                        SimPhone = receiverPhone,
                        Sender = $"[VOICE] {senderPhone}",
                        Otp = result.Otp,
                        Content = text
                    });

                    if (SelectedTabIndex != 3) IncrementUnreadOtp();
                    OnPropertyChanged(nameof(FilteredOtpHistory));
                    OnPropertyChanged(nameof(FilteredOtpHistoryCount));

                    // Phát âm thanh OTP
                    Services.SoundAlertService.PlayOtp();

                    // Toast UI và Windows Toast
                    ShowToast($"[{portName}] 🔑 Voice OTP: {result.Otp} (từ {senderPhone})", MudBlazor.Severity.Success);
                    Services.ToastService.Show($"🔑 Voice OTP — {portName}", $"SIM: {receiverPhone} | Từ: {senderPhone}\nOTP: {result.Otp}\n\"{text}\"");

                    // Gửi Telegram
                    var clipCfg = SettingsService.Current;
                    if (clipCfg != null &&
                        !string.IsNullOrWhiteSpace(clipCfg.TelegramBotToken) &&
                        !string.IsNullOrWhiteSpace(clipCfg.TelegramChatId) &&
                        clipCfg.TelegramOnCall)
                    {
                        string safeCallerHtml = System.Net.WebUtility.HtmlEncode(senderPhone);
                        string safeTextHtml = System.Net.WebUtility.HtmlEncode(text);
                        string tgText =
                            $"🔑 <b>Voice OTP Mới [{portName}]</b>\n" +
                            $"📱 SIM nhận: {receiverPhone}\n" +
                            $"☎️ Người gọi: <code>{safeCallerHtml}</code>\n" +
                            $"🔑 OTP: <code>{result.Otp}</code>\n" +
                            $"📝 Lời thoại: <i>{safeTextHtml}</i>\n" +
                            $"Time: {DateTime.Now:HH:mm:ss dd/MM}";
                        _ = _notifyService.SendTelegramAsync(clipCfg.TelegramBotToken, clipCfg.TelegramChatId, tgText);
                    }

                    // Tự động forward Webhook
                    var webhookRules = AppSettings?.WebhookRules ?? new List<Models.WebhookRule>();
                    foreach (var rule in webhookRules)
                    {
                        _ = Services.WebhookService.TriggerAsync(rule, portName, receiverPhone, $"[VOICE] {senderPhone}", result.Otp, text);
                    }
                }
                else
                {
                    ShowToast($"[{portName}] 📝 Dịch ghi âm: {text}", MudBlazor.Severity.Info);
                }
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
                await _modemService.SweepUnreadSmsAsync(
                    port.PortName,
                    _lifetimeCts.Token);
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

            balanceTasks.Add(RunBalanceLookupAsync(
                port, ussdCode, logResult: true));
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
                    commands.Add(GsmModemService.Uart1UrcRoutingCommand);
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


        if (System.Windows.MessageBox.Show($"Theo dõi {targetPorts.Count} modem để nhận SIM mới?\nKhông gửi lệnh tắt sóng; chỉ cần rút và cắm SIM.", "Chuẩn bị Đổi SIM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        SnackbarMessageQueue.Enqueue($"Đang theo dõi {targetPorts.Count} cổng để nhận SIM...");
        AddLog($"Bắt đầu theo dõi SIM trên {targetPorts.Count} cổng...");

        var swapTasks = targetPorts.Select(async port =>
        {
            InvalidateSimSession(port.PortName);
            Application.Current.Dispatcher.Invoke(() => port.Status = SimStatus.Connecting);
            try
            {
                _modemService.StartHotplugWaitLoop(port.PortName);
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Không thể theo dõi SIM: {ex.Message}", "ERROR");
            }
        }).ToList();
        await Task.WhenAll(swapTasks);

        SnackbarMessageQueue.Enqueue("Đang chờ nhận SIM. Bạn chỉ cần rút và cắm SIM mới.");
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
                var (success, msg) = await WipeAllSmsFromPortAsync(port.PortName);
                if (IsSimSessionCurrent(port.PortName, ccid, epoch))
                    AddLog($"[{port.PortName}] {msg}", success ? "SUCCESS" : "ERROR");
            }
            catch (Exception ex)
            {
                AddLog($"[{port.PortName}] Xóa SMS lỗi: {ex.Message}", "ERROR");
            }
        }));
    }

    public async Task<(bool Success, string Message)> WipeAllSmsFromPortAsync(
        string portName,
        CancellationToken ct = default)
    {
        if (!IsPortReadyForOperation(portName)
            || !TryGetCurrentSimSession(portName, out var ccid, out var epoch, out var simToken))
        {
            return (false, "Cổng không còn Active hoặc phiên SIM đã thay đổi");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, simToken);
        var token = linkedCts.Token;

        using var bgLease = _modemService.SuspendPortBackgroundOperations(portName);
        try
        {
            token.ThrowIfCancellationRequested();

            // 1. Đặt vùng nhớ thao tác sang "SM" (SIM card storage)
            await _modemService.SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, token);

            // 2. Thử xóa nhanh toàn bộ bằng cờ delflag 4
            string bulkRes = await _modemService.SendCommandAsync(portName, "AT+CMGD=1,4", 8000, silent: true, token);

            // 3. Kiểm tra số lượng tin còn lại trên SIM
            string cpmsSm = await _modemService.SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, token);
            bool smClean = GsmModemService.TryParseSimStorageUsage(cpmsSm, out int usedSm, out int totalSm) && usedSm == 0;

            // Nếu vẫn còn tin hoặc lệnh CMGD=1,4 bị modem từ chối -> Quét đọc và xóa từng slot
            if (!smClean || bulkRes.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                var indices = new HashSet<int>();

                // Thử đọc danh sách index bằng text mode
                await _modemService.SendCommandAsync(portName, "AT+CMGF=1", 3000, silent: true, token);
                string listRes = await _modemService.SendCommandAsync(portName, "AT+CMGL=\"ALL\"", 8000, silent: true, token);
                foreach (Match m in Regex.Matches(listRes, @"\+CMGL:\s*(\d+)", RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int idx))
                        indices.Add(idx);
                }

                // Thử thêm PDU mode để quét cạn tin nhắn PDU/Class 0/đặc biệt
                await _modemService.SendCommandAsync(portName, "AT+CMGF=0", 3000, silent: true, token);
                string pduList = await _modemService.SendCommandAsync(portName, "AT+CMGL=4", 8000, silent: true, token);
                foreach (Match m in Regex.Matches(pduList, @"\+CMGL:\s*(\d+)", RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int idx))
                        indices.Add(idx);
                }

                // Xóa từng index cụ thể tìm thấy
                foreach (int idx in indices)
                {
                    await _modemService.SendCommandAsync(portName, $"AT+CMGD={idx},0", 3000, silent: true, token);
                }

                // Nếu vẫn còn báo có tin sau khi xóa index, quét xóa vét toàn bộ dải slot 1..total (hoặc 1..50)
                cpmsSm = await _modemService.SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, token);
                if (GsmModemService.TryParseSimStorageUsage(cpmsSm, out usedSm, out totalSm) && usedSm > 0)
                {
                    int maxSlots = totalSm > 0 ? Math.Min(totalSm, 100) : 50;
                    for (int i = 1; i <= maxSlots; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        await _modemService.SendCommandAsync(portName, $"AT+CMGD={i},0", 1500, silent: true, token);
                    }
                }
            }

            // 4. Đồng thời dọn sạch cả bộ nhớ thiết bị "ME" nếu có lưu tin nhắn
            try
            {
                await _modemService.SendCommandAsync(portName, "AT+CPMS=\"ME\",\"ME\",\"ME\"", 5000, silent: true, token);
                await _modemService.SendCommandAsync(portName, "AT+CMGD=1,4", 5000, silent: true, token);
                string cpmsMe = await _modemService.SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, token);
                if (GsmModemService.TryParseSimStorageUsage(cpmsMe, out int usedMe, out _) && usedMe > 0)
                {
                    string meList = await _modemService.SendCommandAsync(portName, "AT+CMGL=\"ALL\"", 5000, silent: true, token);
                    foreach (Match m in Regex.Matches(meList, @"\+CMGL:\s*(\d+)", RegexOptions.IgnoreCase))
                    {
                        if (int.TryParse(m.Groups[1].Value, out int idx))
                            await _modemService.SendCommandAsync(portName, $"AT+CMGD={idx},0", 2000, silent: true, token);
                    }
                }
            }
            catch { /* Bỏ qua lỗi ME nếu modem không hỗ trợ ME */ }

            // 5. Khôi phục vùng nhớ chuẩn "SM" cho SIM
            await _modemService.SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, token);

            // 6. Kiểm tra lại lần cuối
            string finalCpms = await _modemService.SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, token);
            if (GsmModemService.TryParseSimStorageUsage(finalCpms, out int finalUsed, out int finalTotal))
            {
                if (finalUsed == 0)
                    return (true, $"Đã xóa sạch toàn bộ SMS trong SIM ({finalUsed}/{finalTotal})");
                else
                    return (false, $"Còn lại {finalUsed}/{finalTotal} tin chưa thể xóa");
            }

            return (false, "Không xác minh được bộ nhớ SMS đã về 0");
        }
        catch (OperationCanceledException)
        {
            return (false, "Đã hủy thao tác xóa SMS");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi xóa SMS: {ex.Message}");
        }
    }

    private Task<(bool Success, string Message)> EnsureInitialSmsCleanupCompletedAsync(
        string portName,
        string ccid,
        long epoch,
        CancellationToken simToken,
        CancellationToken waitCancellationToken)
    {
        if (AppSettings?.AutoClearSmsAfterUssd == false)
        {
            return Task.FromResult((
                Success: true,
                Message: "Tự động xóa SMS đã tắt"));
        }

        string sessionKey = $"{portName}#{ccid}#{epoch}";
        return _initialSmsCleanupBarrier.EnsureAsync(
            sessionKey,
            async () =>
            {
                // Keep the original post-USSD grace period inside the shared
                // task. MyVNPT therefore waits for the same delay + cleanup,
                // instead of starting a separate cleanup before USSD releases.
                await Task.Delay(1500, simToken).ConfigureAwait(false);

                if (!IsSimSessionCurrent(portName, ccid, epoch)
                    || !IsPortReadyForOperation(portName))
                {
                    return (
                        Success: false,
                        Message: "Cổng không còn Active hoặc phiên SIM đã thay đổi");
                }

                AddLog(
                    $"[{portName}] [SMS_CLEANUP_START] Bắt đầu xóa SMS (SIM & thiết bị) trước MyVNPT.",
                    "INFO");

                (bool success, string message) =
                    await WipeAllSmsFromPortAsync(portName, simToken)
                        .ConfigureAwait(false);

                if (IsSimSessionCurrent(portName, ccid, epoch))
                {
                    AddLog(
                        $"[{portName}] [SMS_CLEANUP_COMPLETE] {message}",
                        success ? "SUCCESS" : "WARN");
                }

                return (success, message);
            },
            waitCancellationToken);
    }

    public void TriggerAutoClearSmsAfterUssd(string portName)
    {
        if (AppSettings?.AutoClearSmsAfterUssd == false) return;
        if (!TryGetCurrentSimSession(portName, out var ccid, out var epoch, out var token)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureInitialSmsCleanupCompletedAsync(
                        portName,
                        ccid,
                        epoch,
                        token,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsSimSessionCurrent(portName, ccid, epoch))
                    AddLog($"[{portName}] ⚠️ Lỗi tự động xóa sạch SMS: {ex.Message}", "WARN");
            }
        }, token);
    }

    public async Task<string> CheckBalanceForPortAsync(string portName)
    {
        if (!IsPortReadyForOperation(portName))
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";

        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port != null && !string.IsNullOrWhiteSpace(port.NetworkProvider))
        {
            string ussdCode = GetUssdCodeForProvider(port.NetworkProvider);
            AddLog($"Kiểm tra TKC theo yêu cầu cho {port.PortName}...");
            return await RunBalanceLookupAsync(port, ussdCode, logResult: true);
        }
        return "ERROR: Cổng không hợp lệ hoặc không có thông tin nhà mạng";
    }

    /// <summary>
    /// Gửi nguyên mã USSD qua GSMController.USSDCheck tương ứng của SAuto và
    /// chỉ ánh xạ kết quả nhận được lên UI.
    /// </summary>
    public async Task<string> SendUssdForPortAsync(
        string portName,
        string ussdCode)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(ussdCode))
            return "ERROR: Thiếu tham số";

        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null) return "ERROR: Cổng không tìm thấy";

        // Hiển thị trạng thái đang gửi lên cột Nội dung ngay lập tức
        Application.Current.Dispatcher.Invoke(() =>
        {
            port.LastMessageContent = $"[USSD] Đang gửi {ussdCode}...";
            port.Sender = "USSD";
        });

        string result;
        try
        {
            result = await RunSautoUssdAsync(
                portName,
                ussdCode,
                logResult: true,
                cancellationToken: _lifetimeCts.Token);
            result = UssdResponseDecoder.Normalize(result);
        }
        catch (OperationCanceledException)
        {
            result = "ERROR: USSD operation cancelled";
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            bool failed = IsOperationFailureResult(result);
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
        SetOperationStatus(portName, "USSD", !IsOperationFailureResult(result));
        return result;
    }


    /// <summary>
    /// Chạy mã USSD do người dùng hoặc lệnh điều khiển chọn. Mỗi stage chỉ
    /// hoàn tất khi parser nhận được payload +CUSD hợp lệ.
    /// </summary>
    private async Task<string> RunBalanceLookupAsync(
        SimPort port, string ussdCode, bool logResult)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = true);
        try
        {
            string result = await RunSautoUssdAsync(
                port.PortName, ussdCode, logResult: logResult);
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

    private async Task<string> RunSautoUssdAsync(
        string portName,
        string ussdCode,
        bool logResult = false,
        CancellationToken cancellationToken = default)
    {
        CancellationToken effectiveToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _lifetimeCts.Token;

        string result = await _ussdService.SendAsync(
            portName,
            ussdCode,
            effectiveToken);

        if (result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            if (logResult)
            {
                string logLevel = result.Contains(
                    "USSD operation cancelled",
                    StringComparison.OrdinalIgnoreCase)
                    ? "WARN"
                    : "ERROR";
                AddLog($"Kết quả từ {portName}: {result}", logLevel);
            }
        }
        else if (logResult)
        {
            AddLog($"Kết quả từ {portName}: {result}", "SUCCESS");
        }
        return result;
    }


    private void MaybeCooldownPort(string portName, string result)
    {
        if (!ShouldCooldown(result)) return;

        var cooldown = result.Contains("Port not open", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(45);

        _portCooldown.Start(portName, cooldown);
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

        if (GsmModemService.IsRadioDisruptiveCommand(AtCommandInput))
        {
            AtCommandOutput += "[BLOCKED] Chế độ nofake không cho phép lệnh thay đổi nguồn hoặc trạng thái RF.\n";
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
    }

    /// <summary>
    /// Áp dụng cài đặt mới (từ Settings.razor) ngay lập tức:
    /// sync AppSettings và áp dụng call forwarding khi được bật.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        var saved = SettingsService.Current;
        if (saved != null) AppSettings = saved;

        // Không khởi động thêm signal/SMS supervisor. DataPort SAuto là vòng
        // nền duy nhất được phép sở hữu CPIN/CSQ/COPS trên UART.

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
        if (DeleteSmsHistoryItem(sms))
            SnackbarMessageQueue.Enqueue("Đã xóa tin nhắn.");
    }

    public bool DeleteSmsHistoryItem(SmsMessage? sms)
    {
        if (sms == null) return false;

        try
        {
            if (!string.IsNullOrWhiteSpace(sms.DeliveryId))
                _smsInboxStore.Delete(new[] { sms.DeliveryId });
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            AddLog(
                $"[SMS_INBOX_DELETE_FAILED] delivery={sms.DeliveryId}; {ex.GetType().Name}: {ex.Message}",
                "ERROR");
            return false;
        }

        if (!SmsMessages.Remove(sms)) return false;

        OnPropertyChanged(nameof(FilteredSmsMessages));
        OnPropertyChanged(nameof(SmsReceivedCount));
        return true;
    }

    public bool ClearSmsHistory()
    {
        try
        {
            _smsInboxStore.Clear();
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            AddLog(
                $"[SMS_INBOX_CLEAR_FAILED] {ex.GetType().Name}: {ex.Message}",
                "ERROR");
            return false;
        }

        SmsMessages.Clear();
        OnPropertyChanged(nameof(FilteredSmsMessages));
        OnPropertyChanged(nameof(SmsReceivedCount));
        return true;
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
        int deleted = 0;
        foreach (SmsMessage sms in filtered)
        {
            if (DeleteSmsHistoryItem(sms)) deleted++;
        }

        SnackbarMessageQueue.Enqueue($"Đã xóa {deleted}/{filtered.Count} tin nhắn.");
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
            _ = RunSautoUssdAsync(port.PortName, ussdCode, logResult: true);
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
        CancellationToken ct = default,
        string? expectedCcid = null)
    {
        return SendSmsViaServiceAsync(
            portName, phoneNumber, content, ct, expectedCcid);
    }

    /// <summary>
    /// Tương thích với các caller cũ của ToolWeb. Web phải dùng cùng pipeline
    /// với thao tác gửi thủ công để giữ session SIM, cooldown và khóa modem.
    /// </summary>
    public async Task<string> QueueSmsFromWebAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct = default)
    {
        string result = await QueueSmsAsync(portName, phoneNumber, content, ct);
        bool accepted = result.Contains("thành công", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase);
        AddLog(
            $"[{portName}] [WEB_SMS_{(accepted ? "SENT" : "FAILED")}] "
            + (accepted
                ? $"Đã gửi đến {phoneNumber}; đang chờ OTP."
                : result),
            accepted ? "SUCCESS" : "ERROR");
        return result;
    }

    private async Task<string> SendSmsViaServiceAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct,
        string? expectedCcid = null)
    {
        if (!IsPortReadyForOperation(portName))
        {
            RecordPortError(portName, "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi", "SMS");
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi";
        }
        if (!_portSessions.TryGet(portName, out PortSessionLease session))
        {
            RecordPortError(portName, "ERROR: Port has no current SIM session", "SMS");
            return "ERROR: Port has no current SIM session";
        }
        if (!string.IsNullOrWhiteSpace(NormalizeCcid(expectedCcid))
            && !string.Equals(
                session.Ccid,
                NormalizeCcid(expectedCcid),
                StringComparison.Ordinal))
        {
            RecordPortError(
                portName,
                "ERROR: Current SIM does not match the pinned CCID",
                "SMS");
            return "ERROR: Current SIM does not match the pinned CCID";
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        try
        {
            bool loggedWait = false;
            await _portCooldown.WaitAsync(portName, remaining =>
            {
                if (loggedWait) return;
                loggedWait = true;
                AddLog(
                    $"[{portName}] Cổng đang nghỉ sau lỗi trước; tự chờ {Math.Ceiling(remaining.TotalSeconds):0}s rồi gửi SMS.",
                    "INFO");
            }, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            string cancelledResult = _portSessions.IsCurrent(session.PortName, session.Ccid, session.Epoch)
                ? "ERROR: SMS operation cancelled while waiting for port cooldown"
                : "ERROR: SIM session changed while waiting for port cooldown";
            RecordPortError(portName, cancelledResult, "SMS");
            return cancelledResult;
        }

        if (!_portSessions.IsCurrent(session.PortName, session.Ccid, session.Epoch)
            || !IsPortReadyForOperation(portName))
        {
            RecordPortError(portName, "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi trong lúc chờ", "SMS");
            return "ERROR: Cổng không còn Active hoặc phiên SIM đã thay đổi trong lúc chờ";
        }

        string result = await _smsService.SendAsync(
            portName,
            phoneNumber,
            content,
            linkedCts.Token,
            expectedCcid);
        if (result.Contains("thành công", StringComparison.OrdinalIgnoreCase)
            || result.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase))
        {
            RecordSmsSuccess(portName);
            AddLog($"[{portName}] Gửi tin nhắn đến {phoneNumber} thành công.", "SUCCESS");
        }
        else
        {
            RecordPortError(portName, result, "SMS");
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
        CancellationToken ct = default,
        string? expectedCcid = null)
    {
        if (!IsPortReadyForOperation(port)
            || !TryGetCurrentSimSession(port, out var callCcid, out var callEpoch, out var simToken)
            || (!string.IsNullOrWhiteSpace(NormalizeCcid(expectedCcid))
                && !string.Equals(
                    callCcid,
                    NormalizeCcid(expectedCcid),
                    StringComparison.Ordinal)))
        {
            SetOperationStatus(port, "Call", false);
            return false;
        }

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
                operationToken,
                expectedCcid);

            bool completed = result
                && IsSimSessionCurrent(port, callCcid, callEpoch)
                && IsPortReadyForOperation(port);
            SetOperationStatus(port, "Call", completed);
            return completed;
        }
        catch
        {
            SetOperationStatus(port, "Call", false);
            throw;
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
                        if (!IsOperationFailureResult(result))
                        {
                            AddLog($"[BULK SMS] [{sourcePort}] → {phone}: OK", "SUCCESS");
                            SetOperationStatus(sourcePort, "SMS", true);
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
                            SetOperationStatus(sourcePort, "SMS", false);
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
                        SetOperationStatus(sourcePort, "SMS", false);
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

                        if (idxCcid < 0) idxCcid = 0;
                        if (idxImei < 0) idxImei = 1;

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
                                    var entry = new SimBackupEntry
                                    {
                                        Ccid = ccid,
                                        Imei = imei
                                    };
                                    newCache[ccid] = entry;
                                }
                            }
                        }
                    }
                    _imeiCache = newCache;
                    AddLog(
                        $"[IMEI_SOURCE] Đã nạp read-only {newCache.Count} dòng từ imei_backup.csv; nofake không tự tạo XLSX.",
                        "SUCCESS");
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
                _modemImeiCache = new ConcurrentDictionary<string, ModemImeiBackupEntry>(StringComparer.OrdinalIgnoreCase);
                AddLog(
                    $"[IMEI_SOURCE] Không tìm thấy {_imeiCacheFilePath}; SIM vẫn tự động kết nối bằng IMEI hiện có.",
                    "INFO");
            }
        }
    }

    private void LoadImeiCacheWorkbook()
    {
        try
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var newCache = new ConcurrentDictionary<string, SimBackupEntry>();
            var newModemCache = new ConcurrentDictionary<string, ModemImeiBackupEntry>(StringComparer.OrdinalIgnoreCase);
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

                int loaded = 0;
                for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
                {
                    string ccid = NormalizeCcid(worksheet.Cells[row, ccidColumn].Text);
                    string imei = NormalizeImei(worksheet.Cells[row, imeiColumn].Text);
                    if (string.IsNullOrWhiteSpace(ccid) || string.IsNullOrWhiteSpace(imei)) continue;

                    var entry = new SimBackupEntry
                    {
                        Ccid = ccid,
                        Imei = imei
                    };
                    newCache[ccid] = entry;
                    loaded++;
                }

                var modemWorksheet = package.Workbook.Worksheets["Modem Backup"];
                if (modemWorksheet?.Dimension != null)
                {
                    var modemHeaders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int column = 1; column <= modemWorksheet.Dimension.End.Column; column++)
                    {
                        string name = modemWorksheet.Cells[1, column].Text.Trim().TrimStart('\uFEFF');
                        if (!string.IsNullOrWhiteSpace(name)) modemHeaders[name] = column;
                    }

                    if (modemHeaders.TryGetValue("PortName", out int portColumn)
                        && modemHeaders.TryGetValue("IMEI", out int modemImeiColumn))
                    {
                        for (int row = 2; row <= modemWorksheet.Dimension.End.Row; row++)
                        {
                            string portName = NormalizeModemBackupKey(modemWorksheet.Cells[row, portColumn].Text);
                            string imei = NormalizeImei(modemWorksheet.Cells[row, modemImeiColumn].Text);
                            if (string.IsNullOrWhiteSpace(portName)
                                || !Services.ImeiManagementService.IsValidImei(imei)) continue;

                            newModemCache[portName] = new ModemImeiBackupEntry
                            {
                                PortName = portName,
                                Imei = imei
                            };
                        }
                    }
                }
                return loaded;
            }

            int canonicalCount;
            int pendingCount;
            bool pendingIsNewer = File.Exists(_pendingImeiCacheFilePath)
                && (!File.Exists(_imeiCacheFilePath)
                    || File.GetLastWriteTimeUtc(_pendingImeiCacheFilePath)
                        > File.GetLastWriteTimeUtc(_imeiCacheFilePath));
            if (pendingIsNewer)
            {
                canonicalCount = ReadWorkbook(_imeiCacheFilePath);
                pendingCount = ReadWorkbook(_pendingImeiCacheFilePath);
            }
            else
            {
                pendingCount = ReadWorkbook(_pendingImeiCacheFilePath);
                canonicalCount = ReadWorkbook(_imeiCacheFilePath);
            }
            _imeiCache = newCache;
            _modemImeiCache = newModemCache;
            AddLog($"[IMEI_SOURCE] Đã nạp {newCache.Count} SIM và {newModemCache.Count} modem từ XLSX (chính={canonicalCount}, chờ hợp nhất={pendingCount}).", "SUCCESS");

            // Pending and primary remain read-only until the user explicitly
            // imports, exports, or edits backup data.
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
            bool primarySaved = false;
            bool tempSnapshotReady = false;
            try
            {
                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
                using var package = new ExcelPackage();
                package.Workbook.Properties.Title = "ToolGSM IMEI Backup";
                package.Workbook.Properties.Subject = "Minimal CCID to IMEI mapping";
                var worksheet = package.Workbook.Worksheets.Add("IMEI Backup");

                for (int column = 0; column < ImeiBackupColumns.Length; column++)
                    worksheet.Cells[1, column + 1].Value = ImeiBackupColumns[column];

                int row = 2;
                foreach (var entry in _imeiCache.Values.OrderBy(value => value.Ccid, StringComparer.OrdinalIgnoreCase))
                {
                    object?[] values =
                    [
                        entry.Ccid, entry.Imei
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

                double[] widths = [24, 18];
                for (int column = 1; column <= lastColumn; column++)
                    worksheet.Column(column).Width = widths[column - 1];

                if (lastRow >= 2)
                {
                    // Keep CCID/IMEI identifiers exact.
                    worksheet.Cells[2, 1, lastRow, 2].Style.Numberformat.Format = "@";
                    worksheet.Cells[2, 1, lastRow, 2].Style.QuotePrefix = true;
                    // Excel Table tự tạo AutoFilter trong table1.xml. Không đặt thêm
                    // worksheet AutoFilter trên cùng vùng vì Excel sẽ coi hai filter
                    // chồng nhau là nội dung không đọc được và xóa cả Table khi mở file.
                    var table = worksheet.Tables.Add(worksheet.Cells[1, 1, lastRow, lastColumn], "ImeiBackupTable");
                    table.TableStyle = OfficeOpenXml.Table.TableStyles.Medium2;
                    table.ShowFilter = true;
                }

                var modemWorksheet = package.Workbook.Worksheets.Add("Modem Backup");
                for (int column = 0; column < ModemImeiBackupColumns.Length; column++)
                    modemWorksheet.Cells[1, column + 1].Value = ModemImeiBackupColumns[column];

                int modemRow = 2;
                foreach (var entry in _modemImeiCache.Values.OrderBy(value => value.PortName, StringComparer.OrdinalIgnoreCase))
                {
                    object?[] values =
                    [
                        entry.PortName, entry.Imei
                    ];
                    for (int column = 0; column < values.Length; column++)
                        modemWorksheet.Cells[modemRow, column + 1].Value = values[column];
                    modemRow++;
                }

                int modemLastRow = Math.Max(1, modemRow - 1);
                int modemLastColumn = ModemImeiBackupColumns.Length;
                using (var header = modemWorksheet.Cells[1, 1, 1, modemLastColumn])
                {
                    header.Style.Font.Bold = true;
                    header.Style.Font.Color.SetColor(System.Drawing.Color.White);
                    header.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    header.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(30, 58, 138));
                    header.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                    header.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;
                }
                modemWorksheet.Row(1).Height = 26;
                modemWorksheet.View.FreezePanes(2, 1);
                modemWorksheet.Cells[1, 1, modemLastRow, modemLastColumn].Style.VerticalAlignment =
                    OfficeOpenXml.Style.ExcelVerticalAlignment.Center;
                double[] modemWidths = [14, 18];
                for (int column = 1; column <= modemLastColumn; column++)
                    modemWorksheet.Column(column).Width = modemWidths[column - 1];
                if (modemLastRow >= 2)
                {
                    modemWorksheet.Cells[2, 1, modemLastRow, 2].Style.Numberformat.Format = "@";
                    modemWorksheet.Cells[2, 1, modemLastRow, 2].Style.QuotePrefix = true;
                    var modemTable = modemWorksheet.Tables.Add(
                        modemWorksheet.Cells[1, 1, modemLastRow, modemLastColumn],
                        "ModemImeiBackupTable");
                    modemTable.TableStyle = OfficeOpenXml.Table.TableStyles.Medium2;
                    modemTable.ShowFilter = true;
                }

                string directory = Path.GetDirectoryName(_imeiCacheFilePath) ?? AppPaths.RuntimeDirectory;
                string tempPath = Path.Combine(directory, "imei_backup.tmp.xlsx");
                if (File.Exists(tempPath)) File.Delete(tempPath);
                package.SaveAs(new FileInfo(tempPath));
                tempSnapshotReady = true;

                if (File.Exists(_imeiCacheFilePath))
                {
                    string backupPath = Path.Combine(directory, "imei_backup.backup.xlsx");
                    File.Copy(_imeiCacheFilePath, backupPath, overwrite: true);
                }

                File.Move(tempPath, _imeiCacheFilePath, overwrite: true);
                primarySaved = true;
                if (File.Exists(_pendingImeiCacheFilePath)) File.Delete(_pendingImeiCacheFilePath);
            }
            catch (Exception ex)
            {
                if (primarySaved)
                {
                    // The authoritative workbook is already atomically in place.
                    // Failure to remove an older lower-priority snapshot is only
                    // cleanup; LoadImeiCacheWorkbook reads the primary last.
                    AddLog($"Đã lưu imei_backup.xlsx; chưa xóa được snapshot cũ: {ex.Message}", "WARN");
                    return;
                }

                // Excel may lock the main workbook while the user is viewing it. Keep the
                // complete snapshot separately so accepted SIMs survive a restart and are
                // merged automatically on the next successful save.
                bool pendingSaved = false;
                try
                {
                    string directory = Path.GetDirectoryName(_imeiCacheFilePath) ?? AppPaths.RuntimeDirectory;
                    string tempPath = Path.Combine(directory, "imei_backup.tmp.xlsx");
                    if (tempSnapshotReady && File.Exists(tempPath))
                    {
                        File.Move(tempPath, _pendingImeiCacheFilePath, overwrite: true);
                        pendingSaved = File.Exists(_pendingImeiCacheFilePath);
                    }
                }
                catch (Exception pendingEx)
                {
                    AddLog($"Lỗi lưu snapshot IMEI dự phòng: {pendingEx.Message}", "ERROR");
                }
                AddLog($"Lỗi ghi file imei_backup.xlsx: {ex.Message}", "ERROR");
                if (!pendingSaved)
                    throw new IOException("Không lưu được imei_backup.xlsx hoặc snapshot dự phòng.", ex);
            }
        }
    }

    internal static string ResolveNetworkProviderFromCcid(string? ccid)
    {
        string digits = Regex.Replace(ccid ?? string.Empty, @"\D", string.Empty);
        if (digits.StartsWith("898402", StringComparison.Ordinal)) return "VinaPhone";
        if (digits.StartsWith("898404", StringComparison.Ordinal)) return "Viettel";
        if (digits.StartsWith("898401", StringComparison.Ordinal)) return "MobiFone";
        if (digits.StartsWith("898405", StringComparison.Ordinal)) return "Vietnamobile";
        return string.Empty;
    }

    internal static string NormalizeNetworkProvider(string? parsedOperator)
    {
        string provider = parsedOperator?.Trim() ?? string.Empty;
        string upper = provider.ToUpperInvariant();
        if (upper.Contains("VINAPHONE")) return "VinaPhone";
        if (upper.Contains("VIETTEL")) return "Viettel";
        if (upper.Contains("MOBIFONE")) return "MobiFone";
        if (upper.Contains("VIETNAMOBILE")) return "Vietnamobile";
        if (upper.Contains("VNSKY")) return "VNSKY";
        return "No Signal";
    }

    internal static string ExtractPhoneNumberFromUssd(string? content)
    {
        string text = content ?? string.Empty;
        Match match = Regex.Match(
            text,
            @"(?:\bTB\b|thuê bao|thue bao|so tb|số tb|msisdn|sim)[^\d]{0,15}(?<phone>(?:0|84)\d{9,10})",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(
                text,
                @"(?<!\d)(?<phone>(?:84|0)(?:3[2-9]|8[1-9]|9[1-9])\d{7})(?!\d)");
        }
        if (!match.Success)
        {
            match = Regex.Match(
                text,
                @"(?<!\d)(?<phone>(?:84|0)[3-9][0-9]{8})(?!\d)");
        }
        if (!match.Success)
        {
            match = Regex.Match(
                text,
                @"(?<!\d)(?<phone>[345789][0-9]{8})(?!\d)");
        }
        if (!match.Success) return string.Empty;

        string phone = match.Groups["phone"].Success
            ? match.Groups["phone"].Value
            : match.Value;
        if (phone.StartsWith("84", StringComparison.Ordinal))
            phone = "0" + phone[2..];
        else if (!phone.StartsWith("0", StringComparison.Ordinal))
            phone = "0" + phone;
        return phone;
    }

    internal static string ExtractSimRegDateFromUssd(string? content)
    {
        Match match = Regex.Match(
            content ?? string.Empty,
            @"(?:Ngay\s*KH|Ngay\s*kich\s*hoat|Ngay\s*DK|Ngay\s*dang\s*ky)[^\d]{0,15}(?<date>\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4})",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["date"].Value : string.Empty;
    }

    internal static string ExtractPromotionBalanceFromUssd(string? content)
    {
        Match match = Regex.Match(
            content ?? string.Empty,
            @"(?:TK\s*KM|TKKM|Tai\s*khoan\s*khuyen\s*mai|Tài\s*khoản\s*khuyến\s*mãi|Khuyen\s*mai|Khuyến\s*mãi)[^\d]{0,20}(?<balance>\d+[\.\,]\d+|\d+)\s*(?:d|đ|vnd|vnđ|dong|đồng)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["balance"].Value : string.Empty;
    }

    internal static bool TryParseCsqResponse(string? response, out int rssi, out int percent)
    {
        rssi = 99;
        percent = 0;
        Match match = Regex.Match(response ?? string.Empty, @"\+CSQ:\s*(\d{1,2})\s*,", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int parsed)) return false;

        rssi = parsed;
        if (parsed is >= 0 and <= 31)
            percent = (int)Math.Round(parsed / 31d * 100d, MidpointRounding.AwayFromZero);
        return true;
    }

    public void ExportImeiBackupWorkbook(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

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

    private static string NormalizeModemBackupKey(string? portName) =>
        string.IsNullOrWhiteSpace(portName)
            ? string.Empty
            : portName.Trim().ToUpperInvariant();

    private static string NormalizeCcid(string? ccid)
    {
        if (string.IsNullOrWhiteSpace(ccid)) return string.Empty;
        // ICCID is numeric. Never turn a modem error into a fake non-empty
        // identity and invalidate a SIM that is still physically inserted.
        var match = Regex.Match(ccid, @"\b(\d{18,22})\b");
        if (match.Success) return match.Groups[1].Value;
        return string.Empty;
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

    internal void BeginShutdown(bool disconnectModems = true)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _lifetimeCts.Cancel();
            }

            _portSessions.InvalidateAll();
            _backgroundSupervisor.Stop();
            _firebaseService.Stop();
            _fileLogChannel.Writer.TryComplete();
        }

        if (disconnectModems)
            DisconnectModemsForShutdown();
    }

    private void NotifyService_TelegramStatus(string status) =>
        AddLog(status, status.Contains(
            "DELIVERED", StringComparison.OrdinalIgnoreCase)
                ? "SUCCESS"
                : status.Contains("RETRY", StringComparison.OrdinalIgnoreCase)
                  || status.Contains("PAUSED", StringComparison.OrdinalIgnoreCase)
                    ? "WARNING"
                    : "INFO");

    private void QueueTelegramSmsNotification(
        AppSettings config,
        string portName,
        string receiverPhone,
        string senderPhone,
        string extractedOtp,
        string content)
    {
        string chatIds = !string.IsNullOrWhiteSpace(config.TelegramChatIds)
            ? config.TelegramChatIds
            : config.TelegramChatId;
        if (string.IsNullOrWhiteSpace(config.TelegramBotToken)
            || string.IsNullOrWhiteSpace(chatIds))
        {
            AddLog(
                $"[{portName}] [TELEGRAM_CONFIG_MISSING] SMS chỉ được giữ trong hàng đợi RAM; hãy lưu Bot Token và Chat ID.",
                "WARNING");
            SnackbarMessageQueue.Enqueue(
                $"[{portName}] Telegram chưa cấu hình; SMS chỉ chờ gửi trong phiên hiện tại.");
        }

        string text = BuildTelegramSmsNotification(
            portName,
            receiverPhone,
            senderPhone,
            extractedOtp,
            content,
            DateTime.Now);
        _ = _notifyService.SendTelegramAsync(
            config.TelegramBotToken,
            chatIds,
            text);
    }

    internal static string BuildTelegramSmsNotification(
        string portName,
        string receiverPhone,
        string senderPhone,
        string extractedOtp,
        string content,
        DateTime receivedAt)
    {
        bool hasOtp = !string.IsNullOrWhiteSpace(extractedOtp)
            && !string.Equals(extractedOtp, "N/A", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            hasOtp ? "🔐 OTP mới" : "📩 SMS mới",
            $"Port: {System.Net.WebUtility.HtmlEncode(portName)}",
            $"SĐT: {System.Net.WebUtility.HtmlEncode(receiverPhone)}",
            $"Từ: {System.Net.WebUtility.HtmlEncode(senderPhone)}"
        };
        if (hasOtp)
            lines.Add($"OTP: <b>{System.Net.WebUtility.HtmlEncode(extractedOtp)}</b>");
        lines.Add($"Nội dung: {System.Net.WebUtility.HtmlEncode(content)}");
        lines.Add($"Time: {receivedAt:HH:mm:ss dd/MM}");
        return string.Join("\n", lines);
    }

    internal void DisconnectModemsForShutdown()
    {
        if (Interlocked.Exchange(ref _modemsDisconnected, 1) == 0)
            _modemService.DisconnectAll();
    }

    public void Dispose()
    {
        if (_disposed) return;

        BeginShutdown();

        _notifyService.TelegramStatus -= NotifyService_TelegramStatus;

        foreach (SimPort trackedPort in _stateTrackedPorts.Values)
            trackedPort.PropertyChanged -= PortState_PropertyChanged;
        _stateTrackedPorts.Clear();
        _lastLoggedPortStatuses.Clear();
        _firebaseService.Dispose();

        _activeCallers.Clear();

        _smsService.Dispose();
        _ussdService.Dispose();
        _backgroundSupervisor.Dispose();
        _portSessions.Dispose();

        try
        {
            if (_logFileWriterTask != null
                && !_logFileWriterTask.Wait(TimeSpan.FromSeconds(2)))
            {
                _logWriterCts.Cancel();
            }
        }
        catch { }
        _logWriterCts.Dispose();
        _lifetimeCts.Dispose();
        _disposed = true;
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
                finalResult = await RunSautoUssdAsync(portName, item.Content);
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

            bool commandFailed = IsOperationFailureResult(finalResult);
            if (cmdType.Equals("USSD", StringComparison.OrdinalIgnoreCase)
                || cmdType.Equals("SMS", StringComparison.OrdinalIgnoreCase)
                || cmdType.Equals("Call", StringComparison.OrdinalIgnoreCase))
            {
                // A command-panel USSD is complete asynchronously via +CUSD. Do not
                // turn the temporary "waiting for network" state into a false green
                // result; the direct USSD pipeline and configured startup flow set
                // USSD OK when their actual response is available.
                bool waitingForUssd = cmdType.Equals("USSD", StringComparison.OrdinalIgnoreCase)
                    && finalResult.Contains("Đang chờ", StringComparison.OrdinalIgnoreCase);
                if (!waitingForUssd)
                    SetOperationStatus(portName, cmdType, !commandFailed);
                else if (commandFailed)
                    SetOperationStatus(portName, cmdType, false);
            }
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
                if (item.Type.Equals("USSD", StringComparison.OrdinalIgnoreCase)
                    || item.Type.Equals("SMS", StringComparison.OrdinalIgnoreCase)
                    || item.Type.Equals("Call", StringComparison.OrdinalIgnoreCase))
                {
                    SetOperationStatus(portName, item.Type, false);
                }
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


