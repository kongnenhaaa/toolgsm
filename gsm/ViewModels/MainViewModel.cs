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
    private GsmBackgroundSupervisorContext? _backgroundSupervisorContext;
    private readonly Services.ImeiManagementService _imeiManagementService;
    private readonly SmsInboxStore _smsInboxStore;
    private readonly gsm.Services.INotifyService _notifyService = new gsm.Services.NotifyService();
    private readonly gsm.Services.IFirebaseOtpService _firebaseOtpService = new gsm.Services.FirebaseOtpService();
    private const int MaxSmsMessagesInMemory = 5000;
    private const int MaxOtpHistoryInMemory = 2000;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _sentConfirmations = new();
    public IGsmModemService ModemService => _modemService;

    private readonly FirebaseService _firebaseService;
    public ProxyManagerService ProxyManager { get; }
    private readonly ConcurrentDictionary<string, string> _callFailures = new();
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

    }

    private readonly ConcurrentDictionary<string, PendingMyVnptPasswordOperation> _pendingMyVnptPasswordPorts =
        new(StringComparer.OrdinalIgnoreCase);
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

    private readonly object _logFileLock = new();
    
    public event Action<string, string>? OtpReceivedEvent;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly PortCooldownGate _portCooldown = new();

    // Fix #3: Dùng static Random để tránh lỗi seed trùng khi gọi liên tiếp nhanh
    private static readonly Random _rng = new Random();

    // Đánh dấu cổng nào đang có SMS được gửi để USSD tự nhường đường (tránh tranh Semaphore)
    public ConcurrentDictionary<string, bool> SmsInProgressPorts => _smsService.InProgressPorts;

    // Give an incoming SMS/OTP time to be read before the optional balance USSD
    // that follows a successful outbound SMS takes the same modem channel.
    private static readonly TimeSpan AutoBalanceAfterSmsDelay = TimeSpan.FromMinutes(1);

    // Đánh dấu cổng nào đang trong quá trình khởi tạo SIM/IMEI để tránh khởi tạo song song
    // Lease riêng cho từng lần khởi tạo. Dùng bool khiến tác vụ cũ bị hủy có thể để
    // lại khóa vĩnh viễn hoặc xóa nhầm khóa của phiên SIM mới.
    private readonly ConcurrentDictionary<string, Guid> _initializingPorts = new();
    private readonly ConcurrentDictionary<string, byte> _initialBalanceLookupOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _initialSubscriberLookupCompleted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _initialAccountLookupCompleted = new(StringComparer.OrdinalIgnoreCase);
    // USSD timeout recovery must use the same full modem refresh as the UI
    // button, but only one refresh may own a COM at a time.  The cooldown also
    // prevents a permanently unavailable USSD service from rebooting a modem
    // every 30-second retry cycle.
    private readonly ConcurrentDictionary<string, byte> _automaticUssdRefreshOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _automaticUssdRefreshLastAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AutomaticUssdRefreshCooldown = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, byte> _networkReopenOwners = new(StringComparer.OrdinalIgnoreCase);
    // Pha ghi IMEI đã kết thúc và commit trước khi vòng dò mạng bắt đầu, nên
    // reopen chỉ được phép sửa pha mạng. Mỗi (COM + CCID) chỉ có ngân sách
    // reopen hữu hạn; hết ngân sách thì kết luận "không có nhà mạng" thay vì
    // để COM lặp reopen -> resume -> chờ COPS vô hạn ở "Đang xử lý".
    private readonly ConcurrentDictionary<string, int> _networkReopenAttempts = new(StringComparer.OrdinalIgnoreCase);
    internal const int MaxNetworkReopenAttemptsPerSim = 3;
    private readonly ConcurrentDictionary<string, byte> _targetedRecoveryPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _managedRecoveryPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _serviceReconnectRetryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _imeiVerificationRecoveryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _imeiVerificationRecoveryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxImeiVerificationRecoveryAttempts = 2;
    private readonly ConcurrentDictionary<string, byte> _imeiMismatchRepairOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _imeiMismatchRepairAttempts = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxImeiMismatchRepairAttempts = 2;
    private readonly ConcurrentDictionary<string, byte> _pendingNoSimRetryOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ImeiInitializationTimeout = TimeSpan.FromMinutes(3);
    // Accepted IMEI for the current application lifetime. A manual COM refresh must
    // verify and resume this assignment instead of returning the same SIM to the
    // action-required state and allowing a second generated IMEI.
    private readonly ConcurrentDictionary<string, string> _verifiedImeiByCcid = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly PendingNoSimImeiJournal _pendingNoSimImeiJournal = new(
        AppPaths.ForUserDataFile("imei_pending_no_sim.json"),
        AppPaths.ForUserDataFile("imei_pending_no_sim.pending.json"));
    private static readonly string[] ImeiBackupColumns =
    [
        "CCID", "IMEI", "PhoneNumber", "NetworkProvider", "Balance", "PromotionBalance",
        "ExpiryDate", "SimRegDate", "Lock1C", "Lock2C", "CreatedAt", "UpdatedAt",
        "LastPortName", "DeviceName", "HardwareName", "ModemManufacturer", "ModemModel",
        "ModemFirmware", "ModemCapabilities", "Status", "SignalStrength", "SourceFile"
    ];
    private static readonly string[] ModemImeiBackupColumns =
    [
        "PortName", "IMEI", "CreatedAt", "UpdatedAt", "HardwareName",
        "ModemManufacturer", "ModemModel", "ModemFirmware", "SourceFile"
    ];
    private ConcurrentDictionary<string, SimBackupEntry> _imeiCache = new();
    public IReadOnlyDictionary<string, SimBackupEntry> ImeiCache => _imeiCache;
    private ConcurrentDictionary<string, ModemImeiBackupEntry> _modemImeiCache =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, ModemImeiBackupEntry> ModemImeiCache => _modemImeiCache;

    public IReadOnlySet<string> GetKnownImeiTargetsSnapshot()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        lock (_imeiCacheLock)
        {
            foreach (string imei in _imeiCache.Values.Select(entry => entry.Imei))
                known.Add(NormalizeImei(imei));
            foreach (string imei in _modemImeiCache.Values.Select(entry => entry.Imei))
                known.Add(NormalizeImei(imei));
        }

        foreach (string imei in _verifiedImeiByCcid.Values)
            known.Add(NormalizeImei(imei));
        foreach (string imei in _pendingNoSimImeiJournal.GetImeiSnapshot())
            known.Add(NormalizeImei(imei));
        foreach (string imei in _imeiTargetReservations.Keys)
            known.Add(NormalizeImei(imei));
        foreach (SimPort port in GetPortsSnapshot())
            known.Add(NormalizeImei(port.Imei));

        known.RemoveWhere(imei =>
            !Services.ImeiManagementService.IsValidImei(imei));
        return known;
    }

    public bool TryReserveBatchImeiTarget(
        string owner,
        string portName,
        string ccid,
        string targetImei)
    {
        string target = NormalizeImei(targetImei);
        string expectedCcid = NormalizeCcid(ccid);
        if (string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(portName)
            || expectedCcid.Length != 20
            || !Services.ImeiManagementService.IsValidImei(target))
        {
            return false;
        }

        if (_imeiTargetReservations.TryGetValue(target, out string? existingOwner)
            && !string.Equals(existingOwner, owner, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_imeiCacheLock)
        {
            if (_imeiCache.Any(pair =>
                    !string.Equals(
                        NormalizeCcid(pair.Key),
                        expectedCcid,
                        StringComparison.Ordinal)
                    && Services.ImeiManagementService.AreEquivalentImei(
                        pair.Value.Imei, target)))
            {
                return false;
            }
            if (_modemImeiCache.Values.Any(entry =>
                    !string.Equals(
                        entry.PortName,
                        portName,
                        StringComparison.OrdinalIgnoreCase)
                    && Services.ImeiManagementService.AreEquivalentImei(
                        entry.Imei, target)))
            {
                return false;
            }
        }

        if (_verifiedImeiByCcid.Any(pair =>
                !string.Equals(
                    NormalizeCcid(pair.Key),
                    expectedCcid,
                    StringComparison.Ordinal)
                && Services.ImeiManagementService.AreEquivalentImei(
                    pair.Value, target)))
        {
            return false;
        }
        if (_pendingNoSimImeiJournal.GetEntriesSnapshot().Any(entry =>
                (!string.Equals(
                     entry.PortName,
                     portName,
                     StringComparison.OrdinalIgnoreCase)
                 || (!string.IsNullOrWhiteSpace(entry.ExpectedCcid)
                     && !string.Equals(
                         NormalizeCcid(entry.ExpectedCcid),
                         expectedCcid,
                         StringComparison.Ordinal)))
                && Services.ImeiManagementService.AreEquivalentImei(
                    entry.TargetImei, target)))
        {
            return false;
        }
        if (GetPortsSnapshot().Any(port =>
                (!string.Equals(
                     port.PortName,
                     portName,
                     StringComparison.OrdinalIgnoreCase)
                 || !string.Equals(
                     NormalizeCcid(port.Serial),
                     expectedCcid,
                     StringComparison.Ordinal))
                && Services.ImeiManagementService.AreEquivalentImei(
                    port.Imei, target)))
        {
            return false;
        }

        string actualOwner = _imeiTargetReservations.GetOrAdd(target, owner);
        return string.Equals(actualOwner, owner, StringComparison.Ordinal);
    }

    public void ReleaseBatchImeiReservations(string owner) =>
        ReleaseImeiReservations(owner);
    private readonly object _imeiCacheLock = new();
    private readonly ConcurrentDictionary<string, string> _imeiTargetReservations =
        new(StringComparer.Ordinal);
    // IMEI vừa được SAuto ghi/xác minh khi chưa có SIM. Journal nguyên tử giữ
    // mục tiêu qua cả app restart/mất điện; khi SIM được cắm vào, slot 7 và CCID
    // vẫn phải được xác minh trước khi mục tiêu được gắn vĩnh viễn cho SIM.
    private readonly ConcurrentDictionary<string, string> _deferredDetectedCcids =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _deferredCcidOwners =
        new(StringComparer.OrdinalIgnoreCase);

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

    internal static IReadOnlyList<string> SautoInitial111CommandOrder { get; } =
    [
        "AT+CUSD=2",
        "AT+CUSD=1",
        "AT+CUSD=1,\"*111#\",15"
    ];

    internal static IReadOnlyList<string> SautoInitial101CommandOrder { get; } =
    [
        "AT+CUSD=2",
        "AT+CUSD=1",
        "AT+CUSD=1,\"002A0031003000310023\",15"
    ];

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
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
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
        IGsmBackgroundSupervisor backgroundSupervisor,
        ImeiManagementService imeiManagementService,
        SmsInboxStore? smsInboxStore = null)
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
        AppSettings = SettingsService.Current;
        _imeiManagementService = imeiManagementService;
        _smsInboxStore = smsInboxStore ?? new SmsInboxStore();
        _modemService.LogMessage += ModemService_LogMessage;
        _modemService.SmsReceived += ModemService_SmsReceived;
        _modemService.PortDisconnected += ModemService_PortDisconnected;
        _modemService.CallIncoming += ModemService_CallIncoming;
        _modemService.CallEnded += ModemService_CallEnded;
        _modemService.DtmfReceived += ModemService_DtmfReceived;
        _modemService.IncomingCallRinging += ModemService_IncomingCallRinging;
        _modemService.IncomingCallEnded += ModemService_IncomingCallEnded;

        InitializeHardware();
        LoadSmsInbox();
        
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
        _backgroundSupervisor.Start(_backgroundSupervisorContext, _lifetimeCts.Token);
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

    private void LoadSmsInbox()
    {
        foreach (SmsInboxRecord record in _smsInboxStore.GetRecent(MaxSmsMessagesInMemory))
            InsertSmsMessageBounded(ToSmsMessage(record));
        foreach (string warning in _smsInboxStore.RecoveryWarnings)
            AddLog($"[SMS_INBOX_RECOVERY] {warning}", "WARN");
    }

    private bool TryPersistSms(
        SmsInboxRecord record,
        out SmsMessage? message)
    {
        message = null;
        bool newlyCommitted;
        try
        {
            newlyCommitted = _smsInboxStore.Append(record);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or JsonException
                                   or NotSupportedException)
        {
            AddLog(
                $"[{record.PortName}] [SMS_INBOX_PERSIST_FAILED] delivery={record.DeliveryId}: {ex.Message}. Giữ nguyên SMS trên SIM.",
                "ERROR");
            return false;
        }

        // A replay after restart is acknowledged by DeliveryId but must not
        // repeat sounds, webhooks, OTP history or carrier-state side effects.
        if (!newlyCommitted)
            return true;

        message = ToSmsMessage(record);
        if (!SmsMessages.Any(existing =>
                string.Equals(existing.DeliveryId, record.DeliveryId, StringComparison.Ordinal)))
        {
            InsertSmsMessageBounded(message);
        }

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

    private static DateTimeOffset GetSmsDisplayTime(SmsMessage message) =>
        message.SmsTimestampUtc
        ?? (message.ReceivedAtUtc == default
            ? DateTimeOffset.UtcNow
            : message.ReceivedAtUtc);

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
                    var removedPorts = Ports.Where(p =>
                        !availablePorts.Contains(p.PortName)
                        && p.PortName != "COM_VIRTUAL"
                        && !_targetedRecoveryPorts.ContainsKey(p.PortName)).ToList();
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

    public Task RefreshPortAsync(
        string portName,
        CancellationToken cancellationToken = default) =>
        RefreshPortsAsync([portName], cancellationToken);

    public void RefreshAllPorts() => _ = RefreshPortsAsync(GetPortsSnapshot().Select(p => p.PortName));

    public async Task RefreshPortsAsync(
        IEnumerable<string> portNames,
        CancellationToken cancellationToken = default,
        bool resetNetworkReopenBudget = true)
    {
        var names = portNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return;

        foreach (string name in names)
        {
            _targetedRecoveryPorts[name] = 0;
            _managedRecoveryPorts[name] = 0;
            // Làm mới do user/recovery khác yêu cầu là một lần thử mới nên được
            // nạp lại ngân sách; reopen của chính vòng chờ COPS thì không.
            if (resetNetworkReopenBudget)
                ClearNetworkReopenBudget(name);
            InvalidateSimSession(name);
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var port in Ports.Where(p => names.Contains(p.PortName, StringComparer.OrdinalIgnoreCase)))
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang tự mở lại riêng COM...";
                port.LastError = "Đang phục hồi kết nối; IMEI đã xác minh vẫn được giữ theo CCID";
                port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            }
            AddLog($"Đang làm mới riêng {names.Count} cổng; không quét lại toàn bộ dàn...");
            UpdateDashboard();
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            cancellationToken);
        try
        {
            bool[] reconnected = await Task.WhenAll(names.Select(name =>
                ReconnectPortWithBackoffAsync(name, linkedCts.Token)));
            var failedPorts = names.Where((_, index) => !reconnected[index]).ToList();
            if (failedPorts.Count > 0)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var port in Ports.Where(
                        port => failedPorts.Contains(
                            port.PortName,
                            StringComparer.OrdinalIgnoreCase)))
                    {
                        port.Status = SimStatus.NoResponse;
                        port.DeviceName = "Không mở lại được COM sau 4 lần";
                        port.LastError = "Recovery riêng COM đã hết lượt; kiểm tra cáp/driver/nguồn";
                    }
                    UpdateDashboard();
                });
                AddLog(
                    $"Làm mới cổng thất bại: {string.Join(", ", failedPorts)}",
                    "ERROR");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"Làm mới cổng thất bại: {ex.Message}", "ERROR");
        }
        finally
        {
            foreach (string name in names)
            {
                _managedRecoveryPorts.TryRemove(name, out _);
                _targetedRecoveryPorts.TryRemove(name, out _);
            }
        }
    }

    private async Task<bool> ReconnectPortWithBackoffAsync(
        string portName,
        CancellationToken token)
    {
        TimeSpan[] delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        ];

        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > TimeSpan.Zero)
                await Task.Delay(delays[attempt], token);

            if (await _modemService.ReconnectPortAsync(
                portName, 115200, token))
            {
                if (attempt > 0)
                    AddLog($"[{portName}] [PORT_RECOVERY_OK] Mở lại thành công ở lần {attempt + 1}/4.", "SUCCESS");
                return true;
            }

            AddLog($"[{portName}] [PORT_RECOVERY_RETRY] Mở lại chưa thành công (lần {attempt + 1}/4).", "WARN");
        }

        return false;
    }

    private void ScheduleServiceReconnectRetry(string portName)
    {
        if (!_serviceReconnectRetryOwners.TryAdd(portName, 0)) return;
        _targetedRecoveryPorts[portName] = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _lifetimeCts.Token);
                if (GetPortsSnapshot().Any(port =>
                    string.Equals(
                        port.PortName,
                        portName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    AddLog($"[{portName}] [PORT_RECONNECT_REQUEUE] Reconnect trực tiếp thất bại; chuyển sang recovery riêng COM có backoff.", "WARN");
                    await RefreshPortAsync(portName);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _serviceReconnectRetryOwners.TryRemove(portName, out _);
                if (!_managedRecoveryPorts.ContainsKey(portName))
                    _targetedRecoveryPorts.TryRemove(portName, out _);
            }
        }, _lifetimeCts.Token);
    }

    private (long Epoch, CancellationToken Token) StartSimSession(string portName, string ccid)
    {
        var session = _portSessions.Begin(portName, ccid, _lifetimeCts.Token);
        return (session.Epoch, session.Token);
    }

    private void InvalidateSimSession(string portName)
    {
        // Tắt cờ của phiên cũ khi SIM bị mất/thay; cờ sẽ được bật lại ngay khi
        // pipeline đọc được CCID của SIM mới (kể cả SIM đang chờ thao tác IMEI).
        _modemService.SetSimRemovalWatchEnabled(portName, false);
        _portSessions.Invalidate(portName);
        _initializingPorts.TryRemove(portName, out _);
        string prefix = portName + "|";
        foreach (string key in _initialAccountLookupCompleted.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _initialAccountLookupCompleted.TryRemove(key, out _);
        }
        foreach (string key in _initialSubscriberLookupCompleted.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _initialSubscriberLookupCompleted.TryRemove(key, out _);
        }
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

    internal static bool IsRadioStackDisabled(string? cfunResponse) =>
        Regex.IsMatch(cfunResponse ?? string.Empty, @"\+CFUN:\s*(?:0|4)\b", RegexOptions.IgnoreCase);

    internal static bool IsVerifiedModemIdentity(
        string? cfunResponse,
        bool radioMustBeOff,
        string? liveCcid,
        string? expectedCcid,
        string? liveImei,
        string? expectedImei,
        bool sessionCurrent)
    {
        bool cfunMatches = radioMustBeOff
            ? IsRadioStackDisabled(cfunResponse)
            : Regex.IsMatch(
                cfunResponse ?? string.Empty,
                @"\+CFUN:\s*1\b",
                RegexOptions.IgnoreCase);
        return sessionCurrent
            && cfunMatches
            && string.Equals(
                NormalizeCcid(liveCcid),
                NormalizeCcid(expectedCcid),
                StringComparison.OrdinalIgnoreCase)
            && Services.ImeiManagementService.AreEquivalentImei(
                liveImei,
                expectedImei);
    }

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
            string rawStoredImei = await _modemService.SendCommandAsync(
                port.PortName, "AT+EGMR=0,7;", 8000, silent: true, ct: token);
            string liveImei = Regex.Match(rawStoredImei ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
            string liveCcid = await ReadLiveCcidAsync(port.PortName, token, attempts: ccidAttempts);

            bool valid = IsVerifiedModemIdentity(
                cfun,
                radioMustBeOff,
                liveCcid,
                ccid,
                liveImei,
                expectedImei,
                IsSimSessionCurrent(port.PortName, ccid, epoch));

            AddLog($"[{port.PortName}] [{(valid ? "IMEI_VERIFY_OK" : "IMEI_VERIFY")}] phase={phase}; CFUN={cfun.Trim()}; expected={expectedImei}; EGMR_slot7={liveImei}; CCID={liveCcid}",
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

            bool ussdChannelReady = await EnsurePostImeiUssdChannelAsync(
                port, ccid, epoch, token);
            AddLog(
                ussdChannelReady
                    ? $"[{port.PortName}] [IMEI_RESUME_USSD_READY] Kênh AT/URC và CUSD=1 đã khớp sau resume."
                    : $"[{port.PortName}] [IMEI_RESUME_USSD_PENDING] IMEI/CCID đúng nhưng CUSD=1 chưa xác minh được; USSD sẽ tự kiểm tra lại trước khi gửi.",
                ussdChannelReady ? "SUCCESS" : "WARN");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
                port.IsRebooting = false;
                port.Imei = afterRadio.Imei;
                port.Serial = NormalizeCcid(ccid);
                MarkPortIdentityReadyForNetwork(port.PortName);
            });

            activationSucceeded = IsSimSessionCurrent(port.PortName, ccid, epoch)
                && IsVerifiedIdentityReadyForNetwork(
                    port, ccid, afterRadio.Imei, sessionCurrent: true);
            if (activationSucceeded)
            {
                _modemService.StartPollingNetwork(
                    port.PortName,
                    ccid,
                    afterRadio.Imei);
                // Voice URCs are optional setup. Defer them until the modem has
                // had a chance to attach so CLIP/DSCI/QTONEDET cannot contend
                // with the first COPS/USSD pass or an immediate IMEI action.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                            && port.Status == SimStatus.Active)
                        {
                            await _modemService.ConfigureVoiceFeaturesAsync(port.PortName, token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog($"[{port.PortName}] [IMEI_RESUME_VOICE] {ex.Message}", "INFO");
                    }
                }, token);
            }
            return activationSucceeded;
        }
        finally
        {
            // Cancellation/timeout/exception sau CFUN=1 không được để RF bật ngoài kiểm soát.
            if (radioMayBeOn && !activationSucceeded)
                await ForceRadioOffBestEffortAsync();
        }
    }

    private enum SautoResetFailureKind
    {
        None,
        TransientSimNotReady,
        SimRemoved,
        IdentityMismatch,
        SessionChanged
    }

    private readonly record struct SautoResetResult(
        bool IdentityReady,
        SautoResetFailureKind FailureKind);

    private async Task<SautoResetResult> CompleteSautoResetAsync(
        SimPort port, string ccid, string expectedImei, long epoch, CancellationToken token,
        Action? releaseBackgroundOperations = null)
    {
        // CFUN=1,1 was already issued after slot 7 readback. Probe readiness
        // immediately instead of sleeping a blind 10 seconds, and let the normal
        // network loop take over as soon as the UART starts answering again.
        // The IMEI was read back before CFUN=1,1. Only give the reboot a short
        // grace window here; network polling is allowed to finish the attach.
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        bool sawIdentityMismatch = false;
        bool identityVerifiedAfterReset = false;
        bool modemResponded = false;
        int attempt = 0;

        async Task<SautoResetResult> ActivateAsync(string phase)
        {
            // Slot 7 is modem-wide; it does not prove that the physical SIM is
            // still the same one after CFUN=1,1. Re-read the live CCID after the
            // radio/SIM stack is back before committing the accepted mapping.
            string liveCcid = await ReadLiveCcidAsync(
                port.PortName, token, attempts: 4);
            if (!string.Equals(
                liveCcid,
                NormalizeCcid(ccid),
                StringComparison.OrdinalIgnoreCase))
            {
                AddLog(
                    $"[{port.PortName}] [SAUTO_RESET_CCID_FAILED] phase={phase}; expected={NormalizeCcid(ccid)}; live={liveCcid}; không commit IMEI.",
                    "ERROR");
                try
                {
                    await _modemService.SendCommandAsync(
                        port.PortName,
                        "AT+CFUN=4",
                        5000,
                        silent: true,
                        ct: CancellationToken.None);
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(liveCcid))
                {
                    InvalidateSimSession(port.PortName);
                    return new SautoResetResult(
                        false, SautoResetFailureKind.SessionChanged);
                }

                return new SautoResetResult(
                    false, SautoResetFailureKind.TransientSimNotReady);
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return;
                port.IsRebooting = false;
                port.Imei = expectedImei;
                port.Serial = NormalizeCcid(ccid);
                MarkPortIdentityReadyForNetwork(port.PortName);
            });

            bool identityReady = IsSimSessionCurrent(port.PortName, ccid, epoch)
                && IsVerifiedIdentityReadyForNetwork(
                    port, ccid, expectedImei, sessionCurrent: true);
            if (identityReady)
            {
                // Chỉ chạy sau khi slot 7 và CCID đã xác minh đúng. Đây là
                // phần bàn giao sau reboot IMEI, không thay đổi chuỗi SAuto.
                bool ussdChannelReady = await EnsurePostImeiUssdChannelAsync(
                    port, ccid, epoch, token);
                AddLog(
                    ussdChannelReady
                        ? $"[{port.PortName}] [IMEI_POST_USSD_READY] Kênh AT/URC và CUSD=1 đã khớp sau reboot IMEI."
                        : $"[{port.PortName}] [IMEI_POST_USSD_PENDING] IMEI/CCID đúng nhưng CUSD=1 chưa xác minh được; USSD sẽ tự kiểm tra lại trước khi gửi.",
                    ussdChannelReady ? "SUCCESS" : "WARN");

                // The IMEI write was already read back successfully before CFUN=1,1.
                // Release this COM as soon as the modem is usable; do not keep the
                // whole bank behind the foreground IMEI lease while waiting for
                // optional voice setup or the first COPS result.
                releaseBackgroundOperations?.Invoke();
                _modemService.StartPollingNetwork(
                    port.PortName,
                    ccid,
                    expectedImei);
                AddLog($"[{port.PortName}] [IMEI_RESUME_NETWORK] phase={phase}; RF thả tự do, bắt đầu dò COPS/USSD.", "SUCCESS");

                // Voice URCs are not part of the IMEI/network critical path. Run
                // their best-effort setup after the first registration window so
                // CLIP/DSCI cannot delay COPS or the configured startup USSD on this COM.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                            && port.Status == SimStatus.Active)
                        {
                            await _modemService.ConfigureVoiceFeaturesAsync(port.PortName, token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog($"[{port.PortName}] [IMEI_RESUME_VOICE] {ex.Message}", "INFO");
                    }
                }, token);
            }

            return new SautoResetResult(identityReady, identityReady
                ? SautoResetFailureKind.None
                : SautoResetFailureKind.TransientSimNotReady);
        }

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch))
                return new SautoResetResult(false, SautoResetFailureKind.SessionChanged);

            attempt++;
            string cpin = await _modemService.SendCommandAsync(
                port.PortName, "AT+CPIN?", 3000, silent: true, ct: token);
            bool cpinCommandFailed = cpin.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || cpin.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || cpin.Contains("not open", StringComparison.OrdinalIgnoreCase);
            if (!cpinCommandFailed && !string.IsNullOrWhiteSpace(cpin)) modemResponded = true;

            if (cpin.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase))
            {
                InvalidateSimSession(port.PortName);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ClearSimScopedState(port);
                    port.Status = "Chờ cắm SIM";
                    port.DeviceName = "Đang chờ cắm SIM (Hot-plug).";
                    UpdateDashboard();
                });
                return new SautoResetResult(false, SautoResetFailureKind.SimRemoved);
            }

            bool cpinReady = Regex.IsMatch(cpin, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
            string storedImei = string.Empty;
            // Slot 7 is useful for mismatch detection, but reading it on every
            // boot probe adds several seconds on EC20 firmware that is still
            // opening the NV/SIM stack. Read it when CPIN is ready and periodically
            // while the modem is coming back.
            if (cpinReady || attempt % 4 == 0)
            {
                string storedResponse = await _modemService.SendCommandAsync(
                    port.PortName, "AT+EGMR=0,7;", 4000, silent: true, ct: token);
                storedImei = Regex.Match(storedResponse ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
                identityVerifiedAfterReset = ImeiManagementService.AreEquivalentImei(
                    storedImei, expectedImei);
                if (!string.IsNullOrWhiteSpace(storedImei)
                    && !identityVerifiedAfterReset)
                {
                    sawIdentityMismatch = true;
                }
            }

            bool ready = cpinReady
                && identityVerifiedAfterReset
                && IsSimSessionCurrent(port.PortName, ccid, epoch);
            AddLog($"[{port.PortName}] [SAUTO_RESET_VERIFY] attempt={attempt}; CPIN={cpin.Trim()}; slot7={storedImei}; expected={expectedImei}; ready={ready.ToString().ToLowerInvariant()}",
                ready ? "SUCCESS" : "INFO");

            if (ready) return await ActivateAsync("verified");

            // CPIN: NOT READY/OK is a normal post-reset transient, not a reason
            // to hold this COM behind the IMEI lease. The write was already
            // verified before reboot, so let the per-port polling loop wait for
            // CPIN/COPS while USSD recovery proceeds as soon as registration is
            // reported.
            if (modemResponded
                && attempt >= 2
                && !sawIdentityMismatch
                && identityVerifiedAfterReset
                && !cpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                && !cpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase)
                && !cpin.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"[{port.PortName}] [IMEI_VERIFY_DEFERRED] CPIN còn chuyển trạng thái ({cpin.Trim()}); nhả COM cho polling mạng.", "INFO");
                return await ActivateAsync("deferred");
            }
            await Task.Delay(500, token);
        }

        if (sawIdentityMismatch)
        {
            // A confirmed mismatch is different from a slow reboot: keep RF off
            // for that explicit failure, but never turn it off for a transient
            // CPIN/USB timeout.
            await _modemService.SendCommandAsync(port.PortName, "AT+CFUN=4", 5000, silent: true);
            return new SautoResetResult(false, SautoResetFailureKind.IdentityMismatch);
        }

        if (modemResponded
            && identityVerifiedAfterReset
            && IsSimSessionCurrent(port.PortName, ccid, epoch))
        {
            AddLog($"[{port.PortName}] [IMEI_VERIFY_DEFERRED] Modem đã phản hồi nhưng CPIN/slot 7 còn chậm; nhả RF và để polling tự hoàn tất.", "WARN");
            return await ActivateAsync("deferred");
        }

        // Never leave RF enabled with an unverified post-reset identity. The
        // target was written correctly before reboot, but a modem that fails to
        // expose slot 7 after reboot must remain offline until the recovery pass
        // can verify the new IMEI again.
        if (!identityVerifiedAfterReset && IsSimSessionCurrent(port.PortName, ccid, epoch))
        {
            AddLog($"[{port.PortName}] [IMEI_VERIFY_HOLD] Chưa đọc lại được slot 7 sau reboot; giữ RF tắt để không lộ IMEI cũ.", "WARN");
            await _modemService.SendCommandAsync(
                port.PortName, "AT+CFUN=4", 5000, silent: true);
        }

        return new SautoResetResult(false, SautoResetFailureKind.TransientSimNotReady);
    }

    private void ResetInitialUssdStateAfterNewImei(string portName, string ccid)
    {
        string prefix = $"{portName}|{NormalizeCcid(ccid)}|";
        foreach (string key in _initialAccountLookupCompleted.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _initialAccountLookupCompleted.TryRemove(key, out _);
        }
        foreach (string key in _initialSubscriberLookupCompleted.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _initialSubscriberLookupCompleted.TryRemove(key, out _);
        }

        _automaticUssdRefreshLastAt.TryRemove(
            $"{portName}|{NormalizeCcid(ccid)}",
            out _);
    }

    private async Task<bool> EnsurePostImeiUssdChannelAsync(
        SimPort port,
        string ccid,
        long epoch,
        CancellationToken token)
    {
        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)) return false;

        var commands = new List<string>
        {
            "AT+CMGF=1",
            "AT+CSCS=\"GSM\"",
            "AT+CNMI=1,1,0,0,0"
        };
        if (_modemService.GetModemProfile(port.PortName)?.Supports(
                ModemCapability.UrcPortRouting) == true)
        {
            commands.Add("AT+QURCCFG=\"urcport\",\"uart1\"");
        }

        foreach (string command in commands)
        {
            string response = await _modemService.SendCommandAsync(
                port.PortName, command, 5000, silent: true, ct: token);
            if (response.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Đóng ngữ cảnh USSD cũ nếu còn, sau đó bật và đọc lại bit phát URC.
        // CUSD=2 là best-effort vì modem vừa reboot thường chưa có phiên.
        await _modemService.SendCommandAsync(
            port.PortName, "AT+CUSD=2", 5000, silent: true, ct: token);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string enable = await _modemService.SendCommandAsync(
                port.PortName, "AT+CUSD=1", 5000, silent: true, ct: token);
            if (enable.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return false;

            string state = await _modemService.SendCommandAsync(
                port.PortName, "AT+CUSD?", 5000, silent: true, ct: token);
            if (Regex.IsMatch(state, @"\+CUSD:\s*1\b", RegexOptions.IgnoreCase)
                || !Regex.IsMatch(state, @"\+CUSD:\s*0\b", RegexOptions.IgnoreCase))
                return true;

            if (attempt < 2)
                await Task.Delay(150, token);
        }

        return false;
    }

    private void ScheduleImeiVerificationRecovery(
        string portName, string ccid, long epoch)
    {
        string recoveryKey = $"{portName}|{NormalizeCcid(ccid)}|{epoch}";
        string attemptKey = BuildImeiRecoveryCounterKey(portName, ccid);
        if (!_imeiVerificationRecoveryOwners.TryAdd(recoveryKey, 0)) return;

        int attempt = _imeiVerificationRecoveryAttempts.AddOrUpdate(
            attemptKey, 1, (_, previous) => previous + 1);
        if (attempt > MaxImeiVerificationRecoveryAttempts)
        {
            _imeiVerificationRecoveryOwners.TryRemove(recoveryKey, out _);
            AddLog($"[{portName}] [IMEI_VERIFY_TRANSIENT] Đã thử tự khôi phục {MaxImeiVerificationRecoveryAttempts} lần; giữ NoResponse để không chặn nhầm SIM.", "ERROR");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetimeCts.Token);
                if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

                AddLog($"[{portName}] [IMEI_VERIFY_RECOVERY] Lần {attempt}/{MaxImeiVerificationRecoveryAttempts}; refresh riêng COM rồi chạy lại pipeline.", "INFO");
                await RefreshPortAsync(portName);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"[{portName}] [IMEI_VERIFY_RECOVERY] {ex.Message}", "ERROR");
            }
            finally
            {
                _imeiVerificationRecoveryOwners.TryRemove(recoveryKey, out _);
            }
        }, _lifetimeCts.Token);
    }

    private async Task RecoverImeiComAfterFailureAsync(
        SimPort port,
        string ccid,
        long epoch,
        string errorMessage,
        bool scheduleRefresh = true)
    {
        string portName = port.PortName;

        // A failed IMEI write may have left the modem in CFUN=4.  Force the
        // per-COM safe state before releasing the initialization lease; never
        // let a timeout leave the port looking busy while the radio is unknown.
        try
        {
            await _modemService.SendCommandAsync(
                portName, "AT+CFUN=4", 8000, silent: true, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddLog($"[{portName}] [IMEI_RECOVERY_RADIO_OFF] {ex.Message}", "WARN");
        }

        if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
            port.IsRebooting = false;
            port.Status = SimStatus.NoResponse;
            port.LastError = string.IsNullOrWhiteSpace(errorMessage)
                ? "Lỗi IMEI tạm thời; COM đang được tự khôi phục"
                : errorMessage;
            port.DeviceName = "IMEI lỗi – đang tự khôi phục COM...";
            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            UpdateDashboard();
        });

        AddLog($"[{portName}] [IMEI_RECOVERY_SCHEDULED] COM được trả về recovery riêng; lỗi={errorMessage}", "WARN");
        if (scheduleRefresh)
            ScheduleImeiVerificationRecovery(portName, ccid, epoch);
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
        // Fail closed: the session registry is not physical evidence. Falling
        // back to its expected CCID when QCCID/ICCID is empty can bind a
        // hot-swapped SIM to the previous accepted IMEI.
        bool matches = !string.IsNullOrWhiteSpace(liveCcid)
            && string.Equals(liveCcid, NormalizeCcid(ccid), StringComparison.OrdinalIgnoreCase)
            && IsSimSessionCurrent(portName, ccid, epoch);
        if (!matches)
            AddLog($"[{portName}] [SESSION_VERIFY_FAILED] expected_ccid={NormalizeCcid(ccid)} live_ccid={liveCcid} epoch={epoch}", "WARN");
        return matches;
    }

    private async Task ProcessCurrentSimSessionAsync(
        SimPort port, string ccid, bool forceAccept, long epoch, CancellationToken token,
        Guid initializationLease, string? explicitTargetImei = null,
        bool overwriteBackupWithCurrentImei = false,
        Action? releaseBackgroundOperations = null)
    {
        string portName = port.PortName;
        PendingImeiJournalEntry? durableOperation = null;
        using var initializationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        initializationCts.CancelAfter(ImeiInitializationTimeout);
        CancellationToken initializationToken = initializationCts.Token;
        try
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

            string currentImei = NormalizeImei(port.Imei);
            if (string.IsNullOrEmpty(currentImei))
            {
                string imeiResp = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true);
                currentImei = Regex.Match(imeiResp ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
            }

            if (string.IsNullOrEmpty(currentImei) || !IsSimSessionCurrent(portName, ccid, epoch))
            {
                AddLog($"[{portName}] Không đọc được IMEI hoặc phiên SIM đã thay đổi.", "WARN");
                if (IsSimSessionCurrent(portName, ccid, epoch))
                {
                    await RecoverImeiComAfterFailureAsync(
                        port, ccid, epoch, "Không đọc được IMEI khi khởi tạo");
                }
                return;
            }

            if (!IsSimSessionCurrent(portName, ccid, epoch))
            {
                AddLog($"[{portName}] Phiên CCID đã thay đổi trước khi xử lý IMEI; giữ sóng tắt và chờ phiên mới.", "WARN");
                await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Status = SimStatus.NoResponse;
                    port.LastError = "Phiên SIM đã thay đổi trước khi ghi IMEI";
                    port.DeviceName = "Đang chờ xác minh SIM mới...";
                    UpdateDashboard();
                });
                _modemService.StartHotplugWaitLoop(portName);
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() => port.Imei = currentImei);

            var result = await _imeiManagementService.ProcessImeiAsync(
                port,
                ccid,
                currentImei,
                AppSettings,
                queryCcid => FindImeiBackupEntry(queryCcid),
                newEntry =>
                {
                    if (overwriteBackupWithCurrentImei)
                        SaveLatestImeiCacheEntry(newEntry);
                    else
                        AddNewImeiCacheEntry(newEntry);
                },
                action => Application.Current.Dispatcher.Invoke(action),
                forceAccept,
                initializationToken,
                () => ValidateSessionIdentityAsync(
                    portName, ccid, epoch, initializationToken),
                candidate => IsImeiAssignedOrReserved(
                    candidate, portName, ccid),
                explicitTargetImei,
                overwriteBackupWithCurrentImei,
                persistTargetBeforeMutation: target =>
                    durableOperation = PrepareDurableImeiOperation(
                        portName,
                        target,
                        ccid,
                        overwriteBackupWithCurrentImei
                            ? PendingImeiOperationKind.CreateNew
                            : PendingImeiOperationKind.Restore));

            AddLog($"[{portName}] [IMEI_RESULT] status={result.Status} forceAccept={forceAccept} message={result.ErrorMessage}",
                result.Status is Services.ImeiProcessStatus.Matched or Services.ImeiProcessStatus.Applied ? "SUCCESS" : "INFO");

            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

            if (result.Status == Services.ImeiProcessStatus.Matched || result.Status == Services.ImeiProcessStatus.Applied)
            {
                if (durableOperation != null)
                {
                    try
                    {
                        _pendingNoSimImeiJournal.TryMarkPhase(
                            portName,
                            durableOperation.OperationId,
                            result.FinalImei,
                            PendingImeiOperationPhase.SlotVerified);
                    }
                    catch (Exception ex)
                    {
                        // Prepared is already durable and sufficient for replay;
                        // a phase-only update must never discard that ownership.
                        AddLog(
                            $"[{portName}] [IMEI_JOURNAL_PHASE_RETRY] {ex.Message}",
                            "WARN");
                    }
                }
                // Keep the target verified immediately before CFUN=1,1. If the
                // USB endpoint disappears or CPIN is slow after reboot, recovery
                // must resume this target instead of falling back to an older XLSX
                // value and falsely blocking the SIM.
                if (result.ModemResetRequested)
                {
                    string stagedImei = NormalizeImei(result.FinalImei);
                    if (Services.ImeiManagementService.IsValidImei(stagedImei))
                    {
                        _verifiedImeiByCcid[NormalizeCcid(ccid)] = stagedImei;
                        AddLog($"[{portName}] [IMEI_TARGET_STAGED] CCID={NormalizeCcid(ccid)}; IMEI={stagedImei}; giữ mục tiêu qua reboot/recovery.", "INFO");
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.Imei = result.FinalImei;
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang hoàn tất cấu hình modem...";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                });

                bool active;
                SautoResetFailureKind resetFailure = SautoResetFailureKind.None;
                if (result.ModemResetRequested)
                {
                    SautoResetResult resetResult = await CompleteSautoResetAsync(
                        port, ccid, result.FinalImei, epoch, initializationToken,
                        releaseBackgroundOperations);
                    active = resetResult.IdentityReady;
                    resetFailure = resetResult.FailureKind;
                }
                else
                {
                    active = await CompletePortInitializationAsync(
                        port, ccid, result.FinalImei, epoch, initializationToken);
                }
                if (active)
                {
                    _verifiedImeiByCcid[NormalizeCcid(ccid)] = NormalizeImei(result.FinalImei);
                    if (result.ModemResetRequested && overwriteBackupWithCurrentImei)
                    {
                        // Giữ nguyên toàn bộ chuỗi ghi/xác minh/reboot của SAuto.
                        // Chỉ xóa cờ USSD cũ để IMEI mới không bị bỏ qua *101# vì
                        // cùng COM/CCID/epoch đã từng hoàn tất lookup trước đó.
                        ResetInitialUssdStateAfterNewImei(portName, ccid);
                        AddLog(
                            $"[{portName}] [IMEI_NEW_USSD_RESET] Đã xóa trạng thái USSD cũ của đúng CCID; chờ COPS rồi chạy lại *101#.",
                            "INFO");
                    }
                }
                if (active)
                {
                    var existing = FindImeiBackupEntry(ccid);
                    // Kết quả radio-on đã xác minh mới là nguồn sự thật. Dùng
                    // AddNewImeiCacheEntry ở đây giữ IMEI cũ theo first-write-wins,
                    // khiến lần mở app sau chặn toàn bộ SIM vì slot 7 không khớp XLSX.
                    try
                    {
                        SaveLatestImeiCacheEntry(new SimBackupEntry
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
                    catch (Exception ex) when (overwriteBackupWithCurrentImei)
                    {
                        // Slot 7 may be verified, but Create-New is not complete
                        // until either the primary workbook or its pending
                        // snapshot is durable. Remove the volatile trusted target
                        // so recovery falls back to the last durable mapping, and
                        // do not report this Create-New operation as successful.
                        AddLog(
                            $"[{portName}] [IMEI_BACKUP_COMMIT_FAILED] IMEI mới đã xác minh nhưng chưa lưu bền vững: {ex.Message}",
                            "ERROR");
                        _verifiedImeiByCcid.TryRemove(
                            NormalizeCcid(ccid), out _);
                        throw new IOException(
                            "IMEI mới chưa được lưu vào kho chính hoặc snapshot dự phòng.",
                            ex);
                    }

                    if (durableOperation != null)
                    {
                        try
                        {
                            _pendingNoSimImeiJournal.Remove(
                                portName,
                                durableOperation.OperationId,
                                result.FinalImei,
                                ccid);
                        }
                        catch (Exception cleanupEx)
                        {
                            AddLog(
                                $"[{portName}] [IMEI_PENDING_CLEANUP] {cleanupEx.Message}",
                                "WARN");
                        }
                    }
                }
                else if (!active
                    && result.ModemResetRequested
                    && resetFailure == SautoResetFailureKind.TransientSimNotReady
                    && IsSimSessionCurrent(portName, ccid, epoch))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        port.Status = SimStatus.NoResponse;
                        port.LastError = "CPIN chưa sẵn sàng sau reboot; đang tự khôi phục COM";
                        port.DeviceName = "Đang tự khôi phục SIM/IMEI...";
                        UpdateDashboard();
                    });
                    AddLog($"[{portName}] [IMEI_VERIFY_TRANSIENT] Slot 7 chưa báo CPIN READY nhưng chưa phát hiện IMEI sai; tự refresh và chạy lại.", "WARN");
                    ScheduleImeiVerificationRecovery(portName, ccid, epoch);
                }
                else if (!active && IsSimSessionCurrent(portName, ccid, epoch))
                {
                    if (resetFailure == SautoResetFailureKind.IdentityMismatch)
                    {
                        // This target was explicitly accepted and read back
                        // before reboot, so it is trusted for this exact CCID.
                        // Retry the mismatch locally with a bounded owner instead
                        // of leaving the row permanently SecurityBlocked.
                        bool repairScheduled = ScheduleImeiMismatchRepair(
                            portName, ccid, result.FinalImei, epoch);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            port.Status = repairScheduled
                                ? SimStatus.NoResponse
                                : SimStatus.SecurityBlocked;
                            port.LastError = "IMEI sau reboot không khớp IMEI đã ghi";
                            port.DeviceName = repairScheduled
                                ? "IMEI sau reboot chưa khớp – đang tự sửa riêng COM..."
                                : "Đã chặn bảo mật – IMEI sau reboot không khớp";
                            UpdateDashboard();
                        });
                    }
                    else
                    {
                        await RecoverImeiComAfterFailureAsync(
                            port, ccid, epoch,
                            "Không hoàn tất được cấu hình/xác minh SIM sau khi tạo IMEI");
                    }
                }
            }
            else if (result.Status == Services.ImeiProcessStatus.WaitingAccept)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                    port.Status = SimStatus.WaitingAccept;
                    port.LastError = result.ErrorMessage;
                    port.DeviceName = "Chặn SIM – chọn Tạo IMEI mới hoặc Khôi phục IMEI";
                    port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
                    UpdateDashboard();
                });
            }
            else if (result.Status == Services.ImeiProcessStatus.SecurityBlocked)
            {
                if (durableOperation != null)
                {
                    try
                    {
                        _pendingNoSimImeiJournal.TryMarkPhase(
                            portName,
                            durableOperation.OperationId,
                            durableOperation.TargetImei,
                            PendingImeiOperationPhase.Blocked);
                    }
                    catch (Exception phaseEx)
                    {
                        AddLog(
                            $"[{portName}] [IMEI_JOURNAL_PHASE_RETRY] {phaseEx.Message}",
                            "WARN");
                    }
                }
                try
                {
                    // SecurityBlocked is reserved for a confirmed identity/policy
                    // failure. Keep RF disabled while the row is intentionally
                    // blocked; transient command failures are handled above as
                    // recoverable NoResponse instead.
                    await _modemService.SendCommandAsync(
                        portName, "AT+CFUN=4", 8000, silent: true, ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    AddLog($"[{portName}] [IMEI_BLOCK_RADIO_OFF] {ex.Message}", "WARN");
                }
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
                await RecoverImeiComAfterFailureAsync(
                    port, ccid, epoch, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await RecoverImeiComAfterFailureAsync(
                port, ccid, epoch, "Khởi tạo IMEI quá hạn 3 phút");
        }
        catch (OperationCanceledException)
        {
            await RecoverImeiComAfterFailureAsync(
                port, ccid, epoch, "Phiên IMEI bị hủy; COM đang được tự khôi phục", scheduleRefresh: false);
        }
        catch (Exception ex)
        {
            if (IsSimSessionCurrent(portName, ccid, epoch))
            {
                AddLog($"[{portName}] Lỗi xử lý phiên SIM: {ex.Message}", "ERROR");
                await RecoverImeiComAfterFailureAsync(port, ccid, epoch, ex.Message);
            }
        }
        finally
        {
            ReleaseImeiReservations(
                CreateImeiReservationOwner(portName, ccid));
            EndPortInitialization(portName, initializationLease);
        }
    }

    internal static bool TryReserveImeiCandidate(
        ConcurrentDictionary<string, string> reservations,
        string candidate,
        string owner,
        IEnumerable<string> unavailableImeis)
    {
        string normalizedCandidate = Services.ImeiManagementService.ToCanonicalImei(candidate);
        if (!Services.ImeiManagementService.IsValidImei(normalizedCandidate)
            || string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        if (unavailableImeis.Any(existing =>
            Services.ImeiManagementService.AreEquivalentImei(
                existing, normalizedCandidate)))
        {
            return false;
        }

        string actualOwner = reservations.GetOrAdd(normalizedCandidate, owner);
        return string.Equals(actualOwner, owner, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GenerateUniqueReservedImeiTarget(
        ConcurrentDictionary<string, string> reservations,
        string owner,
        IEnumerable<string> unavailableImeis,
        Func<string> generateCandidate,
        int maxAttempts = 1000)
    {
        if (generateCandidate == null)
            throw new ArgumentNullException(nameof(generateCandidate));

        string[] unavailable = unavailableImeis.ToArray();
        for (int attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            string candidate = generateCandidate();
            if (TryReserveImeiCandidate(
                reservations, candidate, owner, unavailable))
            {
                return Services.ImeiManagementService.ToCanonicalImei(
                    candidate);
            }
        }

        throw new InvalidOperationException(
            $"Không tạo được IMEI mới duy nhất sau {Math.Max(1, maxAttempts)} lần thử.");
    }

    private bool IsImeiAssignedOrReserved(
        string candidate,
        string portName,
        string ccid)
    {
        string normalizedCcid = NormalizeCcid(ccid);
        var unavailable = new List<string>();
        lock (_imeiCacheLock)
        {
            unavailable.AddRange(_imeiCache.Values
                .Where(entry => !string.Equals(
                    NormalizeCcid(entry.Ccid),
                    normalizedCcid,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Imei));
        }

        unavailable.AddRange(_verifiedImeiByCcid
            .Where(pair => !string.Equals(
                NormalizeCcid(pair.Key),
                normalizedCcid,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value));
        unavailable.AddRange(GetPortsSnapshot()
            .Where(port => !string.Equals(
                port.PortName, portName, StringComparison.OrdinalIgnoreCase))
            .Select(port => port.Imei));
        // The durable target owned by this exact COM is not a collision with
        // itself during crash/restart repair. Targets owned by every other COM
        // remain unavailable.
        unavailable.AddRange(
            _pendingNoSimImeiJournal.GetImeiSnapshot(portName));

        string normalizedCandidate = NormalizeImei(candidate);
        if (unavailable.Any(existing =>
                Services.ImeiManagementService.AreEquivalentImei(
                    existing, normalizedCandidate)))
        {
            return true;
        }
        if (_imeiTargetReservations.TryGetValue(
                normalizedCandidate, out string? reservedOwner))
        {
            return !IsImeiReservationOwnedByCurrentOperation(
                reservedOwner,
                portName,
                normalizedCcid);
        }

        string owner = CreateImeiReservationOwner(portName, normalizedCcid);
        return !TryReserveImeiCandidate(
            _imeiTargetReservations,
            normalizedCandidate,
            owner,
            unavailable);
    }

    internal static bool IsImeiReservationOwnedByCurrentOperation(
        string? reservedOwner,
        string portName,
        string? ccid)
    {
        if (string.IsNullOrWhiteSpace(reservedOwner))
            return false;

        string normalizedCcid = NormalizeCcid(ccid);
        string directOwner = CreateImeiReservationOwner(
            portName,
            normalizedCcid);
        if (string.Equals(
                reservedOwner,
                directOwner,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(normalizedCcid))
            return false;

        string batchIdentitySuffix = ":" + normalizedCcid;
        return reservedOwner.StartsWith(
                   "BULK:", StringComparison.Ordinal)
               && reservedOwner.EndsWith(
                   batchIdentitySuffix, StringComparison.Ordinal);
    }

    private string GenerateAndReserveNewImeiTarget(SimPort port)
    {
        string owner = CreateImeiReservationOwner(port.PortName, port.Serial);
        var unavailable = new List<string>();
        lock (_imeiCacheLock)
        {
            unavailable.AddRange(_imeiCache.Values.Select(entry => entry.Imei));
            unavailable.AddRange(_modemImeiCache.Values.Select(entry => entry.Imei));
        }

        unavailable.AddRange(_verifiedImeiByCcid.Values);
        unavailable.AddRange(
            _pendingNoSimImeiJournal.GetImeiSnapshot(port.PortName));
        unavailable.AddRange(GetPortsSnapshot().Select(item => item.Imei));

        return GenerateUniqueReservedImeiTarget(
            _imeiTargetReservations,
            owner,
            unavailable,
            Services.ImeiManagementService.GenerateRandomImei);
    }

    private static string CreateImeiReservationOwner(
        string portName,
        string? ccid)
    {
        string normalizedCcid = NormalizeCcid(ccid);
        return !string.IsNullOrWhiteSpace(normalizedCcid)
            ? "SIM:" + normalizedCcid
            : "PORT:" + (portName ?? string.Empty).Trim().ToUpperInvariant();
    }

    private PendingImeiJournalEntry PrepareDurableImeiOperation(
        string portName,
        string targetImei,
        string? expectedCcid,
        PendingImeiOperationKind kind)
    {
        string target = Services.ImeiManagementService.ToCanonicalImei(
            targetImei);
        string operationId;
        PendingImeiOperationKind durableKind = kind;
        if (_pendingNoSimImeiJournal.TryGetEntry(
                portName, out PendingImeiJournalEntry existing))
        {
            if (!Services.ImeiManagementService.AreEquivalentImei(
                    existing.TargetImei, target))
            {
                throw new InvalidOperationException(
                    $"COM còn giao dịch IMEI {existing.TargetImei} chưa hoàn tất; không được ghi đè bằng mục tiêu khác.");
            }
            operationId = existing.OperationId;
            // A replay may intentionally skip taking the backup again, but it
            // is still the original CreateNew/Restore operation. Never let a
            // convenience flag relabel durable ownership after a crash.
            durableKind = ResolveDurableImeiOperationKind(
                existing.Kind,
                kind);
        }
        else
        {
            operationId = "imei-" + Guid.NewGuid().ToString("N");
        }

        return _pendingNoSimImeiJournal.Prepare(
            portName,
            operationId,
            target,
            expectedCcid,
            durableKind);
    }

    internal static bool IsDurableImeiJournalFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException;

    private void MarkImeiJournalBlockedOnUi(
        SimPort port,
        string context,
        Exception exception)
    {
        port.IsRebooting = false;
        port.Status = SimStatus.SecurityBlocked;
        port.DeviceName = "Chặn an toàn – journal IMEI không đọc/ghi được";
        port.LastError =
            "Không thể xác minh journal IMEI bền vững; đã chặn mọi thao tác ghi IMEI.";
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        AddLog(
            $"[{port.PortName}] [IMEI_JOURNAL_BLOCKED] context={context}; {exception.Message}",
            "ERROR");
        UpdateDashboard();
    }

    private async Task HoldPortRadioOffForImeiJournalFailureAsync(SimPort port)
    {
        try
        {
            await _modemService.SendCommandAsync(
                port.PortName,
                "AT+CFUN=4",
                8000,
                silent: true,
                ct: CancellationToken.None);
        }
        catch (Exception radioException)
        {
            AddLog(
                $"[{port.PortName}] [IMEI_JOURNAL_RADIO_OFF_FAILED] {radioException.Message}",
                "WARN");
        }
    }

    private async Task HoldPortOfflineForImeiJournalFailureAsync(
        SimPort port,
        string context,
        Exception exception)
    {
        await HoldPortRadioOffForImeiJournalFailureAsync(port);

        await Application.Current.Dispatcher.InvokeAsync(() =>
            MarkImeiJournalBlockedOnUi(port, context, exception));
    }

    internal static PendingImeiOperationKind ResolveDurableImeiOperationKind(
        PendingImeiOperationKind existingKind,
        PendingImeiOperationKind requestedKind) =>
        existingKind == PendingImeiOperationKind.LegacyNoSim
            ? requestedKind
            : existingKind;

    private void ReleaseImeiReservations(string owner)
    {
        foreach (var reservation in _imeiTargetReservations)
        {
            if (string.Equals(
                reservation.Value, owner, StringComparison.OrdinalIgnoreCase))
            {
                ((ICollection<KeyValuePair<string, string>>)_imeiTargetReservations)
                    .Remove(reservation);
            }
        }
    }

    internal static void ClearSimScopedState(SimPort port)
    {
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
        port.Imei = string.Empty;
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

    private void DeferDetectedCcidUntilPortReady(string portName, string ccid)
    {
        _deferredDetectedCcids[portName] = ccid;
        if (!_deferredCcidOwners.TryAdd(portName, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                for (int attempt = 0; attempt < 480 && !_lifetimeCts.IsCancellationRequested; attempt++)
                {
                    if (!_initializingPorts.ContainsKey(portName))
                    {
                        if (_deferredDetectedCcids.TryRemove(portName, out string? deferredCcid))
                        {
                            ModemService_LogMessage(_modemService, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = $"[PARSE_CCID] {deferredCcid}"
                            });
                        }
                        return;
                    }

                    await Task.Delay(250, _lifetimeCts.Token);
                }

                _deferredDetectedCcids.TryRemove(portName, out _);
                AddLog($"[{portName}] [CCID_DEFER_TIMEOUT] Tác vụ trước chưa nhả khóa; tiếp tục dò SIM nền.", "WARN");
                _modemService.StartHotplugWaitLoop(portName);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _deferredCcidOwners.TryRemove(portName, out _);
                if (_deferredDetectedCcids.TryGetValue(portName, out string? latestCcid))
                    DeferDetectedCcidUntilPortReady(portName, latestCcid);
            }
        });
    }

    private void ModemService_LogMessage(object? sender, GsmDataEventArgs e)
    {
        // Mark a service-initiated, planned reconnect synchronously. The
        // hardware watcher runs off the UI thread and must see this guard before
        // the dispatcher gets around to updating the row.
        if (e.Data.StartsWith("[PORT_HEALTH_RECOVERY]", StringComparison.Ordinal)
            || e.Data.StartsWith("[PORT_RECONNECT]", StringComparison.Ordinal))
        {
            _targetedRecoveryPorts[e.PortName] = 0;
        }
        else if (e.Data.StartsWith("[PORT_RECONNECT_FAILED]", StringComparison.Ordinal)
            || e.Data.StartsWith("[PORT_RECONNECT_DEFERRED]", StringComparison.Ordinal))
        {
            if (!_managedRecoveryPorts.ContainsKey(e.PortName))
                ScheduleServiceReconnectRetry(e.PortName);
        }
        else if (e.Data.StartsWith("[PORT_HEALTH_RECOVERY_FAILED]", StringComparison.Ordinal))
        {
            ScheduleServiceReconnectRetry(e.PortName);
        }
        else if (e.Data.StartsWith("[PARSE_CCID]", StringComparison.Ordinal)
            || e.Data.StartsWith("[WAITING_FOR_SIM]", StringComparison.Ordinal))
        {
            _targetedRecoveryPorts.TryRemove(e.PortName, out _);
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            bool isInternalEvent = e.Data.StartsWith("[PARSE_") || e.Data == "[STATUS_ACTIVE]";
            if (!isInternalEvent) AddLog($"[{e.PortName}] {e.Data}");
            
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);

            if (port == null)
            {
                if (e.Data == "[PORT_OPENED]" || e.Data.StartsWith("[STATUS_SIM_LOCKED]") || e.Data.StartsWith("[PARSE_CCID]") || e.Data.StartsWith("[PARSE_CNUM]") || e.Data.Contains("+COPS:") || e.Data.StartsWith("+CUSD:") || e.Data.StartsWith("[NO_SIM_READY]") || e.Data.StartsWith("[WAITING_FOR_SIM]") || e.Data.StartsWith("[PARSE_IMEI]") || e.Data.StartsWith("[STATUS_NO_RESPONSE]") || e.Data.StartsWith("[NETWORK_WAITING]") || e.Data.StartsWith("[NETWORK_RECOVERY]") || e.Data.StartsWith("[NETWORK_LOST]") || e.Data.StartsWith("[NETWORK_REOPEN_REQUIRED]") || e.Data.StartsWith("[NETWORK_FAILED]") || e.Data.StartsWith("Lỗi kết nối"))
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

            if (e.Data.StartsWith("[PORT_HEALTH_RECOVERY]", StringComparison.Ordinal)
                || e.Data.StartsWith("[PORT_RECONNECT]", StringComparison.Ordinal))
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "COM không phản hồi – đang tự mở lại riêng cổng...";
                port.LastError = "Đang phục hồi UART; giữ IMEI theo CCID đã xác minh";
                UpdateDashboard();
            }
            else if (e.Data == "[PORT_OPENED]")
            {
                // CFUN=1,1 temporarily removes/recreates the USB serial endpoint.
                // Preserve the current CCID/session while the IMEI action owns the
                // reset; clearing it here races the post-reset verification and can
                // leave an Active row without SIM identity columns.
                if (!port.IsRebooting)
                    ClearSimScopedState(port);
                port.Status = SimStatus.Connecting;
                port.DeviceName = port.IsRebooting
                    ? "Đang khởi động lại modem sau khi ghi IMEI..."
                    : "Đang kiểm tra modem/SIM...";
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
            else if (e.Data.StartsWith("[NO_SIM_READY]"))
            {
                string observedImei = Regex.Match(
                    e.Data, @"(?<!\d)\d{15}(?!\d)").Value;
                SchedulePendingNoSimImeiRetry(
                    e.PortName,
                    observedImei);
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
            else if (e.Data.StartsWith("[NETWORK_REOPEN_REQUIRED]") && !_modemService.IsCallInProgress(e.PortName))
            {
                bool sessionCurrent = TryGetCurrentSimSession(
                    e.PortName,
                    out string recoveryCcid,
                    out _,
                    out _);
                bool trustedIdentity = sessionCurrent
                    && !_initializingPorts.ContainsKey(e.PortName)
                    && string.Equals(
                        NormalizeCcid(port.Serial),
                        NormalizeCcid(recoveryCcid),
                        StringComparison.OrdinalIgnoreCase)
                    && HasTrustedImeiForCcid(recoveryCcid, port.Imei);
                if (!trustedIdentity)
                {
                    AddLog(
                        $"[{e.PortName}] [NETWORK_REOPEN_IGNORED] Event cũ hoặc danh tính phiên hiện tại chưa được xác minh.",
                        "INFO");
                }
                else if (IsIdentityReverifyReopen(e.Data))
                {
                    // Reopen vì danh tính cần xác minh lại là yêu cầu đúng đắn
                    // riêng, không thuộc vòng lặp chờ COPS nên không tính ngân sách.
                    RequestNetworkReopen(
                        port,
                        e.PortName,
                        "Cần mở lại riêng COM để xác minh lại danh tính",
                        "[NETWORK_REOPEN] Refresh riêng COM để xác minh lại IMEI/CCID.");
                }
                else if (_networkReopenOwners.ContainsKey(e.PortName))
                {
                    // Reopen của lượt trước còn đang chạy; event trùng không
                    // được tiêu thêm ngân sách của SIM này.
                    AddLog(
                        $"[{e.PortName}] [NETWORK_REOPEN_SKIPPED] Đang mở lại riêng COM cho lượt trước.",
                        "INFO");
                }
                else
                {
                    string reopenKey = BuildNetworkReopenKey(e.PortName, recoveryCcid);
                    int reopenAttempt = _networkReopenAttempts.AddOrUpdate(
                        reopenKey, 1, (_, current) => current + 1);
                    if (ShouldAbandonNetworkReopen(reopenAttempt))
                    {
                        // IMEI đã ghi và commit xong; chỉ mạng là không lên được.
                        // Kết thúc ở trạng thái rõ ràng để COM không nằm mãi ở
                        // "Đang xử lý" và không ai chạm lại vào IMEI.
                        MarkNetworkRegistrationUnavailable(
                            port,
                            $"Có sóng (CSQ) nhưng không đăng ký được nhà mạng sau {MaxNetworkReopenAttemptsPerSim} lượt mở lại COM; kiểm tra SIM/anten rồi bấm Làm mới");
                        AddLog(
                            $"[{e.PortName}] [NETWORK_REOPEN_EXHAUSTED] Đã mở lại COM {MaxNetworkReopenAttemptsPerSim} lượt cho CCID={NormalizeCcid(recoveryCcid)} nhưng COPS vẫn không có nhà mạng; dừng vòng lặp và giữ IMEI đã xác minh.",
                            "ERROR");
                        UpdateDashboard();
                    }
                    else
                    {
                        MarkNetworkRegistrationPending(
                            port,
                            sessionCurrent: true,
                            $"Có sóng nhưng COPS chưa trả nhà mạng – đang mở lại riêng COM (lượt {reopenAttempt}/{MaxNetworkReopenAttemptsPerSim})");
                        UpdateDashboard();
                        RequestNetworkReopen(
                            port,
                            e.PortName,
                            reason: null,
                            $"[NETWORK_REOPEN] Refresh riêng COM vì CSQ tốt nhưng COPS không có nhà mạng (lượt {reopenAttempt}/{MaxNetworkReopenAttemptsPerSim}).");
                    }
                }
            }
            else if ((e.Data.StartsWith("[NETWORK_WAITING]")
                    || e.Data.StartsWith("[NETWORK_LOST]"))
                && !_modemService.IsCallInProgress(e.PortName))
            {
                MarkNetworkRegistrationPending(
                    port,
                    TryGetCurrentSimSession(e.PortName, out _, out _, out _),
                    "Đang chờ đăng ký nhà mạng");
                UpdateDashboard();
            }
            else if (e.Data.StartsWith("[NETWORK_RECOVERY]") && !_modemService.IsCallInProgress(e.PortName))
            {
                // GOOD/CSQ only means the radio can see energy. It must not keep
                // the row Active when COPS/CREG registration is missing.
                MarkNetworkRegistrationPending(
                    port,
                    TryGetCurrentSimSession(e.PortName, out _, out _, out _),
                    "Đang tự khôi phục đăng ký nhà mạng");
                UpdateDashboard();
            }
            else if (e.Data.Contains("[NETWORK_FAILED]") && !_modemService.IsCallInProgress(e.PortName))
            {
                bool simInitialized = TryGetCurrentSimSession(e.PortName, out _, out _, out _)
                    && !string.IsNullOrWhiteSpace(port.Serial)
                    && !string.IsNullOrWhiteSpace(port.Imei);
                // The SIM/IMEI can be valid while registration is still pending;
                // keep the row in the connecting state until a real COPS result.
                port.Status = simInitialized ? SimStatus.Connecting : SimStatus.NoResponse;
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
                // NOT READY/CME 10/13/11 are transient on this modem family
                // during CFUN/IMS changes. GsmModemService now confirms those
                // states through its multi-probe monitor; only an explicit
                // NOT INSERTED indication may clear the UI immediately.
                && (e.Data.Contains("+CPIN: NOT INSERTED") || e.Data.Contains("SIM not inserted")))
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
                if (TryParseCsqResponse(e.Data, out int rssi, out int percent))
                {
                    port.SignalRssi = rssi;
                    port.SignalStrength = percent;
                    port.LastSignalScanAt = DateTime.Now;
                    TryStartVinaInitialLookup(port);
                }
            }
            else if (e.Data.StartsWith("[NETWORK_TYPE]", StringComparison.Ordinal))
            {
                if (!TryGetCurrentSimSession(e.PortName, out _, out _, out _))
                    return;
                port.NetworkType = e.Data.Replace("[NETWORK_TYPE]", string.Empty).Trim();
                TryStartVinaInitialLookup(port);
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
                // A late fallback URC cannot race the foreground IMEI lease.
                if (CanPromoteNetworkRegistration(
                    port,
                    _initializingPorts.ContainsKey(e.PortName),
                    sessionCurrent))
                {
                    MarkPortNetworkActive(e.PortName);
                }
                TryStartVinaInitialLookup(port);
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
                        UpdateImeiCacheEntry(port.Serial, entry => entry.SimRegDate = regDate);
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

                // Parse Network Provider from AT+COPS?
                // EC20 có thể trả tên dài, tên ngắn hoặc mã số không có dấu ngoặc kép.
                if (GsmModemService.TryParseCopsResponse(
                    e.Data, out string parsedOperator, out _))
                {
                    port.NetworkProvider = NormalizeNetworkProvider(parsedOperator);
                    // Some EC20 firmware omits the optional ACT field in +COPS.
                    // The operator response still proves registration, so keep a
                    // neutral type instead of leaving SAuto waiting forever for
                    // a separate [NETWORK_TYPE] event that will never arrive.
                    if (string.IsNullOrWhiteSpace(port.NetworkType))
                        port.NetworkType = "Mạng";
                    string networkUpper = port.NetworkProvider.ToUpperInvariant();

                    // COPS is the point at which radio registration is complete.
                    // A port may have been downgraded to Connecting while waiting
                    // for COPS; promote it here so the configured startup USSD can start.
                    // Do not let a stale COPS response race a Create/Restore
                    // IMEI operation and make the UI look online before the
                    // new identity has been verified after reboot.
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

                        // COPS vừa đăng ký là SAuto phát CUSD=2 ngay; khoảng chờ một giây
                        // nằm giữa CUSD=2 và CUSD=1, không nằm trước CUSD=2.
                        TryStartVinaInitialLookup(port);

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

                    // SMS slot ownership must follow the verified physical SIM,
                    // including CCIDs read by MainViewModel commands rather than
                    // by the modem service's own polling loop.
                    _modemService.SetSmsSimIdentity(e.PortName, ccid);

                    // Bắt đầu theo dõi rút SIM ngay khi đã xác nhận CCID. Không
                    // chờ kế hoạch USSD tự động vì SIM mới có thể đang ở trạng thái
                    // SecurityBlocked/WaitingAccept; tháo SIM trong trạng thái
                    // đó vẫn phải xóa CCID và kết thúc phiên SIM cũ.
                    _modemService.SetSimRemovalWatchEnabled(e.PortName, true);

                    // Ignore a repeated CCID while the same identity is already
                    // verified and waiting for COPS. Without this Connecting
                    // guard, a periodic CCID probe can restart the IMEI pipeline.
                    bool sameCcid = string.Equals(
                        NormalizeCcid(port.Serial),
                        ccid,
                        StringComparison.OrdinalIgnoreCase);
                    bool verifiedConnectingSession = false;
                    if (sameCcid
                        && port.Status == SimStatus.Connecting
                        && TryGetCurrentSimSession(
                            e.PortName,
                            out string currentSessionCcid,
                            out _,
                            out _)
                        && string.Equals(
                            NormalizeCcid(currentSessionCcid),
                            ccid,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string trustedImei = _verifiedImeiByCcid.TryGetValue(
                            ccid, out string? sessionImei)
                                ? sessionImei
                                : _imeiCache.TryGetValue(ccid, out SimBackupEntry? backup)
                                    ? backup.Imei
                                    : string.Empty;
                        verifiedConnectingSession = IsVerifiedImeiResumeMatch(
                            port.Imei,
                            trustedImei);
                    }

                    if (sameCcid
                        && (port.Status == SimStatus.Active
                            || port.Status == SimStatus.WaitingAccept
                            || port.Status == SimStatus.SecurityBlocked
                            || verifiedConnectingSession))
                    {
                        return;
                    }

                    if (!TryBeginPortInitialization(e.PortName, out Guid initializationLease))
                    {
                        // SIM có thể xuất hiện đúng lúc đang ghi IMEI theo nhánh chưa có SIM.
                        // Không được bỏ mất CCID; phát lại event sau khi tác vụ hiện tại nhả khóa.
                        DeferDetectedCcidUntilPortReady(e.PortName, ccid);
                        return;
                    }

                    string previousCcid = NormalizeCcid(port.Serial);
                    if (!string.IsNullOrWhiteSpace(previousCcid)
                        && !string.Equals(
                            previousCcid,
                            ccid,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // A different physical SIM appeared during recovery.
                        // Never carry the old subscriber metadata into its row.
                        ClearSimScopedState(port);
                    }

                    // Set Connecting (không phải Active) khi nhận CCID – chưa verify IMEI
                    port.Status = SimStatus.Connecting;
                    if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug)." || string.IsNullOrWhiteSpace(port.DeviceName))
                    {
                        port.DeviceName = "Đã nhận SIM, đang kiểm tra IMEI...";
                    }

                    port.Serial = ccid;

                    var detectedSession = StartSimSession(e.PortName, ccid);
                    bool hasPendingImei;
                    PendingImeiJournalEntry pendingOperation;
                    string pendingConflict;
                    try
                    {
                        hasPendingImei = TryGetPendingImeiForCcid(
                            e.PortName,
                            ccid,
                            out pendingOperation,
                            out pendingConflict);
                    }
                    catch (Exception exception) when (
                        IsDurableImeiJournalFailure(exception))
                    {
                        // The journal is the durable owner of an in-flight IMEI.
                        // Corruption must block this SIM without leaking the
                        // initialization lease or allowing the normal resume path
                        // to select an older workbook value.
                        EndPortInitialization(e.PortName, initializationLease);
                        MarkImeiJournalBlockedOnUi(
                            port,
                            "parse-ccid",
                            exception);
                        _ = Task.Run(() =>
                            HoldPortRadioOffForImeiJournalFailureAsync(port));
                        return;
                    }
                    if (!hasPendingImei
                        && !string.IsNullOrWhiteSpace(pendingConflict))
                    {
                        port.Status = SimStatus.SecurityBlocked;
                        port.DeviceName = "Chặn SIM – giao dịch IMEI cũ chưa khớp CCID";
                        port.LastError = pendingConflict;
                        AddLog(
                            $"[{e.PortName}] [IMEI_PENDING_CONFLICT] {pendingConflict}",
                            "ERROR");
                        EndPortInitialization(e.PortName, initializationLease);
                        UpdateDashboard();
                        return;
                    }
                    string pendingImei = hasPendingImei
                        ? pendingOperation.TargetImei
                        : string.Empty;
                    _verifiedImeiByCcid.TryGetValue(ccid, out string? verifiedImei);
                    string backupImei = _imeiCache.TryGetValue(ccid, out SimBackupEntry? cachedEntry)
                        ? cachedEntry.Imei
                        : string.Empty;
                    var resume = ResolveAutomaticImeiResumeCandidate(
                        pendingImei, verifiedImei, backupImei);

                    if (!string.IsNullOrWhiteSpace(resume.Imei))
                    {
                        port.Status = SimStatus.Connecting;
                        port.DeviceName = resume.Source == "no-sim"
                            ? "Đã nhận SIM, đang xác minh IMEI vừa tạo..."
                            : "Đang xác minh lại IMEI đã chấp nhận...";
                        port.LastError = string.Empty;
                        AddLog(
                            resume.Source == "no-sim"
                                ? $"[{e.PortName}] [IMEI_NO_SIM_AUTO_RESUME] CCID={ccid}; IMEI={resume.Imei}; tự xác minh và bật mạng, không chặn SIM."
                                : $"[{e.PortName}] [IMEI_SESSION_RESUME] source={resume.Source}; CCID={ccid}; IMEI={resume.Imei}; chỉ xác minh và bật lại mạng.",
                            "INFO");
                        UpdateDashboard();

                        _ = Task.Run(() => ResumeVerifiedImeiSessionAsync(
                            port,
                            ccid,
                            resume.Imei,
                            detectedSession.Epoch,
                            detectedSession.Token,
                            initializationLease,
                            pendingOperation: hasPendingImei
                                ? pendingOperation
                                : null));
                        return;
                    }

                    port.Status = SimStatus.SecurityBlocked;
                    port.DeviceName = "Chặn SIM – chọn Tạo IMEI mới hoặc Khôi phục IMEI";
                    port.LastError = string.Empty;
                    AddLog($"[{e.PortName}] [IMEI_ACTION_REQUIRED] Đã đọc IMEI/CCID; RF vẫn tắt, chờ nút Tạo mới hoặc Khôi phục.", "INFO");
                    EndPortInitialization(e.PortName, initializationLease);
                    UpdateDashboard();

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
            // Service có thể đính kèm nguyên nhân sau mã sự kiện, ví dụ:
            // "[STATUS_NO_RESPONSE] Không xác nhận được CFUN=4...".
            // Bắt theo prefix để cổng không bị treo mãi ở trạng thái Connecting.
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
                // [PARSE_CCID] là nguồn duy nhất khởi chạy state machine IMEI.
                // Event này chỉ cập nhật UI, tránh chạy ProcessImeiAsync lần thứ hai.
                if (port.Status != SimStatus.WaitingAccept
                    && port.Status != SimStatus.SecurityBlocked
                    && port.Status != SimStatus.Active)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang cấu hình SIM mới...";
                    UpdateDashboard();
                }
            }
        });
    }

    private void MarkPortIdentityReadyForNetwork(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        // IMEI/CCID and RF are ready, but CSQ alone is not network registration.
        // Keep the COM pending until a current-session COPS response or a
        // registered CREG fallback explicitly promotes it to Active.
        port.Status = SimStatus.Connecting;
        port.NetworkProvider = string.Empty;
        port.NetworkType = string.Empty;
        ClearImeiRecoveryAttemptsForPort(portName);
        port.TimeoutCount = 0;
        port.SmsErrorCount = 0;
        port.ReconnectCount = 0;
        port.LastError = "IMEI đã xác minh; đang chờ đăng ký nhà mạng";
        
        // Cập nhật tên thiết bị thực tế dựa trên IMEI
        // Liệt kê đầy đủ mọi chuỗi trạng thái tạm thời có thể được set trước đó
        if (port.DeviceName == "Đang chờ cắm SIM (Hot-plug)."
            || port.DeviceName == "Đã nhận SIM, đang khởi tạo..."
            || port.DeviceName == "Đã nhận SIM, đang kiểm tra IMEI..."
            || port.DeviceName == "Đang cấu hình SIM mới..."
            || port.DeviceName == "Chặn SIM – chọn Tạo IMEI mới hoặc Khôi phục IMEI"
            || port.DeviceName == "Đang tráng IMEI Fake..."
            || port.DeviceName == "Đang hoàn tất cấu hình modem..."
            || port.DeviceName == "IMEI đã khớp, đang bật lại mạng..."
            || port.DeviceName == "Đang tự mở lại riêng COM..."
            || port.DeviceName == "Đang tự kết nối lại riêng COM..."
            || string.IsNullOrWhiteSpace(port.DeviceName))
        {
            port.DeviceName = Services.ImeiManagementService.GetDeviceNameFromImei(port.Imei);
        }

        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        UpdateDashboard();
    }

    private void ClearImeiRecoveryAttemptsForPort(string portName)
    {
        string prefix = portName + "|";
        foreach (string key in _imeiVerificationRecoveryAttempts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _imeiVerificationRecoveryAttempts.TryRemove(key, out _);
        }
        foreach (string key in _imeiMismatchRepairAttempts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _imeiMismatchRepairAttempts.TryRemove(key, out _);
        }
    }

    internal static string BuildImeiRecoveryCounterKey(
        string portName,
        string ccid) =>
        $"{(portName ?? string.Empty).Trim().ToUpperInvariant()}|{NormalizeCcid(ccid)}";

    private void MarkPortNetworkActive(string portName)
    {
        var port = Ports.FirstOrDefault(p => p.PortName == portName);
        if (port == null) return;

        port.Status = SimStatus.Active;
        port.LastError = string.Empty;
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        // Mạng đã lên: mọi lượt reopen trước đó không còn tính vào ngân sách.
        ClearNetworkReopenBudget(portName);
        UpdateDashboard();

        foreach (var sms in SmsMessages.Where(s => s.PortName == portName))
        {
            sms.Status = SimStatus.Active;
        }

        _ = gsm.Services.FirebaseService.ClearWebStateAsync(portName);
    }

    private bool HasTrustedImeiForCcid(string ccid, string imei)
    {
        string normalizedCcid = NormalizeCcid(ccid);
        string normalizedImei = NormalizeImei(imei);
        if (string.IsNullOrWhiteSpace(normalizedCcid)
            || !Services.ImeiManagementService.IsValidImei(normalizedImei))
        {
            return false;
        }

        if (_verifiedImeiByCcid.TryGetValue(
                normalizedCcid, out string? verifiedSessionImei)
            && Services.ImeiManagementService.AreEquivalentImei(
                normalizedImei, verifiedSessionImei))
        {
            return true;
        }

        return _imeiCache.TryGetValue(
                normalizedCcid, out SimBackupEntry? exactBackup)
            && Services.ImeiManagementService.AreEquivalentImei(
                normalizedImei, exactBackup.Imei);
    }

    internal static bool IsVerifiedIdentityReadyForNetwork(
        SimPort port,
        string expectedCcid,
        string expectedImei,
        bool sessionCurrent)
    {
        if (!sessionCurrent
            || (port.Status != SimStatus.Connecting
                && port.Status != SimStatus.Active))
        {
            return false;
        }

        string liveCcid = NormalizeCcid(port.Serial);
        string liveImei = NormalizeImei(port.Imei);
        return string.Equals(
                liveCcid,
                NormalizeCcid(expectedCcid),
                StringComparison.OrdinalIgnoreCase)
            && Services.ImeiManagementService.IsValidImei(liveImei)
            && Services.ImeiManagementService.AreEquivalentImei(
                liveImei,
                NormalizeImei(expectedImei));
    }

    internal static bool CanPromoteNetworkRegistration(
        SimPort port,
        bool initializationInProgress,
        bool sessionCurrent) =>
        sessionCurrent
        && !initializationInProgress
        // NetworkUnavailable là kết luận của pha mạng, không phải của SIM/IMEI.
        // Nếu sau đó COPS/CREG vẫn xác nhận đăng ký thì hàng được lên Active
        // ngay, không cần user bấm Làm mới.
        && (port.Status == SimStatus.Connecting
            || port.Status == SimStatus.NetworkUnavailable)
        && !string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial))
        && Services.ImeiManagementService.IsValidImei(NormalizeImei(port.Imei));

    internal static bool MarkNetworkRegistrationPending(
        SimPort port,
        bool sessionCurrent,
        string reason)
    {
        if (!sessionCurrent) return false;

        bool demoted = port.Status == SimStatus.Active;
        if (demoted)
            port.Status = SimStatus.Connecting;

        port.NetworkProvider = string.Empty;
        port.NetworkType = string.Empty;
        port.LastError = reason;
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        return demoted;
    }

    /// <summary>
    /// Kết thúc pha mạng sau khi đã hết ngân sách mở lại COM. IMEI/CCID đã
    /// xác minh vẫn được giữ nguyên; chỉ trạng thái mạng chuyển sang lỗi để
    /// hàng không còn hiển thị spinner "Đang xử lý".
    /// </summary>
    internal static void MarkNetworkRegistrationUnavailable(
        SimPort port,
        string reason)
    {
        port.Status = SimStatus.NetworkUnavailable;
        port.DeviceName = "Không đăng ký được nhà mạng (IMEI đã giữ nguyên)";
        port.NetworkProvider = string.Empty;
        port.NetworkType = string.Empty;
        port.LastError = reason;
        port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
    }

    internal static string BuildNetworkReopenKey(string portName, string ccid) =>
        $"{portName}\u001f{NormalizeCcid(ccid)}";

    internal static bool ShouldAbandonNetworkReopen(int reopenAttempt) =>
        reopenAttempt > MaxNetworkReopenAttemptsPerSim;

    internal static bool IsIdentityReverifyReopen(string? data) =>
        data?.Contains("reason=identity-reverify", StringComparison.OrdinalIgnoreCase)
            == true;

    /// <summary>
    /// Ngân sách reopen thuộc về từng SIM trên từng COM. Xóa khi mạng đã lên
    /// hoặc khi user tự bấm Làm mới; reopen tự động không được tự nạp lại
    /// ngân sách của chính nó.
    /// </summary>
    private void ClearNetworkReopenBudget(string portName)
    {
        string prefix = portName + "\u001f";
        foreach (string key in _networkReopenAttempts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _networkReopenAttempts.TryRemove(key, out _);
        }
    }

    private void RequestNetworkReopen(
        SimPort port,
        string portName,
        string? reason,
        string logMessage)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            MarkNetworkRegistrationPending(port, sessionCurrent: true, reason);
            UpdateDashboard();
        }
        if (!_networkReopenOwners.TryAdd(portName, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                AddLog($"[{portName}] {logMessage}", "WARN");
                // Reopen tự động phải giữ nguyên ngân sách để vòng
                // reopen -> resume -> chờ COPS luôn tiến tới giới hạn.
                await RefreshPortsAsync(
                    [portName],
                    resetNetworkReopenBudget: false);
            }
            finally
            {
                _networkReopenOwners.TryRemove(portName, out _);
            }
        });
    }

    // ---------------------------------------------------------------------
    // THAO TÁC ACCEPT SIM MỚI TỪ UI
    // ---------------------------------------------------------------------
    public async Task<(bool Success, string TargetImei)> CreateNewImeiForPortAsync(
        string portName,
        string? expectedCcid = null)
    {
        var port = GetPortsSnapshot().FirstOrDefault(item =>
            string.Equals(
                item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (port == null) return (false, string.Empty);

        string normalizedExpectedCcid = NormalizeCcid(expectedCcid);
        if (!string.IsNullOrWhiteSpace(normalizedExpectedCcid)
            && !string.Equals(
                NormalizeCcid(port.Serial),
                normalizedExpectedCcid,
                StringComparison.Ordinal))
        {
            AddLog(
                $"[{portName}] [EXPECTED_SIM_BLOCKED] Không tạo IMEI: expected CCID={normalizedExpectedCcid}; live CCID={NormalizeCcid(port.Serial)}.",
                "ERROR");
            return (false, string.Empty);
        }

        string owner = CreateImeiReservationOwner(port.PortName, port.Serial);
        string targetImei = string.Empty;
        try
        {
            bool hasPendingOperation = _pendingNoSimImeiJournal.TryGetEntry(
                port.PortName, out PendingImeiJournalEntry pending);
            if (hasPendingOperation
                && pending.Phase != PendingImeiOperationPhase.Blocked)
            {
                // A crash/restart must resume the same SAuto target, never generate
                // a second IMEI while the first one may already be in modem NV.
                targetImei = pending.TargetImei;
                AddLog(
                    $"[{port.PortName}] [IMEI_PENDING_REUSED] operation={pending.OperationId}; IMEI={targetImei}",
                    "INFO");
            }
            else
            {
                if (hasPendingOperation)
                {
                    _pendingNoSimImeiJournal.Remove(
                        port.PortName,
                        pending.OperationId,
                        pending.TargetImei,
                        string.IsNullOrWhiteSpace(pending.ExpectedCcid)
                            ? null
                            : pending.ExpectedCcid);
                }
                targetImei = GenerateAndReserveNewImeiTarget(port);
            }

            bool hasSim = !string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial));
            bool success = hasSim
                ? await PaintImeiForCurrentSimAsync(
                    port.PortName,
                    targetImei,
                    overwriteBackupWithCurrentImei: true,
                    expectedCcid: normalizedExpectedCcid)
                : await PaintImeiWithoutSimAsync(
                    port.PortName,
                    targetImei,
                    backupCurrentBeforeWrite: true);
            return (success, targetImei);
        }
        catch (Exception exception) when (
            IsDurableImeiJournalFailure(exception))
        {
            await HoldPortOfflineForImeiJournalFailureAsync(
                port,
                "create-new",
                exception);
            return (false, targetImei);
        }
        finally
        {
            ReleaseImeiReservations(owner);
        }
    }

    public async Task<bool> PaintImeiForCurrentSimAsync(
        string portName,
        string targetImei,
        bool overwriteBackupWithCurrentImei = false,
        string? expectedCcid = null,
        CancellationToken cancellationToken = default)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        string ccid = NormalizeCcid(port?.Serial);
        string target = NormalizeImei(targetImei);
        if (port == null
            || string.IsNullOrWhiteSpace(ccid)
            || (!string.IsNullOrWhiteSpace(NormalizeCcid(expectedCcid))
                && !string.Equals(
                    ccid,
                    NormalizeCcid(expectedCcid),
                    StringComparison.Ordinal))
            || !Services.ImeiManagementService.IsValidImei(target))
        {
            return false;
        }
        if (!TryBeginPortInitialization(portName, out Guid initializationLease)) return false;
        IDisposable backgroundLease = _modemService.SuspendPortBackgroundOperations(
            portName,
            preserveCurrentNetworkPollingForResume: false);
        (long Epoch, CancellationToken Token)? activeSession = null;

        try
        {
            (long Epoch, CancellationToken Token) session;
            if (_portSessions.TryGet(portName, out var existingSession)
                && string.Equals(existingSession.Ccid, ccid, StringComparison.OrdinalIgnoreCase))
                session = (existingSession.Epoch, existingSession.Token);
            else
                session = StartSimSession(portName, ccid);
            activeSession = session;
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                session.Token,
                cancellationToken);
            CancellationToken operationToken = operationCts.Token;

            // Một probe ngắn là đủ trước khi sở hữu COM. Sau điểm này epoch/token
            // của phiên SIM chặn mọi thao tác nếu có hot-swap; không lặp QCCID/ICCID
            // nhiều đợt trong CFUN=4 như logic cũ.
            string liveCcid = await ReadLiveCcidAsync(portName, operationToken, attempts: 1);
            if (!HasExactLiveCcidEvidence(liveCcid, ccid))
            {
                await RecoverImeiComAfterFailureAsync(
                    port,
                    ccid,
                    session.Epoch,
                    string.IsNullOrWhiteSpace(NormalizeCcid(liveCcid))
                        ? "Không đọc được CCID trực tiếp ngay trước khi ghi IMEI; đã hủy ghi an toàn"
                        : $"CCID trực tiếp đã đổi ({NormalizeCcid(liveCcid)} != {ccid}); đã hủy ghi IMEI");
                return false;
            }
            if (!IsSimSessionCurrent(portName, ccid, session.Epoch))
                return false;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = "Đang tráng IMEI...";
                UpdateDashboard();
            });

            await ProcessCurrentSimSessionAsync(
                port, ccid, forceAccept: true, session.Epoch, operationToken,
                initializationLease, explicitTargetImei: target,
                overwriteBackupWithCurrentImei: overwriteBackupWithCurrentImei,
                releaseBackgroundOperations: backgroundLease.Dispose);

            return IsVerifiedIdentityReadyForNetwork(
                port,
                ccid,
                target,
                IsSimSessionCurrent(portName, ccid, session.Epoch));
        }
        catch (OperationCanceledException)
        {
            if (activeSession is { } current
                && IsSimSessionCurrent(portName, ccid, current.Epoch))
            {
                await RecoverImeiComAfterFailureAsync(
                    port, ccid, current.Epoch,
                    "Tạo IMEI bị hủy/quá hạn; COM đang được tự khôi phục");
            }
            return false;
        }
        catch (Exception ex)
        {
            if (activeSession is { } current
                && IsSimSessionCurrent(portName, ccid, current.Epoch))
            {
                await RecoverImeiComAfterFailureAsync(
                    port, ccid, current.Epoch, ex.Message);
            }
            else
            {
                AddLog($"[{portName}] [IMEI_ACTION_FAILED] {ex.Message}", "ERROR");
            }
            return false;
        }
        finally
        {
            backgroundLease.Dispose();
            EndPortInitialization(portName, initializationLease);
            if (port.Status == "Chờ cắm SIM")
                _modemService.StartHotplugWaitLoop(portName);
        }
    }

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

    internal static bool IsVerifiedImeiResumeMatch(string? currentImei, string? verifiedImei)
    {
        string current = NormalizeImei(currentImei);
        string verified = NormalizeImei(verifiedImei);
        return current.Length == 15
            && verified.Length == 15
            && Services.ImeiManagementService.AreEquivalentImei(current, verified);
    }

    private bool TryGetPendingImeiForCcid(
        string portName,
        string ccid,
        out PendingImeiJournalEntry pending,
        out string conflictReason)
    {
        conflictReason = string.Empty;
        if (!_pendingNoSimImeiJournal.TryGetEntry(portName, out pending))
            return false;

        string normalizedCcid = NormalizeCcid(ccid);
        if (pending.Phase == PendingImeiOperationPhase.Blocked)
        {
            conflictReason =
                $"Giao dịch {pending.OperationId} đang bị chặn sau lỗi xác minh IMEI.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pending.ExpectedCcid)
            && !string.Equals(
                pending.ExpectedCcid,
                normalizedCcid,
                StringComparison.Ordinal))
        {
            conflictReason =
                $"IMEI {pending.TargetImei} đang chờ CCID {pending.ExpectedCcid}, không phải SIM {normalizedCcid}.";
            return false;
        }

        SimBackupEntry? exactMapping;
        bool assignedToAnotherCcid;
        string pendingTarget = pending.TargetImei;
        lock (_imeiCacheLock)
        {
            exactMapping = _imeiCache.TryGetValue(
                normalizedCcid, out SimBackupEntry? exact)
                    ? exact
                    : _imeiCache.Values.FirstOrDefault(entry =>
                        string.Equals(
                            NormalizeCcid(entry.Ccid),
                            normalizedCcid,
                            StringComparison.OrdinalIgnoreCase));
            assignedToAnotherCcid = _imeiCache.Values.Any(entry =>
                !string.Equals(
                    NormalizeCcid(entry.Ccid),
                    normalizedCcid,
                    StringComparison.OrdinalIgnoreCase)
                && Services.ImeiManagementService.AreEquivalentImei(
                    entry.Imei,
                    pendingTarget));
        }

        // Only the exact CCID -> IMEI mapping proves that this operation was
        // committed before a crash. The same IMEI on another workbook row is a
        // collision and must never tombstone or steal this operation.
        if (assignedToAnotherCcid)
        {
            conflictReason =
                $"IMEI chờ {pending.TargetImei} đã thuộc một CCID khác trong kho; giữ RF tắt để tránh trùng IMEI.";
            return false;
        }

        if (exactMapping != null
            && Services.ImeiManagementService.AreEquivalentImei(
                exactMapping.Imei,
                pending.TargetImei))
        {
            try
            {
                _pendingNoSimImeiJournal.Remove(
                    portName,
                    pending.OperationId,
                    pending.TargetImei,
                    string.IsNullOrWhiteSpace(pending.ExpectedCcid)
                        ? null
                        : normalizedCcid);
            }
            catch (Exception ex)
            {
                AddLog(
                    $"[{portName}] [IMEI_PENDING_CLEANUP] Mapping đã commit; chưa xóa được journal: {ex.Message}",
                    "WARN");
            }
            pending = new PendingImeiJournalEntry();
            return false;
        }

        if (string.IsNullOrWhiteSpace(pending.ExpectedCcid))
        {
            if (!_pendingNoSimImeiJournal.TryBindExpectedCcid(
                    portName,
                    pending.OperationId,
                    normalizedCcid)
                || !_pendingNoSimImeiJournal.TryGetEntry(
                    portName,
                    out pending))
            {
                conflictReason =
                    "Không thể khóa giao dịch IMEI chờ vào đúng CCID hiện tại.";
                return false;
            }
        }

        return true;
    }

    internal static (string Imei, string Source) ResolveAutomaticImeiResumeCandidate(
        string? pendingNoSimImei,
        string? verifiedSessionImei,
        string? backupImei)
    {
        foreach ((string? value, string source) in new[]
        {
            (pendingNoSimImei, "no-sim"),
            (verifiedSessionImei, "session"),
            (backupImei, "xlsx")
        })
        {
            string normalized = NormalizeImei(value);
            if (Services.ImeiManagementService.IsValidImei(normalized))
                return (normalized, source);
        }

        return (string.Empty, string.Empty);
    }

    private readonly record struct ResumeNetworkResult(
        bool IdentityReady,
        bool SessionIdentityFailed);

    private async Task<ResumeNetworkResult> ResumeVerifiedNetworkAsync(
        SimPort port,
        string ccid,
        string verifiedImei,
        long epoch,
        CancellationToken token)
    {
        string portName = port.PortName;
        if (!IsSimSessionCurrent(portName, ccid, epoch))
            return new ResumeNetworkResult(false, true);

        // The SIM/IMEI was already verified in CFUN=4. Do not replay the long
        // offline initialization sequence here; simply release full functionality
        // and let the normal COPS/CSQ loop finish registration on its own COM.
        string radioOn = await _modemService.SendCommandAsync(
            portName, "AT+CFUN=1", 15000, silent: true, ct: token);
        bool hardFailure = radioOn.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase)
            || radioOn.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase)
            || (radioOn.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                && !radioOn.Contains("Timeout", StringComparison.OrdinalIgnoreCase));
        if (hardFailure) return new ResumeNetworkResult(false, false);

        string liveCcid = await ReadLiveCcidAsync(
            portName, token, attempts: 6);
        if (!string.Equals(
            liveCcid,
            NormalizeCcid(ccid),
            StringComparison.OrdinalIgnoreCase))
        {
            AddLog(
                $"[{portName}] [IMEI_RESUME_CCID_FAILED] expected={NormalizeCcid(ccid)}; live={liveCcid}; tắt RF và không đưa lên Active.",
                "ERROR");
            try
            {
                await _modemService.SendCommandAsync(
                    portName,
                    "AT+CFUN=4",
                    5000,
                    silent: true,
                    ct: CancellationToken.None);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(liveCcid))
                InvalidateSimSession(portName);
            return new ResumeNetworkResult(false, true);
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
            port.Imei = verifiedImei;
            port.Serial = NormalizeCcid(ccid);
            port.IsRebooting = false;
            MarkPortIdentityReadyForNetwork(portName);
        });

        bool identityReady = IsSimSessionCurrent(portName, ccid, epoch)
            && IsVerifiedIdentityReadyForNetwork(
                port, ccid, verifiedImei, sessionCurrent: true);
        if (identityReady)
        {
            _modemService.StartPollingNetwork(
                portName,
                ccid,
                verifiedImei);
            AddLog($"[{portName}] [IMEI_RESUME_NETWORK] Đã bật CFUN=1; giữ Connecting đến khi COPS/CREG xác nhận đăng ký mạng.", "SUCCESS");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000, token);
                    if (IsSimSessionCurrent(portName, ccid, epoch)
                        && port.Status == SimStatus.Active)
                    {
                        await _modemService.ConfigureVoiceFeaturesAsync(portName, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AddLog($"[{portName}] [IMEI_RESUME_VOICE] {ex.Message}", "INFO");
                }
            }, token);
        }

        return new ResumeNetworkResult(identityReady, false);
    }

    private async Task ResumeVerifiedImeiSessionAsync(
        SimPort port,
        string ccid,
        string verifiedImei,
        long epoch,
        CancellationToken token,
        Guid initializationLease,
        PendingImeiJournalEntry? pendingOperation)
    {
        string portName = port.PortName;
        bool identityMismatchDetected = false;
        bool sessionIdentityFailed = false;
        try
        {
            if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

            if (!await ValidateSessionIdentityAsync(portName, ccid, epoch, token))
                throw new InvalidOperationException("Không xác minh được CCID sau refresh");

            string storedResponse = await _modemService.SendCommandAsync(
                portName, "AT+EGMR=0,7;", 10000, silent: true, ct: token);
            string storedImei = Regex.Match(
                storedResponse ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
            if (!IsVerifiedImeiResumeMatch(storedImei, verifiedImei))
            {
                identityMismatchDetected = true;
                throw new InvalidOperationException(
                    $"IMEI sau refresh không khớp giá trị đã xác minh ({storedImei} != {verifiedImei})");
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                port.Imei = storedImei;
                port.Status = SimStatus.Connecting;
                port.DeviceName = "IMEI đã khớp, đang bật lại mạng...";
                UpdateDashboard();
            });

            ResumeNetworkResult networkResult = await ResumeVerifiedNetworkAsync(
                port, ccid, verifiedImei, epoch, token);
            sessionIdentityFailed = networkResult.SessionIdentityFailed;
            if (networkResult.IdentityReady)
            {
                string normalizedCcid = NormalizeCcid(ccid);
                string normalizedImei = NormalizeImei(verifiedImei);
                _verifiedImeiByCcid[normalizedCcid] = normalizedImei;
                if (pendingOperation != null)
                {
                    SaveLatestImeiCacheEntry(new SimBackupEntry
                    {
                        Ccid = normalizedCcid,
                        Imei = normalizedImei,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        LastPortName = portName,
                        SourceFile = pendingOperation.Kind == PendingImeiOperationKind.CreateNew
                            ? "pending-imei-create-auto-resume"
                            : "pending-imei-restore-auto-resume"
                    });
                    // Đây là commit bền vững thật sự của IMEI mới khi pha ghi đi
                    // qua đường auto-resume. Không phát cùng một mốc bằng chứng
                    // như đường trực tiếp khiến mọi bên chờ [IMEI_BACKUP_COMMIT]
                    // (runner nghiệm thu, log vận hành) treo dù workbook đã lưu.
                    AddLog(
                        $"[{portName}] [IMEI_BACKUP_COMMIT] CCID={normalizedCcid}; IMEI={normalizedImei}; lưu sau xác minh auto-resume.",
                        "SUCCESS");
                    try
                    {
                        _pendingNoSimImeiJournal.Remove(
                            portName,
                            pendingOperation.OperationId,
                            normalizedImei,
                            normalizedCcid);
                    }
                    catch (Exception cleanupEx)
                    {
                        // The durable CCID mapping now wins over this stale
                        // journal entry on every lookup. Keep the session live
                        // and retry cleanup opportunistically on the next read.
                        AddLog(
                            $"[{portName}] [IMEI_PENDING_CLEANUP] Đã commit CCID/IMEI nhưng chưa xóa được journal: {cleanupEx.Message}",
                            "WARN");
                    }
                }
                AddLog($"[{portName}] [IMEI_SESSION_RESUMED] CCID={ccid}; IMEI={verifiedImei}; đã xác minh, đang chờ COPS và không ghi IMEI lần hai.", "SUCCESS");
                return;
            }

            throw new InvalidOperationException("Không hoàn tất được xác minh/bật mạng sau refresh");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"[{portName}] [IMEI_SESSION_RESUME_FAILED] {ex.Message}", "ERROR");
            if (IsSimSessionCurrent(portName, ccid, epoch))
            {
                bool repairScheduled = false;
                if (identityMismatchDetected)
                {
                    try
                    {
                        await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                    }
                    catch (Exception radioEx)
                    {
                        AddLog($"[{portName}] [IMEI_MISMATCH_RADIO_OFF] {radioEx.Message}", "WARN");
                    }

                    repairScheduled = ScheduleImeiMismatchRepair(
                        portName, ccid, verifiedImei, epoch);
                }
                else if (sessionIdentityFailed)
                {
                    repairScheduled = true;
                    ScheduleImeiVerificationRecovery(portName, ccid, epoch);
                }
                else
                    _modemService.StartPollingNetwork(
                        portName,
                        ccid,
                        verifiedImei);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSimSessionCurrent(portName, ccid, epoch)) return;
                    port.Status = identityMismatchDetected || sessionIdentityFailed
                        ? (repairScheduled ? SimStatus.NoResponse : SimStatus.SecurityBlocked)
                        : SimStatus.Connecting;
                    port.LastError = ex.Message;
                    port.DeviceName = identityMismatchDetected || sessionIdentityFailed
                        ? (repairScheduled
                            ? (sessionIdentityFailed
                                ? "Chưa xác minh lại CCID – đang mở lại riêng COM..."
                                : "IMEI sau reboot chưa khớp – đang tự ghi/xác minh lại...")
                            : "Chặn SIM – IMEI sau refresh chưa được xác minh")
                        : "Đang chờ mạng sau khi tạo IMEI...";
                    UpdateDashboard();
                });
            }
        }
        finally
        {
            EndPortInitialization(portName, initializationLease);
        }
    }

    private bool ScheduleImeiMismatchRepair(
        string portName,
        string ccid,
        string targetImei,
        long epoch)
    {
        string normalizedTarget = NormalizeImei(targetImei);
        if (!Services.ImeiManagementService.IsValidImei(normalizedTarget)) return false;

        string repairKey = $"{portName}|{NormalizeCcid(ccid)}|{epoch}";
        string attemptKey = BuildImeiRecoveryCounterKey(portName, ccid);
        if (!_imeiMismatchRepairOwners.TryAdd(repairKey, 0)) return true;

        int attempt = _imeiMismatchRepairAttempts.AddOrUpdate(
            attemptKey, 1, static (_, previous) => previous + 1);
        if (attempt > MaxImeiMismatchRepairAttempts)
        {
            _imeiMismatchRepairOwners.TryRemove(repairKey, out _);
            AddLog($"[{portName}] [IMEI_MISMATCH_BLOCKED] Đã thử ghi/xác minh lại {MaxImeiMismatchRepairAttempts} lần nhưng IMEI vẫn lệch; giữ RF tắt để bảo vệ SIM.", "ERROR");
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
                if (!IsSimSessionCurrent(portName, ccid, epoch)) return;

                AddLog($"[{portName}] [IMEI_MISMATCH_RECOVERY] Lần {attempt}/{MaxImeiMismatchRepairAttempts}; ghi lại mục tiêu {normalizedTarget} rồi reboot/xác minh lại.", "WARN");
                bool repaired = await PaintImeiForCurrentSimAsync(
                    portName, normalizedTarget, overwriteBackupWithCurrentImei: false);
                if (!repaired && IsSimSessionCurrent(portName, ccid, epoch))
                    AddLog($"[{portName}] [IMEI_MISMATCH_RECOVERY_FAILED] Chưa sửa được sau lần {attempt}; COM vẫn giữ trạng thái an toàn.", "ERROR");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"[{portName}] [IMEI_MISMATCH_RECOVERY_FAILED] {ex.Message}", "ERROR");
            }
            finally
            {
                _imeiMismatchRepairOwners.TryRemove(repairKey, out _);
            }
        }, _lifetimeCts.Token);

        return true;
    }

    private void ModemService_PortDisconnected(object? sender, GsmDataEventArgs e)
    {
        bool targetedRecovery = _targetedRecoveryPorts.ContainsKey(e.PortName);
        var resettingPort = Ports.FirstOrDefault(port => port.PortName == e.PortName);
        if (!targetedRecovery && resettingPort?.IsRebooting != true)
            InvalidateSimSession(e.PortName);
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var port = Ports.FirstOrDefault(p => p.PortName == e.PortName);
            if (port != null)
            {
                if (targetedRecovery)
                {
                    port.Status = SimStatus.Connecting;
                    port.DeviceName = "Đang tự kết nối lại riêng COM...";
                    port.LastError = e.Data;
                    AddLog($"[{e.PortName}] {e.Data}", "WARN");
                    UpdateDashboard();
                }
                else if (port.IsRebooting)
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
                bool inboxRecorded = false;

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

                // Commit every complete decoded SMS before the volatile UI inbox
                // owns it and before GsmModemService may release its SIM slot.
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
                    var durableRecord = new SmsInboxRecord
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
                    if (!TryPersistSms(
                            durableRecord,
                            out SmsMessage? newlyPersistedMessage))
                        return;

                    inboxRecorded = true;
                    e.DeliveryAccepted = true;
                    if (newlyPersistedMessage == null)
                    {
                        AddLog(
                            $"[{e.PortName}] [SMS_REPLAY_ACK] delivery={deliveryId}; inbox đã có, không phát lặp thông báo.",
                            "INFO");
                        return;
                    }
                    AddLog(
                        $"[{e.PortName}] [SMS_UI_RECEIVED] delivery={deliveryId} sender={senderPhone} chars={cleanContent.Length} otp={extractedOtp}",
                        "INFO");
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
                                string bal = strictMatch.Groups[1].Value;
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

                // 4. Đưa lên UI (Cập nhật Tab GSM)
                if (port != null)
                {
                    port.Sender = senderPhone;
                    // SMS thường không có OTP không được phép ghi "N/A" đè mã
                    // đã nhận trước đó trên COM.
                    if (extractedOtp != "N/A")
                        port.Otp = extractedOtp;
                    port.LastMessageContent = displayContent;
                    port.LastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                }

                // Acknowledge only after the durable inbox and in-memory view own
                // the decoded message. The modem retains it when persistence fails.
                if (inboxRecorded)
                    e.DeliveryAccepted = true;
                
                if (extractedOtp != "N/A")
                {
                    AddLog($"[{e.PortName}] Đã bắt được OTP: {extractedOtp} từ {senderPhone}", "SUCCESS");
                    SnackbarMessageQueue.Enqueue($"[{e.PortName}] Đã bắt được OTP: {extractedOtp}");

                    // Lưu lịch sử OTP vào file CSV
                    OtpHistoryService.Append(e.PortName, receiverPhone, senderPhone, extractedOtp, cleanContent);
                    // Cập nhật live vào OtpHistoryList (nếu tab đang mở)
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
        DateTimeOffset receivedAtUtc = DateTimeOffset.UtcNow;
        string deliveryId = SmsInboxStore.CreateDeliveryId(
            "assembled-timeout",
            portName,
            senderPhone,
            content);
        var durableRecord = new SmsInboxRecord
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
        if (!TryPersistSms(durableRecord, out SmsMessage? newlyPersistedMessage)
            || newlyPersistedMessage == null)
            return;

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
            var profile = _modemService.GetModemProfile(e.PortName);
            bool autoAnswerSupported = profile?.Supports(ModemCapability.VoiceCall) == true
                && profile.Supports(ModemCapability.AudioRecord);
            AddLog(autoAnswerSupported
                ? $"[{e.PortName}] Đã nhận cuộc gọi; đang tự động nghe máy và ghi âm."
                : $"[{e.PortName}] Chỉ thông báo cuộc gọi đến; modem chưa hỗ trợ tự động nghe máy/ghi âm.", "INFO");
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
            if (port != null)
            {
                port.LastCallResult = $"Ringing: {session.Caller}";
                port.UpdateDisplayResult("Call");
                AddLog($"[{session.Port}] Đang đổ chuông từ {session.Caller}", "INFO");
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

            // Tự động cập nhật TKC sau khi kết thúc cuộc gọi đến
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckBalanceForPortAsync(session.Port);
            });
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

            InsertSmsMessageBounded(new SmsMessage
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
    public async Task<string> SendUssdForPortAsync(
        string portName,
        string ussdCode,
        string? expectedCcid = null)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(ussdCode))
            return "ERROR: Thiếu tham số";

        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        if (port == null) return "ERROR: Cổng không tìm thấy";
        if (!IsPortReadyForOperation(portName)
            || !_portSessions.TryGet(portName, out PortSessionLease ussdSession)
            || (!string.IsNullOrWhiteSpace(NormalizeCcid(expectedCcid))
                && !string.Equals(
                    ussdSession.Ccid,
                    NormalizeCcid(expectedCcid),
                    StringComparison.Ordinal)))
        {
            SetOperationStatus(portName, "USSD", false);
            return "ERROR: Cổng không còn Active hoặc không đúng CCID đã ghim";
        }

        // Hiển thị trạng thái đang gửi lên cột Nội dung ngay lập tức
        Application.Current.Dispatcher.Invoke(() =>
        {
            port.LastMessageContent = $"[USSD] Đang gửi {ussdCode}...";
            port.Sender = "USSD";
        });

        string result;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            ussdSession.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(110));
        try
        {
            result = await SendUssdThrottledAsync(
                portName, ussdCode, "Manual USSD", maxAttempts: 2, logResult: true,
                cancellationToken: timeoutCts.Token,
                expectedCcid: expectedCcid);
            result = UssdResponseDecoder.Normalize(result);
        }
        catch (OperationCanceledException)
        {
            result = "ERROR: USSD timeout after 110 seconds";
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


    private void TryStartVinaInitialLookup(SimPort port)
    {
        if (!IsVinaNetworkReadyForInitialLookup(port)
            || !TryGetCurrentSimSession(port.PortName, out var ccid, out var epoch, out var token))
        {
            return;
        }

        _ = Task.Run(() => RunInitialBalanceLookupAsync(port, ccid, epoch, token), token);
    }

    /// <summary>
    /// Chạy đúng kế hoạch USSD đã chọn sau khi COPS đăng ký mạng. Mặc định chỉ
    /// chạy *101#; người dùng có thể đổi sang chỉ *111# hoặc *111# rồi *101#.
    /// Mỗi stage chỉ hoàn tất khi parse được dữ liệu đúng nghĩa.
    /// </summary>
    private async Task RunInitialBalanceLookupAsync(
        SimPort port, string ccid, long epoch, CancellationToken token)
    {
        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
            || !IsVinaNetworkReadyForInitialLookup(port))
            return;

        string mode = StartupUssdModes.Normalize(
            SettingsService.Current.StartupUssdMode);
        string ownerKey = $"{port.PortName}|{NormalizeCcid(ccid)}|{epoch}";
        string lookupKey = $"{ownerKey}|{mode}";
        if (_initialAccountLookupCompleted.ContainsKey(lookupKey)) return;
        if (!_initialBalanceLookupOwners.TryAdd(ownerKey, 0)) return;

        IDisposable? backgroundLease = null;
        try
        {
            // Khóa các vòng CPIN/COPS/CMGL nền trong suốt kế hoạch USSD của đúng
            // COM này để không có lệnh quét chen giữa CUSD=2 và CUSD=1.
            backgroundLease = _modemService.SuspendPortBackgroundOperations(port.PortName);

            // COPS/4G chỉ chứng minh SIM đã vào EPS/data.  USSD trên EC20
            // cần CS registration thật sự (CREG 1/5); nếu không modem vẫn
            // trả OK/CUSD=1 nhưng tổng đài không trả payload +CUSD:0,...
            // Gate này thử 3G/2G rồi auto và chỉ cho phép stage chạy tiếp
            // sau khi CREG đã đăng ký, tránh để COM quay vòng USSD giả.
            if (!await _modemService.EnsureCsRegistrationForUssdAsync(
                    port.PortName, token))
            {
                AddLog(
                    $"[{port.PortName}] [USSD_CS_WAIT] COPS/4G có thể đã lên nhưng CREG chưa 1/5; chưa gửi {StartupUssdModes.GetDescription(mode)}, sẽ retry sau 30 giây.",
                    "WARN");
                ScheduleInitialLookupRetry(port, ccid, epoch, lookupKey, mode);
                return;
            }

            AddLog(
                $"[{port.PortName}] [SAUTO_NETWORK_READY] epoch={epoch}; ccid={NormalizeCcid(ccid)}; "
                + $"COPS đã đăng ký {port.NetworkProvider}; tự động chạy {StartupUssdModes.GetDescription(mode)}.",
                "SUCCESS");
            await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = true);

            bool run111 = StartupUssdModes.Includes111(mode);
            bool run101 = StartupUssdModes.Includes101(mode);
            bool subscriberOk = !run111
                || _initialSubscriberLookupCompleted.ContainsKey(lookupKey);
            bool subscriberRanNow = false;
            if (run111 && !subscriberOk)
            {
                subscriberOk = await RunSautoInitialUssdStageAsync(
                    port, ccid, epoch, token,
                    "*111#", SautoInitial111CommandOrder,
                    requireBalance: false,
                    allow111MenuFallback: false);
                if (subscriberOk)
                {
                    _initialSubscriberLookupCompleted.TryAdd(lookupKey, 0);
                    subscriberRanNow = true;
                }
            }

            if (run111 && subscriberOk)
            {
                AddLog(
                    $"[{port.PortName}] [USSD_111_OK] epoch={epoch}; ccid={NormalizeCcid(ccid)}; "
                    + "Đã nhận dữ liệu thuê bao từ *111#.",
                    "SUCCESS");
            }

            bool balanceOk = !run101;
            if (run101
                && subscriberOk
                && IsSimSessionCurrent(port.PortName, ccid, epoch)
                && port.Status == SimStatus.Active)
            {
                if (subscriberRanNow
                    && mode == StartupUssdModes.Subscriber111ThenBalance101)
                {
                    AddLog(
                        $"[{port.PortName}] [USSD_INTER_STAGE_DELAY] Chờ 10 giây sau *111# trước khi chạy *101#.",
                        "INFO");
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }

                balanceOk = await RunSautoInitialUssdStageAsync(
                    port, ccid, epoch, token,
                    "*101#", SautoInitial101CommandOrder,
                    requireBalance: true,
                    allow111MenuFallback:
                        mode == StartupUssdModes.Subscriber111ThenBalance101);
            }

            if (subscriberOk && balanceOk)
            {
                _initialAccountLookupCompleted.TryAdd(lookupKey, 0);
                _automaticUssdRefreshLastAt.TryRemove(
                    $"{port.PortName}|{NormalizeCcid(ccid)}",
                    out _);
                _modemService.SetSimRemovalWatchEnabled(port.PortName, true);
                SetOperationStatus(port.PortName, "USSD", true);
                AddLog(
                    $"[{port.PortName}] [USSD_INITIAL_COMPLETE] epoch={epoch}; ccid={NormalizeCcid(ccid)}; "
                    + $"Đã hoàn tất {StartupUssdModes.GetDescription(mode)}.",
                    "SUCCESS");
            }
            else if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                && port.Status == SimStatus.Active
                && string.Equals(
                    mode,
                    StartupUssdModes.Normalize(SettingsService.Current.StartupUssdMode),
                    StringComparison.Ordinal))
            {
                ScheduleInitialLookupRetry(port, ccid, epoch, lookupKey, mode);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog(
                $"[{port.PortName}] Luồng USSD tự động ({StartupUssdModes.GetDescription(mode)}) lỗi: {ex.Message}",
                "WARN");
        }
        finally
        {
            if (IsSimSessionCurrent(port.PortName, ccid, epoch))
            {
                // Return the UART to the SMS/UI baseline after the direct
                // UCS2 USSD stage. The next USSD stage re-applies UCS2 before
                // sending, so late retries remain deterministic.
                try
                {
                    await _modemService.SendCommandAsync(
                        port.PortName, "AT+CMGF=1", 5000, silent: true);
                }
                catch { }
                try
                {
                    await _modemService.SendCommandAsync(
                        port.PortName, "AT+CSCS=\"GSM\"", 5000, silent: true);
                }
                catch { }
                try
                {
                    await _modemService.SendCommandAsync(
                        port.PortName, "AT+CUSD=1", 5000, silent: true);
                }
                catch { }
            }
            backgroundLease?.Dispose();
            _initialBalanceLookupOwners.TryRemove(ownerKey, out _);
            if (IsSimSessionCurrent(port.PortName, ccid, epoch))
            {
                await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = false);
                if (!string.Equals(
                    mode,
                    StartupUssdModes.Normalize(SettingsService.Current.StartupUssdMode),
                    StringComparison.Ordinal))
                {
                    TryStartVinaInitialLookup(port);
                }
            }
        }
    }

    private void ScheduleInitialLookupRetry(
        SimPort port, string ccid, long epoch, string lookupKey, string mode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Không dùng token của tác vụ khởi tạo/IMEI cũ: một số firmware
                // EC20 hủy token đó sau reboot dù PortSession hiện tại vẫn hợp lệ,
                // làm COM thiếu 111 không bao giờ được đưa lại vào hàng đợi.
                await Task.Delay(TimeSpan.FromSeconds(30), _lifetimeCts.Token);
                if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                    && port.Status == SimStatus.Active
                    && string.Equals(
                        mode,
                        StartupUssdModes.Normalize(SettingsService.Current.StartupUssdMode),
                        StringComparison.Ordinal)
                    && !_initialAccountLookupCompleted.ContainsKey(lookupKey))
                {
                    AddLog(
                        $"[{port.PortName}] [USSD_REQUEUE] {StartupUssdModes.GetDescription(mode)} chưa đủ dữ liệu; "
                        + "đưa riêng COM trở lại hàng đợi tự động.",
                        "WARN");
                    TryStartVinaInitialLookup(port);
                }
            }
            catch (OperationCanceledException) { }
        }, _lifetimeCts.Token);
    }

    private async Task<bool> RunSautoInitialUssdStageAsync(
        SimPort port,
        string ccid,
        long epoch,
        CancellationToken token,
        string ussdCode,
        IReadOnlyList<string> commands,
        bool requireBalance,
        bool allow111MenuFallback)
    {
        int maxAttempts = requireBalance ? 4 : 2;
        TimeSpan retryCadence = TimeSpan.FromSeconds(30);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                || port.Status != SimStatus.Active
                || !IsPortReadyForOperation(port.PortName))
                return false;

            token.ThrowIfCancellationRequested();
            long attemptStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            string previousUssd = port.LastUssdResult ?? string.Empty;
            string previousPhone = port.PhoneNumber ?? string.Empty;
            string previousBalance = port.Balance ?? string.Empty;
            // Mốc để nhận ra +CUSD của chính lượt này kể cả khi nhà mạng trả
            // đúng nội dung như lần trước (TKC không đổi là trường hợp phổ biến).
            DateTime attemptStartedAt = DateTime.Now;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.LastMessageContent = $"[USSD][ĐANG CHẠY] {ussdCode} – lần {attempt}/{maxAttempts}";
                port.Sender = "USSD";
                port.IsBalanceLoading = true;
            });

            // Keep balance retry local to this COM: do not toggle CFUN in the
            // middle of the lookup because that drops a healthy registration
            // and makes the next COPS/USSD pass race the reboot.
            if (requireBalance && attempt == 2)
            {
                AddLog(
                    allow111MenuFallback
                        ? $"[{port.PortName}] [USSD_RETRY_PASSIVE] Giữ radio hiện tại và tiếp tục thử direct *101#; chỉ dùng menu *111# sau khi hết lượt direct."
                        : $"[{port.PortName}] [USSD_RETRY_PASSIVE] Giữ radio hiện tại và tiếp tục thử direct *101#; chế độ hiện tại không tự gọi *111#.",
                    "INFO");
            }

            string result = await SendSautoInitialUssdAsync(
                port.PortName, ccid, epoch, commands, ussdCode,
                cancelSettleDelay: requireBalance && attempt > 1
                    ? TimeSpan.FromSeconds(Math.Min(1 + attempt * 2, 7))
                    : TimeSpan.FromSeconds(1),
                token);

            // +CUSD thường đến sau OK của CUSD=1.
            await Task.Delay(750, token);
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                || port.Status != SimStatus.Active)
                return false;

            bool UssdArrivedThisAttempt() =>
                port.LastUssdResultAt.HasValue
                && port.LastUssdResultAt.Value >= attemptStartedAt;

            bool HasStageResponse() => requireBalance
                ? HasFreshSautoBalanceResponse(
                    result, previousUssd, port.LastUssdResult,
                    previousBalance, port.Balance,
                    UssdArrivedThisAttempt())
                : HasFreshSautoUssdResponse(
                    result, previousUssd, port.LastUssdResult,
                    previousPhone, port.PhoneNumber,
                    UssdArrivedThisAttempt());

            if (HasStageResponse())
            {
                AddLog($"[{port.PortName}] [USSD_STAGE_OK] epoch={epoch}; ccid={NormalizeCcid(ccid)}; {ussdCode} đã trả dữ liệu.", "SUCCESS");
                return true;
            }

            bool receivedLate = false;
            while (true)
            {
                TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(attemptStarted);
                TimeSpan remaining = retryCadence - elapsed;
                if (remaining <= TimeSpan.Zero) break;

                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(500)
                        ? remaining
                        : TimeSpan.FromMilliseconds(500),
                    token);
                if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                    || port.Status != SimStatus.Active)
                    return false;
                if (HasStageResponse())
                {
                    receivedLate = true;
                    break;
                }
            }

            if (receivedLate)
            {
                AddLog($"[{port.PortName}] [USSD_STAGE_OK_LATE] epoch={epoch}; ccid={NormalizeCcid(ccid)}; {ussdCode} trả dữ liệu muộn trước mốc retry.", "SUCCESS");
                return true;
            }

            // Lần cuối vẫn phải chờ đủ cửa sổ 30 giây. Trước đây nhánh này kết thúc
            // ngay sau timeout lệnh (~10 giây), khiến +CUSD chậm của COM8/COM75 bị bỏ lỡ.
            if (attempt == maxAttempts)
            {
                AddLog($"[{port.PortName}] [USSD_NO_RESPONSE] {ussdCode} không trả dữ liệu hợp lệ sau {maxAttempts} lần; kích hoạt phục hồi USSD riêng COM.", "WARN");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.LastMessageContent = $"[USSD][CHỜ PHẢN HỒI] {ussdCode} chưa trả dữ liệu; hệ thống sẽ chạy lại ở nhịp COPS kế tiếp";
                    port.Sender = "USSD";
                });

                // A stale CUSD context is common after an IMEI reboot or a
                // delayed +CUSD URC. Close it and cycle only this radio before
                // putting the lookup back in the 30-second queue. This keeps
                // the COM usable and prevents an endless startup-USSD retry
                // loop from masking a modem that needs a fresh registration.
                if (IsSimSessionCurrent(port.PortName, ccid, epoch)
                    && port.Status == SimStatus.Active)
                {
                    bool recovered = await RecoverUssdSessionFullAsync(port, ccid, epoch, token);
                    AddLog(
                        recovered
                            ? $"[{port.PortName}] [USSD_RECOVERY_READY] Radio/COPS đã sẵn sàng; xếp lại {ussdCode} sau một nhịp."
                            : $"[{port.PortName}] [USSD_RECOVERY_PENDING] Chưa đăng ký lại được COPS; giữ COM và tiếp tục watchdog.",
                        recovered ? "SUCCESS" : "WARN");
                }
                if (!requireBalance) return false;
                break;
            }

            AddLog($"[{port.PortName}] [SAUTO_USSD_RETRY] Thử lại {ussdCode} sau chu kỳ 30 giây.", "WARN");
        }

        if (requireBalance
            && allow111MenuFallback
            && IsSimSessionCurrent(port.PortName, ccid, epoch)
            && port.Status == SimStatus.Active)
        {
            AddLog($"[{port.PortName}] [USSD_101_DIRECT_EXHAUSTED] Direct *101# đã hết lượt; bắt đầu fallback menu *111#.", "WARN");
            if (await TryVinaMenuBalanceFallbackAsync(
                    port, ccid, epoch, token))
            {
                AddLog($"[{port.PortName}] [USSD_101_MENU_RECOVERED] Đã lấy TKC qua mục 1 của *111# sau khi direct *101# hết lượt.", "SUCCESS");
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RecoverUssdSessionFullAsync(
        SimPort port, string ccid, long epoch, CancellationToken token)
    {
        string portName = port.PortName;
        string refreshKey = $"{portName}|{NormalizeCcid(ccid)}";

        if (!IsSimSessionCurrent(portName, ccid, epoch)
            || port.Status != SimStatus.Active)
            return false;

        if (!_automaticUssdRefreshOwners.TryAdd(portName, 0))
        {
            AddLog(
                $"[{portName}] [USSD_AUTO_REFRESH_BUSY] COM đang được một luồng khác khôi phục; giữ phiên hiện tại.",
                "WARN");
            return false;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_automaticUssdRefreshLastAt.TryGetValue(refreshKey, out DateTimeOffset lastAt)
                && now - lastAt < AutomaticUssdRefreshCooldown)
            {
                AddLog(
                    $"[{portName}] [USSD_AUTO_REFRESH_COOLDOWN] Đã refresh gần đây; chờ retry kế tiếp thay vì reboot lại COM.",
                    "WARN");
                return false;
            }

            _automaticUssdRefreshLastAt[refreshKey] = now;
            AddLog(
                $"[{portName}] [USSD_AUTO_REFRESH] *101# không phản hồi; gọi khôi phục đầy đủ như nút Refresh.",
                "WARN");

            bool recovered = await RecoverActivePortAsync(portName);
            AddLog(
                recovered
                    ? $"[{portName}] [USSD_AUTO_REFRESH_READY] Đã reload modem/SIM/IMEI/COPS; pipeline sẽ tự chạy lại *101# trên phiên mới."
                    : $"[{portName}] [USSD_AUTO_REFRESH_FAILED] Không hoàn tất được reload modem; giữ COM để retry có kiểm soát.",
                recovered ? "SUCCESS" : "WARN");
            return recovered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"[{portName}] [USSD_AUTO_REFRESH_FAILED] {ex.Message}", "WARN");
            return false;
        }
        finally
        {
            _automaticUssdRefreshOwners.TryRemove(portName, out _);
        }
    }

    private async Task<bool> TryVinaMenuBalanceFallbackAsync(
        SimPort port,
        string ccid,
        long epoch,
        CancellationToken token)
    {
        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
            || port.Status != SimStatus.Active
            || !IsPortReadyForOperation(port.PortName))
            return false;

        AddLog($"[{port.PortName}] [USSD_101_MENU_FALLBACK] *101# không phản hồi; mở lại *111# và chọn mục 1 – TK bằng tiền.", "WARN");
        string beforeMenu = port.LastUssdResult ?? string.Empty;
        string beforeBalance = port.Balance ?? string.Empty;

        string menuResult = await SendSautoInitialUssdAsync(
            port.PortName, ccid, epoch,
            SautoInitial111CommandOrder, "*111#",
            TimeSpan.FromSeconds(2), token);

        // The +CUSD menu is asynchronous. Five seconds covers the observed EC20
        // response window without imposing the old 30-second retry delay.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, token);
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                || port.Status != SimStatus.Active)
                return false;
            if (menuResult.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(beforeMenu, port.LastUssdResult, StringComparison.Ordinal))
                break;
        }

        if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
            || !IsPortReadyForOperation(port.PortName))
            return false;

        string selectResult = await _modemService.SendCommandAsync(
            port.PortName, "AT+CUSD=1,\"1\",15", 10000, silent: true, ct: token);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500, token);
            if (!IsSimSessionCurrent(port.PortName, ccid, epoch)
                || port.Status != SimStatus.Active)
                return false;

            string currentUssd = port.LastUssdResult ?? string.Empty;
            bool isBalanceContent = Regex.IsMatch(
                currentUssd,
                @"(?:TK\s*chinh|TKC|Tai\s*khoan\s*chinh|Tài\s*khoản\s*chính|So\s*du|Số\s*dư)\s*=|TK\s*chinh\s*:",
                RegexOptions.IgnoreCase);
            bool balanceParsed = !string.IsNullOrWhiteSpace(port.Balance)
                && (!string.Equals(beforeBalance, port.Balance, StringComparison.Ordinal)
                    || !string.Equals(beforeMenu, currentUssd, StringComparison.Ordinal));
            if ((selectResult.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(beforeMenu, currentUssd, StringComparison.Ordinal))
                && (isBalanceContent || balanceParsed))
                return true;
        }

        AddLog($"[{port.PortName}] [USSD_101_MENU_FAILED] Mục 1 của *111# cũng chưa trả TKC; tiếp tục retry riêng COM.", "WARN");
        return false;
    }


    private async Task<string> SendSautoInitialUssdAsync(
        string portName,
        string ccid,
        long epoch,
        IReadOnlyList<string> commands,
        string ussdCode,
        TimeSpan cancelSettleDelay,
        CancellationToken token)
    {
        if (!IsSimSessionCurrent(portName, ccid, epoch)
            || !IsPortReadyForOperation(portName))
            return "ERROR: SIM session changed";

        // Mỗi COM đã có semaphore riêng trong GsmModemService và một owner riêng cho
        // đúng phiên CCID. Không xếp tất cả modem sau một gate toàn cục: SAuto chạy
        // song song theo cổng, còn khóa per-COM vẫn ngăn CUSD/AT chồng lên cùng UART.
        if (!IsSimSessionCurrent(portName, ccid, epoch)
            || !IsPortReadyForOperation(portName))
            return "ERROR: SIM session changed";

        if (commands.Count < 3)
            return "ERROR: Chuỗi lệnh USSD khởi tạo không đầy đủ";

        // The direct 32-port AT test showed that EC20 returns *101# reliably
        // when the same PDU/UCS2 setup is used as the manual USSD service.
        // Keep *111# on the captured SAuto text path, but make the balance
        // stage use the proven encoded command.
        if (string.Equals(ussdCode, "*101#", StringComparison.Ordinal))
        {
            await _modemService.SendCommandAsync(
                portName, "AT+CMGF=0", 5000, silent: true, ct: token);
            await _modemService.SendCommandAsync(
                portName, "AT+CSCS=\"UCS2\"", 5000, silent: true, ct: token);
        }

        // Giữ đúng thứ tự đã xác nhận trên EC20 thực tế: hủy phiên cũ trước,
        // chờ modem settle, rồi bật phát URC và mới gửi mã USSD.
        string cancel = await _modemService.SendCommandAsync(
            portName, commands[0], 2000, silent: true, ct: token);
        if (cancel.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            // EC20 can omit the final OK while closing an already-dead USSD session.
            AddLog($"[{portName}] [USSD_CANCEL_BEST_EFFORT] AT+CUSD=2 không phản hồi ({cancel.Trim()}); vẫn gửi {ussdCode}.", "WARN");
        }

        await Task.Delay(cancelSettleDelay, token);
        if (!IsSimSessionCurrent(portName, ccid, epoch)
            || !IsPortReadyForOperation(portName))
            return "ERROR: SIM session changed";

        // CUSD=1 là cấu hình bắt buộc để modem phát +CUSD về đúng UART.
        // Không suy diễn từ việc lệnh bật trả OK: đọc lại vì EC20 có thể tự
        // rơi về +CUSD:0 sau RF/CS transition.
        string enable = await _modemService.SendCommandAsync(
            portName, commands[1], 5000, silent: true, ct: token);
        if (enable.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return $"ERROR: Không bật được phát kết quả USSD ({enable.Trim()})";

        string presentation = await _modemService.SendCommandAsync(
            portName, "AT+CUSD?", 5000, silent: true, ct: token);
        if (Regex.IsMatch(presentation, @"\+CUSD:\s*0\b", RegexOptions.IgnoreCase))
        {
            enable = await _modemService.SendCommandAsync(
                portName, commands[1], 5000, silent: true, ct: token);
            presentation = await _modemService.SendCommandAsync(
                portName, "AT+CUSD?", 5000, silent: true, ct: token);
            if (enable.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(presentation, @"\+CUSD:\s*0\b", RegexOptions.IgnoreCase))
            {
                return $"ERROR: Modem vẫn tắt phát URC USSD ({presentation.Trim()})";
            }
        }

        return await _modemService.SendCommandAsync(
            portName, commands[2], 10000, silent: true, ct: token);
    }


    private async Task<string> RunBalanceLookupAsync(
        SimPort port, string ussdCode, string reason, int maxAttempts, bool logResult)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => port.IsBalanceLoading = true);
        try
        {
            string result = await SendUssdThrottledAsync(
                port.PortName, ussdCode, reason, maxAttempts: maxAttempts, logResult: logResult);
            // CUSD đã được hoàn tất ở transport và MainViewModel đã nhận URC;
            // chỉ giữ một cửa sổ ngắn để parser cập nhật TKC, không treo UI 10s.
            if (!result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(1200, _lifetimeCts.Token);
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
        CancellationToken cancellationToken = default,
        string? expectedCcid = null)
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

        string result = await _ussdService.SendAsync(
            portName,
            ussdCode,
            maxAttempts,
            effectiveToken,
            expectedCcid);

        if (result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            // USSD cancelled do session thay đổi (hot-swap SIM) hoặc shutdown — không phải lỗi thật
            bool isCancelledNotError = result.Contains("USSD operation cancelled", StringComparison.OrdinalIgnoreCase)
                || result.Contains("SIM session changed", StringComparison.OrdinalIgnoreCase);
            RecordPortError(portName, result, "USSD");
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
    /// sync AppSettings, apply call forwarding và kích hoạt kế hoạch USSD mới
    /// trên các phiên SIM Active chưa hoàn tất đúng chế độ đó.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        var saved = SettingsService.Current;
        if (saved != null) AppSettings = saved;

        // Restart the timers so a newly saved signal interval takes effect now,
        // instead of waiting for the previous interval to expire.
        if (_backgroundSupervisorContext != null)
            _backgroundSupervisor.Start(_backgroundSupervisorContext, _lifetimeCts.Token);

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

        foreach (var port in GetPortsSnapshot().Where(
            p => p.Status == Models.SimStatus.Active))
        {
            TryStartVinaInitialLookup(port);
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
            // Call-history rows may not have a durable DeliveryId. They can be
            // removed from the volatile UI directly; real SMS rows must first
            // be removed from SmsInboxStore so they cannot return after reload.
            if (!string.IsNullOrWhiteSpace(sms.DeliveryId))
            {
                int deleted = _smsInboxStore.Delete([sms.DeliveryId]);
                if (deleted == 0)
                {
                    AddLog(
                        $"[{sms.PortName}] Không tìm thấy SMS bền vững để xóa: {sms.DeliveryId}",
                        "WARN");
                    return false;
                }
            }

            SmsMessages.Remove(sms);
            OnPropertyChanged(nameof(FilteredSmsMessages));
            OnPropertyChanged(nameof(SmsReceivedCount));
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            AddLog(
                $"[{sms.PortName}] Xóa SMS khỏi lịch sử thất bại: {ex.Message}",
                "ERROR");
            return false;
        }
    }

    public bool ClearSmsHistory()
    {
        try
        {
            _smsInboxStore.Clear();
            SmsMessages.Clear();
            OnPropertyChanged(nameof(FilteredSmsMessages));
            OnPropertyChanged(nameof(SmsReceivedCount));
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            AddLog($"Xóa toàn bộ lịch sử SMS thất bại: {ex.Message}", "ERROR");
            return false;
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
        int deleted = 0;
        foreach (SmsMessage sms in filtered)
        {
            if (DeleteSmsHistoryItem(sms)) deleted++;
        }

        SnackbarMessageQueue.Enqueue($"Đã xóa {deleted}/{filtered.Count} tin nhắn.");
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
            if (AppSettings.AutoCheckBalanceAfterSms)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(AutoBalanceAfterSmsDelay, _lifetimeCts.Token);
                    await CheckBalanceForPortAsync(portName);
                }, _lifetimeCts.Token);
            }
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
                _modemImeiCache = new ConcurrentDictionary<string, ModemImeiBackupEntry>(StringComparer.OrdinalIgnoreCase);
                AddLog($"[IMEI_SOURCE] Không tìm thấy {_imeiCacheFilePath}; mọi SIM sẽ bị chặn chờ thao tác IMEI.", "WARN");
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

                var modemWorksheet = package.Workbook.Worksheets["Modem Backup"];
                if (modemWorksheet?.Dimension != null)
                {
                    var modemHeaders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int column = 1; column <= modemWorksheet.Dimension.End.Column; column++)
                    {
                        string name = modemWorksheet.Cells[1, column].Text.Trim().TrimStart('\uFEFF');
                        if (!string.IsNullOrWhiteSpace(name)) modemHeaders[name] = column;
                    }

                    string ModemCell(int row, string name) => modemHeaders.TryGetValue(name, out int column)
                        ? modemWorksheet.Cells[row, column].Text.Trim()
                        : string.Empty;

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
                                Imei = imei,
                                CreatedAt = ModemCell(row, "CreatedAt"),
                                UpdatedAt = ModemCell(row, "UpdatedAt"),
                                HardwareName = ModemCell(row, "HardwareName"),
                                ModemManufacturer = ModemCell(row, "ModemManufacturer"),
                                ModemModel = ModemCell(row, "ModemModel"),
                                ModemFirmware = ModemCell(row, "ModemFirmware"),
                                SourceFile = string.IsNullOrWhiteSpace(ModemCell(row, "SourceFile"))
                                    ? Path.GetFileName(path)
                                    : ModemCell(row, "SourceFile")
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
            foreach (var entry in newCache.Values)
            {
                if (!string.IsNullOrWhiteSpace(entry.PhoneNumber)) _simCache[entry.Ccid] = entry.PhoneNumber;
            }
            AddLog($"[IMEI_SOURCE] Đã nạp {newCache.Count} SIM và {newModemCache.Count} modem từ XLSX (chính={canonicalCount}, chờ hợp nhất={pendingCount}).", "SUCCESS");

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
            bool primarySaved = false;
            bool tempSnapshotReady = false;
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

                var modemWorksheet = package.Workbook.Worksheets.Add("Modem Backup");
                for (int column = 0; column < ModemImeiBackupColumns.Length; column++)
                    modemWorksheet.Cells[1, column + 1].Value = ModemImeiBackupColumns[column];

                int modemRow = 2;
                foreach (var entry in _modemImeiCache.Values.OrderBy(value => value.PortName, StringComparer.OrdinalIgnoreCase))
                {
                    object?[] values =
                    [
                        entry.PortName, entry.Imei, entry.CreatedAt, entry.UpdatedAt,
                        entry.HardwareName, entry.ModemManufacturer, entry.ModemModel,
                        entry.ModemFirmware, entry.SourceFile
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
                double[] modemWidths = [14, 18, 20, 20, 28, 20, 20, 24, 22];
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

    private void SchedulePendingNoSimImeiRetry(
        string portName,
        string observedImei)
    {
        PendingImeiJournalEntry pending;
        try
        {
            if (!_pendingNoSimImeiJournal.TryGetEntry(portName, out pending))
                return;
        }
        catch (Exception exception) when (
            IsDurableImeiJournalFailure(exception))
        {
            SimPort? blockedPort = GetPortsSnapshot().FirstOrDefault(item =>
                string.Equals(
                    item.PortName,
                    portName,
                    StringComparison.OrdinalIgnoreCase));
            if (blockedPort != null)
            {
                _ = Task.Run(() => HoldPortOfflineForImeiJournalFailureAsync(
                    blockedPort,
                    "no-sim-auto-replay",
                    exception));
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.ExpectedCcid)
            || pending.Phase == PendingImeiOperationPhase.Blocked)
            return;

        string ownerKey = $"{portName}|{pending.OperationId}";
        if (!_pendingNoSimRetryOwners.TryAdd(ownerKey, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, _lifetimeCts.Token);
                SimPort? port = GetPortsSnapshot().FirstOrDefault(item =>
                    string.Equals(
                        item.PortName,
                        portName,
                        StringComparison.OrdinalIgnoreCase));
                if (port == null
                    || !string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial)))
                    return;

                if (Services.ImeiManagementService.AreEquivalentImei(
                        observedImei,
                        pending.TargetImei))
                {
                    _pendingNoSimImeiJournal.TryMarkPhase(
                        portName,
                        pending.OperationId,
                        pending.TargetImei,
                        PendingImeiOperationPhase.AwaitingSim);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        port.Imei = Services.ImeiManagementService.ToCanonicalImei(
                            pending.TargetImei);
                        port.Status = "Chờ cắm SIM";
                        port.DeviceName = "IMEI chờ đã khớp – sẵn sàng nhận SIM";
                        port.LastError = string.Empty;
                        UpdateDashboard();
                    });
                    AddLog(
                        $"[{portName}] [IMEI_NO_SIM_REPLAY_MATCHED] operation={pending.OperationId}; IMEI={pending.TargetImei}",
                        "SUCCESS");
                    _modemService.StartHotplugWaitLoop(portName);
                    return;
                }

                AddLog(
                    $"[{portName}] [IMEI_NO_SIM_REPLAY] operation={pending.OperationId}; tự ghi lại mục tiêu {pending.TargetImei} sau restart.",
                    "WARN");
                await PaintImeiWithoutSimAsync(
                    portName,
                    pending.TargetImei,
                    backupCurrentBeforeWrite: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog(
                    $"[{portName}] [IMEI_NO_SIM_REPLAY_FAILED] {ex.Message}",
                    "ERROR");
            }
            finally
            {
                _pendingNoSimRetryOwners.TryRemove(ownerKey, out _);
            }
        }, _lifetimeCts.Token);
    }

    public async Task<bool> PaintImeiWithoutSimAsync(
        string portName,
        string targetImei,
        bool backupCurrentBeforeWrite)
    {
        var port = GetPortsSnapshot().FirstOrDefault(p => p.PortName == portName);
        string target = NormalizeImei(targetImei);
        if (port == null
            || !Services.ImeiManagementService.IsValidImei(target)
            || !string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial)))
            return false;
        if (!TryBeginPortInitialization(portName, out Guid initializationLease)) return false;
        IDisposable backgroundLease = _modemService.SuspendPortBackgroundOperations(
            portName,
            preserveCurrentNetworkPollingForResume: false);
        bool resumeHotplugAfterOperation = false;
        string reservationOwner = CreateImeiReservationOwner(portName, null);
        PendingImeiJournalEntry? durableOperation = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        cts.CancelAfter(ImeiInitializationTimeout);
        try
        {
            if (backupCurrentBeforeWrite)
            {
                var unavailable = new List<string>();
                lock (_imeiCacheLock)
                {
                    unavailable.AddRange(_imeiCache.Values.Select(entry => entry.Imei));
                    unavailable.AddRange(_modemImeiCache.Values.Select(entry => entry.Imei));
                }
                unavailable.AddRange(_verifiedImeiByCcid.Values);
                unavailable.AddRange(
                    _pendingNoSimImeiJournal.GetImeiSnapshot(portName));
                unavailable.AddRange(GetPortsSnapshot()
                    .Where(item => !string.Equals(
                        item.PortName, portName, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Imei));

                if (!TryReserveImeiCandidate(
                    _imeiTargetReservations,
                    target,
                    reservationOwner,
                    unavailable))
                {
                    AddLog(
                        $"[{portName}] [IMEI_TARGET_DUPLICATE] IMEI {target} đã được gán hoặc giữ cho COM khác.",
                        "ERROR");
                    return false;
                }
            }

            // The target must survive an app/power restart before any modem
            // mutation occurs. If neither journal snapshot can be replaced,
            // abort here and leave slot 7 untouched.
            durableOperation = PrepareDurableImeiOperation(
                portName,
                target,
                expectedCcid: null,
                backupCurrentBeforeWrite
                    ? PendingImeiOperationKind.CreateNew
                    : PendingImeiOperationKind.Restore);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.Status = SimStatus.Connecting;
                port.DeviceName = backupCurrentBeforeWrite
                    ? "Đang tạo IMEI mới (chưa có SIM)..."
                    : "Đang khôi phục IMEI (chưa có SIM)...";
                port.LastError = string.Empty;
                UpdateDashboard();
            });

            var result = await _imeiManagementService.ProcessImeiWithoutSimAsync(
                port,
                target,
                previousImei => SaveLatestModemImeiBackup(port, previousImei),
                action => Application.Current.Dispatcher.Invoke(action),
                backupCurrentBeforeWrite,
                cts.Token,
                validateNoSimAsync: async () =>
                {
                    if (!string.IsNullOrWhiteSpace(NormalizeCcid(port.Serial)))
                        return false;
                    string liveCcid = await ReadLiveCcidAsync(
                        portName, cts.Token, attempts: 1);
                    if (string.IsNullOrWhiteSpace(liveCcid)) return true;
                    DeferDetectedCcidUntilPortReady(portName, liveCcid);
                    return false;
                });

            AddLog($"[{portName}] [IMEI_NO_SIM_RESULT] status={result.Status}; message={result.ErrorMessage}",
                result.Status == Services.ImeiProcessStatus.Applied ? "SUCCESS" : "WARN");

            if (result.Status != Services.ImeiProcessStatus.Applied)
            {
                bool transient = result.Status == Services.ImeiProcessStatus.Error;
                if (!transient && durableOperation != null)
                {
                    _pendingNoSimImeiJournal.TryMarkPhase(
                        portName,
                        durableOperation.OperationId,
                        target,
                        PendingImeiOperationPhase.Blocked);
                }
                await _modemService.SendCommandAsync(
                    portName, "AT+CFUN=4", 5000, silent: true, ct: CancellationToken.None);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    port.IsRebooting = false;
                    port.Status = transient ? SimStatus.NoResponse : SimStatus.SecurityBlocked;
                    port.LastError = result.ErrorMessage;
                    port.DeviceName = transient
                        ? "IMEI lỗi tạm thời – COM đang tự khôi phục..."
                        : "IMEI bị chặn bảo mật – cần kiểm tra lại";
                    UpdateDashboard();
                });
                resumeHotplugAfterOperation = true;
                AddLog(
                    transient
                        ? $"[{portName}] [IMEI_NO_SIM_RECOVERY] Giữ IMEI chờ xử lý và trả COM về hot-plug sau lỗi tạm thời."
                        : $"[{portName}] [IMEI_NO_SIM_BLOCKED] Giữ radio tắt vì lỗi xác thực IMEI đã xác nhận.",
                    transient ? "WARN" : "ERROR");
                return false;
            }

            if (durableOperation != null)
            {
                _pendingNoSimImeiJournal.TryMarkPhase(
                    portName,
                    durableOperation.OperationId,
                    result.FinalImei,
                    PendingImeiOperationPhase.SlotVerified);
            }

            bool completed = await CompleteNoSimImeiResetAsync(port, result.FinalImei, cts.Token);
            if (completed && durableOperation != null)
            {
                _pendingNoSimImeiJournal.TryMarkPhase(
                    portName,
                    durableOperation.OperationId,
                    result.FinalImei,
                    PendingImeiOperationPhase.AwaitingSim);
            }
            resumeHotplugAfterOperation = completed;
            return completed;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _modemService.SendCommandAsync(
                    portName, "AT+CFUN=4", 5000, silent: true, ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                AddLog($"[{portName}] [IMEI_NO_SIM_RADIO_OFF] {ex.Message}", "WARN");
            }
            resumeHotplugAfterOperation = true;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.IsRebooting = false;
                port.Status = SimStatus.NoResponse;
                port.LastError = "Tạo IMEI quá hạn; đã giải phóng COM để tự khôi phục";
                port.DeviceName = "Tạo IMEI quá hạn – đang tự khôi phục COM...";
                UpdateDashboard();
            });
            return false;
        }
        catch (Exception exception) when (
            IsDurableImeiJournalFailure(exception))
        {
            // Do not return this COM to the hot-plug/polling loops.  The exact
            // target may already be in slot 7, and only the durable journal can
            // prove which operation owns it after restart.
            resumeHotplugAfterOperation = false;
            await HoldPortOfflineForImeiJournalFailureAsync(
                port,
                "no-sim-create-or-restore",
                exception);
            return false;
        }
        catch (Exception ex)
        {
            resumeHotplugAfterOperation = true;
            AddLog($"[{portName}] [IMEI_NO_SIM_RECOVERY] {ex.Message}", "ERROR");
            try
            {
                await _modemService.SendCommandAsync(
                    portName, "AT+CFUN=4", 5000, silent: true, ct: CancellationToken.None);
            }
            catch (Exception radioEx)
            {
                AddLog($"[{portName}] [IMEI_NO_SIM_RADIO_OFF] {radioEx.Message}", "WARN");
            }
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                port.IsRebooting = false;
                port.Status = SimStatus.NoResponse;
                port.LastError = ex.Message;
                port.DeviceName = "Tạo IMEI lỗi – đang tự khôi phục COM...";
                UpdateDashboard();
            });
            return false;
        }
        finally
        {
            EndPortInitialization(portName, initializationLease);
            backgroundLease.Dispose();
            if (backupCurrentBeforeWrite)
                ReleaseImeiReservations(reservationOwner);
            if (resumeHotplugAfterOperation)
                _modemService.StartHotplugWaitLoop(portName);
        }
    }

    private async Task<bool> CompleteNoSimImeiResetAsync(
        SimPort port,
        string expectedImei,
        CancellationToken ct)
    {
        string portName = port.PortName;
        // ProcessImeiWithoutSimAsync đã đọc lại slot 7 trước khi phát CFUN=1,1.
        // SAuto không chen CFUN=4/CFUN?/EGMR trong lúc modem đang reboot: nó chờ
        // khoảng 10 giây rồi quay lại nguyên vòng khởi tạo no-SIM.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        ct.ThrowIfCancellationRequested();

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ClearSimScopedState(port);
            port.Imei = expectedImei;
            port.IsRebooting = false;
            port.Status = "Chờ cắm SIM";
            port.DeviceName = "Đã đổi IMEI – đang chờ cắm SIM";
            port.LastError = string.Empty;
            port.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            UpdateDashboard();
        });
        return true;
    }

    internal static bool IsVinaNetworkReadyForInitialLookup(SimPort port) =>
        port.Status == SimStatus.Active
        && string.Equals(port.NetworkProvider, "VinaPhone", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(port.NetworkType);

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
        if (provider == "45204") return "Viettel";
        if (provider == "45202") return "VinaPhone";
        if (provider == "45201") return "MobiFone";
        if (provider == "45205") return "Vietnamobile";
        if (provider == "45207") return "Gmobile";
        if (provider == "45208") return "iTel";
        if (provider == "45209") return "Wintel";

        string upper = provider.ToUpperInvariant();
        if (upper.Contains("VINAPHONE") || upper.Contains("VINA")) return "VinaPhone";
        if (upper.Contains("VIETTEL")) return "Viettel";
        if (upper.Contains("MOBIFONE") || upper.Contains("MOBI")) return "MobiFone";
        if (upper.Contains("VIETNAMOBILE") || upper.Contains("VNM")) return "Vietnamobile";
        if (upper.Contains("GMOBILE")) return "Gmobile";
        if (upper.Contains("WINTEL")) return "Wintel";
        if (upper.Contains("ITELECOM") || upper.Contains("ITEL")) return "iTel";
        return provider;
    }

    internal static bool HasFreshSautoUssdResponse(
        string? commandResult,
        string? previousUssd,
        string? currentUssd,
        string? previousPhone,
        string? currentPhone,
        bool ussdArrivedThisAttempt = false)
    {
        bool phoneChanged = !string.IsNullOrWhiteSpace(currentPhone)
            && !string.Equals(previousPhone, currentPhone, StringComparison.Ordinal);
        bool commandHasCusd =
            commandResult?.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase) ?? false;
        // Payload trùng nội dung lần trước vẫn là phản hồi mới nếu +CUSD vừa
        // đến trong chính lượt này; chỉ so chuỗi sẽ làm vòng dò lặp vô hạn.
        bool currentUssdChanged = !string.IsNullOrWhiteSpace(currentUssd)
            && (ussdArrivedThisAttempt
                || !string.Equals(previousUssd, currentUssd, StringComparison.Ordinal));

        // A menu, advertisement, or error +CUSD is not a successful *111#.
        // Only fresh payload text that actually contains a subscriber number,
        // or a freshly parsed PhoneNumber, can complete this stage.
        string freshText =
            $"{(commandHasCusd ? commandResult : string.Empty)}\n"
            + $"{(currentUssdChanged ? currentUssd : string.Empty)}";
        return phoneChanged
            || !string.IsNullOrWhiteSpace(ExtractPhoneNumberFromUssd(freshText));
    }

    internal static bool HasFreshSautoBalanceResponse(
        string? commandResult,
        string? previousUssd,
        string? currentUssd,
        string? previousBalance,
        string? currentBalance,
        bool ussdArrivedThisAttempt = false)
    {
        bool commandHasCusd =
            commandResult?.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase) ?? false;
        // Cùng lý do như trên: TKC không đổi (rất phổ biến, nhất là SIM 0đ) vẫn
        // phải được coi là *101# đã trả dữ liệu cho lượt hiện tại.
        bool currentUssdChanged = !string.IsNullOrWhiteSpace(currentUssd)
            && (ussdArrivedThisAttempt
                || !string.Equals(previousUssd, currentUssd, StringComparison.Ordinal));
        bool receivedFreshUssd = commandHasCusd || currentUssdChanged;
        bool hasParsedBalance = !string.IsNullOrWhiteSpace(currentBalance);
        bool balanceChanged = hasParsedBalance
            && !string.Equals(previousBalance, currentBalance, StringComparison.Ordinal);

        // Do not let a cached Balance value make a fresh menu/error response look
        // like a successful *101# query.  A same-value balance is valid, but only
        // when the new +CUSD payload itself contains a balance field.  A changed
        // parsed value is also sufficient because it was produced by this response.
        string freshText =
            $"{(commandHasCusd ? commandResult : string.Empty)}\n"
            + $"{(currentUssdChanged ? currentUssd : string.Empty)}";
        bool freshTextHasBalance = Regex.IsMatch(
            freshText,
            @"(?:TK\s*(?:g[oố]c|ch[ií]nh)|TKC|T[aà]i\s*kho[aả]n(?:\s*ch[ií]nh)?|S[oố]\s*d[uư]|balance)\s*[:=]?\s*[-+]?\d",
            RegexOptions.IgnoreCase);
        return receivedFreshUssd && (balanceChanged || freshTextHasBalance);
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
            if (_imeiCache.TryGetValue(normalizedCcid, out var existing)
                && Services.ImeiManagementService.TryNormalizeBackupImei(existing.Imei, out _))
            {
                // First-write-wins: IMEI đầu tiên là IMEI gốc. Các lần tráng/khôi phục
                // chỉ được bổ sung metadata, tuyệt đối không thay thế trường IMEI.
                MergeBackupEntryFirstWriteWins(existing, newEntry);
                if (currentPort != null) EnrichBackupEntry(existing, currentPort);
            }
            else
            {
                newEntry.SourceFile = "imei_backup.xlsx";
                _imeiCache[normalizedCcid] = newEntry;
            }
            SaveImeiCache();
        }
    }

    public void SaveLatestImeiCacheEntry(SimBackupEntry newEntry)
    {
        if (newEntry == null) return;
        string normalizedCcid = NormalizeCcid(newEntry.Ccid);
        string normalizedImei = NormalizeImei(newEntry.Imei);
        if (string.IsNullOrEmpty(normalizedCcid)
            || !Services.ImeiManagementService.IsValidImei(normalizedImei)) return;

        newEntry.Ccid = normalizedCcid;
        newEntry.Imei = normalizedImei;
        var currentPort = GetPortsSnapshot().FirstOrDefault(port =>
            string.Equals(NormalizeCcid(port.Serial), normalizedCcid, StringComparison.OrdinalIgnoreCase));
        if (currentPort != null) EnrichBackupEntry(newEntry, currentPort);

        lock (_imeiCacheLock)
        {
            bool hadExisting = _imeiCache.TryGetValue(
                normalizedCcid, out var existing);
            SimBackupEntry? original = hadExisting && existing != null
                ? CloneSimBackupEntry(existing)
                : null;
            SimBackupEntry committed;
            if (existing != null)
            {
                string createdAt = existing.CreatedAt;
                committed = CloneSimBackupEntry(existing);
                MergeBackupEntryFirstWriteWins(committed, newEntry);
                committed.Imei = normalizedImei;
                if (!string.IsNullOrWhiteSpace(createdAt)) committed.CreatedAt = createdAt;
                committed.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                committed.SourceFile = "imei_backup.xlsx";
                if (currentPort != null) EnrichBackupEntry(committed, currentPort);
            }
            else
            {
                committed = CloneSimBackupEntry(newEntry);
                if (string.IsNullOrWhiteSpace(committed.CreatedAt))
                    committed.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                committed.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                committed.SourceFile = "imei_backup.xlsx";
            }

            _imeiCache[normalizedCcid] = committed;
            try
            {
                SaveImeiCache();
            }
            catch
            {
                if (original != null)
                    _imeiCache[normalizedCcid] = original;
                else
                    _imeiCache.TryRemove(normalizedCcid, out _);
                throw;
            }
        }
    }

    public bool SaveLatestModemImeiBackup(SimPort port, string currentImei)
    {
        string key = NormalizeModemBackupKey(port?.PortName);
        string imei = NormalizeImei(currentImei);
        if (port == null || string.IsNullOrWhiteSpace(key)
            || !Services.ImeiManagementService.IsValidImei(imei)) return false;

        lock (_imeiCacheLock)
        {
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string createdAt = _modemImeiCache.TryGetValue(key, out var existing)
                && !string.IsNullOrWhiteSpace(existing.CreatedAt)
                    ? existing.CreatedAt
                    : now;

            _modemImeiCache[key] = new ModemImeiBackupEntry
            {
                PortName = key,
                Imei = imei,
                CreatedAt = createdAt,
                UpdatedAt = now,
                HardwareName = port.HardwareName,
                ModemManufacturer = port.ModemManufacturer,
                ModemModel = port.ModemModel,
                ModemFirmware = port.ModemFirmware,
                SourceFile = "imei_backup.xlsx"
            };
            SaveImeiCache();
            return _modemImeiCache.TryGetValue(key, out var persisted)
                && Services.ImeiManagementService.AreEquivalentImei(persisted.Imei, imei);
        }
    }

    public bool TryGetModemImeiBackup(string portName, out ModemImeiBackupEntry entry)
    {
        string key = NormalizeModemBackupKey(portName);
        if (_modemImeiCache.TryGetValue(key, out var found)
            && Services.ImeiManagementService.IsValidImei(found.Imei))
        {
            entry = found;
            return true;
        }
        entry = new ModemImeiBackupEntry();
        return false;
    }

    private static string NormalizeModemBackupKey(string? portName) =>
        string.IsNullOrWhiteSpace(portName) ? string.Empty : portName.Trim().ToUpperInvariant();

    private static SimBackupEntry CloneSimBackupEntry(SimBackupEntry source) => new()
    {
        Ccid = source.Ccid,
        Imei = source.Imei,
        PhoneNumber = source.PhoneNumber,
        NetworkProvider = source.NetworkProvider,
        Balance = source.Balance,
        PromotionBalance = source.PromotionBalance,
        ExpiryDate = source.ExpiryDate,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        SourceFile = source.SourceFile,
        SimRegDate = source.SimRegDate,
        Lock1C = source.Lock1C,
        Lock2C = source.Lock2C,
        LastPortName = source.LastPortName,
        DeviceName = source.DeviceName,
        HardwareName = source.HardwareName,
        ModemManufacturer = source.ModemManufacturer,
        ModemModel = source.ModemModel,
        ModemFirmware = source.ModemFirmware,
        ModemCapabilities = source.ModemCapabilities,
        Status = source.Status,
        SignalStrength = source.SignalStrength
    };

    internal static void MergeBackupEntryFirstWriteWins(SimBackupEntry target, SimBackupEntry source)
    {
        static void Copy(string value, Action<string> assign)
        {
            if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
        }

        Copy(source.PhoneNumber, value => target.PhoneNumber = value);
        Copy(source.NetworkProvider, value => target.NetworkProvider = value);
        Copy(source.Balance, value => target.Balance = value);
        Copy(source.PromotionBalance, value => target.PromotionBalance = value);
        Copy(source.ExpiryDate, value => target.ExpiryDate = value);
        Copy(source.SimRegDate, value => target.SimRegDate = value);
        Copy(source.Lock1C, value => target.Lock1C = value);
        Copy(source.Lock2C, value => target.Lock2C = value);
        Copy(source.UpdatedAt, value => target.UpdatedAt = value);
        Copy(source.LastPortName, value => target.LastPortName = value);
        Copy(source.DeviceName, value => target.DeviceName = value);
        Copy(source.HardwareName, value => target.HardwareName = value);
        Copy(source.ModemManufacturer, value => target.ModemManufacturer = value);
        Copy(source.ModemModel, value => target.ModemModel = value);
        Copy(source.ModemFirmware, value => target.ModemFirmware = value);
        Copy(source.ModemCapabilities, value => target.ModemCapabilities = value);
        Copy(source.Status, value => target.Status = value);
        if (source.SignalStrength != 0) target.SignalStrength = source.SignalStrength;
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


