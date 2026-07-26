using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

internal sealed class PortReconnectCoordinator
{
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _operations =
        new(StringComparer.OrdinalIgnoreCase);

    internal int ActiveCount => _operations.Count;

    internal Task<bool> RunAsync(string portName, Func<Task<bool>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(operation);

        var candidate = new Lazy<Task<bool>>(
            operation,
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<bool>> active = _operations.GetOrAdd(portName, candidate);
        return AwaitAndReleaseAsync(portName, active);
    }

    private async Task<bool> AwaitAndReleaseAsync(
        string portName,
        Lazy<Task<bool>> operation)
    {
        try
        {
            return await operation.Value.ConfigureAwait(false);
        }
        finally
        {
            // Remove only the generation that just completed. A new reconnect
            // may already have been registered after another waiter removed it.
            ((ICollection<KeyValuePair<string, Lazy<Task<bool>>>>)_operations)
                .Remove(new KeyValuePair<string, Lazy<Task<bool>>>(portName, operation));
        }
    }
}

public interface IGsmModemService
{
    Task<string> SendCommandAsync(
        string portName,
        string command,
        int timeoutMs = 5000,
        bool silent = false,
        CancellationToken ct = default);
    Task<string> SendRawAsync(string portName, string data, int timeoutMs = 5000, bool silent = false);
    Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 15000, CancellationToken ct = default);
    Task<bool> VerifyExpectedCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken ct = default);
    Task SweepUnreadSmsAsync(string portName);
    Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile);
    Task<bool> UploadFileToModemAsync(string portName, string localFile, string remoteFile);
    void StartPollingNetwork(
        string portName,
        string expectedCcid,
        string expectedImei);
    /// <summary>
    /// Bật/tắt xác nhận rút SIM nhanh. Cờ được bật ngay khi CCID của phiên hiện
    /// tại đã được xác nhận, kể cả khi SIM còn đang chờ thao tác IMEI.
    /// </summary>
    void SetSimRemovalWatchEnabled(string portName, bool enabled);
    void SetSmsSimIdentity(string portName, string? ccid);
    List<string> GetAvailablePorts();
    string ConnectAll(int baudRate = 115200);
    Task<bool> ReconnectPortAsync(
        string portName,
        int baudRate = 115200,
        CancellationToken ct = default);
    void Disconnect(string portName);
    void DisconnectAll();
    IDisposable SuspendPortBackgroundOperations(
        string portName,
        bool preserveCurrentNetworkPollingForResume = true);
    void StartHotplugWaitLoop(string portName);
    Task HandleSimInsertedAsync(string portName);
    Task<bool> ReinitializeSettingsAsync(string portName, CancellationToken ct = default);
    Task ReloadSimAsync(string portName);
    Task<bool> ReloadAndResumeSimAsync(string portName, CancellationToken ct = default);
    Task<bool> CallWithAudioAsync(string portName, string phoneNumber, string? wavPath, int durationSeconds = 30, bool record = false, CancellationToken ct = default);
    Task ConfigureVoiceFeaturesAsync(string portName, CancellationToken ct = default);
    bool IsCallInProgress(string portName);
    QuectelModemProfile? GetModemProfile(string portName);


    // Events
    event EventHandler<GsmDataEventArgs> SmsReceived;
    event EventHandler<GsmDataEventArgs> LogMessage;
    event EventHandler<GsmDataEventArgs> PortDisconnected;
    event EventHandler<GsmDataEventArgs> CallIncoming;
    event EventHandler<GsmDataEventArgs> CallEnded;
    event EventHandler<GsmDataEventArgs> DtmfReceived;
    
    event EventHandler<gsm.Models.IncomingCallSession> IncomingCallRinging;
    event EventHandler<gsm.Models.IncomingCallSession> IncomingCallEnded;
}

public class GsmDataEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string MsgIndex { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string DeliveryId { get; set; } = string.Empty;
    public bool DeliveryAccepted { get; set; }
}

public class GsmModemService : IGsmModemService
{
    private const int DirectCmtMaxPendingChars = 16 * 1024;
    private const int DirectCmtMaxDecodeAttempts = 4;
    private static readonly TimeSpan DirectCmtMaxPendingAge = TimeSpan.FromSeconds(12);
    private const long DirectCmtQuarantineMaxBytes = 2L * 1024 * 1024;
    private const int DirectCmtQuarantineArchiveCount = 3;
    private const string DirectCmtQuarantineFileName = "sms_direct_quarantine.jsonl";
    private const string DirectCmtDecodeSentinel = "__TOOLGSM_DIRECT_CMT_BODY_END__";

    private static readonly Regex SmsMemoryFullRegex = new(
        @"\+CMS\s+ERROR:\s*(?:302|322)\b|memory\s+full",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class DirectCmtRetryState
    {
        public required string Fingerprint { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
        public int Attempts { get; set; }
        public int MaxObservedChars { get; set; }
    }

    private sealed record DirectCmtQuarantineRecord(
        DateTimeOffset QuarantinedAtUtc,
        string PortName,
        string Reason,
        int Attempts,
        int RawChars,
        string Sha256,
        string Raw);

    private sealed class SmsReadQueueState
    {
        public SmsReadQueueState(long generation)
        {
            Generation = generation;
            Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        public long Generation { get; }
        public Channel<string> Queue { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Worker { get; set; }
    }

    private enum PortConnectResult
    {
        AlreadyConnected,
        Opened,
        BackingOff,
        Failed
    }

    private sealed record UsbPortCandidate(
        string PortName,
        string LocationInformation,
        string VidPid,
        int InterfaceNumber);

    private sealed record SautoInitializationResult(
        QuectelModemProfile Profile,
        string ImeiResponse,
        string CpinResponse,
        bool RadioLocked);

    private sealed class IncomingCallRecordingState
    {
        public IncomingCallRecordingState(string remoteFileName)
        {
            RemoteFileName = remoteFileName;
        }

        public string RemoteFileName { get; }
        public object Sync { get; } = new();
        public TaskCompletionSource<bool> SetupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ended { get; set; }
        public bool RecordingStarted { get; set; }
        public bool FinalizationStarted { get; set; }
        public IDisposable? BackgroundLease { get; set; }
    }

    internal static IReadOnlyList<string> SautoInitializationCommandOrder { get; } =
    [
        "\u001b",
        "ATI",
        "AT+CPMS=\"ME\",\"SM\",\"MT\"",
        "AT+CFUN=4",
        "AT+CNMI=1,1,0,0,0",
        "AT+CFUN?",
        "AT+EGMR=0,7;",
        "AT+CNMI?",
        "AT+CSCS=\"GSM\"",
        "AT+QURCCFG=\"urcport\",\"uart1\"",
        "AT+CMGF=1",
        "AT+CPMS=\"SM\",\"SM\",\"SM\"",
        "AT+CPMS=\"ME\",\"ME\",\"ME\"",
        "AT+CPMS=\"SM\",\"SM\",\"SM\"",
        "AT+CPMS?",
        "AT+CNMI=1,1,0,0,0",
        "AT+QCFG=\"nwscanmode\",0,1",
        "AT+QURCCFG=\"urcport\",\"uart1\"",
        "AT+CPIN?"
    ];

    private readonly ConcurrentDictionary<string, SerialPort> _serialPorts = new();
    private readonly ConcurrentDictionary<string, gsm.Models.IncomingCallSession> _incomingCalls = new();
    private readonly ConcurrentDictionary<string, byte> _incomingCallNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IncomingCallRecordingState> _incomingCallRecordings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _portBuffers = new();
    private readonly ConcurrentDictionary<string, object> _portBufferLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _commandTcs = new();
    private readonly ConcurrentDictionary<string, int> _connectionErrors = new();
    private readonly ConcurrentDictionary<string, DateTime> _sleepingPorts = new();
    private readonly ConcurrentDictionary<string, string> _portVendors = new();
    private readonly ConcurrentDictionary<string, QuectelModemProfile> _modemProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SerialDataReceivedEventHandler> _dataReceivedHandlers = new();
    private readonly ConcurrentDictionary<string, bool> _isDownloading = new();
    private readonly ConcurrentDictionary<string, bool> _activeCalls = new();
    private readonly ConcurrentDictionary<string, byte> _outgoingCallOperations = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _outgoingCallEndSignals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingCts = new();
    private sealed record NetworkPollingIdentity(string Ccid, string Imei);

    private readonly ConcurrentDictionary<string, NetworkPollingIdentity> _pollingExpectedIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _keepAliveCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portHealthCts = new();
    private readonly ConcurrentDictionary<string, byte> _portHealthRecoveryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _portHealthFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simMonitorCts = new();
    private readonly ConcurrentDictionary<string, int> _suspendedBackgroundPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetworkPollingIdentity> _pendingNetworkPollingPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backgroundOperationSync = new();
    private readonly ConcurrentDictionary<string, bool> _lastSimState = new();
    private readonly ConcurrentDictionary<string, bool> _simStackDisabledByTool = new();
    private readonly ConcurrentDictionary<string, int> _simRemovalEvidenceCounts = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _simRemovalEvidenceSince = new();
    // Một số board không chạy vòng GlobalSimMonitor trong lúc đang polling mạng.
    // Giữ một bộ xác nhận độc lập cho URC rút SIM để UI không phải chờ hết chu kỳ
    // quét sóng dài (có thể lên tới hàng chục giây).
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simRemovalConfirmationCts = new();
    private readonly ConcurrentDictionary<string, byte> _simRemovalWatchEnabled = new(StringComparer.OrdinalIgnoreCase);
    // CPIN/QSIMSTAT can report a short-lived absent state while the modem
    // changes CFUN or the CS/IMS domain. Require both consecutive probes and a
    // minimum elapsed window before clearing a live SIM from the UI.
    private const int SimRemovalConfirmationCycles = 6;
    private static readonly TimeSpan SimRemovalConfirmationWindow = TimeSpan.FromSeconds(5);
    // An offline SIM-stack restart (CFUN=0 -> CFUN=4) can temporarily report
    // CPIN NOT READY / QSIMSTAT=0 while the card is still inserted. During that
    // window, removal monitors must not mistake the transient state for a hot-swap.
    private readonly ConcurrentDictionary<string, byte> _rebootRecoveryInProgress = new();
    // A modem can keep reporting CSQ while the SIM stack itself is wedged.
    // Keep this recovery per COM and bounded so a CME 13 cannot spin an
    // unbounded CFUN/COPS loop or stall all other ports.
    private readonly ConcurrentDictionary<string, byte> _networkSimRecoveryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _networkSimRecoveryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxNetworkSimRecoveryAttempts = 3;
    private const int MaxNetworkSimRecoveryAttemptsWithHardReset = MaxNetworkSimRecoveryAttempts + 1;
    private const int MaxNetworkSimRecoveryAttemptsWithManualOperator = MaxNetworkSimRecoveryAttemptsWithHardReset + 1;
    private const int MaxNetworkRegistrationRecoveryPassesBeforeReopen = 6;
    internal const int NetworkLossConfirmationMisses = 3;
    /// <summary>Guard chống race condition: đánh dấu port đang trong quá trình khởi tạo SIM đầu tiên.</summary>
    private readonly ConcurrentDictionary<string, bool> _simInitInProgress = new();
    private readonly ConcurrentDictionary<string, bool> _simInsertInProgress = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portLifetimeCts = new();
    private readonly PortReconnectCoordinator _portReconnects = new();
    private readonly object _connectLock = new object();
    private static readonly TimeSpan PortReconnectDelay = TimeSpan.FromMilliseconds(1500);


    public bool IsCallInProgress(string portName) =>
        _outgoingCallOperations.ContainsKey(portName)
        || (_activeCalls.TryGetValue(portName, out bool active) && active);

    public QuectelModemProfile? GetModemProfile(string portName) =>
        _modemProfiles.TryGetValue(portName, out var profile) ? profile : null;

    public async Task ConfigureVoiceFeaturesAsync(string portName, CancellationToken ct = default)
    {
        QuectelModemProfile? profile = GetModemProfile(portName);
        if (profile?.Supports(ModemCapability.VoiceCall) != true) return;

        var commands = new List<string>(4);
        if (profile.Supports(ModemCapability.CallerIdPresentation))
            commands.Add("AT+CLIP=1");
        if (profile.Supports(ModemCapability.CallStatusIndication))
            commands.Add("AT^DSCI=1");
        if (profile.Supports(ModemCapability.DtmfDetection))
            commands.Add("AT+QTONEDET=1");
        commands.Add("AT+CRC=1");

        foreach (string command in commands)
        {
            ct.ThrowIfCancellationRequested();
            string response = await SendCommandAsync(portName, command, 3000, silent: true, ct: ct);
            bool accepted = response.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[VOICE_CONFIG] {command} => {(accepted ? "OK" : response.Trim())}"
            });
        }
    }

    public IDisposable SuspendPortBackgroundOperations(
        string portName,
        bool preserveCurrentNetworkPollingForResume = true)
    {
        lock (_backgroundOperationSync)
        {
            int leaseCount = _suspendedBackgroundPorts.AddOrUpdate(
                portName, 1, static (_, current) => current + 1);
            if (leaseCount == 1)
            {
                if (ShouldPreserveNetworkPollingOnSuspension(
                        preserveCurrentNetworkPollingForResume)
                    && _pollingExpectedIdentities.TryGetValue(
                        portName, out NetworkPollingIdentity? pollingIdentity))
                    _pendingNetworkPollingPorts[portName] = pollingIdentity;
                CancelLoop(_pollingCts, portName);
                _pollingExpectedIdentities.TryRemove(portName, out _);
                CancelLoop(_keepAliveCts, portName);
                CancelLoop(_simMonitorCts, portName);
            }

            // IMEI mutation is a fail-closed boundary.  A polling request that
            // belonged to the pre-mutation IMEI must never be restored merely
            // because the suspension lease was disposed.  Remove that captured
            // request even when this is a nested lease; a later explicit
            // StartPollingNetwork call after exact CCID + slot-7 verification is
            // the only operation allowed to enqueue a new identity.
            if (!ShouldPreserveNetworkPollingOnSuspension(
                    preserveCurrentNetworkPollingForResume))
                _pendingNetworkPollingPorts.TryRemove(portName, out _);
        }

        return new BackgroundOperationLease(() =>
        {
            NetworkPollingIdentity? resumeNetworkPollingIdentity = null;
            lock (_backgroundOperationSync)
            {
                if (!_suspendedBackgroundPorts.TryGetValue(portName, out int leaseCount))
                    return;

                if (leaseCount > 1)
                {
                    _suspendedBackgroundPorts[portName] = leaseCount - 1;
                    return;
                }

                _suspendedBackgroundPorts.TryRemove(portName, out _);
                _pendingNetworkPollingPorts.TryRemove(
                    portName, out resumeNetworkPollingIdentity);
            }

            // CompleteSautoResetAsync reaches Active while the IMEI operation still owns
            // this lease. Its StartPollingNetwork request must run after the lease opens;
            // dropping it here left the UI with only IMEI/CCID/CSQ and no COPS/USSD data.
            if (resumeNetworkPollingIdentity != null)
                StartPollingNetwork(
                    portName,
                    resumeNetworkPollingIdentity.Ccid,
                    resumeNetworkPollingIdentity.Imei);
        });

        static void CancelLoop(
            ConcurrentDictionary<string, CancellationTokenSource> loops,
            string name)
        {
            if (!loops.TryRemove(name, out var cts)) return;
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
    }

    private sealed class BackgroundOperationLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal static bool ShouldPreserveNetworkPollingOnSuspension(
        bool preserveRequested) => preserveRequested;

    // ===================== SMS DECODE + MULTIPART =====================
    private const string OtpKeywordPattern =
        @"(?:otp|m[aã]\s*otp|m[aã]\s*x[aá]c\s*th[uự]c|m[aã]\s*x[aá]c\s*nh[aậ]n|" +
        @"verification\s*code|auth(?:entication)?\s*code|security\s*code|passcode|" +
        @"m[aã]\s*pin|m[aậ]t\s*kh[aẩ]u|token|pin|code)";

    private static readonly Regex OtpAfterKeywordRegex = new(
        $@"(?<![\p{{L}}\p{{N}}]){OtpKeywordPattern}(?![\p{{L}}\p{{N}}])[^\d]{{0,48}}(?<code>\d{{4,8}})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OtpBeforeKeywordRegex = new(
        $@"(?<!\d)(?<code>\d{{4,8}})(?!\d)[^\d]{{0,48}}(?<![\p{{L}}\p{{N}}]){OtpKeywordPattern}(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // WhatsApp formats its six-digit code as XXX-XXX and often does not use
    // the literal words "OTP" or "verification code". Keep this pattern
    // context-bound to WhatsApp so ordinary dates/phone numbers are not
    // promoted to OTPs.
    private static readonly Regex WhatsAppGroupedOtpRegex = new(
        $@"(?<![\p{{L}}\p{{N}}])whatsapp(?![\p{{L}}\p{{N}}])[^\d]{{0,48}}(?<first>\d{{3}})\s*[-\u2010-\u2015\u2212]\s*(?<second>\d{{3}})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericOnlyOtpRegex = new(
        @"^\s*(?<code>\d{4,8})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VnptEcontractOtpRegex = new(
        @"(?:vnpt|econtract|e-contract|ma\s+otp|otp|ky\s+hop\s+dong)[^\d]{0,160}(?<code>\d{6})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? ExtractOtp(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // Xóa SĐT đã che (***7003) trước khi tìm mã để không lấy nhầm 4 số cuối.
        string text = Regex.Replace(content.Trim(), @"\*+\d+", "");

        // Chỉ nhận số gắn với ngữ cảnh OTP/mã xác thực. Không còn fallback lấy bừa
        // số 4-8 chữ số vì nó biến số tiền (19980đ), phút, shortcode... thành OTP.
        Match groupedMatch = WhatsAppGroupedOtpRegex.Match(text);
        if (groupedMatch.Success)
            return groupedMatch.Groups["first"].Value + groupedMatch.Groups["second"].Value;

        Match match = OtpAfterKeywordRegex.Match(text);
        if (match.Success) return match.Groups["code"].Value;

        match = OtpBeforeKeywordRegex.Match(text);
        if (match.Success) return match.Groups["code"].Value;

        string repaired = TextEncodingNormalizer.RepairMojibake(text);
        match = VnptEcontractOtpRegex.Match(repaired);
        if (match.Success) return match.Groups["code"].Value;

        // Một SMS chỉ chứa duy nhất dãy số vẫn là định dạng OTP hợp lệ phổ biến.
        match = NumericOnlyOtpRegex.Match(text);
        return match.Success ? match.Groups["code"].Value : null;
    }

    public static string DecodeSmsBody(string raw)
        => SmsBodyDecoder.Decode(raw).Content;

    static bool IsHexString(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length % 2 != 0) return false;
        foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
        return s.Length >= 4;
    }

    static string DecodeUcs2Hex(string hex)
    {
        // Loại bỏ User Data Header (UDH) của tin nhắn ghép nối trong chế độ Text
        // UDH 8-bit ref: 05 00 03 [Ref] [Total] [Seq] -> 6 bytes = 12 hex chars
        if (hex.StartsWith("050003", StringComparison.OrdinalIgnoreCase) && hex.Length >= 12)
        {
            hex = hex.Substring(12);
        }
        // UDH 16-bit ref: 06 08 04 [RefHi] [RefLo] [Total] [Seq] -> 7 bytes = 14 hex chars
        // Lưu ý: Nếu UDH lẻ byte (7 bytes), hệ thống SMS thường thêm 1 byte padding (lên 8 bytes = 16 hex chars) để căn lề UCS2
        else if (hex.StartsWith("060804", StringComparison.OrdinalIgnoreCase) && hex.Length >= 14)
        {
            hex = hex.Substring(hex.Length % 4 == 2 ? 14 : 16); // tự động bù padding
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return Encoding.BigEndianUnicode.GetString(bytes).Trim('\0');
    }

    private readonly SmsMultipartJournal _multipartJournal =
        CreateMultipartJournal();
    private readonly SmsSimCleanupJournal _simCleanupJournal =
        CreateSimCleanupJournal();
    private readonly ConcurrentDictionary<string, DateTime> _deliveredStoredSms = new();
    private readonly ConcurrentDictionary<string, SmsReadQueueState> _smsReadQueues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _smsPortGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _smsSimIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _networkSimIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _networkIdentityGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _directCmtRetryOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirectCmtRetryState> _directCmtRetryStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _directCmtQuarantineGate = new();
    private readonly ConcurrentDictionary<string, byte> _multipartReplayOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _multipartCompletionRetryOwners =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _multipartPartCleanupRetryOwners =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSweepLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _smsSweepRetryOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _smsRetryLogAt = new();
    private readonly ConcurrentDictionary<string, int> _smsReadRetryAttempts = new();
    // Value 1 = one read is queued/running; value 2 = the same SIM index was
    // announced again while it was busy and must be read once more. EC20 can
    // recycle an index immediately after CMGD, so silently dropping the second
    // notification can postpone a new SMS until the recovery sweep.
    private readonly ConcurrentDictionary<string, int> _queuedSmsIndices = new();
    private const int MaxStoredSmsReadRetryAttempts = 8;
    private const int MaxMultipartJournalRetryAttempts = 8;

    internal static string StableSmsDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolGSM",
            "Data");

    private static SmsMultipartJournal CreateMultipartJournal()
    {
        string stablePath = Path.Combine(
            StableSmsDataDirectory, "sms_multipart_journal.json");
        return new SmsMultipartJournal(
            stablePath,
            legacyPaths: DiscoverLegacyMultipartJournalPaths(stablePath));
    }

    private static SmsSimCleanupJournal CreateSimCleanupJournal()
    {
        string primaryPath = Path.Combine(
            StableSmsDataDirectory, "sms_sim_cleanup_journal.json");
        string fallbackPath = Path.Combine(
            StableSmsDataDirectory, "sms_sim_cleanup_journal.pending.json");
        return new SmsSimCleanupJournal(primaryPath, fallbackPath);
    }

    internal static IReadOnlyList<string> DiscoverLegacyMultipartJournalPaths(
        string stablePath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(AppBootstrap.DataDir, "sms_multipart_journal.json")
        };
        try
        {
            string appDirectory = AppBootstrap.AppDir.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string? publishRoot = Directory.GetParent(appDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(publishRoot)
                && Directory.Exists(publishRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(
                             publishRoot, "publish*"))
                {
                    paths.Add(Path.Combine(
                        directory, "Data", "sms_multipart_journal.json"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or DirectoryNotFoundException)
        {
            // Migration is best effort and never deletes/mutates a legacy file.
        }

        paths.RemoveWhere(path => string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(stablePath),
            StringComparison.OrdinalIgnoreCase));
        return paths.ToArray();
    }

    private void TrimDeliveredStoredSms()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var item in _deliveredStoredSms
                     .Where(x => now - x.Value > TimeSpan.FromMinutes(10))
                     .ToArray())
            _deliveredStoredSms.TryRemove(item.Key, out _);

        const int maxRecentDeliveries = 20_000;
        int overflow = _deliveredStoredSms.Count - maxRecentDeliveries;
        if (overflow <= 0) return;
        foreach (KeyValuePair<string, DateTime> item in _deliveredStoredSms
                     .OrderBy(x => x.Value)
                     .Take(overflow)
                     .ToArray())
            _deliveredStoredSms.TryRemove(item.Key, out _);
    }

    private void RememberDeliveredSms(string deliveryId)
    {
        _deliveredStoredSms[deliveryId] = DateTime.UtcNow;
        TrimDeliveredStoredSms();
    }

    public void SetSmsSimIdentity(string portName, string? ccid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        string normalized = Regex.Match(
            ccid ?? string.Empty, @"(?<!\d)89\d{16,20}(?!\d)").Value;
        bool networkIdentityChanged;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            networkIdentityChanged = _networkSimIdentities.TryRemove(
                portName, out _);
        }
        else
        {
            networkIdentityChanged =
                !_networkSimIdentities.TryGetValue(
                    portName, out string? currentNetworkCcid)
                || !string.Equals(
                    currentNetworkCcid,
                    normalized,
                    StringComparison.Ordinal);
            _networkSimIdentities[portName] = normalized;
        }
        if (networkIdentityChanged)
        {
            _networkIdentityGenerations.AddOrUpdate(
                portName, 1, static (_, current) => current + 1);
            InvalidateNetworkRecoveryForIdentityChange(portName);
        }

        bool changed;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            changed = _smsSimIdentities.TryRemove(portName, out _);
        }
        else
        {
            changed = !_smsSimIdentities.TryGetValue(portName, out string? current)
                || !string.Equals(current, normalized, StringComparison.Ordinal);
            if (changed)
            {
                try
                {
                    _multipartJournal.RebindLegacyPortScope(
                        portName,
                        $"ccid:{normalized}");
                }
                catch (Exception ex) when (ex is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
                {
                    _smsSimIdentities.TryRemove(portName, out _);
                    InvalidateSmsQueueGeneration(portName);
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[SMS_IDENTITY_BLOCKED] Không thể chuyển journal cũ sang CCID an toàn: {ex.Message}. Giữ SMS trên SIM, chưa xóa."
                    });
                    return;
                }
            }
            _smsSimIdentities[portName] = normalized;
        }

        if (changed)
        {
            InvalidateSmsQueueGeneration(portName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                string scope = $"ccid:{normalized}";
                ScheduleCompletedMultipartReplay(scope, portName);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        await SweepUnreadSmsAsync(portName).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[SMS_IDENTITY_SWEEP_RETRY] {ex.Message}"
                        });
                    }
                });
            }
        }
    }

    private void InvalidateNetworkRecoveryForIdentityChange(string portName)
    {
        // A polling/recovery loop belongs to the physical SIM identity that
        // started it.  Cancel it synchronously when CCID changes so an old
        // CME-13 task cannot run CFUN/COPS against a hot-swapped card.
        lock (_backgroundOperationSync)
        {
            lock (_pollingCts)
            {
                if (_pollingCts.TryRemove(
                        portName, out CancellationTokenSource? pollingCts))
                {
                    try { pollingCts.Cancel(); } catch { }
                    pollingCts.Dispose();
                }
            }
            _pollingExpectedIdentities.TryRemove(portName, out _);
            _pendingNetworkPollingPorts.TryRemove(portName, out _);
        }
        _rebootRecoveryInProgress.TryRemove(portName, out _);

        string prefix = portName + "\u001f";
        foreach (string key in _networkSimRecoveryAttempts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _networkSimRecoveryAttempts.TryRemove(key, out _);
        }
    }

    internal bool CancelNetworkPollingForIdentityReverification(
        string portName,
        string expectedCcid,
        string expectedImei,
        string reason)
    {
        CancellationTokenSource? pollingCts = null;
        lock (_backgroundOperationSync)
        {
            if (!_pollingExpectedIdentities.TryGetValue(
                    portName, out NetworkPollingIdentity? currentIdentity)
                || !NetworkPollingIdentitiesMatch(
                    currentIdentity.Ccid,
                    currentIdentity.Imei,
                    expectedCcid,
                    expectedImei))
            {
                return false;
            }

            _pollingExpectedIdentities.TryRemove(portName, out _);
            _pendingNetworkPollingPorts.TryRemove(portName, out _);
            lock (_pollingCts)
            {
                _pollingCts.TryRemove(portName, out pollingCts);
            }
        }

        if (pollingCts != null)
        {
            try { pollingCts.Cancel(); } catch { }
            pollingCts.Dispose();
        }

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[NETWORK_REOPEN_REQUIRED] reason=identity-reverify; expected_ccid={expectedCcid}; expected_imei={expectedImei}; {reason}"
        });
        return true;
    }

    private void ClearNetworkSimRecoveryAttempts(string portName)
    {
        string prefix = portName + "\u001f";
        foreach (string key in _networkSimRecoveryAttempts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _networkSimRecoveryAttempts.TryRemove(key, out _);
        }
        // Backward-compatible cleanup for entries created by an older build.
        _networkSimRecoveryAttempts.TryRemove(portName, out _);
    }

    private void ClearNetworkSimRecoveryState(string portName)
    {
        ClearNetworkSimRecoveryAttempts(portName);
        string prefix = portName + "\u001f";
        foreach (string key in _networkSimRecoveryOwners.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _networkSimRecoveryOwners.TryRemove(key, out _);
        }
        _networkSimRecoveryOwners.TryRemove(portName, out _);
    }

    private long CurrentSmsGeneration(string portName) =>
        _smsPortGenerations.GetOrAdd(portName, 1);

    private bool IsCurrentSmsGeneration(string portName, long generation) =>
        _smsPortGenerations.TryGetValue(portName, out long current)
        && current == generation
        && _serialPorts.ContainsKey(portName);

    private bool TryGetSmsScope(
        string portName,
        long generation,
        out string scope)
    {
        scope = string.Empty;
        if (!IsCurrentSmsGeneration(portName, generation)
            || !_smsSimIdentities.TryGetValue(portName, out string? ccid)
            || string.IsNullOrWhiteSpace(ccid))
            return false;
        scope = $"ccid:{ccid}";
        return true;
    }

    private void InvalidateSmsQueueGeneration(string portName)
    {
        _smsPortGenerations.AddOrUpdate(
            portName, 1, static (_, current) => checked(current + 1));
        if (_smsReadQueues.TryRemove(portName, out SmsReadQueueState? state))
        {
            try { state.Cancellation.Cancel(); } catch { }
            state.Queue.Writer.TryComplete();
        }
        foreach (string key in _queuedSmsIndices.Keys
                     .Where(key => key.StartsWith(
                         portName + "\u001f", StringComparison.Ordinal)))
            _queuedSmsIndices.TryRemove(key, out _);
        foreach (string key in _smsRetryLogAt.Keys
                     .Where(key => key.StartsWith(
                          portName + "\u001f", StringComparison.Ordinal)))
            _smsRetryLogAt.TryRemove(key, out _);
        foreach (string key in _smsReadRetryAttempts.Keys
                     .Where(key => key.StartsWith(
                         portName + "\u001f", StringComparison.Ordinal)))
            _smsReadRetryAttempts.TryRemove(key, out _);
    }

    private static string BuildDeliveryId(string kind, params string?[] fields)
    {
        string identity = string.Join(
            '\u001f',
            new[] { kind }.Concat(fields.Select(value => value ?? string.Empty)));
        return $"sms-{kind}-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static int BuildStableDirectReference(string deliveryId) =>
        BitConverter.ToInt32(
            SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId)),
            0) & int.MaxValue;

    private static string NormalizeStoredSmsForIdentity(string raw)
    {
        string normalized = Regex.Replace(
            raw ?? string.Empty,
            @"""REC\s+(?:UN)?READ""",
            "\"REC\"",
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"(?:^|\r?\n)AT\+Q?CMGR\s*=\s*\d+\s*(?=\r?\n|$)",
            string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"(?:\r?\n)?OK\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        return Regex.Replace(normalized.Trim(), @"\s+", " ");
    }

    private async Task<string> ReadStoredSmsAsync(
        string port,
        string msgIndex,
        CancellationToken ct)
    {
        // Quectel EC20/EC2x exposes uid, segment and total through QCMGR in text mode.
        // This must be tried before CMGR: CMGR in text mode strips UDH on several EC20
        // firmware banks, which turns one long Vietnamese SMS into unrelated 67-char rows.
        // QCMGR either retains the PDU UDH or returns uid/msg_seg/msg_total explicitly.
        // Fall back to standard CMGR for older firmware and non-Quectel modems.
        IReadOnlyList<string> commands = GetStoredSmsReadCommandOrder(
            GetModemProfile(port), msgIndex);
        foreach (string command in commands)
        {
            // A valid SMS body must not be discarded only because the modem
            // omitted the trailing OK terminator.
            string response = await SendCommandAsync(
                port, command, 8000, silent: true, ct: ct);
            if (command.StartsWith("AT+QCMGR=", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCompleteStoredSmsResponse(response, "+QCMGR:")
                    || HasUsableStoredSmsBody(response))
                    return response;
                continue;
            }
            if (command.Equals("AT+CMGF=0", StringComparison.OrdinalIgnoreCase))
                continue;
            return response;
        }
        return string.Empty;
    }

    internal static IReadOnlyList<string> GetStoredSmsReadCommandOrder(
        QuectelModemProfile? profile,
        string msgIndex) => profile?.Supports(ModemCapability.QuectelStoredSms) == true
        // If a firmware revision rejects QCMGR in text mode, switch this COM to
        // PDU mode before CMGR so UDH/ref/seq/total cannot be stripped.
        ? [$"AT+QCMGR={msgIndex}", "AT+CMGF=0", $"AT+CMGR={msgIndex}"]
        : [$"AT+CMGR={msgIndex}"];

    internal static bool IsCompleteStoredSmsResponse(string response, string? requiredHeader = null)
    {
        if (string.IsNullOrWhiteSpace(response)
            || EndsWithModemCommandError(response)) return false;
        if (requiredHeader != null && !response.Contains(requiredHeader, StringComparison.OrdinalIgnoreCase)) return false;
        if (requiredHeader == null
            && !Regex.IsMatch(response, @"(?:\+CMGR:|\+QCMGR:|\+CMT:)", RegexOptions.IgnoreCase))
            return false;
        if (!Regex.IsMatch(response, @"(?:^|\r?\n)OK\s*$", RegexOptions.IgnoreCase)) return false;
        return !string.IsNullOrWhiteSpace(SmsBodyDecoder.Decode(response).Content);
    }

    // Some EC20 firmware sends the complete +CMGR/+QCMGR body but drops the
    // trailing OK while another URC is interleaved.  The body is still safe to
    // decode; rejecting it here made a valid SMS wait forever in the SIM store.
    internal static bool HasUsableStoredSmsBody(string response)
    {
        if (string.IsNullOrWhiteSpace(response)
            || EndsWithModemCommandError(response)
            || !Regex.IsMatch(response, @"(?:\+CMGR:|\+QCMGR:)", RegexOptions.IgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(SmsBodyDecoder.Decode(response).Content);
    }

    internal static bool EndsWithModemCommandError(string response) =>
        Regex.IsMatch(
            response ?? string.Empty,
            @"(?:^|\r?\n)\s*(?:ERROR|\+(?:CMS|CME)\s+ERROR\s*:[^\r\n]*)\s*$",
            RegexOptions.IgnoreCase);

    internal sealed record CmglRoutingResult(
        IReadOnlyList<string> Indices,
        string CommandResponseData,
        bool PreservedForPendingCommand);

    internal static CmglRoutingResult RouteCmglData(
        string? currentData,
        string? pendingCommand)
    {
        string data = currentData ?? string.Empty;
        MatchCollection matches = Regex.Matches(
            data,
            @"\+CMGL:\s*(\d+)",
            RegexOptions.IgnoreCase);
        string[] indices = matches
            .Select(match => match.Groups[1].Value)
            .ToArray();
        bool preserve = Regex.IsMatch(
            pendingCommand ?? string.Empty,
            @"^AT\+CMGL\s*=",
            RegexOptions.IgnoreCase);
        if (preserve || matches.Count == 0)
            return new CmglRoutingResult(indices, data, preserve);

        // An unsolicited CMGL listing has no command owner. Remove only the
        // index tokens that were routed to the durable per-port read queue.
        // A pending AT+CMGL response is intentionally never changed here: the
        // recovery parser must see every original header before it can prove
        // that a recyclable SIM slot is absent.
        var remaining = new StringBuilder(data);
        foreach (Match match in matches.Cast<Match>().Reverse())
            remaining.Remove(match.Index, match.Length);
        return new CmglRoutingResult(indices, remaining.ToString(), false);
    }

    internal static bool TryParseTrustedPduStoredSmsIndexSnapshot(
        string? response,
        out IReadOnlySet<string> indices)
    {
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        // Never expose a partial snapshot to callers. A malformed record after
        // one valid record must fail closed with an empty result; otherwise a
        // recovery caller could accidentally treat the partial list as proof
        // that another recyclable SIM slot is absent.
        indices = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(response)
            || Regex.IsMatch(
                response,
                @"(?:^|\r?\n)\s*(?:ERROR|\+(?:CMS|CME)\s+ERROR\s*:[^\r\n]*)\s*(?:\r?\n|$)",
                RegexOptions.IgnoreCase))
            return false;

        string[] lines = response
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
        if (lines.Length == 0
            || !string.Equals(
                lines[^1], "OK", StringComparison.OrdinalIgnoreCase))
            return false;

        int cursor = 0;
        if (Regex.IsMatch(
                lines[cursor],
                @"^AT\+CMGL\s*=\s*4$",
                RegexOptions.IgnoreCase))
            cursor++;

        while (cursor < lines.Length - 1)
        {
            Match header = Regex.Match(
                lines[cursor],
                @"^\+CMGL:\s*(?<index>\d+)\s*,\s*(?<status>[0-4])\s*,\s*(?:""[^""]*""|[^,]*)\s*,\s*(?<length>\d+)\s*$",
                RegexOptions.IgnoreCase);
            if (!header.Success || cursor + 1 >= lines.Length - 1)
                return false;

            string pdu = lines[cursor + 1];
            if (pdu.Length < 2
                || (pdu.Length & 1) != 0
                || !Regex.IsMatch(
                    pdu, @"\A[0-9A-F]+\z", RegexOptions.IgnoreCase))
                return false;

            int declaredLength = int.Parse(
                header.Groups["length"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            int pduOctets = pdu.Length / 2;
            int smscOctets = Convert.ToByte(pdu[..2], 16);
            bool lengthMatches = pduOctets == declaredLength
                || pduOctets == declaredLength + smscOctets + 1;
            if (declaredLength <= 0
                || smscOctets >= pduOctets
                || !lengthMatches
                || !parsed.Add(header.Groups["index"].Value))
                return false;

            cursor += 2;
        }

        // `OK` (optionally preceded only by the exact echoed command) is the
        // sole trustworthy representation of an empty PDU-mode SIM listing.
        if (cursor != lines.Length - 1) return false;
        indices = parsed;
        return true;
    }

    private string? TryAssembleMultipartExact(
        string port,
        string scope,
        string sender,
        DecodedSmsBody decoded,
        string msgIndex,
        string deliveryId,
        out List<string> indicesToDelete,
        out string completedDeliveryId)
    {
        indicesToDelete = new List<string>();
        completedDeliveryId = deliveryId;
        if (decoded.Concatenation == null)
        {
            TrimDeliveredStoredSms();
            // Without UDH/QCMGR metadata, a 67/153-character standalone SMS is
            // indistinguishable from a stripped multipart segment. Holding that
            // heuristic in the SIM caused permanent storage deadlocks. Publish
            // each uncertain record independently; the durable inbox may group
            // it later without hiding or losing a real SMS.
            if (_deliveredStoredSms.ContainsKey(deliveryId))
            {
                if (!string.IsNullOrWhiteSpace(msgIndex))
                    indicesToDelete.Add(msgIndex);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(msgIndex))
                indicesToDelete.Add(msgIndex);
            return decoded.Content;
        }

        IReadOnlyList<SmsMultipartJournal.Part> durableParts;
        try
        {
            durableParts = _multipartJournal.RecordAndGetParts(
                scope,
                sender,
                decoded.Concatenation,
                decoded.Content,
                portName: port,
                partIdentity: deliveryId);
            completedDeliveryId = _multipartJournal.GetMessageIdForPartIdentity(
                scope, deliveryId);
            if (string.IsNullOrWhiteSpace(completedDeliveryId))
                throw new InvalidDataException(
                    "Multipart segment was committed without a resolvable delivery identity.");
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[MULTIPART_JOURNAL_ERROR] Không lưu an toàn được phần {decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}: {ex.Message}. Giữ nguyên SMS trên SIM."
            });
            return null;
        }

        // The committed journal now owns this exact decoded segment, so a known
        // SIM identity may release the current slot even before all parts arrive.
        if (!string.IsNullOrWhiteSpace(msgIndex))
            indicesToDelete.Add(msgIndex);

        bool durableComplete = durableParts.Count == decoded.Concatenation.Total
            && Enumerable.Range(1, decoded.Concatenation.Total)
                .SequenceEqual(durableParts.Select(part => part.Sequence));
        if (durableComplete)
            return string.Concat(durableParts.Select(part => part.Content));
        return null;
    }

    private void QueueStoredSmsRead(string port, string msgIndex)
    {
        if (!Regex.IsMatch(msgIndex, @"^\d+$")) return;
        long generation = CurrentSmsGeneration(port);
        string queueKey = $"{port}\u001f{generation}\u001f{msgIndex}";
        if (!_queuedSmsIndices.TryAdd(queueKey, 1))
        {
            _queuedSmsIndices.AddOrUpdate(queueKey, 2, static (_, _) => 2);
            return;
        }

        SmsReadQueueState state;
        while (true)
        {
            if (!IsCurrentSmsGeneration(port, generation))
            {
                _queuedSmsIndices.TryRemove(queueKey, out _);
                return;
            }

            state = _smsReadQueues.GetOrAdd(port, p =>
            {
                var created = new SmsReadQueueState(generation);
                created.Worker = Task.Run(
                    () => ProcessStoredSmsQueueAsync(p, created),
                    created.Cancellation.Token);
                return created;
            });
            if (state.Generation == generation) break;

            // InvalidateSmsQueueGeneration can run between reading generation
            // and GetOrAdd. Remove that orphaned old state; otherwise every new
            // enqueue sees the mismatch forever and this COM never reads SMS.
            if (_smsReadQueues.TryRemove(
                    new KeyValuePair<string, SmsReadQueueState>(port, state)))
            {
                try { state.Cancellation.Cancel(); } catch { }
                state.Queue.Writer.TryComplete();
            }
        }
        if (state.Generation != generation
            || state.Cancellation.IsCancellationRequested
            || !state.Queue.Writer.TryWrite(msgIndex))
        {
            _queuedSmsIndices.TryRemove(queueKey, out _);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    if (IsCurrentSmsGeneration(port, generation))
                        await SweepUnreadSmsAsync(port).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = port,
                        Data = $"[SMS_QUEUE_SWEEP_RETRY] {ex.Message}"
                    });
                }
            });
        }
    }

    private async Task ProcessStoredSmsQueueAsync(
        string port,
        SmsReadQueueState state)
    {
        CancellationToken token = state.Cancellation.Token;
        await foreach (string msgIndex in state.Queue.Reader.ReadAllAsync(token))
        {
            if (!IsCurrentSmsGeneration(port, state.Generation)) break;
            // A CMGR can fail while another AT command owns this COM. Keep the
            // SIM index claimed and schedule it again instead of dropping it.
            bool completed = false;
            bool retry = false;
            try
            {
                completed = await ProcessStoredSmsAsync(
                    port, msgIndex, state.Generation, token);
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                retry = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Lỗi đọc index {msgIndex}: {ex.Message}. SMS vẫn được giữ trên SIM." });
            }

            if (!completed)
                retry = true;

            if (completed)
            {
                string queueKey =
                    $"{port}\u001f{state.Generation}\u001f{msgIndex}";
                _smsReadRetryAttempts.TryRemove(queueKey, out _);
                while (_queuedSmsIndices.TryGetValue(queueKey, out int pending))
                {
                    if (pending > 1)
                    {
                        // The just-completed path already issued CMGD. A second
                        // immediate CMGR normally hits an empty slot forever;
                        // if EC20 recycled the index for a new burst message, a
                        // full CMGL sweep discovers that new record safely.
                        if (!_queuedSmsIndices.TryRemove(
                                new KeyValuePair<string, int>(queueKey, pending)))
                            continue;
                        ScheduleSafeUnreadSmsSweep(
                            port,
                            "duplicate-index-after-cleanup");
                        break;
                    }

                    if (_queuedSmsIndices.TryRemove(
                        new KeyValuePair<string, int>(queueKey, pending))) break;
                }
            }

            if (retry)
            {
                // Do not block this single-reader queue while a long AT command
                // is running. A duplicate CMTI during this yield is coalesced by
                // QueueStoredSmsRead (state 2).
                ScheduleStoredSmsRetry(
                    port,
                    msgIndex,
                    $"{port}\u001f{state.Generation}\u001f{msgIndex}",
                    state);
            }
        }
    }

    private void ScheduleStoredSmsRetry(
        string port,
        string msgIndex,
        string queueKey,
        SmsReadQueueState state)
    {
        int attempt = _smsReadRetryAttempts.AddOrUpdate(
            queueKey, 1, static (_, current) => checked(current + 1));
        if (attempt > MaxStoredSmsReadRetryAttempts)
        {
            _queuedSmsIndices.TryRemove(queueKey, out _);
            _smsReadRetryAttempts.TryRemove(queueKey, out _);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_QUEUE_RETRY_DEFERRED] index={msgIndex}; đã hết {MaxStoredSmsReadRetryAttempts} lần retry nhanh. SMS còn nguyên trên SIM; chuyển sang sweep chậm có giới hạn."
            });
            ScheduleSafeUnreadSmsSweep(
                port,
                "stored-read-retry-budget",
                initialDelayMs: 30000);
            return;
        }

        int delayMs = Math.Min(
            10000,
            250 * (1 << Math.Min(attempt - 1, 5)));
        // Keep one claimed key while the delayed write is pending. The SIM
        // record remains untouched. Exponential bounded delay prevents one
        // poisoned slot from monopolizing the COM command semaphore.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, state.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!_queuedSmsIndices.ContainsKey(queueKey)
                    || !IsCurrentSmsGeneration(port, state.Generation))
                    return;
                if (!_smsReadQueues.TryGetValue(
                        port, out SmsReadQueueState? current)
                    || !ReferenceEquals(current, state)
                    || !state.Queue.Writer.TryWrite(msgIndex))
                {
                    _queuedSmsIndices.TryRemove(queueKey, out _);
                    _smsReadRetryAttempts.TryRemove(queueKey, out _);
                }
            }
            catch (OperationCanceledException)
                when (state.Cancellation.IsCancellationRequested)
            {
                _queuedSmsIndices.TryRemove(queueKey, out _);
                _smsReadRetryAttempts.TryRemove(queueKey, out _);
            }
            catch (Exception ex)
            {
                _queuedSmsIndices.TryRemove(queueKey, out _);
                _smsReadRetryAttempts.TryRemove(queueKey, out _);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_QUEUE] Retry index {msgIndex} failed: {ex.Message}; SMS retained on SIM."
                });
            }
        });
    }

    private SmsSimCleanupJournal.Intent? PrepareMultipartSimCleanup(
        string port,
        string scope,
        string simIndex,
        string messageId,
        string partIdentity,
        long generation)
    {
        if (!TryGetSmsScope(port, generation, out string currentScope)
            || !string.Equals(scope, currentScope, StringComparison.Ordinal))
            return null;

        try
        {
            return _simCleanupJournal.Prepare(
                scope,
                port,
                simIndex,
                messageId,
                partIdentity);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_SIM_CLEANUP_BLOCKED] index={simIndex} delivery={messageId}; không ghi bền vững được ý định xóa: {ex.Message}. Giữ nguyên SMS trên SIM."
            });
            return null;
        }
    }

    private sealed record LockedStoredSmsRead(
        string Response,
        bool PduModeAttempted);

    private async Task<string> SendCommandWhilePortLockedAsync(
        string portName,
        SerialPort serialPort,
        string command,
        int timeoutMs,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(
            command,
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandTcs.TryAdd(portName, tcs))
            return "ERROR: Another command is already in progress";

        try
        {
            if (!serialPort.IsOpen) return "ERROR: Port not open";
            serialPort.Write(command + "\r\n");
            try
            {
                string response = await tcs.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(timeoutMs),
                    ct);
                return response.Trim();
            }
            catch (TimeoutException)
            {
                tcs.TrySetCanceled();
                return "ERROR: Timeout (device did not return OK/ERROR)";
            }
        }
        catch (IOException ex)
        {
            return $"ERROR: Serial I/O - {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"ERROR: Serial port state - {ex.Message}";
        }
        finally
        {
            if (_commandTcs.TryGetValue(
                    portName,
                    out TaskCompletionSource<string>? current)
                && ReferenceEquals(current, tcs))
                _commandTcs.TryRemove(portName, out _);
        }
    }

    private async Task<LockedStoredSmsRead> ReadStoredSmsWhilePortLockedAsync(
        string portName,
        SerialPort serialPort,
        string msgIndex,
        CancellationToken ct)
    {
        bool pduModeAttempted = false;
        foreach (string command in GetStoredSmsReadCommandOrder(
                     GetModemProfile(portName),
                     msgIndex))
        {
            if (command.Equals("AT+CMGF=0", StringComparison.OrdinalIgnoreCase))
                pduModeAttempted = true;

            string response = await SendCommandWhilePortLockedAsync(
                portName,
                serialPort,
                command,
                8000,
                ct);
            if (command.StartsWith(
                    "AT+QCMGR=", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCompleteStoredSmsResponse(response, "+QCMGR:")
                    || HasUsableStoredSmsBody(response))
                    return new LockedStoredSmsRead(response, pduModeAttempted);
                continue;
            }

            if (command.Equals("AT+CMGF=0", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCommandFailure(response))
                    return new LockedStoredSmsRead(string.Empty, true);
                continue;
            }

            return new LockedStoredSmsRead(response, pduModeAttempted);
        }

        return new LockedStoredSmsRead(string.Empty, pduModeAttempted);
    }

    private async Task<string> ReadFreshCcidWhilePortLockedAsync(
        string portName,
        SerialPort serialPort,
        CancellationToken ct)
    {
        string[] commands =
        [
            "AT+QCCID",
            "AT+ICCID",
            "AT+CCID",
            "AT+CRSM=176,12258,0,0,10"
        ];
        foreach (string command in commands)
        {
            string response = await SendCommandWhilePortLockedAsync(
                portName,
                serialPort,
                command,
                5000,
                ct);
            Match ccid = Regex.Match(
                response ?? string.Empty,
                @"(?<!\d)89\d{16,20}(?!\d)");
            if (ccid.Success) return ccid.Value;

            // A timed-out response makes the serial command boundary
            // ambiguous. Do not send CMGD or treat a later response as CCID.
            if (response?.Contains(
                    "Timeout", StringComparison.OrdinalIgnoreCase) == true)
                return string.Empty;
        }
        return string.Empty;
    }

    private async Task<bool> RestoreSmsReceiveModeWhilePortLockedAsync(
        string portName,
        SerialPort serialPort)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CMGF=1", 5000, CancellationToken.None);
            await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CSCS=\"UCS2\"", 5000, CancellationToken.None);
            await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CNMI=1,1,0,0,0", 5000, CancellationToken.None);

            string cmgf = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CMGF?", 5000, CancellationToken.None);
            string cscs = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CSCS?", 5000, CancellationToken.None);
            string cnmi = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CNMI?", 5000, CancellationToken.None);
            if (Regex.IsMatch(cmgf, @"\+CMGF:\s*1\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(
                    cscs, @"\+CSCS:\s*""UCS2""", RegexOptions.IgnoreCase)
                && Regex.IsMatch(
                    cnmi, @"\+CNMI:\s*1\s*,\s*1\b", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    internal static string BuildStoredSmsDeliveryId(
        string scope,
        string msgIndex,
        string response)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || !Regex.IsMatch(msgIndex ?? string.Empty, @"^\d+$")
            || !(IsCompleteStoredSmsResponse(response)
                 || HasUsableStoredSmsBody(response)))
            return string.Empty;

        DecodedSmsBody decoded = SmsBodyDecoder.Decode(response);
        if (string.IsNullOrWhiteSpace(decoded.Content)) return string.Empty;
        string sender = ParseSenderFromCmgr(response);
        if (sender == "Unknown" && !string.IsNullOrWhiteSpace(decoded.Sender))
            sender = DecodeSmsSender(decoded.Sender);
        if (string.IsNullOrWhiteSpace(sender)) sender = "Unknown";
        return BuildDeliveryId(
            "stored",
            scope,
            msgIndex,
            sender,
            NormalizeStoredSmsForIdentity(response));
    }

    internal static bool StoredSmsMatchesExpectedIdentity(
        string scope,
        string msgIndex,
        string expectedDeliveryId,
        string response)
    {
        if (string.IsNullOrWhiteSpace(expectedDeliveryId)) return false;
        string actualDeliveryId = BuildStoredSmsDeliveryId(
            scope,
            msgIndex,
            response);
        return string.Equals(
            actualDeliveryId,
            expectedDeliveryId,
            StringComparison.Ordinal);
    }

    private async Task<bool> DeleteStoredSmsIndexAsync(
        string port,
        string index,
        string expectedScope,
        string expectedDeliveryId,
        string reason,
        long generation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(index)
            || !Regex.IsMatch(index, @"^\d+$")
            || !TryGetSmsScope(port, generation, out string currentScope)
            || !string.Equals(
                expectedScope, currentScope, StringComparison.Ordinal)
            || !EnsurePortOpen(port, out SerialPort? serialPort)
            || serialPort == null
            || !_semaphores.TryGetValue(port, out SemaphoreSlim? semaphore))
            return false;

        bool lockAcquired = await semaphore.WaitAsync(10000, ct);
        if (!lockAcquired) return false;

        bool pduModeAttempted = false;
        bool deleted = false;
        try
        {
            if (!TryGetSmsScope(port, generation, out currentScope)
                || !string.Equals(
                    expectedScope, currentScope, StringComparison.Ordinal))
                return false;

            LockedStoredSmsRead freshRead =
                await ReadStoredSmsWhilePortLockedAsync(
                    port, serialPort, index, ct);
            pduModeAttempted = freshRead.PduModeAttempted;
            string freshCcid = await ReadFreshCcidWhilePortLockedAsync(
                port, serialPort, ct);
            string expectedCcid = expectedScope.StartsWith(
                    "ccid:", StringComparison.Ordinal)
                ? expectedScope["ccid:".Length..]
                : string.Empty;
            if (string.IsNullOrWhiteSpace(freshCcid)
                || !string.Equals(
                    freshCcid, expectedCcid, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(freshCcid))
                    SetSmsSimIdentity(port, freshCcid);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_SIM_CLEANUP_IDENTITY_BLOCKED] index={index} reason={reason}; CCID fresh không khớp CCID đã đọc SMS. Không gửi CMGD."
                });
                return false;
            }

            if (!TryGetSmsScope(port, generation, out currentScope)
                || !string.Equals(
                    expectedScope, currentScope, StringComparison.Ordinal)
                || !StoredSmsMatchesExpectedIdentity(
                    expectedScope,
                    index,
                    expectedDeliveryId,
                    freshRead.Response))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_SIM_CLEANUP_CONTENT_BLOCKED] index={index} reason={reason}; slot đã đổi nội dung hoặc không đọc lại đầy đủ. Không gửi CMGD."
                });
                return false;
            }

            string response = await SendCommandWhilePortLockedAsync(
                port,
                serialPort,
                $"AT+CMGD={index},0",
                5000,
                ct);
            if (!TryGetSmsScope(port, generation, out currentScope)
                || !string.Equals(
                    expectedScope, currentScope, StringComparison.Ordinal))
                return false;

            deleted = !EndsWithModemCommandError(response)
                && Regex.IsMatch(
                    response ?? string.Empty,
                    @"(?:^|\r?\n)\s*OK\s*$",
                    RegexOptions.IgnoreCase);
            if (!deleted)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_SIM_CLEANUP_FAILED] index={index} reason={reason}; sẽ đọc lại đúng nội dung trước khi thử xóa: {(response ?? string.Empty).Trim()}"
                });
            }
        }
        finally
        {
            try
            {
                if (pduModeAttempted)
                {
                    bool restored =
                        await RestoreSmsReceiveModeWhilePortLockedAsync(
                            port, serialPort);
                    if (!restored)
                    {
                        deleted = false;
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = port,
                            Data = "[SMS_RECEIVE_MODE_RESTORE_BLOCKED] Chưa xác minh lại được CMGF=1/CSCS=UCS2/CNMI=1,1; giữ cleanup intent để tự phục hồi lần sau."
                        });
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        if (deleted)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_SIM_CLEANUP] index={index} reason={reason}; đã giải phóng bộ nhớ SIM sau khi xác minh lại CCID và nội dung."
            });
        }
        return deleted;
    }

    private async Task<bool> ProcessStoredSmsAsync(
        string port,
        string msgIndex,
        long generation,
        CancellationToken ct)
    {
        if (!TryGetSmsScope(port, generation, out string scope))
            return false;

        string smsContent = await ReadStoredSmsAsync(port, msgIndex, ct);
        if (!TryGetSmsScope(port, generation, out scope))
            return false;

        bool completeResponse = IsCompleteStoredSmsResponse(smsContent);
        bool success = completeResponse || HasUsableStoredSmsBody(smsContent);
        if (!success)
        {
            string retryKey = $"{port}\u001f{generation}\u001f{msgIndex}";
            DateTime now = DateTime.UtcNow;
            if (!_smsRetryLogAt.TryGetValue(retryKey, out DateTime lastLog)
                || now - lastLog >= TimeSpan.FromSeconds(2))
            {
                _smsRetryLogAt[retryKey] = now;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_WAITING_MODEM] index={msgIndex}; giữ nguyên SMS trên SIM, đọc lại khi COM sẵn sàng."
                });
            }
            return false;
        }

        if (!completeResponse)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_READ_RECOVERED] index={msgIndex}; body hợp lệ dù thiếu terminator."
            });
        }

        DecodedSmsBody decoded = SmsBodyDecoder.Decode(smsContent);
        if (string.IsNullOrWhiteSpace(decoded.Content))
            return false;

        string sender = ParseSenderFromCmgr(smsContent);
        if (sender == "Unknown" && !string.IsNullOrWhiteSpace(decoded.Sender))
            sender = DecodeSmsSender(decoded.Sender);
        if (string.IsNullOrWhiteSpace(sender))
            sender = "Unknown";

        string storedDeliveryId = BuildDeliveryId(
            "stored",
            scope,
            msgIndex,
            sender,
            NormalizeStoredSmsForIdentity(smsContent));
        string? fullContent = TryAssembleMultipartExact(
            port,
            scope,
            sender,
            decoded,
            msgIndex,
            storedDeliveryId,
            out List<string> indicesToDelete,
            out string completedDeliveryId);

        if (fullContent == null)
        {
            if (indicesToDelete.Count == 0)
                return false;

            if (decoded.Concatenation != null
                && PrepareMultipartSimCleanup(
                    port,
                    scope,
                    msgIndex,
                    completedDeliveryId,
                    storedDeliveryId,
                    generation) == null)
                return false;

            if (indicesToDelete.Count != 1
                || !string.Equals(
                    indicesToDelete[0], msgIndex, StringComparison.Ordinal))
                return false;

            bool cleaned = await DeleteStoredSmsIndexAsync(
                port,
                msgIndex,
                scope,
                storedDeliveryId,
                decoded.Concatenation == null
                    ? "already delivered"
                    : $"multipart {decoded.Concatenation.Reference} part {decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}",
                generation,
                ct);
            if (cleaned && decoded.Concatenation != null)
            {
                RecordMultipartPartCleanupOrRetry(
                    completedDeliveryId,
                    storedDeliveryId,
                    port);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[MULTIPART] sender={sender} ref={decoded.Concatenation.Reference} seq={decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}; phần đã lưu bền vững, đang chờ phần còn lại."
                });
            }
            return cleaned;
        }

        bool alreadyAccepted = _deliveredStoredSms.ContainsKey(completedDeliveryId)
            || decoded.Concatenation != null
            && _multipartJournal.IsDeliveryAcknowledged(completedDeliveryId);
        if (!alreadyAccepted)
        {
            var delivery = new GsmDataEventArgs
            {
                PortName = port,
                Data = fullContent,
                MsgIndex = msgIndex,
                Sender = sender,
                Otp = ExtractOtp(fullContent) ?? string.Empty,
                DeliveryId = completedDeliveryId
            };
            try
            {
                SmsReceived?.Invoke(this, delivery);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[SMS_QUEUE] Không phát được SMS index {msgIndex}; giữ nguyên trên SIM: {ex.Message}"
                });
                return false;
            }

            if (!delivery.DeliveryAccepted)
                return false;

            RememberDeliveredSms(completedDeliveryId);
            if (decoded.Concatenation != null)
            {
                try
                {
                    _multipartJournal.MarkDeliveryAcknowledged(
                        completedDeliveryId);
                }
                catch (Exception ex) when (ex is IOException
                                              or UnauthorizedAccessException)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = port,
                        Data = $"[MULTIPART_JOURNAL_WARN] Inbox đã nhận SMS nhưng chưa ghi được trạng thái xác nhận: {ex.Message}"
                    });
                    return false;
                }
            }
        }

        if (decoded.Concatenation != null
            && PrepareMultipartSimCleanup(
                port,
                scope,
                msgIndex,
                completedDeliveryId,
                storedDeliveryId,
                generation) == null)
            return false;

        if (indicesToDelete.Count != 1
            || !string.Equals(
                indicesToDelete[0], msgIndex, StringComparison.Ordinal))
            return false;

        bool cleanupSucceeded = await DeleteStoredSmsIndexAsync(
            port,
            msgIndex,
            scope,
            storedDeliveryId,
            decoded.Concatenation != null
                ? "multipart delivered"
                : "SMS delivered",
            generation,
            ct);
        if (!cleanupSucceeded)
            return false;

        if (decoded.Concatenation != null)
        {
            bool cleanupRecorded = RecordMultipartPartCleanupOrRetry(
                completedDeliveryId,
                storedDeliveryId,
                port);
            try
            {
                if (cleanupRecorded
                    && _multipartJournal.IsSimCleanupConfirmed(
                        completedDeliveryId))
                    _multipartJournal.Complete(completedDeliveryId);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[MULTIPART_JOURNAL_WARN] Đã dọn SIM nhưng chưa dọn được journal: {ex.Message}"
                });
                ScheduleMultipartJournalCompletionRetry(
                    completedDeliveryId,
                    port);
            }
        }

        _smsRetryLogAt.TryRemove(
            $"{port}\u001f{generation}\u001f{msgIndex}",
            out _);
        return true;
    }

    static readonly Regex CmgrHeaderRegex = new(
        @"\+(?:Q?CMGR|CMT):\s*""[^""]*"",\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ParseSenderFromCmgr(string raw)
    {
        Match direct = Regex.Match(raw, @"\+CMT:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (direct.Success)
        {
            string directSender = DecodeSmsSender(direct.Groups[1].Value);
            if (IsHexString(directSender))
            {
                try { return DecodeUcs2Hex(directSender); } catch { }
            }
            return directSender;
        }
        var m = CmgrHeaderRegex.Match(raw);
        if (m.Success)
        {
            string val = DecodeSmsSender(m.Groups[1].Value);
            if (IsHexString(val))
            {
                if (Regex.IsMatch(val, @"^\d+$") && !Regex.IsMatch(val, @"^(00[2-7][0-9])+$")) return val;
                try { return DecodeUcs2Hex(val); } catch { }
            }
            return val;
        }
        return "Unknown";
    }

    public static string DecodeSmsSender(string? rawSender)
    {
        string value = rawSender?.Trim() ?? string.Empty;
        // Some EC20C firmware renders an alphanumeric sender as concatenated decimal ASCII:
        // 86 105 110 97 80 104 111 110 101 => "VinaPhone".
        // Limit this fallback to values longer than a valid phone number so ordinary numeric
        // senders are never transformed.
        if (value.Length > 15 && value.All(char.IsDigit) && TryDecodeDecimalAscii(value, out string decoded))
            return decoded;
        return value;
    }

    private static bool TryDecodeDecimalAscii(string value, out string decoded)
    {
        var memo = new Dictionary<int, string?>();
        string? Parse(int offset)
        {
            if (offset == value.Length) return string.Empty;
            if (memo.TryGetValue(offset, out string? cached)) return cached;
            // Printable ASCII codes are 2 or 3 decimal digits. Prefer 3 digits where valid.
            foreach (int width in new[] { 3, 2 })
            {
                if (offset + width > value.Length ||
                    !int.TryParse(value.AsSpan(offset, width), out int code) || code is < 32 or > 126)
                    continue;
                string? tail = Parse(offset + width);
                if (tail != null) return memo[offset] = ((char)code) + tail;
            }
            memo[offset] = null;
            return null;
        }

        decoded = Parse(0) ?? string.Empty;
        return decoded.Length >= 2 && decoded.Any(char.IsLetter);
    }
    // ==================================================================

    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;
    public event EventHandler<GsmDataEventArgs>? PortDisconnected;
    public event EventHandler<GsmDataEventArgs>? CallIncoming;
    public event EventHandler<GsmDataEventArgs>? CallEnded;
    public event EventHandler<GsmDataEventArgs>? DtmfReceived;

    public event EventHandler<gsm.Models.IncomingCallSession>? IncomingCallRinging;
    public event EventHandler<gsm.Models.IncomingCallSession>? IncomingCallEnded;

    public List<string> GetAvailablePorts()
    {
        var allSystemPorts = new HashSet<string>(SerialPort.GetPortNames());
        var usbPorts = new List<UsbPortCandidate>();
        var bluetoothPorts = new HashSet<string>();

        // 1. Quét tìm các cổng COM thuộc USB
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
            {
                if (key != null)
                {
                    foreach (var vidPid in key.GetSubKeyNames())
                    {
                        using (var vidPidKey = key.OpenSubKey(vidPid))
                        {
                            if (vidPidKey == null) continue;
                            foreach (var instance in vidPidKey.GetSubKeyNames())
                            {
                                using (var instanceKey = vidPidKey.OpenSubKey(instance))
                                {
                                    if (instanceKey == null) continue;
                                    using (var paramsKey = instanceKey.OpenSubKey("Device Parameters"))
                                    {
                                        if (paramsKey != null)
                                        {
                                            var portName = paramsKey.GetValue("PortName") as string;
                                            if (!string.IsNullOrEmpty(portName))
                                            {
                                                string location = instanceKey.GetValue("LocationInformation") as string
                                                    ?? string.Empty;
                                                int interfaceNumber = ParseUsbInterfaceNumber(vidPid);
                                                usbPorts.Add(new UsbPortCandidate(
                                                    portName,
                                                    location,
                                                    vidPid,
                                                    interfaceNumber));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Quét tìm các cổng COM thuộc Bluetooth để loại trừ
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM"))
            {
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using (var subKey = key.OpenSubKey(sub))
                        {
                            if (subKey == null) continue;
                            foreach (var instance in subKey.GetSubKeyNames())
                            {
                                using (var instanceKey = subKey.OpenSubKey(instance))
                                {
                                    if (instanceKey == null) continue;
                                    using (var paramsKey = instanceKey.OpenSubKey("Device Parameters"))
                                    {
                                        if (paramsKey != null)
                                        {
                                            var portName = paramsKey.GetValue("PortName") as string;
                                            if (!string.IsNullOrEmpty(portName))
                                            {
                                                bluetoothPorts.Add(portName);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Lọc các cổng COM thực sự đang hoạt động và là USB, đồng thời loại bỏ hoàn toàn Bluetooth
        var filteredCandidates = new List<UsbPortCandidate>();
        var seenPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in usbPorts)
        {
            if (allSystemPorts.Contains(candidate.PortName)
                && !bluetoothPorts.Contains(candidate.PortName)
                && seenPorts.Add(candidate.PortName))
            {
                filteredCandidates.Add(candidate);
            }
        }

        // Registry USB có thể cập nhật chậm hơn SerialPort.GetPortNames khi một bank
        // 32/64 cổng vừa được cắm. SAuto vẫn giữ các COM Windows đã liệt kê; không
        // được làm mất 1-4 cổng chỉ vì thiếu metadata topology trong Registry.
        foreach (string portName in allSystemPorts)
        {
            if (bluetoothPorts.Contains(portName) || !seenPorts.Add(portName)) continue;
            filteredCandidates.Add(new UsbPortCandidate(
                portName, string.Empty, string.Empty, int.MaxValue));
        }

        // Registry enumeration order is not physical USB order. Sort by the USB
        // topology first so separate GSM boxes/hubs stay together. For the
        // XR21V1414 bank, Sauto's left-to-right order when looking from the power
        // connector side is channel A, B, C, D (MI_00, MI_02, MI_04, MI_06).
        // This keeps STT aligned with the physical sockets instead of COM number.
        var filtered = filteredCandidates
            .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.LocationInformation) ? 1 : 0)
            .ThenBy(candidate => candidate.LocationInformation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetPhysicalInterfaceRank)
            .ThenBy(candidate => GetPortNumber(candidate.PortName))
            .Select(candidate => candidate.PortName)
            .ToList();

        return filtered;
    }

    private static int ParseUsbInterfaceNumber(string vidPid)
    {
        Match match = Regex.Match(vidPid, @"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(
            match.Groups[1].Value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out int value)
            ? value
            : int.MaxValue;
    }

    private static int GetPhysicalInterfaceRank(UsbPortCandidate candidate)
    {
        bool isXr21V1414 = candidate.VidPid.Contains("VID_04E2&PID_1414", StringComparison.OrdinalIgnoreCase);
        if (!isXr21V1414)
            return candidate.InterfaceNumber;

        return candidate.InterfaceNumber switch
        {
            0x00 => 0, // Channel A - leftmost socket from the power connector side
            0x02 => 1, // Channel B
            0x04 => 2, // Channel C
            0x06 => 3, // Channel D
            _ => candidate.InterfaceNumber + 10
        };
    }

    private static int GetPortNumber(string portName)
    {
        Match match = Regex.Match(portName, @"\d+");
        return match.Success && int.TryParse(match.Value, out int value) ? value : int.MaxValue;
    }

    public string ConnectAll(int baudRate = 115200)
    {
        var newlyOpenedPorts = new ConcurrentBag<string>();
        var failedPorts = new ConcurrentBag<string>();

        lock (_connectLock)
        {
            // ConnectAll is also used by the hot-plug watcher. Preserve each
            // COM's retry count/sleep window so a frequent discovery scan cannot
            // defeat the per-port backoff and hammer a failing driver.

            var ports = GetAvailablePorts();
            BackendConcurrency.ConfigureThreadPool(ports.Count);
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = "SYSTEM", Data = $"[HỆ THỐNG] Quét cổng COM: Phát hiện {ports.Count} cổng trong Windows ({string.Join(", ", ports)})" });

            Parallel.ForEach(ports, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, ports.Count)
            }, p =>
            {
                PortConnectResult result = TryConnectPort(p, baudRate);
                if (result == PortConnectResult.Opened)
                {
                    newlyOpenedPorts.Add(p);
                }
                else if (result == PortConnectResult.Failed)
                {
                    failedPorts.Add(p);
                }
            });
        }

        // CHỈ gửi lệnh khởi tạo SAU KHI đã mở kết nối xong toàn bộ các cổng COM.
        // Điều này đảm bảo quá trình đọc/ghi USB (AT commands) không xung đột với quá trình OS nhận diện cổng COM mới.
        if (newlyOpenedPorts.Count > 0)
        {
            _ = InitializeOpenedPortsAsync(newlyOpenedPorts);
        }

        string result = "";
        if (newlyOpenedPorts.Count > 0) result += $"Mới: {string.Join(", ", newlyOpenedPorts)}. ";
        if (failedPorts.Count > 0) result += $"Lỗi: {string.Join(", ", failedPorts)}.";
        return string.IsNullOrWhiteSpace(result) ? "Không có cổng mới cần kết nối" : result.Trim();
    }

    public Task<bool> ReconnectPortAsync(
        string portName,
        int baudRate = 115200,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        return _portReconnects.RunAsync(
            portName,
            () => ReconnectPortCoreAsync(portName, baudRate, ct));
    }

    private async Task<bool> ReconnectPortCoreAsync(
        string portName,
        int baudRate,
        CancellationToken ct)
    {
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = "[PORT_RECONNECT] Đóng/mở lại riêng COM; không quét lại các cổng khác."
        });

        Disconnect(portName);
        await Task.Delay(PortReconnectDelay, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        PortConnectResult result;
        lock (_connectLock)
        {
            // This path deliberately preserves the failure counters and sleep
            // windows belonging to every other COM.
            result = TryConnectPort(portName, baudRate);
        }

        if (result == PortConnectResult.Opened)
        {
            await InitializeOpenedPortsAsync([portName]).ConfigureAwait(false);
            return true;
        }

        if (result == PortConnectResult.AlreadyConnected)
            return true;

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = result == PortConnectResult.BackingOff
                ? "[PORT_RECONNECT_DEFERRED] COM đang trong thời gian backoff riêng."
                : "[PORT_RECONNECT_FAILED] Không thể mở lại COM."
        });
        return false;
    }

    private PortConnectResult TryConnectPort(string portName, int baudRate)
    {
        if (_serialPorts.ContainsKey(portName))
            return PortConnectResult.AlreadyConnected;

        if (_sleepingPorts.TryGetValue(portName, out DateTime sleepUntil))
        {
            if (DateTime.Now < sleepUntil)
                return PortConnectResult.BackingOff;

            _sleepingPorts.TryRemove(portName, out _);
        }

        SerialPort? serialPort = null;
        try
        {
            serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                DtrEnable = true,
                RtsEnable = true
            };

            SerialDataReceivedEventHandler handler =
                (s, e) => HandleDataReceived(portName, serialPort);
            serialPort.DataReceived += handler;
            serialPort.ErrorReceived +=
                (s, e) => HandleErrorReceived(portName, serialPort, e);
            serialPort.Open();

            if (!_serialPorts.TryAdd(portName, serialPort))
            {
                serialPort.Close();
                serialPort.Dispose();
                return PortConnectResult.AlreadyConnected;
            }

            _dataReceivedHandlers.TryAdd(portName, handler);
            _semaphores.TryAdd(portName, new SemaphoreSlim(1, 1));
            _portBuffers.TryAdd(portName, new StringBuilder());
            _portBufferLocks.TryAdd(portName, new object());
            _connectionErrors.TryRemove(portName, out _);
            if (_portLifetimeCts.TryRemove(portName, out var staleLifetime))
            {
                try { staleLifetime.Cancel(); staleLifetime.Dispose(); } catch { }
            }
            _portLifetimeCts[portName] = new CancellationTokenSource();

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"Đã kết nối thành công {portName} (Baud: {baudRate})"
            });
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[PORT_OPENED]"
            });

            StartGlobalSimMonitor(portName);
            StartPortHealthSupervisor(portName);
            return PortConnectResult.Opened;
        }
        catch (Exception ex)
        {
            try { serialPort?.Close(); } catch { }
            try { serialPort?.Dispose(); } catch { }

            int errors = _connectionErrors.AddOrUpdate(
                portName, 1, static (_, old) => old + 1);
            if (errors >= 3)
            {
                _sleepingPorts[portName] = DateTime.Now.AddSeconds(30);
                _connectionErrors.TryRemove(portName, out _);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"Lỗi kết nối {portName} quá 3 lần: {ex.Message}. Tạm ngưng kết nối cổng này trong 30 giây để tránh spam log."
                });
            }
            else
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"Lỗi kết nối {portName}: {ex.Message}"
                });
            }
            return PortConnectResult.Failed;
        }
    }

    private void StartPortHealthSupervisor(string portName)
    {
        if (_portHealthCts.TryRemove(portName, out var oldCts))
        {
            try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
        }

        var healthCts = new CancellationTokenSource();
        _portHealthCts[portName] = healthCts;
        CancellationToken token = healthCts.Token;

        _ = Task.Run(async () =>
        {
            int consecutiveFailures = 0;
            try
            {
                // Let the SAuto initialization sequence own the port first.
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(
                        GetPortHealthProbeInterval(consecutiveFailures),
                        token);
                    if (token.IsCancellationRequested) break;

                    bool coordinatedRecoveryOwnsPort =
                        (_lastSimState.TryGetValue(portName, out bool simPresent)
                         && !simPresent
                         && _pollingCts.ContainsKey(portName))
                        || _simInitInProgress.ContainsKey(portName)
                        || _simInsertInProgress.ContainsKey(portName)
                        || _rebootRecoveryInProgress.ContainsKey(portName);
                    if (ShouldDeferPortHealthProbe(
                            _suspendedBackgroundPorts.ContainsKey(portName),
                            IsCallInProgress(portName),
                            _commandTcs.ContainsKey(portName)
                                && consecutiveFailures == 0,
                            coordinatedRecoveryOwnsPort))
                    {
                        continue;
                    }

                    bool healthy = _serialPorts.TryGetValue(portName, out var serialPort)
                        && serialPort.IsOpen;
                    string probe = healthy
                        ? string.Empty
                        : "ERROR: Port not open";
                    if (healthy)
                    {
                        probe = await SendCommandAsync(
                            portName, "AT", 3000, silent: true, ct: token);
                        if (IsDeferredPortHealthProbeResponse(probe))
                        {
                            continue;
                        }

                        healthy = probe.Contains("OK", StringComparison.OrdinalIgnoreCase)
                            && !probe.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                            && !probe.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                    }

                    if (healthy)
                    {
                        consecutiveFailures = 0;
                        _portHealthFailureCounts.TryRemove(portName, out _);
                        continue;
                    }

                    consecutiveFailures = NextPortHealthFailureCount(
                        consecutiveFailures,
                        probe);
                    _portHealthFailureCounts[portName] = consecutiveFailures;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[PORT_HEALTH] Không nhận phản hồi AT ({consecutiveFailures}/2); đang theo dõi để tự mở lại COM."
                    });

                    if (consecutiveFailures < 2
                        || !_portHealthRecoveryOwners.TryAdd(portName, 0))
                    {
                        continue;
                    }

                    try
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = "[PORT_HEALTH_RECOVERY] COM không phản hồi 2 chu kỳ; đóng/mở lại riêng cổng và khởi tạo lại SIM."
                        });
                        // ReconnectPortAsync owns disconnect + delay + open. It
                        // coalesces with any UI/IMEI recovery already running for
                        // this COM and never scans or opens unrelated ports. This
                        // is a planned recovery, so do not raise PortDisconnected:
                        // the ViewModel must keep the row present or its 3-second
                        // watcher can mistake the COM for a new device and call
                        // the full-bank ConnectAll path.
                        bool reconnected = await ReconnectPortAsync(portName, 115200);
                        if (!reconnected)
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = "[PORT_HEALTH_RECOVERY_FAILED] Không thể mở lại COM."
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[PORT_HEALTH_RECOVERY_FAILED] {ex.Message}"
                        });
                    }
                    finally
                    {
                        _portHealthRecoveryOwners.TryRemove(portName, out _);
                    }
                    break;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_portHealthCts.TryGetValue(portName, out var current)
                    && ReferenceEquals(current, healthCts))
                {
                    _portHealthCts.TryRemove(portName, out _);
                }
                healthCts.Dispose();
            }
        }, token);
    }

    internal static bool ShouldDeferPortHealthProbe(
        bool backgroundSuspended,
        bool callInProgress,
        bool commandPending,
        bool coordinatedRecoveryOwnsPort) =>
        backgroundSuspended
        || callInProgress
        || commandPending
        || coordinatedRecoveryOwnsPort;

    internal static bool IsDeferredPortHealthProbeResponse(string? response) =>
        response?.Contains(
            "Timeout waiting for lock", StringComparison.OrdinalIgnoreCase) == true
        || response?.Contains(
            "Another command is already in progress", StringComparison.OrdinalIgnoreCase) == true;

    internal static TimeSpan GetPortHealthProbeInterval(
        int confirmedFailures) =>
        confirmedFailures > 0
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(15);

    internal static int NextPortHealthFailureCount(
        int currentCount,
        string? probeResponse)
    {
        if (IsDeferredPortHealthProbeResponse(probeResponse))
            return Math.Max(0, currentCount);

        bool healthy =
            probeResponse?.Contains("OK", StringComparison.OrdinalIgnoreCase) == true
            && probeResponse.Contains(
                "Timeout", StringComparison.OrdinalIgnoreCase) == false
            && probeResponse.Contains(
                "ERROR", StringComparison.OrdinalIgnoreCase) == false;
        return healthy ? 0 : Math.Max(0, currentCount) + 1;
    }

    internal static bool IsDeferredNetworkPollingResponse(string? response) =>
        IsDeferredPortHealthProbeResponse(response);

    internal static int NextNetworkLossMissCount(
        int currentCount,
        string? copsResponse)
    {
        if (TryParseCopsResponse(copsResponse, out _, out _))
            return 0;

        if (IsDeferredNetworkPollingResponse(copsResponse))
            return Math.Max(0, currentCount);

        return Math.Max(0, currentCount) + 1;
    }

    internal static bool ShouldReportNetworkLoss(
        string? copsResponse,
        int consecutiveMisses) =>
        consecutiveMisses >= NetworkLossConfirmationMisses
        && !IsDeferredNetworkPollingResponse(copsResponse)
        && !TryParseCopsResponse(copsResponse, out _, out _);

    internal static TimeSpan GetNetworkRegistrationProbeInterval(
        int configuredSignalScanSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(
            configuredSignalScanSeconds,
            5,
            15));

    private async Task InitializeOpenedPortsAsync(IReadOnlyCollection<string> portNames)
    {
        BackendConcurrency.ConfigureThreadPool(portNames.Count);
        var tasks = portNames.Select(async portName =>
        {
            try
            {
                if (_portLifetimeCts.TryGetValue(portName, out var lifetime))
                    await InitializeModemAsync(portName, lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[STATUS_NO_RESPONSE] Lỗi khởi tạo modem: {ex.Message}"
                });
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task HandleSimInsertedSafelyAsync(string portName)
    {
        try
        {
            await HandleSimInsertedAsync(portName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[STATUS_NO_RESPONSE] Lỗi xử lý SIM vừa cắm: {ex.Message}"
            });
            StartHotplugWaitLoop(portName);
        }
    }

    private void HandleErrorReceived(
        string portName,
        SerialPort sp,
        SerialErrorReceivedEventArgs args)
    {
        // UART overrun/frame/parity events are transient and do not prove USB removal.
        // SAuto keeps the handle alive; the next AT command determines real connectivity.
        // Actual unplugging is still handled by IOException/UnauthorizedAccessException.
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[SERIAL_TRANSIENT] {args.EventType}; giữ COM và xác minh bằng lệnh AT kế tiếp."
        });
    }

    private async Task<string> ReadCcidWithFallbackAsync(
        string portName,
        int timeoutMs = 5000,
        bool silent = true,
        CancellationToken ct = default)
    {
        string vendor = _portVendors.TryGetValue(portName, out var v) ? v : "";
        string ccid = "ERROR";

        if (vendor.Contains("QUECTEL"))
        {
            ccid = await SendCommandAsync(
                portName, "AT+QCCID", timeoutMs, silent, ct);
        }
        
        if (!HasReadableCcid(ccid))
        {
            ccid = await SendCommandAsync(
                portName, "AT+CCID", timeoutMs, silent, ct);
        }

        if (!HasReadableCcid(ccid))
        {
            string crsm = await SendCommandAsync(
                portName,
                "AT+CRSM=176,12258,0,0,10",
                timeoutMs,
                silent,
                ct);
            if (!crsm.Contains("ERROR") && crsm.Contains("+CRSM:"))
            {
                ccid = crsm; // Lấy luôn chuỗi raw để logic parse phía trên tự xử lý
            }
        }

        // Tie every SMS read/delete operation to the physical SIM identity.
        // A transient unreadable response must not clear a previously verified
        // CCID; explicit removal/disconnect paths own that transition.
        Match identity = Regex.Match(
            ccid ?? string.Empty, @"(?<!\d)89\d{16,20}(?!\d)");
        if (identity.Success)
            SetSmsSimIdentity(portName, identity.Value);

        return ccid ?? "ERROR";
    }

    internal static bool ResponseMatchesExpectedCcid(
        string? response,
        string? expectedCcid)
    {
        string expected = Regex.Replace(
            expectedCcid ?? string.Empty, @"\D", string.Empty);
        if (expected.Length != 20) return false;

        return Regex.Matches(
                response ?? string.Empty,
                @"(?<!\d)89\d{18}(?!\d)")
            .Select(match => match.Value)
            .Any(value => string.Equals(
                value, expected, StringComparison.Ordinal));
    }

    public async Task<bool> VerifyExpectedCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken ct = default)
    {
        string expected = Regex.Replace(
            expectedCcid ?? string.Empty, @"\D", string.Empty);
        if (expected.Length != 20) return false;

        string response;
        try
        {
            response = await ReadCcidWithFallbackAsync(
                portName, timeoutMs: 5000, silent: true, ct: ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }

        bool matches = ResponseMatchesExpectedCcid(response, expected);
        if (!matches)
        {
            string observed = Regex.Match(
                response ?? string.Empty,
                @"(?<!\d)89\d{18}(?!\d)").Value;
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[EXPECTED_SIM_BLOCKED] expected CCID={expected}; live CCID={(string.IsNullOrWhiteSpace(observed) ? "UNREADABLE" : observed)}."
            });
        }

        return matches;
    }

    internal static bool IsRadioDisabledResponse(string? response) =>
        Regex.IsMatch(response ?? string.Empty, @"\+CFUN:\s*(?:0|4)\b", RegexOptions.IgnoreCase);

    private async Task<bool> ConfirmCfunAsync(string portName, int expected, CancellationToken ct = default)
    {
        string state = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true, ct: ct);
        return Regex.IsMatch(state, $@"\+CFUN:\s*{expected}\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Khởi tạo lại SIM stack mà không bao giờ bật RF. Theo EC20, CFUN=0 tắt SIM+RF,
    /// còn CFUN=4 bật lại phần SIM trong airplane mode nhưng vẫn khóa phát/thu RF.
    /// </summary>
    private async Task<bool> RestartSimStackOfflineAsync(
        string portName,
        CancellationToken ct = default,
        Func<bool>? identityIsCurrent = null)
    {
        if (identityIsCurrent?.Invoke() == false) return false;
        string minimum = await SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true, ct: ct);
        if (identityIsCurrent?.Invoke() == false
            || IsCommandFailure(minimum)
            || !await ConfirmCfunAsync(portName, 0, ct)
            || identityIsCurrent?.Invoke() == false)
            return false;

        await Task.Delay(500, ct);
        if (identityIsCurrent?.Invoke() == false) return false;
        string airplane = await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct: ct);
        if (identityIsCurrent?.Invoke() == false
            || IsCommandFailure(airplane)
            || !await ConfirmCfunAsync(portName, 4, ct)
            || identityIsCurrent?.Invoke() == false)
            return false;

        await Task.Delay(800, ct);
        return true;
    }

    /// <summary>
    /// Repairs the SIM/network stack when COPS returns CME 13.  CSQ can still
    /// be readable in this state, but CPIN/USSD are unusable until the SIM
    /// task is restarted.  The sequence deliberately uses CFUN=0 -> 4 -> 1
    /// instead of a full modem reset, so an active serial port and SMS URCs are
    /// preserved.  It is bounded per COM and never marks the SIM as removed.
    /// </summary>
    private async Task<bool> RecoverNetworkSimFailureAsync(
        string portName,
        string sourceResponse,
        CancellationToken ct)
    {
        if (!_networkSimIdentities.TryGetValue(
                portName, out string? expectedCcid)
            || string.IsNullOrWhiteSpace(expectedCcid)
            || !_pollingExpectedIdentities.TryGetValue(
                portName, out NetworkPollingIdentity? pollingIdentity)
            || !string.Equals(
                pollingIdentity.Ccid,
                expectedCcid,
                StringComparison.Ordinal))
            return false;
        string expectedImei = pollingIdentity.Imei;

        long generation = _networkIdentityGenerations.GetOrAdd(portName, 1);
        string recoveryKey = $"{portName}\u001f{generation}\u001f{expectedCcid}";
        bool IdentityIsCurrent() =>
            _networkIdentityGenerations.TryGetValue(portName, out long currentGeneration)
            && currentGeneration == generation
            && _networkSimIdentities.TryGetValue(portName, out string? currentCcid)
            && _pollingExpectedIdentities.TryGetValue(
                portName, out NetworkPollingIdentity? currentPollingIdentity)
            && string.Equals(
                currentCcid, expectedCcid, StringComparison.Ordinal)
            && NetworkPollingIdentitiesMatch(
                currentPollingIdentity.Ccid,
                currentPollingIdentity.Imei,
                expectedCcid,
                expectedImei);

        async Task<bool> HoldOfflineIfIdentityChangedAsync(string stage)
        {
            if (IdentityIsCurrent()) return false;
            try
            {
                await SendCommandAsync(
                    portName,
                    "AT+CFUN=4",
                    5000,
                    silent: true,
                    ct: CancellationToken.None);
            }
            catch { }
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[NETWORK_SIM_IDENTITY_CHANGED] stage={stage}; hủy recovery cũ và giữ RF tắt để xác minh CCID mới."
            });
            return true;
        }

        async Task<bool> VerifyExpectedCcidAsync()
        {
            if (!IdentityIsCurrent()) return false;
            string response = await ReadCcidWithFallbackAsync(
                portName, 4000, silent: true);
            Match match = Regex.Match(
                response ?? string.Empty,
                @"(?<!\d)89\d{16,20}(?!\d)");
            return match.Success
                && string.Equals(
                    match.Value, expectedCcid, StringComparison.Ordinal)
                && IdentityIsCurrent();
        }

        async Task<bool> VerifyExpectedImeiAsync()
        {
            if (!IdentityIsCurrent()) return false;
            string response = await SendCommandAsync(
                portName,
                "AT+EGMR=0,7;",
                10000,
                silent: true,
                ct: ct);
            Match match = Regex.Match(
                response ?? string.Empty,
                @"(?<!\d)\d{15}(?!\d)");
            return match.Success
                && NetworkRecoveryImeiMatches(
                    match.Value,
                    expectedImei)
                && IdentityIsCurrent();
        }

        if (!_networkSimRecoveryOwners.TryAdd(recoveryKey, 0))
            return false;

        int attempt = _networkSimRecoveryAttempts.AddOrUpdate(
            recoveryKey, 1, static (_, current) => current + 1);
        if (attempt > MaxNetworkSimRecoveryAttemptsWithManualOperator)
        {
            _networkSimRecoveryOwners.TryRemove(recoveryKey, out _);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[NETWORK_REOPEN_REQUIRED] COPS tiếp tục trả CME 13 sau {MaxNetworkSimRecoveryAttemptsWithManualOperator} bước phục hồi; mở lại riêng COM."
            });
            return false;
        }

        _rebootRecoveryInProgress[portName] = 0;
        try
        {
            string cpinBefore = await SendCommandAsync(
                portName, "AT+CPIN?", 3000, silent: true, ct: ct);
            if (await HoldOfflineIfIdentityChangedAsync("cpin-before"))
                return false;
            bool locked = cpinBefore.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                || cpinBefore.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[NETWORK_SIM_FAILURE] COPS/USSD trả CME 13; CPIN={cpinBefore.Trim()}; "
                    + $"khởi động lại SIM stack (lần {attempt}/{MaxNetworkSimRecoveryAttemptsWithManualOperator})."
            });
            if (locked)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[NETWORK_SIM_RECOVERY_SKIPPED] SIM đang khóa: {cpinBefore.Trim()}"
                });
                return false;
            }

            if (attempt > MaxNetworkSimRecoveryAttemptsWithHardReset)
            {
                // Some EC20 firmware keeps automatic selection in CME 13 even
                // after CFUN recovery.  Try the two Vietnamese operator codes
                // explicitly; only a subsequent +COPS response counts as
                // success, so the UI never becomes Active on an OK-only ACK.
                string[] operatorCodes = GetOperatorCodesForCcid(expectedCcid);
                foreach (string operatorCode in operatorCodes)
                {
                    if (await HoldOfflineIfIdentityChangedAsync("before-force-operator"))
                        return false;
                    string forced = await SendCommandAsync(
                        portName, $"AT+COPS=1,2,\"{operatorCode}\"", 20000, silent: true, ct: ct);
                    if (await HoldOfflineIfIdentityChangedAsync("after-force-operator"))
                        return false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_OPERATOR_FORCE] mã {operatorCode}: {forced.Trim()}"
                    });
                    if (IsCommandFailure(forced)) continue;

                    for (int probe = 0; probe < 8; probe++)
                    {
                        await Task.Delay(1000, ct);
                        if (await HoldOfflineIfIdentityChangedAsync("force-operator-probe"))
                            return false;
                        string copsForced = await SendCommandAsync(
                            portName, "AT+COPS?", 5000, silent: true, ct: ct);
                        if (TryParseCopsResponse(copsForced, out _, out _)
                            && await VerifyExpectedCcidAsync())
                        {
                            _lastSimState[portName] = true;
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = $"[NETWORK_OPERATOR_FORCE_OK] COPS đã đăng ký sau khi ép mã {operatorCode}."
                            });
                            return true;
                        }
                    }
                }

                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[NETWORK_OPERATOR_FORCE_FAILED] Không đăng ký được 45202/45204; giữ watchdog và không đánh dấu SIM đã rút."
                });
                return false;
            }

            bool hardReset = attempt > MaxNetworkSimRecoveryAttempts;
            if (hardReset)
            {
                if (await HoldOfflineIfIdentityChangedAsync("before-hard-reset"))
                    return false;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[NETWORK_SIM_HARD_RESET] Đã thử đủ SIM-stack recovery; gửi CFUN=1,1 và chờ modem đăng ký lại."
                });
                string reset = await SendCommandAsync(
                    portName, "AT+CFUN=1,1", 15000, silent: true, ct: ct);
                if (await HoldOfflineIfIdentityChangedAsync("after-hard-reset"))
                    return false;
                bool resetAccepted = !reset.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase)
                    && (reset.Contains("OK", StringComparison.OrdinalIgnoreCase)
                        || reset.Contains("Timeout", StringComparison.OrdinalIgnoreCase));
                if (!resetAccepted)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_SIM_RECOVERY_FAILED] CFUN=1,1 bị từ chối: {reset.Trim()}"
                    });
                    return false;
                }

                // Full reset may temporarily drop the UART response.  Probe
                // until CPIN/CCID is back instead of declaring the COM dead.
                await Task.Delay(5000, ct);
                bool hardResetReady = false;
                for (int probe = 0; probe < 20; probe++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await HoldOfflineIfIdentityChangedAsync("hard-reset-probe"))
                        return false;
                    string cpinAfterReset = await SendCommandAsync(
                        portName, "AT+CPIN?", 3000, silent: true, ct: ct);
                    bool cpinReadyAfterReset = Regex.IsMatch(
                        cpinAfterReset, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                    if (cpinReadyAfterReset || probe % 2 == 1)
                        hardResetReady = await VerifyExpectedCcidAsync();
                    if (hardResetReady) break;
                    await Task.Delay(1000, ct);
                }

                if (!hardResetReady)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = "[NETWORK_SIM_RECOVERY_FAILED] Modem chưa phản hồi SIM sau CFUN=1,1; giữ watchdog để tự mở lại COM nếu cần."
                    });
                    return false;
                }

                bool hardResetImeiVerified = await VerifyExpectedImeiAsync();
                if (ShouldAbortNetworkPollingAfterHardReset(
                        hardResetImeiVerified))
                {
                    if (!IdentityIsCurrent())
                    {
                        await HoldOfflineIfIdentityChangedAsync(
                            "hard-reset-imei-check");
                        return false;
                    }

                    try
                    {
                        await SendCommandAsync(
                            portName,
                            "AT+CFUN=4",
                            5000,
                            silent: true,
                            ct: CancellationToken.None);
                    }
                    catch { }
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_SIM_HARD_RESET_IMEI_MISMATCH] expected={expectedImei}; fatal=true; giữ RF tắt và yêu cầu pipeline IMEI xác minh lại."
                    });
                    CancelNetworkPollingForIdentityReverification(
                        portName,
                        pollingIdentity.Ccid,
                        pollingIdentity.Imei,
                        "Hard-reset trả về slot 7 không khớp; dừng vòng COPS/CFUN chung cho tới khi pipeline xác minh lại.");
                    return false;
                }

                _lastSimState[portName] = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[NETWORK_SIM_HARD_RESET_OK] SIM READY sau reboot; quay lại dò COPS và *111."
                });
                return true;
            }

            if (!await RestartSimStackOfflineAsync(
                    portName, ct, IdentityIsCurrent))
            {
                if (await HoldOfflineIfIdentityChangedAsync("offline-restart"))
                    return false;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[NETWORK_SIM_RECOVERY_FAILED] Không chuyển được CFUN=0 -> CFUN=4; {sourceResponse.Trim()}"
                });
                return false;
            }

            // Re-enable the radio only after the SIM side is stable.  Do not
            // issue COPS=0 here: it may return CME 13 again while CPIN is still
            // settling and would just extend the blocking command unnecessarily.
            if (await HoldOfflineIfIdentityChangedAsync("before-radio-on"))
                return false;
            string radioOn = await SendCommandAsync(
                portName, "AT+CFUN=1", 15000, silent: true, ct: ct);
            if (await HoldOfflineIfIdentityChangedAsync("after-radio-on"))
                return false;
            if (IsCommandFailure(radioOn))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[NETWORK_SIM_RECOVERY_FAILED] CFUN=1 lỗi: {radioOn.Trim()}"
                });
                return false;
            }

            bool simReady = false;
            for (int probe = 0; probe < 10; probe++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);
                if (await HoldOfflineIfIdentityChangedAsync("radio-on-probe"))
                    return false;
                string cpinAfter = await SendCommandAsync(
                    portName, "AT+CPIN?", 3000, silent: true, ct: ct);
                bool cpinReadyAfter = Regex.IsMatch(
                    cpinAfter, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                if (cpinReadyAfter || probe % 2 == 1)
                    simReady = await VerifyExpectedCcidAsync();
                if (simReady) break;
            }

            if (!simReady)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[NETWORK_SIM_RECOVERY_FAILED] SIM chưa READY sau khi bật lại RF; giữ COM hoạt động để thử lại có giới hạn."
                });
                return false;
            }

            _lastSimState[portName] = true;
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[NETWORK_SIM_RECOVERY_OK] SIM READY; trả lại vòng dò COPS và *111 ngay."
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[NETWORK_SIM_RECOVERY_FAILED] {ex.Message}"
            });
            return false;
        }
        finally
        {
            _rebootRecoveryInProgress.TryRemove(portName, out _);
            _networkSimRecoveryOwners.TryRemove(recoveryKey, out _);
        }
    }

    internal static string[] GetOperatorCodesForCcid(string? ccid)
    {
        string digits = Regex.Replace(ccid ?? string.Empty, @"\D", string.Empty);
        if (digits.StartsWith("898402", StringComparison.Ordinal)) return ["45202"];
        if (digits.StartsWith("898404", StringComparison.Ordinal)) return ["45204"];
        if (digits.StartsWith("898401", StringComparison.Ordinal)) return ["45201"];
        if (digits.StartsWith("898405", StringComparison.Ordinal)) return ["45205"];
        if (digits.StartsWith("898407", StringComparison.Ordinal)) return ["45207"];
        if (digits.StartsWith("898408", StringComparison.Ordinal)) return ["45208"];
        if (digits.StartsWith("898409", StringComparison.Ordinal)) return ["45209"];
        return [];
    }

    internal static bool NetworkRecoveryImeiMatches(
        string? observedImei,
        string? expectedImei) =>
        ImeiManagementService.IsUsableObservedImei(observedImei)
        && ImeiManagementService.IsValidImei(
            ImeiManagementService.ToCanonicalImei(expectedImei))
        && ImeiManagementService.AreEquivalentImei(
            observedImei,
            expectedImei);

    internal static bool ShouldAbortNetworkPollingAfterHardReset(
        bool exactImeiVerified) => !exactImeiVerified;

    private async Task InitializeModemAsync(string portName, CancellationToken ct)
    {
        // Guard: Đánh dấu cổng đang trong quá trình khởi tạo, ngăn GlobalSimMonitor gọi HandleSimInsertedAsync song song
        _simInitInProgress[portName] = true;
        try
        {
            await InitializeModemCoreAsync(portName, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested)
                _simInitInProgress.TryRemove(portName, out _);
        }
    }


    private static bool IsCommandFailure(string response) =>
        string.IsNullOrWhiteSpace(response)
        || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || response.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

    internal static bool HasReadableCcid(string response) =>
        !IsCommandFailure(response)
        && Regex.IsMatch(response, @"(?<!\d)89\d{16,20}(?!\d)");

    private void ClearSimRemovalEvidence(string portName)
    {
        _simRemovalEvidenceCounts.TryRemove(portName, out _);
        _simRemovalEvidenceSince.TryRemove(portName, out _);
    }

    private void CancelSimRemovalConfirmation(string portName)
    {
        if (_simRemovalConfirmationCts.TryRemove(portName, out var cts))
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    public void SetSimRemovalWatchEnabled(string portName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(portName)) return;

        if (enabled)
        {
            _simRemovalWatchEnabled[portName] = 0;
            ClearSimRemovalEvidence(portName);
            return;
        }

        _simRemovalWatchEnabled.TryRemove(portName, out _);
        CancelSimRemovalConfirmation(portName);
        ClearSimRemovalEvidence(portName);
    }

    private bool IsSimRemovalWatchEnabled(string portName) =>
        _simRemovalWatchEnabled.ContainsKey(portName);

    private void ScheduleSimRemovalConfirmation(string portName)
    {
        if (!IsSimRemovalWatchEnabled(portName)
            || !_lastSimState.TryGetValue(portName, out bool wasPresent)
            || !wasPresent)
            return;

        CancelSimRemovalConfirmation(portName);
        var cts = new CancellationTokenSource();
        _simRemovalConfirmationCts[portName] = cts;
        _ = ConfirmSimRemovalAfterDelayAsync(portName, cts);
    }

    private async Task ConfirmSimRemovalAfterDelayAsync(
        string portName,
        CancellationTokenSource confirmationCts)
    {
        CancellationToken token = confirmationCts.Token;
        try
        {
            // Cho đúng yêu cầu hot-plug: sau 5 giây kể từ URC mất SIM thì xác minh
            // một lần và cập nhật UI ngay, không chờ vòng quét sóng kế tiếp.
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            if (token.IsCancellationRequested
                || !_serialPorts.ContainsKey(portName)
                || !_lastSimState.TryGetValue(portName, out bool wasPresent)
                || !wasPresent
                || _suspendedBackgroundPorts.ContainsKey(portName)
                || _rebootRecoveryInProgress.ContainsKey(portName)
                || _simStackDisabledByTool.TryGetValue(portName, out bool stackDisabled) && stackDisabled)
                return;

            string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true, ct: token)
                ?? string.Empty;
            string cpinText = cpin ?? string.Empty;

            string qsimstat = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                ? (await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true, ct: token)
                    ?? string.Empty)
                : string.Empty;
            string liveCcid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true)
                ?? string.Empty;
            string cfun = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true, ct: token)
                ?? string.Empty;
            string qsimText = qsimstat ?? string.Empty;
            string ccidText = liveCcid ?? string.Empty;
            string cfunText = cfun ?? string.Empty;

            bool cpinPresent = Regex.IsMatch(
                cpinText, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase)
                || cpinText.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                || cpinText.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
            bool sensorPresent = Regex.IsMatch(
                qsimText, @"\+QSIMSTAT:\s*1\s*,\s*1", RegexOptions.IgnoreCase);
            bool sensorAbsent = Regex.IsMatch(
                qsimText, @"\+QSIMSTAT:\s*1\s*,\s*0", RegexOptions.IgnoreCase);
            bool ccidPresent = HasReadableCcid(ccidText);
            bool explicitNotInserted = cpinText.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase);
            bool radioActive = Regex.IsMatch(cfunText, @"\+CFUN:\s*1\b", RegexOptions.IgnoreCase);
            bool sensorProbeAvailable = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true;
            bool confirmedAbsent = !cpinPresent && !sensorPresent && !ccidPresent
                && (sensorProbeAvailable
                    ? sensorAbsent && (explicitNotInserted || IsConfirmedSimAbsentDuringPolling(
                        cpinText, qsimText, ccidText, cfunText, stackDisabledByTool: false))
                    : explicitNotInserted && radioActive);

            if (!confirmedAbsent) return;

            // A single CPIN/QSIMSTAT removal result is not enough. During a
            // CFUN/IMS transition several EC20 firmware versions briefly report
            // NOT INSERTED/1,0 on every port. Re-read the independent signals
            // after a short settle window; only two consecutive absent probes may
            // turn off RF and enter the hot-plug loop.
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (token.IsCancellationRequested
                || !_serialPorts.ContainsKey(portName)
                || !_lastSimState.TryGetValue(portName, out wasPresent)
                || !wasPresent
                || _suspendedBackgroundPorts.ContainsKey(portName)
                || _rebootRecoveryInProgress.ContainsKey(portName)
                || _simStackDisabledByTool.TryGetValue(portName, out stackDisabled) && stackDisabled)
                return;

            string cpinSecond = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true, ct: token)
                ?? string.Empty;
            string qsimSecond = sensorProbeAvailable
                ? (await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true, ct: token)
                    ?? string.Empty)
                : string.Empty;
            string ccidSecond = await ReadCcidWithFallbackAsync(portName, 4000, silent: true)
                ?? string.Empty;
            string cfunSecond = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true, ct: token)
                ?? string.Empty;
            bool secondCpinPresent = Regex.IsMatch(
                cpinSecond, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase)
                || cpinSecond.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                || cpinSecond.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
            bool secondSensorPresent = Regex.IsMatch(
                qsimSecond, @"\+QSIMSTAT:\s*1\s*,\s*1", RegexOptions.IgnoreCase);
            bool secondSensorAbsent = Regex.IsMatch(
                qsimSecond, @"\+QSIMSTAT:\s*1\s*,\s*0", RegexOptions.IgnoreCase);
            bool secondCcidPresent = HasReadableCcid(ccidSecond);
            bool secondExplicitNotInserted = cpinSecond.Contains(
                "NOT INSERTED", StringComparison.OrdinalIgnoreCase);
            bool secondRadioActive = Regex.IsMatch(
                cfunSecond, @"\+CFUN:\s*1\b", RegexOptions.IgnoreCase);
            bool confirmedSecondAbsent = !secondCpinPresent
                && !secondSensorPresent
                && !secondCcidPresent
                && (sensorProbeAvailable
                    ? secondSensorAbsent && (secondExplicitNotInserted
                        || IsConfirmedSimAbsentDuringPolling(
                            cpinSecond, qsimSecond, ccidSecond, cfunSecond, stackDisabledByTool: false))
                    : secondExplicitNotInserted && secondRadioActive);
            if (!confirmedSecondAbsent) return;

            // Tách task xác nhận khỏi dictionary trước khi phát log. MainViewModel
            // sẽ vô hiệu hóa cờ khi nhận WAITING_FOR_SIM; không được hủy chính task
            // đang gửi CFUN=4 và khởi động vòng chờ SIM.
            if (_simRemovalConfirmationCts.TryGetValue(portName, out var currentConfirmation)
                && ReferenceEquals(currentConfirmation, confirmationCts))
                _simRemovalConfirmationCts.TryRemove(portName, out _);
            _lastSimState[portName] = false;
            SetSmsSimIdentity(portName, null);
            ClearSimRemovalEvidence(portName);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (xác nhận sau 5 giây)."
            });
            await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true, ct: token);
            StartHotplugWaitLoop(portName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SIM_REMOVAL_CONFIRM_ERROR] Không xác nhận được SIM sau 5 giây: {ex.Message}"
            });
        }
        finally
        {
            if (_simRemovalConfirmationCts.TryGetValue(portName, out var current)
                && ReferenceEquals(current, confirmationCts))
                _simRemovalConfirmationCts.TryRemove(portName, out _);
            try { confirmationCts.Dispose(); } catch { }
        }
    }

    private bool RegisterSimRemovalEvidence(string portName)
    {
        DateTimeOffset since = _simRemovalEvidenceSince.GetOrAdd(
            portName, _ => DateTimeOffset.UtcNow);
        int evidence = _simRemovalEvidenceCounts.AddOrUpdate(
            portName, 1, (_, old) => old + 1);
        return evidence >= SimRemovalConfirmationCycles
            && DateTimeOffset.UtcNow - since >= SimRemovalConfirmationWindow;
    }

    internal static bool ShouldVerifySimRemoval(
        string cpin,
        bool stackDisabledByTool,
        bool removalUrcPending)
    {
        if (stackDisabledByTool) return false;
        bool explicitNotInserted = cpin.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase);
        bool transientNotReady = cpin.Contains("NOT READY", StringComparison.OrdinalIgnoreCase)
            || cpin.Contains("ERROR: 10", StringComparison.OrdinalIgnoreCase)
            || cpin.Contains("ERROR: 13", StringComparison.OrdinalIgnoreCase)
            || cpin.Contains("ERROR: 14", StringComparison.OrdinalIgnoreCase);
        return explicitNotInserted || (transientNotReady && removalUrcPending);
    }

    internal static bool IsConfirmedSimAbsentDuringPolling(
        string cpin,
        string qsimstat,
        string ccid,
        string cfun,
        bool stackDisabledByTool)
    {
        if (stackDisabledByTool || IsRadioDisabledResponse(cfun)) return false;

        bool cpinPresent = Regex.IsMatch(
            cpin ?? string.Empty, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase)
            || (cpin?.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cpin?.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase) ?? false);
        bool sensorPresent = Regex.IsMatch(
            qsimstat ?? string.Empty,
            @"\+QSIMSTAT:\s*1\s*,\s*1",
            RegexOptions.IgnoreCase);
        if (cpinPresent || sensorPresent || HasReadableCcid(ccid)) return false;

        bool explicitlyNotInserted = cpin?.Contains(
            "NOT INSERTED", StringComparison.OrdinalIgnoreCase) ?? false;
        if (explicitlyNotInserted) return true;

        // NOT READY/CME ERROR alone can be transient while the CS/IMS domain changes.
        // During the active RF polling cycle it becomes reliable removal evidence only
        // when CFUN is still 1 and the physical SIM sensor independently reports absent.
        bool radioActive = Regex.IsMatch(
            cfun ?? string.Empty, @"\+CFUN:\s*1\b", RegexOptions.IgnoreCase);
        bool sensorAbsent = Regex.IsMatch(
            qsimstat ?? string.Empty,
            @"\+QSIMSTAT:\s*1\s*,\s*0",
            RegexOptions.IgnoreCase);
        bool cpinUnavailable = (cpin?.Contains("NOT READY", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cpin?.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ?? false)
            || string.IsNullOrWhiteSpace(cpin);
        return radioActive && sensorAbsent && cpinUnavailable;
    }

    private async Task SendEscapeWithoutResponseAsync(string portName, CancellationToken ct)
    {
        if (!EnsurePortOpen(portName, out var sp) || sp == null)
            throw new IOException($"Không mở được {portName} để gửi ESC.");
        if (!_semaphores.TryGetValue(portName, out var semaphore))
            throw new IOException($"Không có khóa serial cho {portName}.");

        await semaphore.WaitAsync(ct);
        try
        {
            sp.Write("\u001b");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ReopenSerialHandleBetweenSautoPassesAsync(
        string portName,
        CancellationToken ct)
    {
        if (!_serialPorts.TryGetValue(portName, out SerialPort? sp)
            || !_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
            return;

        await semaphore.WaitAsync(ct);
        try
        {
            if (sp.IsOpen) sp.Close();
            if (_portBuffers.TryGetValue(portName, out StringBuilder? buffer))
            {
                object bufferGate = _portBufferLocks.GetOrAdd(portName, static _ => new object());
                lock (bufferGate) buffer.Clear();
            }
            await Task.Delay(100, ct);
            sp.Open();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Không loại COM khỏi bảng. Lượt kế tiếp/EnsurePortOpen sẽ thử lại,
            // giống SAuto giữ cổng lỗi riêng thay vì làm mất cả hàng.
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SAUTO_REOPEN_RETRY] Chưa mở lại được handle: {ex.Message}"
            });
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<SautoInitializationResult> RunSautoInitializationSequenceAsync(
        string portName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await SendEscapeWithoutResponseAsync(portName, ct);
        await Task.Delay(100, ct);

        string ati = await SendCommandAsync(portName, "ATI", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CPMS=\"ME\",\"SM\",\"MT\"", 5000, silent: true, ct: ct);
        await Task.Delay(100, ct);

        string cfun4 = await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct: ct);
        await Task.Delay(100, ct);
        await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true, ct: ct);
        await Task.Delay(200, ct);
        string cfunState = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true, ct: ct);

        bool radioLocked = !IsCommandFailure(cfun4)
            && Regex.IsMatch(cfunState, @"\+CFUN:\s*4\b", RegexOptions.IgnoreCase);
        if (!radioLocked)
        {
            await Task.Delay(200, ct);
            cfun4 = await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct: ct);
            cfunState = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true, ct: ct);
            radioLocked = !IsCommandFailure(cfun4)
                && Regex.IsMatch(cfunState, @"\+CFUN:\s*4\b", RegexOptions.IgnoreCase);
        }

        if (!radioLocked)
        {
            return new SautoInitializationResult(
                QuectelModemProfile.FromIdentity(string.Empty, string.Empty, string.Empty),
                "ERROR",
                "ERROR",
                false);
        }

        await Task.Delay(200, ct);
        string imei = await SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true, ct: ct);
        await Task.Delay(100, ct);
        await SendCommandAsync(portName, "AT+CNMI?", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, silent: true, ct: ct);
        await Task.Delay(150, ct);
        await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true, ct: ct);
        await Task.Delay(150, ct);
        await SendCommandAsync(portName, "AT+CMGF=1", 5000, silent: true, ct: ct);
        await Task.Delay(150, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, ct: ct);
        await Task.Delay(200, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"ME\",\"ME\",\"ME\"", 5000, silent: true, ct: ct);
        await Task.Delay(200, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true, ct: ct);
        await Task.Delay(150, ct);

        // SAuto only sets AUTO RAT here; it does not inject IMS into the no-SIM loop.
        await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 3000, silent: true, ct: ct);

        await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true, ct: ct);
        await Task.Delay(500, ct);
        string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true, ct: ct);

        string model = Regex.Match(ati, @"\b(?:EC|EG|BG|RG|RM|EM|EP|UC)[A-Z0-9-]{2,}\b", RegexOptions.IgnoreCase).Value;
        var profile = QuectelModemProfile.FromIdentity(
            ati.Contains("Quectel", StringComparison.OrdinalIgnoreCase) ? "Quectel" : string.Empty,
            model,
            ati);
        _portVendors[portName] = profile.Manufacturer.ToUpperInvariant();
        _modemProfiles[portName] = profile;
        return new SautoInitializationResult(profile, imei, cpin, true);
    }

    private async Task InitializeModemCoreAsync(string portName, CancellationToken ct)
    {
        SautoInitializationResult result = await RunSautoInitializationSequenceAsync(portName, ct);
        if (!result.RadioLocked)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[STATUS_NO_RESPONSE] Không xác nhận được CFUN=4 theo chuỗi SAuto."
            });
            return;
        }

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[MODEM_PROFILE] manufacturer={result.Profile.Manufacturer}; model={result.Profile.Model}; firmware={result.Profile.FirmwareRevision}; capabilities={result.Profile.CapabilityText}; quirks={result.Profile.QuirkText}"
        });

        string cleanImei = Regex.Match(result.ImeiResponse ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
        if (!string.IsNullOrWhiteSpace(cleanImei))
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {cleanImei}" });

        string cpinResponse = result.CpinResponse;
        bool simLocked = cpinResponse.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                      || cpinResponse.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
        bool simReady = Regex.IsMatch(
            cpinResponse, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
        if (simLocked)
        {
            _lastSimState[portName] = false;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpinResponse.Trim()}" });
            return;
        }

        string ccid = simReady
            ? await ReadCcidWithFallbackAsync(
                portName, 5000, silent: true, ct: ct)
            : "ERROR";
        if (ShouldAttemptStartupOfflineSimRecovery(cpinResponse, ccid))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[SIM_STARTUP_OFFLINE_RECOVERY] CPIN/CCID chưa sẵn sàng; khởi tạo lại SIM bằng CFUN=0 -> CFUN=4, RF vẫn khóa."
            });

            if (await RestartSimStackOfflineAsync(portName, ct))
            {
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    cpinResponse = await SendCommandAsync(
                        portName, "AT+CPIN?", 5000, silent: true, ct: ct);
                    simLocked = cpinResponse.Contains(
                            "SIM PIN", StringComparison.OrdinalIgnoreCase)
                        || cpinResponse.Contains(
                            "SIM PUK", StringComparison.OrdinalIgnoreCase);
                    if (simLocked) break;

                    ccid = await ReadCcidWithFallbackAsync(
                        portName, 5000, silent: true, ct: ct);
                    if (HasReadableCcid(ccid))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[SIM_STARTUP_OFFLINE_RECOVERED] SIM READY sau {attempt} lượt; RF giữ ở CFUN=4."
                        });
                        break;
                    }

                    if (attempt < 4)
                        await Task.Delay(750, ct);
                }
            }
        }

        if (simLocked)
        {
            _lastSimState[portName] = false;
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[STATUS_SIM_LOCKED] {cpinResponse.Trim()}"
            });
            return;
        }

        if (HasReadableCcid(ccid))
        {
            _lastSimState[portName] = true;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
            return;
        }

        _lastSimState[portName] = false;
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[NO_SIM_READY] imei={cleanImei}"
        });
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Không đọc được SIM" });
        StartHotplugWaitLoop(portName);
    }

    internal static bool ShouldAttemptStartupOfflineSimRecovery(
        string? cpinResponse,
        string? ccidResponse)
    {
        string cpin = cpinResponse ?? string.Empty;
        bool simLocked = cpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
            || cpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
        return !simLocked && !HasReadableCcid(ccidResponse ?? string.Empty);
    }

    public async Task ReloadSimAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return;
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[INFO] Đang khởi tạo lại SIM stack ở chế độ khóa RF..." });

        if (!await RestartSimStackOfflineAsync(portName))
            throw new InvalidOperationException("Không thể khởi tạo lại SIM stack an toàn bằng CFUN=0 -> CFUN=4");
    }

    public async Task<bool> ReloadAndResumeSimAsync(string portName, CancellationToken ct = default)
    {
        if (!_serialPorts.ContainsKey(portName)) return false;
        if (!_rebootRecoveryInProgress.TryAdd(portName, 0)) return false;

        try
        {
            await ReloadSimAsync(portName);

            // Không reboot CFUN=1,1: EC20 sẽ trở lại full functionality và có thể attach
            // trước khi danh tính được xác minh. Chỉ cấu hình lại trong CFUN=4.
            if (!await ReinitializeSettingsAsync(portName, ct)) return false;

            bool simReady = false;
            for (int attempt = 0; attempt < 45; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (!_serialPorts.ContainsKey(portName)) return false;

                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true, ct: ct);
                if (cpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                    || cpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}"
                    });
                    return false;
                }

                simReady = cpin.Contains("READY", StringComparison.OrdinalIgnoreCase)
                    && !cpin.Contains("NOT READY", StringComparison.OrdinalIgnoreCase);
                if (!simReady)
                {
                    string qsim = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                        ? await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true, ct: ct)
                        : string.Empty;
                    simReady = Regex.IsMatch(qsim, @"\+QSIMSTAT:\s*1\s*,\s*1");

                    // SIM_DET có thể không được nối hoặc dùng polarity khác trên bo 32/64
                    // cổng. CCID đọc được là bằng chứng mạnh hơn CPIN NOT READY tạm thời.
                    if (!simReady && attempt % 3 == 2)
                    {
                        string ccid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true);
                        simReady = HasReadableCcid(ccid);
                    }
                }
                if (simReady) break;

                // NOT READY/CME 10 có thể xuất hiện khi SIM stack vừa chuyển 0 -> 4.
                await Task.Delay(1500, ct);
            }

            if (!simReady) return false;

            _lastSimState[portName] = true;
        }
        finally
        {
            _rebootRecoveryInProgress.TryRemove(portName, out _);
        }

        // Re-enter the normal identity pipeline (CCID -> IMEI -> configuration) only
        // after the modem and SIM are both ready.
        await HandleSimInsertedAsync(portName);
        return true;
    }

    public async Task<bool> ReinitializeSettingsAsync(string portName, CancellationToken ct = default)
    {
        // Chờ modem boot lên (AT trả về OK)
        bool ready = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
            string ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
            if (!ping.Contains("Timeout") && !ping.Contains("ERROR"))
            {
                ready = true;
                break;
            }
            await Task.Delay(1500, ct);
        }

        if (!ready) 
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "ERROR: Modem đã bị rút trong lúc khởi động lại." });
            return false;
        }

        // [SECURITY CRITICAL] Thực thi đúng 100% chuỗi lệnh khởi tạo chuẩn từ SAuto
        await SendEscapeWithoutResponseAsync(portName, ct);
        await Task.Delay(100, ct);
        // CMGD thuộc vòng no-SIM đã capture. Khi đang cấu hình một SIM thật,
        // SAuto đi thẳng sang boot/network; không được xóa SMS vừa đến của SIM.
        foreach (string cmd in SautoInitializationCommandOrder
            .Skip(1)
            .Where(cmd => !cmd.StartsWith("AT+CMGD=", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            await SendCommandAsync(portName, cmd, 5000, silent: true, ct: ct);
        }

        return true;
    }

    public void StartGlobalSimMonitor(string portName)
    {
        if (_suspendedBackgroundPorts.ContainsKey(portName)) return;

        CancellationToken token;
        lock (_simMonitorCts)
        {
            if (_simMonitorCts.TryGetValue(portName, out var oldCts))
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch {}
            }
            var newCts = new CancellationTokenSource();
            _simMonitorCts[portName] = newCts;
            token = newCts.Token;
        }

        _ = Task.Run(async () =>
        {
            // Chờ 20 giây để quá trình Initialize ban đầu hoàn tất, tránh xung đột
            try { await Task.Delay(20000, token); } catch { return; }

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(5000, token); } catch { break; } // Quét mỗi 5 giây
                if (_suspendedBackgroundPorts.ContainsKey(portName)) continue;
                if (!_serialPorts.ContainsKey(portName)) break;
                if (IsCallInProgress(portName)) continue;
                if (_rebootRecoveryInProgress.ContainsKey(portName)) continue;
                if (_pollingCts.ContainsKey(portName) || _simInitInProgress.ContainsKey(portName)) continue;

                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                
                // Nếu timeout (modem đang bận gọi điện) thì bỏ qua vòng lặp này
                if (string.IsNullOrWhiteSpace(cpin)) continue;

                // Do not use Contains("READY"): it also matches "+CPIN: NOT READY".
                bool isSimPresent = Regex.IsMatch(cpin, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                bool isSimLocked = cpin.Contains("SIM PIN") || cpin.Contains("SIM PUK");
                bool stackDisabledByTool = _simStackDisabledByTool.TryGetValue(portName, out bool stackDisabled) && stackDisabled;
                // EC20C reports CPIN NOT READY / CME 10 while CFUN=0/4 even when the card is
                // physically still inserted. Never turn that tool-induced radio transition into
                // a physical-removal event; the unsolicited QSIMSTAT/CPIN handler will detect a
                // real removal once the SIM stack is enabled again.
                bool removalUrcPending = _simRemovalEvidenceCounts.TryGetValue(portName, out int urcEvidence)
                    && urcEvidence > 0;
                bool isSimRemoved = ShouldVerifySimRemoval(cpin, stackDisabledByTool, removalUrcPending);

                // CPIN/CME đơn lẻ không đủ kết luận SIM đã bị rút. Sau USSD hoặc lúc
                // modem chuyển miền CS/IMS, một số EC20 trả CME 10 dù CCID vẫn đọc được.
                // Xác minh thêm cảm biến và danh tính SIM trước khi tăng bằng chứng rút.
                if (isSimRemoved)
                {
                    string qsimstat = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                        ? await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true)
                        : string.Empty;
                    string liveCcid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true);
                    if (Regex.IsMatch(qsimstat, @"\+QSIMSTAT:\s*1\s*,\s*1") || HasReadableCcid(liveCcid))
                    {
                        isSimPresent = true;
                        isSimRemoved = false;
                    }
                }

                // Quectel sometimes returns generic ERROR when SIM is removed if CMEE=2 drops
                if (!isSimPresent && !isSimRemoved && cpin.Contains("ERROR"))
                {
                    string qsimstat = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                        ? await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true)
                        : string.Empty;
                    // QSIMSTAT=0 ở CFUN=4 không chứng minh SIM đã bị rút trên EC20C.
                    // Chỉ cập nhật PRESENT khi cảm biến báo chắc chắn; removal thật do URC
                    // hoặc CPIN NOT INSERTED đảm nhiệm.
                    if (Regex.IsMatch(qsimstat, @"\+QSIMSTAT:\s*1\s*,\s*1"))
                        isSimPresent = true;
                }

                _lastSimState.TryGetValue(portName, out bool lastState);

                if (isSimLocked)
                {
                    ClearSimRemovalEvidence(portName);
                    _lastSimState[portName] = false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }

                if (isSimPresent && !lastState)
                {
                    CancelSimRemovalConfirmation(portName);
                    ClearSimRemovalEvidence(portName);
                    // Guard: Nếu InitializeModemAsync đang chạy (trong 20s đầu) hoặc đang handle SIM khác → bỏ qua
                    if (_simInitInProgress.ContainsKey(portName)) continue;

                    _lastSimState[portName] = true;
                    _ = HandleSimInsertedSafelyAsync(portName);
                }
                else if (isSimPresent)
                {
                    CancelSimRemovalConfirmation(portName);
                    ClearSimRemovalEvidence(portName);
                }
                else if (isSimRemoved && lastState && IsSimRemovalWatchEnabled(portName))
                {
                    // Require consecutive, identity-confirmed removal cycles over
                    // a real elapsed window. This filters the transient QSIMSTAT=0
                    // wave emitted by some GSM boards during RF/IMS changes.
                    if (!RegisterSimRemovalEvidence(portName)) continue;
                    ClearSimRemovalEvidence(portName);
                    _lastSimState[portName] = false;
                    SetSmsSimIdentity(portName, null);
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (Quét nền)!" });
                    _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    StartHotplugWaitLoop(portName);
                }
                else if (isSimRemoved && lastState)
                {
                    // Chỉ bật theo dõi rút SIM sau khi kế hoạch USSD tự động đã hoàn tất.
                    ClearSimRemovalEvidence(portName);
                }
                
                if (!_lastSimState.ContainsKey(portName) && (isSimPresent || isSimRemoved))
                {
                    _lastSimState[portName] = isSimPresent;
                }
            }
        });
    }

    public void StartHotplugWaitLoop(string portName)
    {
        if (_suspendedBackgroundPorts.ContainsKey(portName)) return;

        if (_keepAliveCts.TryRemove(portName, out var oldKeepAlive))
        {
            try { oldKeepAlive.Cancel(); oldKeepAlive.Dispose(); } catch { }
        }

        CancellationTokenSource loopCts;
        lock (_pollingCts)
        {
            if (_pollingCts.TryGetValue(portName, out var oldCts))
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
            }
            loopCts = new CancellationTokenSource();
            _pollingCts[portName] = loopCts;
        }

        CancellationToken token = loopCts.Token;
        bool IsCurrentLoop() => !token.IsCancellationRequested
            && _pollingCts.TryGetValue(portName, out var current)
            && ReferenceEquals(current, loopCts);

        _ = Task.Run(async () =>
        {
            int recoverableStackPasses = 0;
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[WAITING_FOR_SIM] Đang chờ SIM theo chuỗi khởi tạo SAuto; RF giữ ở CFUN=4"
            });

            while (IsCurrentLoop() && _serialPorts.ContainsKey(portName))
            {
                try
                {
                    // The captured SAuto no-SIM loop starts a fresh initialization pass
                    // roughly once per nine seconds. The sequence itself accounts for
                    // most of that interval; one second separates consecutive passes.
                    await Task.Delay(1000, token);
                    SautoInitializationResult result = await RunSautoInitializationSequenceAsync(portName, token);
                    if (!result.RadioLocked)
                    {
                        await ReopenSerialHandleBetweenSautoPassesAsync(portName, token);
                        continue;
                    }

                    string imei = Regex.Match(result.ImeiResponse ?? string.Empty, @"(?<!\d)\d{15}(?!\d)").Value;
                    if (!string.IsNullOrEmpty(imei))
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei}" });

                    if (result.CpinResponse.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                        || result.CpinResponse.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {result.CpinResponse.Trim()}" });
                        continue;
                    }

                    if (!Regex.IsMatch(result.CpinResponse, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase))
                    {
                        if (IsOfflineRecoverableSimStackResponse(
                                result.CpinResponse))
                        {
                            recoverableStackPasses++;
                            if (ShouldRunHotplugOfflineRecovery(
                                    recoverableStackPasses))
                            {
                                LogMessage?.Invoke(this, new GsmDataEventArgs
                                {
                                    PortName = portName,
                                    Data = $"[SIM_HOTPLUG_OFFLINE_RECOVERY] CPIN chưa sẵn sàng lượt {recoverableStackPasses}; thử CFUN=0 -> CFUN=4, RF vẫn khóa."
                                });

                                if (await RestartSimStackOfflineAsync(
                                        portName, token))
                                {
                                    string recoveredCcid = string.Empty;
                                    for (int attempt = 1; attempt <= 4; attempt++)
                                    {
                                        string recoveredCpin =
                                            await SendCommandAsync(
                                                portName,
                                                "AT+CPIN?",
                                                5000,
                                                silent: true,
                                                ct: token);
                                        if (recoveredCpin.Contains(
                                                "SIM PIN",
                                                StringComparison.OrdinalIgnoreCase)
                                            || recoveredCpin.Contains(
                                                "SIM PUK",
                                                StringComparison.OrdinalIgnoreCase))
                                        {
                                            LogMessage?.Invoke(this, new GsmDataEventArgs
                                            {
                                                PortName = portName,
                                                Data = $"[STATUS_SIM_LOCKED] {recoveredCpin.Trim()}"
                                            });
                                            break;
                                        }

                                        recoveredCcid =
                                            await ReadCcidWithFallbackAsync(
                                                portName,
                                                5000,
                                                silent: true,
                                                ct: token);
                                        if (HasReadableCcid(recoveredCcid))
                                            break;
                                        if (attempt < 4)
                                            await Task.Delay(750, token);
                                    }

                                    if (HasReadableCcid(recoveredCcid))
                                    {
                                        _lastSimState[portName] = true;
                                        CancelSimRemovalConfirmation(portName);
                                        string recoveredCcidDigits = Regex.Match(
                                            recoveredCcid,
                                            @"(?<!\d)89\d{16,20}(?!\d)").Value;
                                        LogMessage?.Invoke(this, new GsmDataEventArgs
                                        {
                                            PortName = portName,
                                            Data = $"[SIM_HOTPLUG_OFFLINE_RECOVERED] CCID={recoveredCcidDigits}; RF giữ ở CFUN=4."
                                        });
                                        LogMessage?.Invoke(this, new GsmDataEventArgs
                                        {
                                            PortName = portName,
                                            Data = $"[PARSE_CCID] {recoveredCcidDigits}"
                                        });
                                        LogMessage?.Invoke(this, new GsmDataEventArgs
                                        {
                                            PortName = portName,
                                            Data = "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận SIM sau phục hồi offline có giới hạn"
                                        });
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            recoverableStackPasses = 0;
                        }

                        await ReopenSerialHandleBetweenSautoPassesAsync(portName, token);
                        continue;
                    }

                    recoverableStackPasses = 0;
                    string ccidResponse = await ReadCcidWithFallbackAsync(
                        portName, 5000, silent: true, ct: token);
                    if (!HasReadableCcid(ccidResponse))
                    {
                        await ReopenSerialHandleBetweenSautoPassesAsync(portName, token);
                        continue;
                    }

                    _lastSimState[portName] = true;
                    CancelSimRemovalConfirmation(portName);
                    string ccid = Regex.Match(ccidResponse, @"\d{18,22}").Value;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid}" });
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận SIM theo chuỗi SAuto" });
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[WAITING_FOR_SIM] Lặp khởi tạo lỗi: {ex.Message}" });
                }
            }

            // Keep the lease in the dictionary while MainViewModel verifies/writes IMEI.
            // StartPollingNetwork or a restarted hot-plug loop will atomically replace it;
            // meanwhile the global monitor cannot inject extra AT commands into the trace.
        });
    }

    internal static bool IsOfflineRecoverableSimStackResponse(
        string? cpinResponse)
    {
        string response = cpinResponse ?? string.Empty;
        if (response.Contains(
                "NOT INSERTED", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(response, @"\+CME ERROR:\s*10\b"))
            return false;

        return response.Contains("NOT READY", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(response, @"\+CME ERROR:\s*13\b");
    }

    internal static bool ShouldRunHotplugOfflineRecovery(
        int consecutiveRecoverablePasses) =>
        consecutiveRecoverablePasses == 1
        || (consecutiveRecoverablePasses > 1
            && consecutiveRecoverablePasses % 3 == 0);


    public async Task HandleSimInsertedAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return;

        CancelSimRemovalConfirmation(portName);

        // Nếu SIM được cắm đúng lúc init đang chạy, chờ init kết thúc thay vì bỏ event;
        // bỏ event ở đây sẽ làm _lastSimState=true và không còn transition kế tiếp.
        for (int i = 0; i < 30 && _simInitInProgress.ContainsKey(portName); i++)
        {
            if (!_serialPorts.ContainsKey(portName)) return;
            await Task.Delay(1000);
        }
        if (_simInitInProgress.ContainsKey(portName))
        {
            _lastSimState[portName] = false;
            return;
        }
        if (!_simInsertInProgress.TryAdd(portName, true)) return;

        try
        {
            // [SECURITY FIX] Tắt radio NGAY LẬP TỨC khi phát hiện SIM hot-plug,
            // trước cả delay và CPIN check — ngăn modem kịp đăng ký mạng với IMEI gốc.
            // VNPT/carrier ghi nhận IMEI trong vòng ~0.5s kể từ khi modem attach mạng.
            await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);

            // Đợi SIM khởi động đủ để phản hồi CPIN (ngắn hơn trước vì radio đã tắt)
            await Task.Delay(1000);

            string cpinState = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
            if (cpinState.Contains("SIM PIN") || cpinState.Contains("SIM PUK"))
            {
                _lastSimState[portName] = false;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpinState.Trim()}" });
                return;
            }

        // Đọc IMEI hiện tại (radio đã tắt, IMEI đọc từ NV)
            string currentImei = await SendCommandAsync(portName, "AT+EGMR=0,7;", 5000, silent: true);
            string cleanImei = "";
            if (!string.IsNullOrWhiteSpace(currentImei) && !currentImei.Contains("ERROR"))
            {
                cleanImei = Regex.Match(currentImei, @"(?<!\d)\d{15}(?!\d)").Value;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {cleanImei}" });
            }

            // EC20F không phải firmware nào cũng trả cùng một lệnh ở CFUN=4.
            // Thử QCCID -> ICCID -> CRSM trước, rồi reset riêng SIM stack bằng 0 -> 4
            // và thử lại. RF không bao giờ được bật trong giai đoạn nhận diện này.
            string pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
            bool hasSim = HasReadableCcid(pollResp);

            if (!hasSim && await RestartSimStackOfflineAsync(portName))
            {
                for (int attempt = 0; attempt < 4 && !hasSim; attempt++)
                {
                    cpinState = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                    pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
                    hasSim = HasReadableCcid(pollResp);
                    if (!hasSim && attempt < 3)
                        await Task.Delay(750);
                }
            }

            if (hasSim)
            {
                string ccid = Regex.Match(pollResp, @"(?<!\d)89\d{16,20}(?!\d)").Value;
                _lastSimState[portName] = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid}" });

                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận diện SIM; RF giữ tắt và chờ thao tác IMEI."
                });
            }
            else
            {
                _lastSimState[portName] = false;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Không đọc được SIM (Lỗi phần cứng hoặc SIM hỏng)" });
                StartHotplugWaitLoop(portName);
            }
        }
        finally
        {
            _simInsertInProgress.TryRemove(portName, out _);
        }
    }

    private bool IsNetworkSimIdentityCurrent(
        string portName,
        string expectedCcid) =>
        _networkSimIdentities.TryGetValue(
            portName, out string? currentCcid)
        && string.Equals(
            currentCcid,
            expectedCcid,
            StringComparison.Ordinal);

    private bool IsNetworkPollingIdentityCurrent(
        string portName,
        NetworkPollingIdentity expectedIdentity) =>
        IsNetworkSimIdentityCurrent(portName, expectedIdentity.Ccid)
        && _pollingExpectedIdentities.TryGetValue(
            portName, out NetworkPollingIdentity? currentIdentity)
        && NetworkPollingIdentitiesMatch(
            currentIdentity.Ccid,
            currentIdentity.Imei,
            expectedIdentity.Ccid,
            expectedIdentity.Imei);

    internal static bool NetworkPollingIdentitiesMatch(
        string? currentCcid,
        string? currentImei,
        string? expectedCcid,
        string? expectedImei) =>
        string.Equals(
            currentCcid,
            expectedCcid,
            StringComparison.Ordinal)
        && NetworkRecoveryImeiMatches(currentImei, expectedImei);

    internal bool HasActiveNetworkPollingIdentity(
        string portName,
        string expectedCcid,
        string expectedImei) =>
        _pollingExpectedIdentities.TryGetValue(
            portName, out NetworkPollingIdentity? identity)
        && NetworkPollingIdentitiesMatch(
            identity.Ccid,
            identity.Imei,
            expectedCcid,
            expectedImei);

    internal bool HasPendingNetworkPollingIdentity(
        string portName,
        string expectedCcid,
        string expectedImei) =>
        _pendingNetworkPollingPorts.TryGetValue(
            portName, out NetworkPollingIdentity? identity)
        && NetworkPollingIdentitiesMatch(
            identity.Ccid,
            identity.Imei,
            expectedCcid,
            expectedImei);

    public void StartPollingNetwork(
        string portName,
        string expectedCcid,
        string expectedImei)
    {
        string normalizedExpectedCcid = Regex.Match(
            expectedCcid ?? string.Empty,
            @"(?<!\d)89\d{16,20}(?!\d)").Value;
        string normalizedExpectedImei =
            ImeiManagementService.ToCanonicalImei(expectedImei);
        if (string.IsNullOrWhiteSpace(normalizedExpectedCcid)
            || !ImeiManagementService.IsValidImei(normalizedExpectedImei))
            return;
        var expectedIdentity = new NetworkPollingIdentity(
            normalizedExpectedCcid,
            normalizedExpectedImei);

        CancellationToken token;
        lock (_backgroundOperationSync)
        {
            if (!IsNetworkSimIdentityCurrent(
                    portName, normalizedExpectedCcid))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[NETWORK_POLL_BLOCKED] expected_ccid={normalizedExpectedCcid}; phiên SIM đã thay đổi trước khi polling bắt đầu."
                });
                return;
            }

            if (_suspendedBackgroundPorts.ContainsKey(portName))
            {
                _pendingNetworkPollingPorts[portName] = expectedIdentity;
                return;
            }

            _pendingNetworkPollingPorts.TryRemove(portName, out _);
            lock (_pollingCts)
            {
                if (_pollingCts.TryGetValue(portName, out var oldCts))
                {
                    try { oldCts.Cancel(); oldCts.Dispose(); } catch {}
                }
                var newCts = new CancellationTokenSource();
                _pollingCts[portName] = newCts;
                _pollingExpectedIdentities[portName] = expectedIdentity;
                token = newCts.Token;
            }
        }

        // Recovery sweep is independent from network/operator detection. +CMTI can be lost
        // while a long AT command is running or while the USB serial driver reconnects.
        // CMGL=ALL also recovers multipart segments already marked REC READ by CMGR before
        // a restart. Delay the first bulk sweep until SAuto's CPIN/CSQ/COPS/USSD startup
        // window has completed; live +CMTI/+CMT is still processed immediately.
        _ = Task.Run(async () =>
        {
            bool firstSweep = true;
            while (!token.IsCancellationRequested
                   && _serialPorts.ContainsKey(portName)
                   && IsNetworkPollingIdentityCurrent(
                       portName, expectedIdentity))
            {
                try
                {
                    await Task.Delay(
                        firstSweep ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(15),
                        token);
                    firstSweep = false;
                    if (!token.IsCancellationRequested && !IsCallInProgress(portName))
                        await SweepUnreadSmsAsync(portName);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SWEEP] Lỗi quét bù SMS: {ex.Message}" });
                }
            }
        }, token);

        // Tạo luồng ngầm chờ thiết bị đăng ký mạng thành công để lấy nhà mạng (Tránh việc AT+COPS? chạy quá sớm lúc chưa có sóng)
        // Lặp vô hạn cho đến khi có mạng hoặc cổng bị rút
        _ = Task.Run(async () =>
        {
            int cycles = 0;
            int waitingNoticeCount = 0;
            bool operatorReported = false;
            int consecutiveCopsMissesAfterRegistration = 0;
            while (true)
            {
                try
                {
                    await Task.Delay(
                        operatorReported
                            ? GetNetworkRegistrationProbeInterval(
                                SettingsService.Current.SignalScanIntervalSeconds)
                            : TimeSpan.FromMilliseconds(500),
                        token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;
                if (!IsNetworkPollingIdentityCurrent(
                        portName, expectedIdentity)) break;
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                if (IsCallInProgress(portName)) continue;

                // CPIN is a guard, not the network critical path. Keep its
                // timeout bounded so a slow reboot cannot postpone COPS/USSD.
                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 3000, silent: true, ct: token);
                if (cpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                    || cpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }

                // StartGlobalSimMonitor deliberately yields while this active polling CTS owns
                // the port. Therefore removal evidence must be completed here; otherwise an
                // unsolicited QSIMSTAT/CPIN removal URC remains stuck at one evidence forever
                // and the UI continues displaying the old SIM as Active.
                bool stackDisabledByTool = _simStackDisabledByTool.TryGetValue(
                    portName, out bool stackDisabled) && stackDisabled;
                bool cpinReady = Regex.IsMatch(
                    cpin, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                if (IsSimRemovalWatchEnabled(portName)
                    && !stackDisabledByTool && !cpinReady)
                {
                    string qsimstat = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                        ? await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true, ct: token)
                        : string.Empty;
                    string liveCcid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true);
                    string cfun = await SendCommandAsync(
                        portName, "AT+CFUN?", 3000, silent: true, ct: token);
                    bool stillPresent = Regex.IsMatch(
                        qsimstat, @"\+QSIMSTAT:\s*1\s*,\s*1", RegexOptions.IgnoreCase)
                        || HasReadableCcid(liveCcid);

                    if (stillPresent)
                    {
                        ClearSimRemovalEvidence(portName);
                    }
                    else if (IsConfirmedSimAbsentDuringPolling(
                        cpin, qsimstat, liveCcid, cfun, stackDisabledByTool))
                    {
                        if (RegisterSimRemovalEvidence(portName))
                        {
                            ClearSimRemovalEvidence(portName);
                            _lastSimState[portName] = false;
                            SetSmsSimIdentity(portName, null);
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (xác minh theo chu kỳ quét sóng)!"
                            });
                            await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true, ct: token);
                            StartHotplugWaitLoop(portName);
                            break;
                        }
                    }
                    else
                    {
                        // Evidence must be consecutive. A CFUN transition, timeout, or
                        // contradictory probe restarts the delayed confirmation window.
                        ClearSimRemovalEvidence(portName);
                    }
                }
                else if (cpinReady)
                {
                    ClearSimRemovalEvidence(portName);
                }

                cycles++;
                // Poll registration every active pass, capped at 15 seconds.
                // The separate signal supervisor owns CSQ updates, so replacing
                // the old four-CSQ/one-COPS cadence does not add per-port traffic.
                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true, ct: token);
                if (TryParseCopsResponse(copsStr, out _, out string act))
                {
                    string netType = MapCopsAccessTechnology(act);
                    if (!string.IsNullOrWhiteSpace(netType))
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_TYPE] {netType}" });
                    
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    operatorReported = true;
                    consecutiveCopsMissesAfterRegistration = 0;
                    waitingNoticeCount = 0;
                    ClearNetworkSimRecoveryAttempts(portName);
                    continue;
                }

                // A local lock/command-contention result means AT+COPS? never
                // reached the modem. It is not evidence that registration was
                // lost, so keep the verified Active state and retry COPS on the
                // next network scan instead of starting COPS/CFUN recovery.
                if (IsDeferredNetworkPollingResponse(copsStr))
                {
                    cycles = operatorReported ? 4 : Math.Min(cycles, 4);
                    continue;
                }

                // +CME ERROR: 13 is a SIM-stack failure, not a weak-signal
                // result.  Recover it before repeatedly issuing COPS=0; CSQ may
                // still look healthy while CPIN and *111 are completely dead.
                if (copsStr.Contains("+CME ERROR: 13", StringComparison.OrdinalIgnoreCase))
                {
                    bool recovered = await RecoverNetworkSimFailureAsync(
                        portName, copsStr, token);
                    cycles = 0;
                    if (recovered)
                    {
                        consecutiveCopsMissesAfterRegistration = 0;
                        continue;
                    }
                }

                // Only probe CSQ after a COPS miss. A slow CSQ response must not
                // postpone the first network registration/USSD attempt.
                await Task.Delay(100, token);
                string csqStr = await SendCommandAsync(
                    portName, "AT+CSQ", 2000, silent: true, ct: token);
                if (csqStr.Contains("+CSQ:", StringComparison.OrdinalIgnoreCase))
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = csqStr.Trim() });

                string? recoveryCreg = null;
                string? recoveryCgreg = null;
                string? recoveryCereg = null;
                if (operatorReported)
                {
                    consecutiveCopsMissesAfterRegistration =
                        NextNetworkLossMissCount(
                            consecutiveCopsMissesAfterRegistration,
                            copsStr);

                    // Before changing the UI, confirm that no registration
                    // domain still reports home/roaming service. Some EC20
                    // firmware temporarily omits the COPS operator while CREG,
                    // CGREG or CEREG remains valid.
                    recoveryCreg = await SendCommandAsync(
                        portName, "AT+CREG?", 4000, silent: true, ct: token);
                    recoveryCgreg = await SendCommandAsync(
                        portName, "AT+CGREG?", 4000, silent: true, ct: token);
                    recoveryCereg = await SendCommandAsync(
                        portName, "AT+CEREG?", 4000, silent: true, ct: token);
                    string confirmedNetworkType =
                        ResolveRegisteredFallbackNetworkType(
                            recoveryCreg,
                            recoveryCgreg,
                            recoveryCereg);
                    if (!string.IsNullOrWhiteSpace(confirmedNetworkType))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[NETWORK_FALLBACK] type={confirmedNetworkType}; CREG/CGREG/CEREG vẫn đăng ký trong lúc COPS tạm thời không trả nhà mạng."
                        });
                        consecutiveCopsMissesAfterRegistration = 0;
                        waitingNoticeCount = 0;
                        cycles = 0;
                        ClearNetworkSimRecoveryAttempts(portName);
                        continue;
                    }

                    if (IsDeferredNetworkPollingResponse(recoveryCreg)
                        || IsDeferredNetworkPollingResponse(recoveryCgreg)
                        || IsDeferredNetworkPollingResponse(recoveryCereg))
                    {
                        cycles = 4;
                        continue;
                    }

                    bool explicitlyUnregistered =
                        AreAllRegistrationDomainsExplicitlyUnregistered(
                            recoveryCreg,
                            recoveryCgreg,
                            recoveryCereg);
                    if (!explicitlyUnregistered
                        && !ShouldReportNetworkLoss(
                            copsStr,
                            consecutiveCopsMissesAfterRegistration))
                    {
                        // Inconclusive transport/modem misses are debounced.
                        // Explicit unregistered states bypass the debounce and
                        // start recovery on this same pass.
                        cycles = 4;
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[NETWORK_PROBE_RETRY] COPS chưa xác nhận ({consecutiveCopsMissesAfterRegistration}/{NetworkLossConfirmationMisses}); giữ Active và kiểm tra lại."
                        });
                        continue;
                    }

                    // COPS disappeared after a previously healthy registration.
                    // Explicit loss is recovered immediately; otherwise only
                    // repeated, non-contention misses may re-enter recovery.
                    consecutiveCopsMissesAfterRegistration = 0;
                    operatorReported = false;
                    waitingNoticeCount = 0;
                    cycles = 30;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = explicitlyUnregistered
                            ? "[NETWORK_LOST] COPS và CREG/CGREG/CEREG xác nhận mất đăng ký; khôi phục ngay."
                            : "[NETWORK_LOST] COPS không phản hồi qua nhiều lần xác minh; bắt đầu khôi phục đăng ký mạng."
                    });
                }

                // Nếu modem có CSQ nhưng không tự hoàn tất COPS, khởi động lại
                // auto-selection giống SAuto. Sau vài lần không thành công, thực
                // hiện detach/attach riêng COM; nếu vẫn kẹt thì cycle RF ngắn để
                // không để COM lặp dò COPS vô hạn với radio ở trạng thái nửa sống.
                if (cycles >= 30)
                {
                    waitingNoticeCount++;
                    string creg = recoveryCreg ?? await SendCommandAsync(
                        portName, "AT+CREG?", 4000, silent: true, ct: token);
                    string cgreg = recoveryCgreg ?? await SendCommandAsync(
                        portName, "AT+CGREG?", 4000, silent: true, ct: token);
                    string cereg = recoveryCereg ?? await SendCommandAsync(
                        portName, "AT+CEREG?", 4000, silent: true, ct: token);
                    string registeredType = ResolveRegisteredFallbackNetworkType(
                        creg, cgreg, cereg);
                    if (!string.IsNullOrWhiteSpace(registeredType))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[NETWORK_FALLBACK] type={registeredType}; CREG/CGREG/CEREG đã đăng ký nhưng COPS không trả tên nhà mạng."
                        });
                        operatorReported = true;
                        waitingNoticeCount = 0;
                        ClearNetworkSimRecoveryAttempts(portName);
                        continue;
                    }

                    string copsAuto;
                    string recoveryAction = "auto-select";
                    if (waitingNoticeCount % 3 == 0)
                    {
                        string detach = await SendCommandAsync(
                            portName, "AT+COPS=2", 5000, silent: true, ct: token);
                        // EC20 needs a short SIM detach settle window. 300 ms
                        // can make the immediate COPS=0 return CME 13 even
                        // though CPIN is READY; let the modem finish detach.
                        await Task.Delay(1500, token);
                        copsAuto = await SendCommandAsync(
                            portName, "AT+COPS=0", 15000, silent: true, ct: token);
                        recoveryAction = $"detach/attach ({detach.Trim()})";

                        if (waitingNoticeCount % 6 == 0)
                        {
                            string rfOff = await SendCommandAsync(
                                portName, "AT+CFUN=4", 8000, silent: true, ct: token);
                            await Task.Delay(500, token);
                            string rfOn = await SendCommandAsync(
                                portName, "AT+CFUN=1", 15000, silent: true, ct: token);
                            recoveryAction += $"; RF cycle ({rfOff.Trim()} -> {rfOn.Trim()})";
                        }
                    }
                    else
                    {
                        copsAuto = await SendCommandAsync(
                            portName, "AT+COPS=0", 15000, silent: true, ct: token);
                    }
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = csqStr.Contains(
                            "+CSQ:", StringComparison.OrdinalIgnoreCase)
                            ? $"[NETWORK_RECOVERY] COPS chưa trả nhà mạng nhưng CSQ vừa xác nhận; {recoveryAction} (lần {waitingNoticeCount}): {copsAuto.Trim()}"
                            : $"[NETWORK_RECOVERY] COPS và CSQ đều chưa xác nhận; {recoveryAction} (lần {waitingNoticeCount}): {copsAuto.Trim()}"
                    });
                    if (copsAuto.Contains("+CME ERROR: 13", StringComparison.OrdinalIgnoreCase))
                    {
                        await RecoverNetworkSimFailureAsync(
                            portName, copsAuto, token);
                    }
                    if (ShouldRequestNetworkReopen(waitingNoticeCount))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[NETWORK_REOPEN_REQUIRED] Đã thử {waitingNoticeCount} lượt auto-select/detach/RF nhưng chưa có COPS; mở lại riêng COM."
                        });
                        break;
                    }
                    cycles = 0;
                    continue;
                }
            }
        }, token);
    }

    internal static bool ShouldRequestNetworkReopen(int recoveryPasses) =>
        recoveryPasses >= MaxNetworkRegistrationRecoveryPassesBeforeReopen;

    internal static string ResolveRegisteredFallbackNetworkType(
        string? creg,
        string? cgreg,
        string? cereg)
    {
        if (IsNetworkRegistered(cereg)) return "4G";
        if (IsNetworkRegistered(cgreg)) return "3G";
        if (IsNetworkRegistered(creg)) return "2G";
        return string.Empty;
    }

    internal static string MapCopsAccessTechnology(string? act) => act?.Trim() switch
    {
        "0" or "1" or "3" or "8" => "2G",
        "2" or "4" or "5" or "6" => "3G",
        "7" or "9" => "4G",
        _ => string.IsNullOrWhiteSpace(act) || act == "?" ? string.Empty : $"Unknown({act.Trim()})"
    };

    internal static bool IsNetworkRegistered(string? response)
    {
        return TryParseNetworkRegistrationState(response, out int state)
            && state is 1 or 5;
    }

    internal static bool TryParseNetworkRegistrationState(
        string? response,
        out int state)
    {
        state = -1;
        if (string.IsNullOrWhiteSpace(response)) return false;
        Match match = Regex.Match(
            response,
            @"\+(?:C|CG|CE)REG:\s*(?:\d+\s*,\s*)?(?<stat>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["stat"].Value, out state);
    }

    internal static bool AreAllRegistrationDomainsExplicitlyUnregistered(
        string? creg,
        string? cgreg,
        string? cereg) =>
        TryParseNetworkRegistrationState(creg, out int csState)
        && TryParseNetworkRegistrationState(cgreg, out int psState)
        && TryParseNetworkRegistrationState(cereg, out int epsState)
        && csState is not (1 or 5)
        && psState is not (1 or 5)
        && epsState is not (1 or 5);

    internal static bool TryParseCopsResponse(
        string? response, out string operatorName, out string accessTechnology)
    {
        operatorName = string.Empty;
        accessTechnology = string.Empty;
        if (string.IsNullOrWhiteSpace(response)) return false;

        // EC20 can return the operator in long/short alphanumeric or numeric format.
        // Numeric format is not guaranteed to be quoted, so accepting only "..."
        // caused healthy registered COMs to wait forever before starting USSD.
        Match match = Regex.Match(
            response,
            @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*(?:""(?<operator>[^""]+)""|(?<operator>[^,\r\n]+))(?:\s*,\s*(?<act>\d+))?",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        operatorName = match.Groups["operator"].Value.Trim();
        accessTechnology = match.Groups["act"].Success
            ? match.Groups["act"].Value.Trim()
            : string.Empty;
        return !string.IsNullOrWhiteSpace(operatorName);
    }

    public void StartKeepAliveLoop(string portName)
    {
        CancellationToken token;
        lock (_keepAliveCts)
        {
            if (_keepAliveCts.TryGetValue(portName, out var oldCts))
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch {}
            }
            var newCts = new CancellationTokenSource();
            _keepAliveCts[portName] = newCts;
            token = newCts.Token;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(90000, token); // 90 giây
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;
                if (!_serialPorts.ContainsKey(portName)) break;
                if (IsCallInProgress(portName)) continue;
                
                await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                await SendCommandAsync(portName, "AT+CREG?", 5000, silent: true);
                string csq = await SendCommandAsync(portName, "AT+CSQ", 5000, silent: true);
                if (csq.Contains("+CSQ:", StringComparison.OrdinalIgnoreCase))
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = csq.Trim() });

                string cops = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true);
                if (TryParseCopsResponse(cops, out _, out string act))
                {
                    string netType = MapCopsAccessTechnology(act);
                    if (!string.IsNullOrWhiteSpace(netType))
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_TYPE] {netType}" });
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = cops.Trim() });
                }
                
                // Sweep bù (quét tin nhắn kẹt định kỳ)
                await SweepUnreadSmsAsync(portName);
                /*
                string cmglCommand = GetModemProfile(portName)?.IsQuectel == true ? "AT+CMGL=4" : "AT+CMGL=\"ALL\"";
                string cmgl = await SendCommandAsync(portName, cmglCommand, 25000, silent: true);
                if (!string.IsNullOrWhiteSpace(cmgl) && !cmgl.Contains("ERROR") && cmgl.Contains("+CMGL:"))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[SWEEP] Vét được tin nhắn chưa đọc từ SIM!" });
                    // HandleDataReceived already extracts every +CMGL index and routes each stored
                    // message through QCMGR/CMGR + the exact multipart assembler. Emitting the raw
                    // CMGL response here a second time bypassed that assembler and produced duplicate,
                    // cut SMS entries in the UI/Telegram pipeline.
                }
                */
            }
        }, token);
    }

    public async Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile)
    {
        if (!_portVendors.TryGetValue(portName, out var v) || !v.Contains("QUECTEL"))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi: Tính năng tải file chỉ hỗ trợ trên modem Quectel." });
            return string.Empty;
        }

        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return string.Empty;
        if (!_semaphores.TryGetValue(portName, out var semaphore)) return string.Empty;

        await semaphore.WaitAsync();
        _isDownloading[portName] = true;
        try
        {
            if (_dataReceivedHandlers.TryGetValue(portName, out var handler))
            {
                sp.DataReceived -= handler;
            }

            sp.Write($"AT+QFOPEN=\"{remoteFile}\",2\r");
            
            string res = await ReadUntilAsync(sp, "OK", 3000);
            if (string.IsNullOrWhiteSpace(res)) return string.Empty;

            var match = Regex.Match(res, @"\+QFOPEN:\s*(\d+)");
            if (!match.Success) return string.Empty;
            int handleId = int.Parse(match.Groups[1].Value);

            using var fs = new FileStream(localFile, FileMode.Create, FileAccess.Write);
            
            while(true)
            {
                sp.Write($"AT+QFREAD={handleId},4096\r");
                
                string line = "";
                bool eof = false;
                DateTime start = DateTime.Now;
                while ((DateTime.Now - start).TotalSeconds < 5)
                {
                    if (sp.BytesToRead > 0)
                    {
                        line += (char)sp.ReadChar();
                        if (line.EndsWith("CONNECT ")) break;
                        if (line.Contains("OK\r\n")) { eof = true; break; }
                    }
                    else await Task.Delay(10);
                }
                if (eof) break;

                start = DateTime.Now;
                string lenStr = "";
                while((DateTime.Now - start).TotalSeconds < 2)
                {
                    if (sp.BytesToRead > 0)
                    {
                        char c = (char)sp.ReadChar();
                        if (c == '\r') continue;
                        if (c == '\n') break;
                        lenStr += c;
                    }
                    else await Task.Delay(5);
                }

                if (!int.TryParse(lenStr, out int bytesToRead) || bytesToRead <= 0) break;

                byte[] buf = new byte[bytesToRead];
                int total = 0;
                start = DateTime.Now;
                while(total < bytesToRead && (DateTime.Now - start).TotalSeconds < 5)
                {
                    if (sp.BytesToRead > 0)
                    {
                        total += sp.Read(buf, total, bytesToRead - total);
                    }
                    else await Task.Delay(5);
                }
                fs.Write(buf, 0, total);

                await ReadUntilAsync(sp, "OK", 1000);
            }

            sp.Write($"AT+QFCLOSE={handleId}\r");
            await ReadUntilAsync(sp, "OK", 1000);
            
            // Delete file from RAM to free up memory
            sp.Write($"AT+QFDEL=\"{remoteFile}\"\r");
            await ReadUntilAsync(sp, "OK", 1000);

            return localFile;
        }
        catch(Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi tải file {remoteFile}: {ex.Message}" });
            return string.Empty;
        }
        finally
        {
            _isDownloading[portName] = false;
            if (_dataReceivedHandlers.TryGetValue(portName, out var handler))
            {
                sp.DataReceived += handler;
            }
            semaphore.Release();
            ScheduleUnreadSmsSweepAfterExclusiveIo(portName);
        }
    }

    public async Task<bool> UploadFileToModemAsync(string portName, string localFile, string remoteFile)
    {
        if (!_portVendors.TryGetValue(portName, out var v) || !v.Contains("QUECTEL"))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi: Tính năng tải file lên chỉ hỗ trợ trên modem Quectel." });
            return false;
        }

        if (!File.Exists(localFile)) return false;
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return false;
        if (!_semaphores.TryGetValue(portName, out var semaphore)) return false;

        FileInfo fi = new FileInfo(localFile);
        long fileSize = fi.Length;
        int uploadTimeoutSeconds = Math.Clamp((int)(fileSize / 1024) + 30, 30, 300);
        string interceptedSerialText = string.Empty;

        // Delete old file if exists
        await SendCommandAsync(portName, $"AT+QFDEL=\"{remoteFile}\"", 3000, silent: true);

        await semaphore.WaitAsync();
        _isDownloading[portName] = true;
        try
        {
            if (_dataReceivedHandlers.TryGetValue(portName, out var handler))
            {
                sp.DataReceived -= handler;
            }

            sp.Write($"AT+QFUPL=\"{remoteFile}\",{fileSize},{uploadTimeoutSeconds}\r");

            // Read until "CONNECT" is received
            string resp = "";
            DateTime start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < 5)
            {
                if (sp.BytesToRead > 0)
                {
                    resp += (char)sp.ReadChar();
                    if (resp.Contains("CONNECT")) break;
                }
                else
                {
                    await Task.Delay(10);
                }
            }
            interceptedSerialText += resp;
            if (!resp.Contains("CONNECT", StringComparison.OrdinalIgnoreCase)) return false;

            // Write raw bytes
            using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[1024];
                int bytesRead = 0;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    sp.Write(buffer, 0, bytesRead);
                    await Task.Delay(15); // Short delay to prevent buffer overrun
                }
            }

            // Read until "OK" or "+QFUPL" is received
            string finalResp = "";
            start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < uploadTimeoutSeconds)
            {
                if (sp.BytesToRead > 0)
                {
                    finalResp += (char)sp.ReadChar();
                    if (finalResp.Contains("OK") || finalResp.Contains("ERROR")) break;
                }
                else
                {
                    await Task.Delay(10);
                }
            }
            interceptedSerialText += finalResp;

            foreach (Match cmti in Regex.Matches(
                interceptedSerialText,
                @"\+CMTI:\s*(?:""[^""]*""|[^,\r\n]+)\s*,\s*(\d+)",
                RegexOptions.IgnoreCase))
            {
                QueueStoredSmsRead(portName, cmti.Groups[1].Value);
            }

            return finalResp.Contains("OK") || finalResp.Contains("+QFUPL:");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi tải file lên modem {remoteFile}: {ex.Message}" });
            return false;
        }
        finally
        {
            _isDownloading[portName] = false;
            if (_dataReceivedHandlers.TryGetValue(portName, out var handler))
            {
                sp.DataReceived += handler;
            }
            semaphore.Release();
            ScheduleUnreadSmsSweepAfterExclusiveIo(portName);
        }
    }

    private void ScheduleUnreadSmsSweepAfterExclusiveIo(string portName)
    {
        // QFUPL/QFREAD temporarily own the serial stream, so an incoming +CMTI
        // can be delayed or absorbed into the file-transfer response. The SMS
        // itself remains on the SIM; sweep every slot after the handler is back.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250);
                DateTime waitUntil = DateTime.UtcNow.AddMinutes(6);
                while (DateTime.UtcNow < waitUntil
                    && (_suspendedBackgroundPorts.ContainsKey(portName) || IsCallInProgress(portName)))
                {
                    await Task.Delay(250);
                }
                await SweepUnreadSmsAsync(portName);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SWEEP_AFTER_FILE_IO] {ex.Message}"
                });
            }
        });
    }

    private readonly record struct DirectCmtFrame(int Start, int Length, string Raw);
    private readonly record struct DirectCmtTerminalLine(
        int TokenStart,
        int SeparatorStart);

    /// <summary>
    /// Extract only complete unsolicited +CMT frames from the serial buffer.
    /// A DataReceived callback may split a CMT header/body across several
    /// chunks; the previous end-of-buffer regex treated the first chunk as a
    /// complete frame and removed the remaining bytes.  Leave incomplete data
    /// untouched and consume only an explicit terminator, a following CMT
    /// header/URC boundary, or a complete one-line text-mode body.
    /// </summary>
    private static IReadOnlyList<DirectCmtFrame> ExtractCompleteDirectCmtFrames(
        string data,
        bool commandPending,
        bool allowIdleEndOfBuffer = false)
    {
        var frames = new List<DirectCmtFrame>();
        if (string.IsNullOrEmpty(data)) return frames;

        int search = 0;
        while (search < data.Length)
        {
            int start = data.IndexOf("+CMT:", search, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;

            int headerEnd = data.IndexOf('\n', start);
            if (headerEnd < 0) break; // header itself is split
            int bodyStart = headerEnd + 1;
            if (bodyStart >= data.Length) break;

            // A short direct SMS is frequently followed immediately by a
            // signal/operator URC (+CSQ/+COPS/+CREG), not another SMS header.
            // Treat that line boundary as the end of the CMT frame too; the old
            // parser only recognized +CMT/+CMTI and consequently left short OTPs
            // stuck in the serial buffer.
            int frameEnd = FindNextUrcBoundary(data, bodyStart);

            // A standalone OK/ERROR is legal SMS text. One terminal-looking
            // body line is therefore never a CMT boundary. When a command is
            // pending, a later second standalone terminal is unambiguous: keep
            // the first in the SMS and leave the final one for the command TCS.
            if (commandPending)
            {
                int terminalLimit = frameEnd >= 0 ? frameEnd : data.Length;
                IReadOnlyList<DirectCmtTerminalLine> terminalLines =
                    FindDirectCmtTerminalLines(data, bodyStart, terminalLimit);
                if (terminalLines.Count >= 2)
                {
                    int commandTerminator = terminalLines[^1].SeparatorStart;
                    if (frameEnd < 0 || commandTerminator < frameEnd)
                        frameEnd = commandTerminator;
                }
            }

            if (frameEnd < 0)
            {
                // A callback ending after the first body line does not prove a
                // text SMS is single-line; later chunks may contain more lines.
                // Accept an end-of-buffer delimiter only on a scheduled idle
                // retry after no additional bytes arrived.
                bool completeLineAtEnd = data.EndsWith('\n')
                    && data[bodyStart..].Trim('\r', '\n').Length > 0;
                if (!allowIdleEndOfBuffer
                    || !completeLineAtEnd
                    || commandPending)
                    break;
                frameEnd = data.Length;
            }

            if (frameEnd <= start) break;
            frames.Add(new DirectCmtFrame(start, frameEnd - start, data.Substring(start, frameEnd - start)));
            search = frameEnd;
        }

        return frames;
    }

    // Kept internal so the serial-frame boundary behavior can be regression
    // tested without opening a physical COM port.
    internal static IReadOnlyList<string> ExtractCompleteDirectCmtFramesForTest(
        string data,
        bool commandPending = false,
        bool allowIdleEndOfBuffer = false) =>
        ExtractCompleteDirectCmtFrames(
            data,
            commandPending,
            allowIdleEndOfBuffer)
            .Select(frame => frame.Raw)
            .ToArray();

    internal static string DecodeDirectCmtContentForTest(string raw) =>
        DecodeDirectCmtFrame(raw).Content;

    internal static bool ShouldQuarantineDirectCmtForTest(
        int attempts,
        TimeSpan age,
        int observedChars) =>
        ShouldQuarantineDirectCmt(attempts, age, observedChars);

    internal static (string Quarantined, string Remaining)
        SplitDirectCmtForQuarantineForTest(
            string data,
            bool commandPending = false)
    {
        DirectCmtFrame candidate = FindPendingDirectCmtCandidate(
            data, commandPending);
        if (candidate.Length <= 0) return (string.Empty, data);
        return (
            candidate.Raw,
            data.Remove(candidate.Start, candidate.Length));
    }

    internal static bool IsSmsMemoryFullResponse(string? response) =>
        SmsMemoryFullRegex.IsMatch(response ?? string.Empty);

    private static int FindNextUrcBoundary(string data, int start)
    {
        if (start < data.Length && IsKnownDirectCmtBoundary(data[start..]))
            return FindLineSeparatorStart(data, start);

        int search = start;
        while (search < data.Length)
        {
            int newline = data.IndexOf('\n', search);
            if (newline < 0 || newline + 1 >= data.Length) return -1;
            int candidate = newline + 1;
            string rest = data[candidate..];
            // A real SMS body may legitimately contain lines such as
            // "+ Cách 1". Only documented modem URCs delimit a direct CMT;
            // treating every leading plus sign as a URC truncated Vietnamese
            // carrier instructions.
            if (IsKnownDirectCmtBoundary(rest)) return newline;
            search = candidate;
        }
        return -1;
    }

    private static bool IsKnownDirectCmtBoundary(string rest) =>
        Regex.IsMatch(
            rest,
            @"^\+(?:CMTI?|CSQ|COPS|C(?:G|E)?REG|CUSD|CLIP|QSIMSTAT|CPIN|QTONEDET|CTZE|QIND|CCFC|CMS\s+ERROR|CME\s+ERROR):",
            RegexOptions.IgnoreCase)
        || rest.StartsWith("NO CARRIER", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DirectCmtTerminalLine>
        FindDirectCmtTerminalLines(
            string data,
            int bodyStart,
            int endExclusive)
    {
        var result = new List<DirectCmtTerminalLine>();
        int lineStart = Math.Clamp(bodyStart, 0, data.Length);
        int limit = Math.Clamp(endExclusive, lineStart, data.Length);
        while (lineStart < limit)
        {
            int newline = data.IndexOf('\n', lineStart);
            int lineEnd = newline >= 0 && newline < limit ? newline : limit;
            string line = data.Substring(lineStart, lineEnd - lineStart)
                .Trim('\r', ' ', '\t');
            if (line.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new DirectCmtTerminalLine(
                    lineStart,
                    FindLineSeparatorStart(data, lineStart)));
            }

            if (newline < 0 || newline >= limit) break;
            lineStart = newline + 1;
        }
        return result;
    }

    private static int FindLineSeparatorStart(string data, int tokenStart)
    {
        int separator = Math.Clamp(tokenStart, 0, data.Length);
        if (separator > 0 && data[separator - 1] == '\n')
        {
            separator--;
            if (separator > 0 && data[separator - 1] == '\r') separator--;
        }
        return separator;
    }

    private static DecodedSmsBody DecodeDirectCmtFrame(string raw)
    {
        if (!Regex.IsMatch(raw, "\\+CMT:\\s*\"", RegexOptions.IgnoreCase))
            return SmsBodyDecoder.Decode(raw);

        int headerEnd = raw.IndexOf('\n');
        if (headerEnd < 0 || headerEnd + 1 >= raw.Length)
            return SmsBodyDecoder.Decode(raw);

        string body = raw[(headerEnd + 1)..].TrimEnd('\r', '\n');
        int lastNewline = body.LastIndexOf('\n');
        string finalLine = (lastNewline >= 0 ? body[(lastNewline + 1)..] : body)
            .Trim('\r', ' ', '\t');
        if (!finalLine.Equals("OK", StringComparison.OrdinalIgnoreCase)
            && !finalLine.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            return SmsBodyDecoder.Decode(raw);

        // SmsBodyDecoder strips a final modem terminator from a CMGR/CMT
        // envelope. Extraction has already kept the real command terminator
        // out of this raw frame, so protect a terminal-looking body line with a
        // sentinel and remove only that sentinel afterwards.
        DecodedSmsBody decoded = SmsBodyDecoder.Decode(
            raw.TrimEnd('\r', '\n')
            + "\r\n"
            + DirectCmtDecodeSentinel);
        string suffix = "\n" + DirectCmtDecodeSentinel;
        string content = decoded.Content.EndsWith(suffix, StringComparison.Ordinal)
            ? decoded.Content[..^suffix.Length]
            : decoded.Content.Equals(
                DirectCmtDecodeSentinel, StringComparison.Ordinal)
                ? string.Empty
                : decoded.Content;
        return decoded with { Content = content };
    }

    private static bool ShouldQuarantineDirectCmt(
        int attempts,
        TimeSpan age,
        int observedChars) =>
        observedChars >= DirectCmtMaxPendingChars
        || attempts >= DirectCmtMaxDecodeAttempts
        || age >= DirectCmtMaxPendingAge;

    private static DirectCmtFrame FindPendingDirectCmtCandidate(
        string data,
        bool commandPending)
    {
        int start = data.IndexOf("+CMT:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return default;

        int headerEnd = data.IndexOf('\n', start);
        int bodyStart = headerEnd >= 0 ? headerEnd + 1 : start + 5;
        int end = FindNextUrcBoundary(data, bodyStart);

        // A malformed/split header must never absorb a later valid CMT. The
        // generic URC search starts at the presumed body, so explicitly catch
        // a second direct header in that first body line.
        if (bodyStart < data.Length
            && data.AsSpan(bodyStart).StartsWith(
                "+CMT:", StringComparison.OrdinalIgnoreCase))
        {
            int nestedBoundary = FindLineSeparatorStart(data, bodyStart);
            if (nestedBoundary > start && (end < 0 || nestedBoundary < end))
                end = nestedBoundary;
        }

        if (commandPending)
        {
            int terminalLimit = end >= 0 ? end : data.Length;
            IReadOnlyList<DirectCmtTerminalLine> terminalLines =
                FindDirectCmtTerminalLines(data, bodyStart, terminalLimit);
            if (terminalLines.Count > 0)
            {
                // Preserve the last standalone terminator for the pending AT
                // command. A valid body OK + command OK pair is extracted by
                // the normal path before quarantine is considered.
                int commandBoundary = terminalLines[^1].SeparatorStart;
                if (commandBoundary > start
                    && (end < 0 || commandBoundary < end))
                    end = commandBoundary;
            }
        }

        if (end < 0) end = data.Length;
        if (end <= start) return default;
        return new DirectCmtFrame(
            start,
            end - start,
            data.Substring(start, end - start));
    }

    private bool DispatchDecodedSms(
        string portName,
        string sender,
        string content,
        string deliveryId,
        string msgIndex = "")
    {
        var delivery = new GsmDataEventArgs
        {
            PortName = portName,
            Data = content,
            MsgIndex = msgIndex,
            Sender = sender,
            Otp = ExtractOtp(content) ?? string.Empty,
            DeliveryId = deliveryId
        };
        try
        {
            SmsReceived?.Invoke(this, delivery);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_DELIVERY_RETRY] delivery={deliveryId}; inbox chưa nhận, sẽ thử lại: {ex.Message}"
            });
            return false;
        }

        return delivery.DeliveryAccepted;
    }

    private bool DirectScopeStillCurrent(string portName, string scope)
    {
        if (!_serialPorts.ContainsKey(portName)) return false;
        if (scope.StartsWith("ccid:", StringComparison.Ordinal))
        {
            return _smsSimIdentities.TryGetValue(portName, out string? ccid)
                && string.Equals(
                    scope,
                    $"ccid:{ccid}",
                    StringComparison.Ordinal);
        }
        return string.Equals(scope, portName, StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleCompletedMultipartReplay(
        string scope,
        string portName,
        int delayMs = 250,
        int attempt = 1)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || string.IsNullOrWhiteSpace(portName)
            || !_multipartReplayOwners.TryAdd(scope, 0))
            return;

        _ = Task.Run(async () =>
        {
            bool retryNeeded = false;
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (!DirectScopeStillCurrent(portName, scope)) return;

                IReadOnlyList<SmsMultipartJournal.CompletedSnapshot> snapshots =
                    _multipartJournal.GetCompletedSnapshots(
                        scope,
                        includeAcknowledged: true);
                foreach (SmsMultipartJournal.CompletedSnapshot snapshot in snapshots)
                {
                    if (!DirectScopeStillCurrent(portName, scope)) return;

                    if (!snapshot.DeliveryAcknowledged)
                    {
                        bool accepted = DispatchDecodedSms(
                            portName,
                            snapshot.Sender,
                            snapshot.Content,
                            snapshot.MessageId);
                        if (!accepted)
                        {
                            retryNeeded = true;
                            continue;
                        }

                        _multipartJournal.MarkDeliveryAcknowledged(
                            snapshot.MessageId);
                        RememberDeliveredSms(snapshot.MessageId);
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[MULTIPART_REPLAYED] delivery={snapshot.MessageId}; inbox đã nhận bản ghép bền vững."
                        });
                    }

                    // Stored multipart records may still have their final SIM
                    // slot. The normal CMGR path will delete that exact slot and
                    // then complete the journal. Direct CMT has no recyclable SIM
                    // slot, so its acknowledged journal can be removed now.
                    if (!snapshot.RequiresSimCleanup
                        || snapshot.SimCleanupConfirmed)
                        _multipartJournal.Complete(snapshot.MessageId);
                }
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                retryNeeded = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[MULTIPART_REPLAY_RETRY] {ex.Message}"
                });
            }
            finally
            {
                _multipartReplayOwners.TryRemove(scope, out _);
                if (retryNeeded && DirectScopeStillCurrent(portName, scope))
                {
                    if (attempt < MaxMultipartJournalRetryAttempts)
                    {
                        ScheduleCompletedMultipartReplay(
                            scope,
                            portName,
                            Math.Min(delayMs * 2, 30000),
                            attempt + 1);
                    }
                    else
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[MULTIPART_REPLAY_DEFERRED] scope={scope}; hết retry nhanh, journal vẫn còn trên đĩa và sweep định kỳ sẽ thử lại."
                        });
                    }
                }
            }
        });
    }

    private void ScheduleSafeUnreadSmsSweep(
        string portName,
        string reason,
        int initialDelayMs = 0)
    {
        if (!_smsSweepRetryOwners.TryAdd(portName, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (initialDelayMs > 0)
                    await Task.Delay(initialDelayMs).ConfigureAwait(false);
                DateTime deadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < deadline
                       && _serialPorts.ContainsKey(portName)
                       && (_commandTcs.ContainsKey(portName)
                           || _suspendedBackgroundPorts.ContainsKey(portName)
                           || IsCallInProgress(portName)))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
                await SweepUnreadSmsAsync(portName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SWEEP_RETRY] reason={reason}; {ex.Message}"
                });
            }
            finally
            {
                _smsSweepRetryOwners.TryRemove(portName, out _);
            }
        });
    }

    private void ScheduleMultipartJournalCompletionRetry(
        string messageId,
        string portName,
        int delayMs = 1000,
        int attempt = 1)
    {
        if (string.IsNullOrWhiteSpace(messageId)
            || !_multipartCompletionRetryOwners.TryAdd(messageId, 0))
            return;

        _ = Task.Run(async () =>
        {
            bool retryNeeded = false;
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                _multipartJournal.Complete(messageId);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                retryNeeded = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[MULTIPART_JOURNAL_CLEANUP_RETRY] delivery={messageId}: {ex.Message}"
                });
            }
            finally
            {
                _multipartCompletionRetryOwners.TryRemove(messageId, out _);
                if (retryNeeded && !_serialPorts.IsEmpty)
                {
                    if (attempt < MaxMultipartJournalRetryAttempts)
                    {
                        ScheduleMultipartJournalCompletionRetry(
                            messageId,
                            portName,
                            Math.Min(delayMs * 2, 30000),
                            attempt + 1);
                    }
                    else
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[MULTIPART_JOURNAL_CLEANUP_DEFERRED] delivery={messageId}; hết retry nhanh, journal bền vững sẽ được sweep thử lại."
                        });
                    }
                }
            }
        });
    }

    private bool RecordMultipartPartCleanupOrRetry(
        string messageId,
        string partIdentity,
        string portName)
    {
        try
        {
            _multipartJournal.MarkPartCleaned(messageId, partIdentity);
            _simCleanupJournal.Complete(partIdentity, messageId);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[MULTIPART_SLOT_STATE_RETRY] delivery={messageId}: {ex.Message}"
            });
            ScheduleMultipartPartCleanupRetry(
                messageId,
                partIdentity,
                portName);
            return false;
        }
    }

    private void ScheduleMultipartPartCleanupRetry(
        string messageId,
        string partIdentity,
        string portName,
        int delayMs = 1000,
        int attempt = 1)
    {
        string ownerKey = $"{messageId}\u001f{partIdentity}";
        if (!_multipartPartCleanupRetryOwners.TryAdd(ownerKey, 0)) return;
        _ = Task.Run(async () =>
        {
            bool retryNeeded = false;
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                _multipartJournal.MarkPartCleaned(messageId, partIdentity);
                _simCleanupJournal.Complete(partIdentity, messageId);
                if (_multipartJournal.IsDeliveryAcknowledged(messageId)
                    && _multipartJournal.IsSimCleanupConfirmed(messageId))
                    _multipartJournal.Complete(messageId);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                retryNeeded = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[MULTIPART_SLOT_STATE_RETRY] delivery={messageId}: {ex.Message}"
                });
            }
            finally
            {
                _multipartPartCleanupRetryOwners.TryRemove(ownerKey, out _);
                if (retryNeeded && !_serialPorts.IsEmpty)
                {
                    if (attempt < MaxMultipartJournalRetryAttempts)
                    {
                        ScheduleMultipartPartCleanupRetry(
                            messageId,
                            partIdentity,
                            portName,
                            Math.Min(delayMs * 2, 30000),
                            attempt + 1);
                    }
                    else
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[MULTIPART_SLOT_STATE_DEFERRED] delivery={messageId}; hết retry nhanh, cleanup intent còn bền vững để sweep phục hồi."
                        });
                        ScheduleSafeUnreadSmsSweep(
                            portName,
                            "multipart-cleanup-retry-budget",
                            initialDelayMs: 30000);
                    }
                }
            }
        });
    }

    private void ScheduleDirectCmtRetry(string portName, int delayMs = 1000)
    {
        if (!_directCmtRetryOwners.TryAdd(portName, 0)) return;
        _ = Task.Run(async () =>
        {
            bool stillPending = false;
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (_serialPorts.TryGetValue(portName, out SerialPort? serialPort)
                    && serialPort.IsOpen)
                {
                    HandleDataReceived(portName, serialPort);
                    object gate = _portBufferLocks.GetOrAdd(
                        portName, static _ => new object());
                    lock (gate)
                    {
                        stillPending = _portBuffers.TryGetValue(
                                portName, out StringBuilder? pendingBuffer)
                            && pendingBuffer.ToString().Contains(
                                "+CMT:",
                                StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                stillPending = true;
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_DIRECT_RETRY] {ex.Message}"
                });
            }
            finally
            {
                _directCmtRetryOwners.TryRemove(portName, out _);
                if (stillPending && _serialPorts.ContainsKey(portName))
                    ScheduleDirectCmtRetry(portName, 2000);
            }
        });
    }

    private DirectCmtRetryState ObserveDirectCmtFailure(
        string portName,
        string raw)
    {
        string fingerprint = BuildDirectCmtFailureFingerprint(raw);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return _directCmtRetryStates.AddOrUpdate(
            portName,
            _ => new DirectCmtRetryState
            {
                Fingerprint = fingerprint,
                FirstSeenUtc = now,
                Attempts = 1,
                MaxObservedChars = raw.Length
            },
            (_, current) =>
            {
                if (!string.Equals(
                    current.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
                {
                    return new DirectCmtRetryState
                    {
                        Fingerprint = fingerprint,
                        FirstSeenUtc = now,
                        Attempts = 1,
                        MaxObservedChars = raw.Length
                    };
                }

                current.Attempts++;
                current.MaxObservedChars = Math.Max(
                    current.MaxObservedChars,
                    raw.Length);
                return current;
            });
    }

    private static string BuildDirectCmtFailureFingerprint(string raw)
    {
        // A split frame grows between callbacks. Once the header CR/LF exists,
        // it is the stable identity of the pending frame; before then, the CMT
        // marker itself is stable. Do not hash the growing body, otherwise a
        // slow byte stream could reset FirstSeenUtc forever and defeat the time
        // bound (the state is per COM and is cleared after consume/quarantine).
        int headerEnd = raw.IndexOfAny(['\r', '\n']);
        int prefixLength = headerEnd >= 0
            ? Math.Min(headerEnd, 256)
            : Math.Min(raw.Length, "+CMT:".Length);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(raw[..prefixLength])));
    }

    private void ClearDirectCmtFailure(
        string portName,
        string? raw = null)
    {
        if (!_directCmtRetryStates.TryGetValue(
            portName, out DirectCmtRetryState? state))
            return;
        if (raw != null
            && !string.Equals(
                state.Fingerprint,
                BuildDirectCmtFailureFingerprint(raw),
                StringComparison.Ordinal))
            return;
        ((ICollection<KeyValuePair<string, DirectCmtRetryState>>)
            _directCmtRetryStates).Remove(
                new KeyValuePair<string, DirectCmtRetryState>(portName, state));
    }

    private bool TryQuarantineFailedDirectCmt(
        string portName,
        string raw,
        string reason)
    {
        DirectCmtRetryState state = ObserveDirectCmtFailure(portName, raw);
        if (!ShouldQuarantineDirectCmt(
            state.Attempts,
            DateTimeOffset.UtcNow - state.FirstSeenUtc,
            state.MaxObservedChars))
            return false;

        return TryWriteAndCommitDirectCmtQuarantine(
            portName, raw, reason, state);
    }

    private bool TryQuarantinePendingDirectCmt(
        string portName,
        string data,
        bool commandPending,
        out DirectCmtFrame quarantined)
    {
        quarantined = FindPendingDirectCmtCandidate(data, commandPending);
        if (quarantined.Length <= 0) return false;

        // With a quoted text-mode header, one final OK/ERROR while a command is
        // pending is intentionally ambiguous: it can be the entire SMS body.
        // Wait for the command's own timeout/a second terminator instead of
        // quarantining a valid short message. PDU mode has no such ambiguity;
        // its body must decode as hex and can use the bounded failure path.
        if (commandPending
            && quarantined.Raw.Length < DirectCmtMaxPendingChars
            && Regex.IsMatch(
                quarantined.Raw,
                "^\\+CMT:\\s*\"",
                RegexOptions.IgnoreCase))
        {
            quarantined = default;
            return false;
        }

        DirectCmtRetryState state = ObserveDirectCmtFailure(
            portName, quarantined.Raw);
        if (!ShouldQuarantineDirectCmt(
            state.Attempts,
            DateTimeOffset.UtcNow - state.FirstSeenUtc,
            state.MaxObservedChars))
        {
            quarantined = default;
            return false;
        }

        string reason = state.MaxObservedChars >= DirectCmtMaxPendingChars
            ? "pending-frame-size-limit"
            : "pending-frame-retry-limit";
        if (TryWriteAndCommitDirectCmtQuarantine(
            portName, quarantined.Raw, reason, state))
            return true;

        quarantined = default;
        return false;
    }

    private bool TryWriteAndCommitDirectCmtQuarantine(
        string portName,
        string raw,
        string reason,
        DirectCmtRetryState state)
    {
        try
        {
            string path = WriteDirectCmtQuarantine(
                portName, raw, reason, state.Attempts);
            ClearDirectCmtFailure(portName, raw);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_DIRECT_QUARANTINED] reason={reason}; attempts={state.Attempts}; chars={raw.Length}; file={path}. Khung há»ng Ä‘Ã£ lÆ°u bá»n vá»¯ng vÃ  bá» khá»i buffer."
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or JsonException)
        {
            // Never discard the only raw copy unless the quarantine append was
            // flushed successfully. A storage failure therefore remains
            // retryable even after the decode/time budget is exhausted.
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_DIRECT_QUARANTINE_WRITE_FAILED] {ex.Message}; giá»¯ nguyÃªn raw +CMT."
            });
            ScheduleDirectCmtRetry(portName, 2000);
            return false;
        }
    }

    private string WriteDirectCmtQuarantine(
        string portName,
        string raw,
        string reason,
        int attempts)
    {
        string directory = StableSmsDataDirectory;
        string path = Path.Combine(directory, DirectCmtQuarantineFileName);
        var record = new DirectCmtQuarantineRecord(
            DateTimeOffset.UtcNow,
            portName,
            reason,
            attempts,
            raw.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
                .ToLowerInvariant(),
            raw);
        byte[] payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(record) + Environment.NewLine);

        lock (_directCmtQuarantineGate)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(path)
                && new FileInfo(path).Length + payload.Length
                    > DirectCmtQuarantineMaxBytes)
            {
                for (int archive = DirectCmtQuarantineArchiveCount;
                     archive >= 1;
                     archive--)
                {
                    string source = archive == 1
                        ? path
                        : path + $".{archive - 1}";
                    string destination = path + $".{archive}";
                    if (File.Exists(destination)) File.Delete(destination);
                    if (File.Exists(source)) File.Move(source, destination);
                }
            }

            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        return path;
    }

    private bool TryProcessDirectCmtFrame(
        string portName,
        string rawDirect)
    {
        DecodedSmsBody decoded = DecodeDirectCmtFrame(rawDirect);
        if (string.IsNullOrWhiteSpace(decoded.Content))
        {
            if (TryQuarantineFailedDirectCmt(
                portName,
                rawDirect,
                "complete-frame-undecodable"))
                return true;

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[SMS_DIRECT_WAITING_DECODE] Khung +CMT chưa giải mã hoàn chỉnh; giữ nguyên bộ đệm và thử lại."
            });
            ScheduleDirectCmtRetry(portName);
            return false;
        }

        string sender = ParseSenderFromCmgr(rawDirect);
        if (sender == "Unknown" && !string.IsNullOrWhiteSpace(decoded.Sender))
            sender = DecodeSmsSender(decoded.Sender);
        if (string.IsNullOrWhiteSpace(sender))
            sender = "Unknown";

        long generation = CurrentSmsGeneration(portName);
        string scope = TryGetSmsScope(portName, generation, out string simScope)
            ? simScope
            : portName;
        string normalizedRaw = NormalizeStoredSmsForIdentity(rawDirect);

        if (decoded.Concatenation == null)
        {
            string deliveryId = BuildDeliveryId(
                "direct",
                scope,
                sender,
                normalizedRaw);
            var single = new SmsConcatInfo(
                BuildStableDirectReference(deliveryId),
                1,
                1);
            try
            {
                _multipartJournal.RecordAndGetParts(
                    scope,
                    sender,
                    single,
                    decoded.Content,
                    portName: portName,
                    partIdentity: deliveryId,
                    messageIdHint: deliveryId);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_DIRECT_WAL_ERROR] Chưa lưu được +CMT đơn: {ex.Message}. Giữ nguyên khung nhận."
                });
                ScheduleDirectCmtRetry(portName);
                return false;
            }

            bool singleAlreadyAccepted = _multipartJournal.IsDeliveryAcknowledged(
                    deliveryId)
                || _deliveredStoredSms.ContainsKey(deliveryId);
            bool accepted = singleAlreadyAccepted || DispatchDecodedSms(
                portName,
                sender,
                decoded.Content,
                deliveryId);
            if (!accepted)
            {
                // The WAL now owns the only direct-delivery copy. Release this
                // serial frame so later burst messages cannot be blocked.
                ScheduleCompletedMultipartReplay(scope, portName, 1000);
                return true;
            }

            try
            {
                if (!singleAlreadyAccepted)
                    _multipartJournal.MarkDeliveryAcknowledged(deliveryId);
                RememberDeliveredSms(deliveryId);
                _multipartJournal.Complete(deliveryId);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_DIRECT_WAL_CLEANUP_RETRY] delivery={deliveryId}: {ex.Message}"
                });
                ScheduleCompletedMultipartReplay(scope, portName, 1000);
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_DELIVERY] direct delivery={deliveryId} sender={sender} chars={decoded.Content.Length} otp={ExtractOtp(decoded.Content) ?? string.Empty}"
            });
            return true;
        }

        string partIdentity = BuildDeliveryId(
            "direct-part",
            scope,
            sender,
            normalizedRaw);
        IReadOnlyList<SmsMultipartJournal.Part> parts;
        string messageId;
        try
        {
            parts = _multipartJournal.RecordAndGetParts(
                scope,
                sender,
                decoded.Concatenation,
                decoded.Content,
                portName: portName,
                partIdentity: partIdentity);
            messageId = _multipartJournal.GetMessageIdForPartIdentity(
                scope,
                partIdentity);
            if (string.IsNullOrWhiteSpace(messageId))
                throw new InvalidDataException(
                    "Direct multipart segment has no durable delivery identity.");
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[MULTIPART_JOURNAL_ERROR] Không lưu được +CMT phần {decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}: {ex.Message}. Giữ nguyên khung nhận."
            });
            ScheduleDirectCmtRetry(portName);
            return false;
        }

        bool complete = parts.Count == decoded.Concatenation.Total
            && Enumerable.Range(1, decoded.Concatenation.Total)
                .SequenceEqual(parts.Select(part => part.Sequence));
        if (!complete)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[MULTIPART] direct sender={sender} ref={decoded.Concatenation.Reference} seq={decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}; phần đã lưu bền vững."
            });
            return true;
        }

        string content = string.Concat(parts.Select(part => part.Content));
        bool alreadyAccepted = _multipartJournal.IsDeliveryAcknowledged(messageId)
            || _deliveredStoredSms.ContainsKey(messageId);
        bool deliveryAccepted = alreadyAccepted || DispatchDecodedSms(
            portName,
            sender,
            content,
            messageId);
        if (!deliveryAccepted)
        {
            // Every part is durable now, so release the volatile serial frame
            // and replay from disk instead of blocking later burst traffic.
            ScheduleCompletedMultipartReplay(scope, portName, 1000);
            return true;
        }

        try
        {
            if (!alreadyAccepted)
                _multipartJournal.MarkDeliveryAcknowledged(messageId);
            RememberDeliveredSms(messageId);
            _multipartJournal.Complete(messageId);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            // The durable inbox already owns this delivery. Replay/cleanup is
            // idempotent by MessageId, so consuming the CMT frame remains safe.
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[MULTIPART_JOURNAL_WARN] Inbox đã nhận +CMT nhưng journal cần thử dọn lại: {ex.Message}"
            });
            ScheduleCompletedMultipartReplay(scope, portName, 1000);
        }

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[SMS_DELIVERY] direct multipart delivery={messageId} sender={sender} chars={content.Length} otp={ExtractOtp(content) ?? string.Empty}"
        });
        return true;
    }

    private async Task<string> ReadUntilAsync(SerialPort sp, string keyword, int timeoutMs)
    {
        string current = "";
        DateTime start = DateTime.Now;
        while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
        {
            if (sp.BytesToRead > 0)
            {
                current += sp.ReadExisting();
                if (current.Contains(keyword)) return current;
            }
            await Task.Delay(10);
        }
        return current;
    }

    private void HandleDataReceived(string portName, SerialPort sp)
    {
        // SerialPort may raise overlapping DataReceived callbacks. StringBuilder,
        // frame removal and command completion must be atomic per COM or one
        // callback can overwrite/remove bytes still needed by another callback.
        object gate = _portBufferLocks.GetOrAdd(portName, static _ => new object());
        lock (gate)
        {
            HandleDataReceivedCore(portName, sp);
        }
    }

    private void HandleDataReceivedCore(string portName, SerialPort sp)
    {
        if (_isDownloading.TryGetValue(portName, out var isDown) && isDown) return;

        try
        {
            string chunk = sp.ReadExisting();
            while (sp.BytesToRead > 0)
            {
                Thread.Sleep(10);
                chunk += sp.ReadExisting();
            }

            bool idleBufferRetry = chunk.Length == 0;
            if (!_portBuffers.TryGetValue(portName, out var buffer)) return;
            // A scheduled direct-CMT retry may intentionally run with no new
            // serial bytes while a complete frame is still in the buffer.
            if (string.IsNullOrWhiteSpace(chunk) && buffer.Length == 0) return;
            if (!string.IsNullOrEmpty(chunk)) buffer.Append(chunk);
            
            string currentData = buffer.ToString();
            
            // Buffer giới hạn 32 KB — đủ chứa cả PDU SMS Unicode dài nhất (thường < 600 hex chars/phần)
            // Không reset buffer khi còn dữ liệu hợp lệ đang được xử lý. Chỉ reset khi thực sự overflow.
            if (buffer.Length > 32000)
            {
                // Cuu cac +CMTI chua xu ly truoc khi xoa buffer de khong miss SMS
                var salvageCmti = Regex.Matches(buffer.ToString(), @"\+CMTI:\s*(?:""[^""]*""|[^,\r\n]+)\s*,\s*(\d+)");
                bool hasPendingDirect = currentData.Contains(
                    "+CMT:", StringComparison.OrdinalIgnoreCase);
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = hasPendingDirect
                    ? $"[WARNING] Buffer lớn ({buffer.Length} chars) còn +CMT chưa bàn giao; xử lý trước, không xóa dữ liệu SMS."
                    : $"[WARNING] Buffer overflow ({buffer.Length} chars) - đang làm sạch; cứu {salvageCmti.Count} CMTI." });
                if (!hasPendingDirect)
                {
                    buffer.Clear();
                    currentData = "";
                    foreach (Match m in salvageCmti)
                        QueueStoredSmsRead(portName, m.Groups[1].Value);
                    if (salvageCmti.Count == 0)
                        ScheduleSafeUnreadSmsSweep(portName, "serial-buffer-overflow");
                }
            }

            Match smsMemoryError = SmsMemoryFullRegex.Match(currentData);
            if (smsMemoryError.Success)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SMS_MEMORY_RECOVERY] Bộ nhớ SMS báo '{smsMemoryError.Value.Trim()}'. Đang quét và chỉ xóa từng slot sau khi đã lưu bền vững." });
                ScheduleSafeUnreadSmsSweep(portName, "sim-memory-full");
                // A pending command must receive its actual CMS error instead
                // of timing out. Unsolicited memory-full URCs can be consumed.
                if (!_commandTcs.ContainsKey(portName))
                {
                    buffer.Replace(smsMemoryError.Value, "");
                    currentData = buffer.ToString();
                }
            }
            
            // Bắt trạng thái mạng URC
            string pendingRegistrationCommand = _commandTcs.TryGetValue(portName, out var pendingRegistrationTcs)
                ? pendingRegistrationTcs.Task.AsyncState as string ?? string.Empty
                : string.Empty;
            var regMatches = Regex.Matches(
                currentData,
                @"\+(C(?:G|E)?REG):\s*(?<first>[0-9])(?:\s*,\s*(?<second>[0-9]))?(?:[^\r\n]*)");
            if (regMatches.Count > 0)
            {
                foreach (Match match in regMatches)
                {
                    string regType = match.Groups[1].Value;
                    bool isRequestedResponse = pendingRegistrationCommand.Equals(
                        $"AT+{regType}?", StringComparison.OrdinalIgnoreCase);
                    // Query: +CREG: <n>,<stat>; URC: +CREG: <stat>[,...]. Trước đây luôn lấy
                    // chữ số đầu nên có thể báo nhầm <n>=1 là "đã đăng ký CS".
                    string stat = isRequestedResponse && match.Groups["second"].Success
                        ? match.Groups["second"].Value
                        : match.Groups["first"].Value;
                    if (stat == "1" || stat == "5")
                    {
                        string netName = regType switch
                        {
                            "CGREG" => "PS (Data 3G)",
                            "CEREG" => "EPS (Data 4G LTE)",
                            _ => "CS (Thoại/2G)"
                        };
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_REG] Đã đăng ký mạng {netName}" });
                    }
                    if (!isRequestedResponse)
                        buffer.Replace(match.Value, "");
                }
                currentData = buffer.ToString();
            }

            // Luồng dev xử lý trực tiếp +CLIP/NO CARRIER ở bên dưới. Không lấy URC
            // ra khỏi buffer trước luồng này, nếu không UI sẽ không nhận được cuộc gọi.

            // ---------------------------------------------------------
            // 1. ƯU TIÊN SỐ 1: BẮT TIN NHẮN XEN NGANG (URC)
            // (Luôn quét tin nhắn đến trước, bất kể có lệnh nào đang chạy)
            // ---------------------------------------------------------
            if (currentData.Contains("+CMTI:"))
            {
                var matches = Regex.Matches(currentData, @"\+CMTI:\s*(?:""[^""]*""|[^,\r\n]+)\s*,\s*(\d+)");
                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        string msgIndex = match.Groups[1].Value;
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Phát hiện tin nhắn ở vị trí {msgIndex}, đang đọc..." });
                        
                        // Cắt bỏ phần thông báo này khỏi bộ đệm để không xử lý lại
                        buffer.Replace(match.Value, ""); 
                    }
                    currentData = buffer.ToString();
                    
                    // CMTI và CMGL cùng đi qua một hàng đợi duy nhất theo COM để không đọc/xóa
                    // trùng index hoặc đảo thứ tự các đoạn của cùng một tin dài.
                    foreach (Match match in matches) QueueStoredSmsRead(portName, match.Groups[1].Value);
                }
            }

            // Some firmware only supports CNMI direct-delivery mode (2,2) and emits
            // +CMT followed by the message body instead of storing an index and sending
            // +CMTI. Consume complete +CMT frames here so those messages are not lost.
            if (currentData.Contains("+CMT:"))
            {
                // Do not use an end-of-buffer ($) as a frame terminator here.  A
                // SerialPort DataReceived callback is allowed to split the CMT
                // header/body across several chunks; treating the first chunk as a
                // complete frame used to publish a truncated OTP and then remove
                // the still-incomplete bytes from the buffer.  Only consume a
                // direct frame when the modem supplied an explicit terminator
                // (OK/ERROR or the next URC line).  A text-mode direct CMT
                // has no OK on some firmware, so a final CRLF is accepted only
                // when the body is already complete and there is no pending
                // command response to be joined to it.
                // A poisoned first frame must not strand a later valid CMT or a
                // pending AT response. Make bounded passes: consume durable
                // deliveries, quarantine an over-budget malformed prefix, then
                // immediately continue with the remainder of the same buffer.
                for (int pass = 0;
                     pass < 64
                     && currentData.Contains(
                         "+CMT:", StringComparison.OrdinalIgnoreCase);
                     pass++)
                {
                    bool commandPending = _commandTcs.ContainsKey(portName);
                    IReadOnlyList<DirectCmtFrame> directMatches =
                        ExtractCompleteDirectCmtFrames(
                            currentData,
                            commandPending,
                            allowIdleEndOfBuffer: idleBufferRetry);
                    var consumedDirectFrames = new List<DirectCmtFrame>();
                    bool decodeRetryPending = false;
                    foreach (DirectCmtFrame direct in directMatches)
                    {
                        if (TryProcessDirectCmtFrame(portName, direct.Raw))
                        {
                            consumedDirectFrames.Add(direct);
                            ClearDirectCmtFailure(portName, direct.Raw);
                        }
                        else
                        {
                            decodeRetryPending = true;
                        }
                    }

                    // Remove from the end so offsets calculated against
                    // currentData remain valid for every frame in this pass.
                    foreach (DirectCmtFrame direct in
                             consumedDirectFrames.OrderByDescending(x => x.Start))
                    {
                        if (direct.Start >= 0 && direct.Length > 0
                            && direct.Start + direct.Length <= buffer.Length)
                            buffer.Remove(direct.Start, direct.Length);
                    }
                    currentData = buffer.ToString();

                    // A complete but undecodable frame owns its retry schedule.
                    // Do not count it twice as an incomplete frame in this pass.
                    if (decodeRetryPending) break;
                    if (consumedDirectFrames.Count > 0) continue;

                    if (TryQuarantinePendingDirectCmt(
                        portName,
                        currentData,
                        commandPending,
                        out DirectCmtFrame quarantined))
                    {
                        if (quarantined.Start >= 0
                            && quarantined.Length > 0
                            && quarantined.Start + quarantined.Length
                                <= buffer.Length)
                        {
                            buffer.Remove(
                                quarantined.Start,
                                quarantined.Length);
                            currentData = buffer.ToString();
                            continue;
                        }
                    }

                    ScheduleDirectCmtRetry(portName);
                    break;
                }
            }

            // Xử lý kết quả quét AT+CMGL="REC UNREAD"
            // Định dạng: +CMGL: <index>,"REC UNREAD",...
            if (currentData.Contains("+CMGL:"))
            {
                string pendingCommand = _commandTcs.TryGetValue(
                        portName,
                        out TaskCompletionSource<string>? pendingCmglTcs)
                    ? pendingCmglTcs.Task.AsyncState as string ?? string.Empty
                    : string.Empty;
                CmglRoutingResult routing = RouteCmglData(
                    currentData,
                    pendingCommand);
                if (routing.Indices.Count > 0)
                {
                    foreach (string msgIndex in routing.Indices)
                    {
                        // Chỉ log nếu không trùng với các index đang đọc từ +CMTI
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[Sweep] Đã vét được tin nhắn kẹt ở vị trí {msgIndex}, đang đọc..." });
                    }

                    if (!routing.PreservedForPendingCommand)
                    {
                        buffer.Clear();
                        buffer.Append(routing.CommandResponseData);
                        currentData = buffer.ToString();
                    }

                    foreach (string msgIndex in routing.Indices)
                        QueueStoredSmsRead(portName, msgIndex);
                }
            }

            // ---------------------------------------------------------
            // 1.2 BẮT CUỘC GỌI ĐẾN VÀ KẾT THÚC
            // ---------------------------------------------------------
            HandleIncomingCallUrcs(portName, ref currentData, buffer);

            if (currentData.Contains("NO CARRIER"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "NO CARRIER");
                _ = OnIncomingCallEnded(portName);
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "NO CARRIER" });
                buffer.Replace("NO CARRIER", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("BUSY"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "BUSY");
                _ = OnIncomingCallEnded(portName);
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "BUSY" });
                buffer.Replace("BUSY", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("NO ANSWER"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "NO ANSWER");
                _ = OnIncomingCallEnded(portName);
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "NO ANSWER" });
                buffer.Replace("NO ANSWER", "");
                currentData = buffer.ToString();
            }
            
            // ---------------------------------------------------------
            // 1.3. BẮT TÍN HIỆU PHÍM BẤM DTMF (+QTONEDET)
            // ---------------------------------------------------------
            if (currentData.Contains("+QTONEDET:"))
            {
                var dtmfMatch = Regex.Match(currentData, @"\+QTONEDET:\s*(\d+)");
                if (dtmfMatch.Success)
                {
                    string dtmfCode = dtmfMatch.Groups[1].Value;
                    string dtmfChar = dtmfCode;
                    if (int.TryParse(dtmfCode, out int asciiVal))
                    {
                        if (asciiVal >= 48 && asciiVal <= 57)
                        {
                            dtmfChar = ((char)asciiVal).ToString();
                        }
                        else if (asciiVal == 42)
                        {
                            dtmfChar = "*";
                        }
                        else if (asciiVal == 35)
                        {
                            dtmfChar = "#";
                        }
                        else if (asciiVal >= 65 && asciiVal <= 68)
                        {
                            dtmfChar = ((char)asciiVal).ToString();
                        }
                    }
                    
                    DtmfReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = dtmfChar });
                    buffer.Replace(dtmfMatch.Value, "");
                    currentData = buffer.ToString();
                }
            }

            // ---------------------------------------------------------
            // 1.4. BẮT RÚT SIM VÀ CẮM SIM (HOT-PLUG)
            // ---------------------------------------------------------
            string pendingSimCommand = _commandTcs.TryGetValue(portName, out var pendingSimTcs)
                ? pendingSimTcs.Task.AsyncState as string ?? string.Empty
                : string.Empty;
            bool isQsimQueryResponse = pendingSimCommand.StartsWith("AT+QSIMSTAT?", StringComparison.OrdinalIgnoreCase);
            bool isCpinQueryResponse = pendingSimCommand.StartsWith("AT+CPIN?", StringComparison.OrdinalIgnoreCase);

            if (currentData.Contains("+QSIMSTAT: 1,1") && !isQsimQueryResponse)
            {
                buffer.Replace("+QSIMSTAT: 1,1", "");
                currentData = buffer.ToString();
                CancelSimRemovalConfirmation(portName);
                
                _lastSimState.TryGetValue(portName, out bool lastState);
                if (!lastState)
                {
                    _lastSimState[portName] = true;
                    // Khởi động luồng đọc CCID và IMEI, sau đó báo UI
                    _ = HandleSimInsertedSafelyAsync(portName);
                }
            }

            bool stackDisabledByTool = _simStackDisabledByTool.TryGetValue(portName, out bool stackDisabled) && stackDisabled;
            // NOT READY is a normal transient response on EC20 during CFUN/IMS
            // changes. Only NOT INSERTED is strong unsolicited removal evidence;
            // the periodic monitor will independently verify weaker QSIMSTAT=0.
            bool hasUnsolicitedCpinRemoval = !stackDisabledByTool && !isCpinQueryResponse
                && currentData.Contains("+CPIN: NOT INSERTED");
            bool hasUnsolicitedQsimRemoval = !stackDisabledByTool && !isQsimQueryResponse && currentData.Contains("+QSIMSTAT: 1,0");
            if (hasUnsolicitedCpinRemoval || hasUnsolicitedQsimRemoval)
            {
                if (_rebootRecoveryInProgress.ContainsKey(portName))
                {
                    buffer.Replace("+CPIN: NOT READY", "");
                    buffer.Replace("+CPIN: NOT INSERTED", "");
                    buffer.Replace("+QSIMSTAT: 1,0", "");
                    currentData = buffer.ToString();
                }
                else
                {
                    buffer.Replace("+CPIN: NOT READY", "");
                    buffer.Replace("+CPIN: NOT INSERTED", "");
                    buffer.Replace("+QSIMSTAT: 1,0", "");
                    currentData = buffer.ToString();

                // AT+QSIMSTAT? cũng trả "+QSIMSTAT: 1,0" như response. Chỉ xử lý rút SIM
                // khi trạng thái trước đó thực sự là có SIM; nếu không sẽ tự restart polling
                // mỗi 2 giây và không bao giờ chạy được probe CFUN=1.
                    _lastSimState.TryGetValue(portName, out bool wasPresent);
                    if (wasPresent)
                    {
                        // QSIMSTAT=0 is only a probe on some GSM boards (and can
                        // be inverted/transient). Do not spend a confirmation cycle
                        // on it; the polling monitor will re-read CPIN/QSIMSTAT/CCID.
                        if (hasUnsolicitedCpinRemoval)
                            RegisterSimRemovalEvidence(portName);
                        else
                        {
                            // Mark only that a probe needs verification. The
                            // confirmation counter is advanced by full polling
                            // cycles, not by every unsolicited URC.
                            _simRemovalEvidenceSince.TryAdd(portName, DateTimeOffset.UtcNow);
                            _simRemovalEvidenceCounts.TryAdd(portName, 1);
                        }
                        ScheduleSimRemovalConfirmation(portName);
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = hasUnsolicitedCpinRemoval
                                ? "[SIM_REMOVAL_PENDING] Modem báo mất SIM; đang xác minh lại trước khi đổi trạng thái."
                                : "[SIM_REMOVAL_PROBE] QSIMSTAT báo SIM chưa sẵn sàng; giữ dữ liệu và chờ xác minh CCID."
                        });
                    }
                }
            }

            // ---------------------------------------------------------
            // 1.5. BẮT KẾT QUẢ USSD (+CUSD)
            // ---------------------------------------------------------
            if (currentData.Contains("+CUSD:"))
            {
                var match = Regex.Match(currentData, @"\+CUSD:\s*\d+,""[\s\S]*?""(,\d+)?\r?\n?|\+CUSD:\s*\d+\r?\n?");
                if (match.Success)
                {
                    string ussdData = match.Value;
                    if (_commandTcs.TryGetValue(portName, out var t) && t.Task.AsyncState is string c
                        && c.StartsWith("AT+CUSD=1", StringComparison.OrdinalIgnoreCase))
                    {
                        // +CUSD is both the completion payload for the pending command and an
                        // unsolicited modem event consumed by MainViewModel.  Previously this
                        // branch completed the command without publishing the event, so the UI
                        // never parsed the phone number/activation date even though the modem
                        // had returned them successfully.
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = ussdData.Trim()
                        });
                        t.TrySetResult(currentData.Substring(0, match.Index + match.Length).Trim());
                        buffer.Remove(0, match.Index + match.Length);
                    }
                    else
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = ussdData.Trim() });
                        buffer.Replace(ussdData, "");
                    }
                    currentData = buffer.ToString();
                }
            }

            // ---------------------------------------------------------
            // 2. XỬ LÝ LỆNH TỪ PHẦN MỀM ĐANG GỬI XUỐNG (TCS)
            // ---------------------------------------------------------
            bool hasUndeliveredDirectCmt = currentData.Contains(
                "+CMT:", StringComparison.OrdinalIgnoreCase);
            if (_commandTcs.TryGetValue(portName, out var tcs))
            {
                if (hasUndeliveredDirectCmt)
                {
                    // Never let a command terminator consume the prefix that
                    // contains a direct SMS whose durable inbox hand-off failed.
                    ScheduleDirectCmtRetry(portName);
                }
                else
                {
                // Kiểm tra dấu hiệu kết thúc của lệnh AT (OK, ERROR, hoặc CMS/CME ERROR, hoặc dấu nhắc >, hoặc CONNECT)
                var match = Regex.Match(currentData, @"(?:\r?\nOK\r?\n?|\r?\nERROR\r?\n?|\+CMS ERROR:[^\r\n]*\r?\n?|\+CME ERROR:[^\r\n]*\r?\n?|>\s*|\r?\nCONNECT\r?\n?)");
                if (match.Success)
                {
                    if (tcs.Task.AsyncState is string cmd
                        && cmd.StartsWith("AT+CUSD=1", StringComparison.OrdinalIgnoreCase))
                    {
                        // CUSD=1 is asynchronous: OK only acknowledges that the
                        // modem accepted the request. Release the per-COM command
                        // lock at that point; a later +CUSD is handled below as an
                        // unsolicited event and still reaches MainViewModel.
                        bool ackOnlyCompleted = false;
                        if (!currentData.Contains("+CUSD:") &&
                            !currentData.Contains("ERROR") &&
                            !currentData.Contains("+CME ERROR") &&
                            !currentData.Contains("+CMS ERROR"))
                        {
                            int ackEndIndex = match.Index + match.Length;
                            tcs.TrySetResult(currentData.Substring(0, ackEndIndex));
                            buffer.Remove(0, ackEndIndex);
                            currentData = buffer.ToString();
                            ackOnlyCompleted = true;
                        }

                        // VNSKY có lỗi gửi "+CME ERROR: 100" trước "+CUSD:"
                        if (!ackOnlyCompleted && currentData.Contains("+CME ERROR: 100"))
                        {
                            buffer.Replace("+CME ERROR: 100", ""); 
                            currentData = buffer.ToString();
                        }
                        else if (!ackOnlyCompleted)
                        {
                            int endIndex = match.Index + match.Length;
                            tcs.TrySetResult(currentData.Substring(0, endIndex));
                            buffer.Remove(0, endIndex);
                            currentData = buffer.ToString();
                        }
                    }
                    else
                    {
                        int endIndex = match.Index + match.Length;
                        tcs.TrySetResult(currentData.Substring(0, endIndex));
                        buffer.Remove(0, endIndex);
                        currentData = buffer.ToString();
                    }
                }
                }
            }
            // ---------------------------------------------------------
            // 3. DỌN DẸP RÁC BỘ ĐỆM AN TOÀN
            // ---------------------------------------------------------
            else
            {
                if (hasUndeliveredDirectCmt)
                {
                    // The raw direct frame is the only remaining copy until
                    // the inbox or multipart journal accepts it.
                    ScheduleDirectCmtRetry(portName);
                }
                else
                {
                // Chỉ xóa bộ đệm khi thiết bị nhả rác có chữ OK/ERROR chuẩn
                var match = Regex.Match(currentData, @"(?:\r?\nOK\r?\n?|\r?\nERROR\r?\n?|\+CMS ERROR:[^\r\n]*\r?\n?|\+CME ERROR:[^\r\n]*\r?\n?|>\s*)");
                if (match.Success)
                {
                    buffer.Remove(0, match.Index + match.Length);
                    currentData = buffer.ToString();
                }
                // Nếu bị nhiễu sóng, dữ liệu rác dồn quá nhiều thì xóa để chống tràn RAM
                else if (currentData.Length > 2000) 
                {
                    buffer.Clear();
                    currentData = "";
                }
                }
            }
        }
        catch (IOException)
        {
            Disconnect(portName);
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Bị rút cáp USB đột ngột!" });
        }
        catch (UnauthorizedAccessException)
        {
            Disconnect(portName);
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Mất quyền truy cập COM Port!" });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi không xác định: {ex.Message}" });
        }
    }

    public void DisconnectAll()
    {
        foreach (var cts in _pollingCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var cts in _keepAliveCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var cts in _portHealthCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var cts in _simMonitorCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var cts in _simRemovalConfirmationCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var cts in _portLifetimeCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var pending in _commandTcs.Values)
            pending.TrySetResult("ERROR: Port disconnected");
        foreach (var kvp in _serialPorts)
        {
            try
            {
                kvp.Value.Close();
                kvp.Value.Dispose();
            }
            catch { }
        }
        // Không dispose semaphore đang có thể được SendCommandAsync.Release() trong finally.
        _serialPorts.Clear();
        _semaphores.Clear();
        _portBuffers.Clear();
        _portBufferLocks.Clear();
        _commandTcs.Clear();
        _connectionErrors.Clear();
        _sleepingPorts.Clear();
        _portVendors.Clear();
        _modemProfiles.Clear();
        _pollingCts.Clear();
        _pollingExpectedIdentities.Clear();
        _pendingNetworkPollingPorts.Clear();
        _keepAliveCts.Clear();
        _portHealthCts.Clear();
        _portHealthRecoveryOwners.Clear();
        _portHealthFailureCounts.Clear();
        _simMonitorCts.Clear();
        _simRemovalConfirmationCts.Clear();
        _simRemovalWatchEnabled.Clear();
        _lastSimState.Clear();
        _simRemovalEvidenceCounts.Clear();
        _simRemovalEvidenceSince.Clear();
        _rebootRecoveryInProgress.Clear();
        _networkSimRecoveryOwners.Clear();
        _networkSimRecoveryAttempts.Clear();
        _simInitInProgress.Clear();
        _simInsertInProgress.Clear();
        _portLifetimeCts.Clear();
        _dataReceivedHandlers.Clear();
        _isDownloading.Clear();
        _incomingCalls.Clear();
        _incomingCallNotifications.Clear();
        foreach (var signal in _outgoingCallEndSignals.Values)
            signal.TrySetResult("Port disconnected");
        _outgoingCallEndSignals.Clear();
        foreach (SmsReadQueueState state in _smsReadQueues.Values)
        {
            try { state.Cancellation.Cancel(); } catch { }
            state.Queue.Writer.TryComplete();
        }
        _smsReadQueues.Clear();
        _queuedSmsIndices.Clear();
        _smsRetryLogAt.Clear();
        _smsReadRetryAttempts.Clear();
        _smsSweepLocks.Clear();
        _smsSweepRetryOwners.Clear();
        _smsSimIdentities.Clear();
        _networkSimIdentities.Clear();
        _networkIdentityGenerations.Clear();
        _smsPortGenerations.Clear();
        _directCmtRetryOwners.Clear();
        _directCmtRetryStates.Clear();
        _multipartReplayOwners.Clear();
        _multipartCompletionRetryOwners.Clear();
        _multipartPartCleanupRetryOwners.Clear();
    }

    public void Disconnect(string portName)
    {
        _networkSimIdentities.TryRemove(portName, out _);
        _networkIdentityGenerations.TryRemove(portName, out _);
        InvalidateNetworkRecoveryForIdentityChange(portName);
        _incomingCalls.TryRemove(portName, out _);
        _incomingCallNotifications.TryRemove(portName, out _);
        if (_outgoingCallEndSignals.TryRemove(portName, out var callEndSignal))
            callEndSignal.TrySetResult("Port disconnected");
        InvalidateSmsQueueGeneration(portName);
        if (_smsSimIdentities.TryRemove(portName, out string? smsCcid))
            _multipartReplayOwners.TryRemove($"ccid:{smsCcid}", out _);
        _directCmtRetryOwners.TryRemove(portName, out _);
        _directCmtRetryStates.TryRemove(portName, out _);
        _multipartReplayOwners.TryRemove(portName, out _);

        if (_portLifetimeCts.TryRemove(portName, out var lifetimeCts))
        {
            try { lifetimeCts.Cancel(); lifetimeCts.Dispose(); } catch { }
        }
        if (_portHealthCts.TryRemove(portName, out var healthCts))
        {
            try { healthCts.Cancel(); healthCts.Dispose(); } catch { }
        }
        _portHealthRecoveryOwners.TryRemove(portName, out _);
        _portHealthFailureCounts.TryRemove(portName, out _);
        if (_serialPorts.TryGetValue(portName, out var sp))
        {
            try
            {
                sp.Close();
                sp.Dispose();
            }
            catch { }
            _serialPorts.TryRemove(portName, out _);
        }
        
        if (_semaphores.TryGetValue(portName, out var sem))
        {
            // Không Dispose ngay: một SendCommandAsync đang kết thúc có thể còn Release().
            // Sau khi xóa khỏi dictionary semaphore sẽ được GC thu hồi an toàn.
            _semaphores.TryRemove(portName, out _);
            if (_commandTcs.TryRemove(portName, out var pendingCommand))
                pendingCommand.TrySetResult("ERROR: Port disconnected");
            _connectionErrors.TryRemove(portName, out _);
            _dataReceivedHandlers.TryRemove(portName, out _);
            _isDownloading.TryRemove(portName, out _);
            _sleepingPorts.TryRemove(portName, out _);
            _portVendors.TryRemove(portName, out _);
            _modemProfiles.TryRemove(portName, out _);

            if (_pollingCts.TryRemove(portName, out var pCts))
            {
                try { pCts.Cancel(); pCts.Dispose(); } catch {}
            }
            if (_keepAliveCts.TryRemove(portName, out var kCts))
            {
                try { kCts.Cancel(); kCts.Dispose(); } catch {}
            }
            if (_simMonitorCts.TryRemove(portName, out var smCts))
            {
                try { smCts.Cancel(); smCts.Dispose(); } catch {}
            }
            _lastSimState.TryRemove(portName, out _);
            ClearSimRemovalEvidence(portName);
            _rebootRecoveryInProgress.TryRemove(portName, out _);
            ClearNetworkSimRecoveryState(portName);
            _simInitInProgress.TryRemove(portName, out _);
            _simInsertInProgress.TryRemove(portName, out _);
        }

        _portBuffers.TryRemove(portName, out _);
        _portBufferLocks.TryRemove(portName, out _);

        // Dọn cancellation state kể cả khi kết nối bị lỗi giữa chừng trước lúc tạo semaphore.
        if (_pollingCts.TryRemove(portName, out var polling)) { try { polling.Cancel(); polling.Dispose(); } catch { } }
        if (_keepAliveCts.TryRemove(portName, out var keepAlive)) { try { keepAlive.Cancel(); keepAlive.Dispose(); } catch { } }
        if (_portHealthCts.TryRemove(portName, out var health)) { try { health.Cancel(); health.Dispose(); } catch { } }
        if (_simMonitorCts.TryRemove(portName, out var simMonitor)) { try { simMonitor.Cancel(); simMonitor.Dispose(); } catch { } }
        _simRemovalWatchEnabled.TryRemove(portName, out _);
        CancelSimRemovalConfirmation(portName);
        _lastSimState.TryRemove(portName, out _);
        ClearSimRemovalEvidence(portName);
        _rebootRecoveryInProgress.TryRemove(portName, out _);
        ClearNetworkSimRecoveryState(portName);
        _simInitInProgress.TryRemove(portName, out _);
        _simInsertInProgress.TryRemove(portName, out _);
        if (_smsSweepLocks.TryRemove(portName, out _))
        {
            // Do not dispose here: a sweep already holding this lock may still
            // execute its finally/Release after the COM is disconnected.
        }
    }

    private bool EnsurePortOpen(string portName, out SerialPort? sp)
    {
        if (_serialPorts.TryGetValue(portName, out sp))
        {
            if (sp.IsOpen) return true;
            try
            {
                sp.Open();
                if (sp.IsOpen) return true;
                
                // NẾU Open() KHÔNG throw lỗi nhưng IsOpen VẪN false (Lỗi driver Windows ảo)
                Disconnect(portName);
                PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi ngầm: Không thể mở cổng dù driver không báo lỗi!" });
            }
            catch (Exception ex)
            {
                Disconnect(portName);
                PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Mất kết nối: {ex.Message}" });
            }
        }
        else
        {
            Disconnect(portName);
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Không tìm thấy kết nối cổng COM trong danh mục kết nối!" });
        }
        sp = null;
        return false;
    }

    public async Task<string> SendCommandAsync(
        string portName,
        string command,
        int timeoutMs = 5000,
        bool silent = false,
        CancellationToken ct = default)
    {
        if (Regex.IsMatch(command, @"^AT\+CFUN\s*=\s*[04](?:\D|$)", RegexOptions.IgnoreCase))
            _simStackDisabledByTool[portName] = true;
        else if (Regex.IsMatch(command, @"^AT\+CFUN\s*=\s*1(?:\D|$)", RegexOptions.IgnoreCase))
            _simStackDisabledByTool[portName] = false;

        // Kéo dài thời gian chờ cho các lệnh đặc biệt
        // CUSD=1 opens an asynchronous network session. CUSD=2 only closes it and
        // must complete immediately on OK instead of waiting for a +CUSD payload.
        if (command.StartsWith("AT+CUSD=1", StringComparison.OrdinalIgnoreCase))
        {
            // CUSD is asynchronous. HandleDataReceivedCore releases the command
            // on the transport ACK (OK); a later +CUSD is consumed as an URC, so
            // a silent network request cannot hold this COM's UART for 45 seconds.
            timeoutMs = Math.Max(timeoutMs, 10000);
        }
        else if (command.StartsWith("AT+CMGR", StringComparison.OrdinalIgnoreCase)
                 || command.StartsWith("AT+QCMGR", StringComparison.OrdinalIgnoreCase))
            timeoutMs = Math.Max(timeoutMs, 8000);

        if (!EnsurePortOpen(portName, out var sp) || sp == null)
        {
            return "ERROR: Port not open";
        }
        
        if (!_semaphores.TryGetValue(portName, out var semaphore))
        {
            return "ERROR: Semaphore missing";
        }

        bool lockAcquired = await semaphore.WaitAsync(timeoutMs, ct);
        if (!lockAcquired)
        {
            return "ERROR: Timeout waiting for lock";
        }

        var tcs = new TaskCompletionSource<string>(command, TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandTcs.TryAdd(portName, tcs))
        {
            semaphore.Release();
            return "ERROR: Another command is already in progress";
        }

        try
        {
            // Do not discard serial data here. EC20 can emit +CMTI/+CMT in the
            // short gap between AT commands; clearing either buffer drops OTPs.
            // HandleDataReceived already removes completed command frames.

            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {command}" });
            
            sp.Write(command + "\r\n");
            
            // Mỗi COM có TCS + semaphore riêng. Cancellation chỉ dừng lệnh của
            // COM hiện tại và nhả khóa ngay, không đợi hết timeout 30 giây.
            string finalResp;
            try
            {
                finalResp = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            }
            catch (TimeoutException)
            {
                tcs.TrySetCanceled();
                return "ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)";
            }

            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (IOException ex)
        {
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Cáp bị rút khi đang gửi lệnh!" });
            return $"ERROR: Rút cáp đột ngột - {ex.Message}";
        }
        finally
        {
            if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, tcs))
            {
                _commandTcs.TryRemove(portName, out _);
            }
            semaphore.Release();
        }
    }

    /// <summary>
    /// Gửi dữ liệu thô (raw data) không kèm \r\n, dùng để gửi URL hoặc Data binary sau khi nhận CONNECT/>
    /// </summary>
    public async Task<string> SendRawAsync(string portName, string data, int timeoutMs = 5000, bool silent = false)
    {
        if (!EnsurePortOpen(portName, out var sp) || sp == null)
        {
            return "ERROR: Port not open";
        }
        
        if (!_semaphores.TryGetValue(portName, out var semaphore))
        {
            return "ERROR: Semaphore missing";
        }

        bool lockAcquired = await semaphore.WaitAsync(timeoutMs);
        if (!lockAcquired)
        {
            return "ERROR: Timeout waiting for lock";
        }

        var tcs = new TaskCompletionSource<string>("RAW_DATA", TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandTcs.TryAdd(portName, tcs))
        {
            semaphore.Release();
            return "ERROR: Another command is already in progress";
        }

        try
        {
            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> [RAW] {data}" });
            
            sp.Write(data);
            
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                tcs.TrySetCanceled();
                return "ERROR: Timeout waiting for response after raw data";
            }
            
            string finalResp = await tcs.Task;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (IOException ex)
        {
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Cáp bị rút khi đang gửi lệnh!" });
            return $"ERROR: Rút cáp đột ngột - {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
        finally
        {
            if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, tcs))
            {
                _commandTcs.TryRemove(portName, out _);
            }
            semaphore.Release();
        }
    }
    // Giới hạn ký tự an toàn cho 1 đoạn SMS
    private const int MaxGsmPartLength = 160;
    private const int MaxGsmChunkBodyLength = 150;
    private const int MaxUcs2PartLength = 70;
    private const int MaxUcs2ChunkBodyLength = 60;
    private const int MinimumSmsPayloadTimeoutMs = 90_000;
    internal const string SmsPayloadSubmittedMarker = "[SMS_PAYLOAD_SUBMITTED]";

    internal static int GetSmsPayloadTimeoutMs(int requestedTimeoutMs) =>
        Math.Max(requestedTimeoutMs, MinimumSmsPayloadTimeoutMs);

    internal static bool IsCleanSmsRecoveryProbe(string response) =>
        Regex.IsMatch(response, @"(?:^|\r?\n)OK(?:\r?\n|$)", RegexOptions.IgnoreCase)
        && !response.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase)
        && !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    public async Task<string> SendSmsAsync(
        string portName,
        string phoneNumber,
        string message,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return "ERROR: SMS operation cancelled";
        if (!GsmDestination.TryNormalizeSms(phoneNumber, out phoneNumber))
            return "ERROR: Invalid SMS destination";

        // Kiểm tra xem message có ký tự nằm ngoài bảng mã GSM cơ bản hay không
        // (Sử dụng cách kiểm tra đơn giản: nếu có bất kỳ ký tự nào > 127 thì coi là Unicode)
        bool isGsm = (message ?? "").All(c => c <= 127);
        int maxLen = isGsm ? MaxGsmPartLength : MaxUcs2PartLength;
        int maxChunk = isGsm ? MaxGsmChunkBodyLength : MaxUcs2ChunkBodyLength;

        if (string.IsNullOrEmpty(message) || message.Length <= maxLen)
        {
            return await SendSmsPartAsync(portName, phoneNumber, message ?? "", isGsm, timeoutMs, ct);
        }

        var chunks = SplitMessageIntoChunks(message, maxChunk);
        int total = chunks.Count;
        var results = new List<string>();

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) return "ERROR: SMS operation cancelled";
            string partBody = $"[{i + 1}/{total}] {chunks[i]}";
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SMS_MULTIPART] Đang gửi đoạn {i + 1}/{total}..." });

            string resp = await SendSmsPartAsync(portName, phoneNumber, partBody, isGsm, timeoutMs, ct);
            results.Add(resp);

            if (resp.Contains("ERROR"))
            {
                return $"ERROR: Gửi thất bại ở đoạn {i + 1}/{total} - {resp}";
            }

            // Chờ 1.5s giữa các đoạn để mạng có thể nhận đúng thứ tự
            if (i < total - 1)
            {
                try { await Task.Delay(1500, ct); }
                catch (OperationCanceledException) { return "ERROR: SMS operation cancelled"; }
            }
        }

        return $"OK (Đã gửi {total} đoạn thành công)";
    }

    private static List<string> SplitMessageIntoChunks(string message, int maxBodyLength)
    {
        var chunks = new List<string>();
        int pos = 0;
        while (pos < message.Length)
        {
            int remaining = message.Length - pos;
            int len = Math.Min(maxBodyLength, remaining);

            if (len < remaining)
            {
                int lastSpace = message.LastIndexOf(' ', pos + len - 1, len);
                if (lastSpace > pos) len = lastSpace - pos;
            }

            chunks.Add(message.Substring(pos, len).Trim());
            pos += len;
        }
        return chunks;
    }

    private static bool IsSmsSetupFailure(string response) =>
        response.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(response, @"(?:^|\r?\n)ERROR(?:\r?\n|$)", RegexOptions.IgnoreCase)
        || response.Contains("+CMS ERROR:", StringComparison.OrdinalIgnoreCase)
        || response.Contains("+CME ERROR:", StringComparison.OrdinalIgnoreCase);

    private async Task<string> SendSmsPartAsync(
        string portName,
        string phoneNumber,
        string message,
        bool isGsm,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        if (!EnsurePortOpen(portName, out var sp) || sp == null) return "ERROR: Port not open";
        if (!_semaphores.TryGetValue(portName, out var semaphore)) return "ERROR: Semaphore missing";

        bool lockAcquired;
        try { lockAcquired = await semaphore.WaitAsync(timeoutMs, ct); }
        catch (OperationCanceledException) { return "ERROR: SMS operation cancelled"; }
        if (!lockAcquired) return "ERROR: Timeout waiting for lock";

        TaskCompletionSource<string>? tcs = null;
        bool payloadSubmitted = false;

        async Task<string> SendInnerAsync(
            string cmd,
            CancellationToken token = default,
            int commandTimeoutMs = 5000)
        {
            token.ThrowIfCancellationRequested();
            var innerTcs = new TaskCompletionSource<string>(cmd, TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, innerTcs))
                return "ERROR: Another command is already in progress";
            try
            {
                sp.Write(cmd + "\r");
                Task completed = await Task.WhenAny(innerTcs.Task, Task.Delay(commandTimeoutMs, token));
                token.ThrowIfCancellationRequested();
                if (completed != innerTcs.Task)
                {
                    innerTcs.TrySetCanceled();
                    return $"ERROR: Timeout configuring SMS with {cmd}";
                }
                return await innerTcs.Task;
            }
            finally
            {
                if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, innerTcs))
                    _commandTcs.TryRemove(portName, out _);
            }
        }

        async Task<(bool Recovered, string? LateSubmitConfirmation)> RecoverSmsChannelAsync()
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[SMS_RECOVERY] Quá hạn chờ phản hồi gửi; đang thoát chế độ nhập SMS và đồng bộ lại modem..."
            });

            // ESC chỉ hủy trạng thái nhập còn treo, không gửi lại payload nên không tạo SMS trùng.
            // Đăng ký probe ngay sau ESC để không bỏ mất +CMGS/OK nếu xác nhận đến muộn.
            try { if (sp.IsOpen) sp.Write("\x1B"); } catch { }

            int consecutiveCleanProbes = 0;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                string probeResponse = await SendInnerAsync("AT", CancellationToken.None, 2500);

                // Có modem trả +CMGS/OK ngay sau mốc timeout. Khi đó phải công nhận lần gửi
                // vừa rồi đã thành công thay vì báo lỗi và khiến người dùng gửi lại thủ công.
                if (probeResponse.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = "[SMS_RECOVERY] Modem trả xác nhận gửi muộn; SMS đã gửi thành công."
                    });
                    return (true, probeResponse.Trim());
                }

                if (IsCleanSmsRecoveryProbe(probeResponse))
                {
                    consecutiveCleanProbes++;
                    if (consecutiveCleanProbes >= 2)
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = "[SMS_RECOVERY] Đã đồng bộ lại modem; cổng sẵn sàng cho thao tác tiếp theo."
                        });
                        return (true, null);
                    }
                }
                else
                {
                    consecutiveCleanProbes = 0;
                }

                await Task.Delay(250);
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[SMS_RECOVERY_FAILED] Modem chưa về trạng thái lệnh AT sạch; hãy Refresh riêng cổng này trước khi gửi lại."
            });
            return (false, null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> AT+CMGS=\"{phoneNumber}\"" });

            string setupResponse = await SendInnerAsync("AT+CMGF=1", ct);
            if (IsSmsSetupFailure(setupResponse)) return setupResponse;
            
            if (isGsm)
            {
                setupResponse = await SendInnerAsync("AT+CSMP=17,167,0,0", ct);
                if (IsSmsSetupFailure(setupResponse)) return setupResponse;
                setupResponse = await SendInnerAsync("AT+CSCS=\"GSM\"", ct);
                if (IsSmsSetupFailure(setupResponse)) return setupResponse;
            }
            else
            {
                setupResponse = await SendInnerAsync("AT+CSMP=17,167,0,8", ct);
                if (IsSmsSetupFailure(setupResponse)) return setupResponse;
                setupResponse = await SendInnerAsync("AT+CSCS=\"UCS2\"", ct);
                if (IsSmsSetupFailure(setupResponse)) return setupResponse;
            }

            tcs = new TaskCompletionSource<string>("AT+CMGS", TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, tcs))
            {
                return "ERROR: Another command is already in progress";
            }

            sp.Write($"AT+CMGS=\"{phoneNumber}\"\r");

            int promptTimeoutMs = Math.Clamp(timeoutMs / 3, 5000, 10000);
            var timeoutTask = Task.Delay(promptTimeoutMs, ct);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetCanceled();
                // Abort text-entry mode before the outer SMS service retries. Without
                // ESC, a late prompt makes the next AT command part of the SMS body and
                // leaves the modem stuck until the following cooldown.
                try { if (sp.IsOpen) sp.Write("\x1B"); } catch { }
                await Task.Delay(200, ct);
                return "ERROR: Timeout waiting for > prompt";
            }

            string promptResp = await tcs.Task;
            if (!promptResp.Contains(">"))
            {
                return promptResp.Contains("ERROR") ? promptResp : $"ERROR: Modem rejected SMS with {promptResp}";
            }

            if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, tcs))
                _commandTcs.TryRemove(portName, out _);
            
            tcs = new TaskCompletionSource<string>("SMS_PAYLOAD", TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, tcs))
                return "ERROR: Another command is already in progress";

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {message}" });
            ct.ThrowIfCancellationRequested();

            if (isGsm)
            {
                sp.Write(message + "\x1A");
            }
            else
            {
                string hexMessage = BitConverter.ToString(Encoding.BigEndianUnicode.GetBytes(message)).Replace("-", "");
                sp.Write(hexMessage + "\x1A");
            }
            // SerialPort.Write returning after Ctrl+Z is the irreversible
            // ownership boundary. From this point, timeout/cancel/disconnect is
            // ambiguous and must never be retried automatically.
            payloadSubmitted = true;

            // Sau Ctrl+Z, nhà mạng/modem có thể cần lâu mới trả +CMGS/OK. Chờ tối thiểu
            // 90 giây; nếu vẫn quá hạn thì không retry vì SMS có thể đã được nhận.
            timeoutTask = Task.Delay(GetSmsPayloadTimeoutMs(timeoutMs), ct);
            completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetCanceled();

                // Gỡ TCS cũ trước khi phục hồi. Nếu để nguyên, ERROR/OK do ESC hoặc AT
                // có thể bị nhận nhầm thành kết quả của payload đã quá hạn.
                if (_commandTcs.TryGetValue(portName, out var pendingPayload)
                    && ReferenceEquals(pendingPayload, tcs))
                {
                    _commandTcs.TryRemove(portName, out _);
                }

                (bool recovered, string? lateSubmitConfirmation) = await RecoverSmsChannelAsync();
                if (!string.IsNullOrWhiteSpace(lateSubmitConfirmation))
                    return lateSubmitConfirmation;

                if (!recovered)
                    return $"ERROR: {SmsPayloadSubmittedMarker} Timeout sending SMS payload; SMS channel recovery failed";

                return $"ERROR: {SmsPayloadSubmittedMarker} Timeout sending SMS payload";
            }

            string finalResp = await tcs.Task;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (OperationCanceledException)
        {
            try { if (sp.IsOpen) sp.Write("\x1B"); } catch { }
            return payloadSubmitted
                ? $"ERROR: {SmsPayloadSubmittedMarker} SMS operation cancelled after Ctrl+Z"
                : "ERROR: SMS operation cancelled before Ctrl+Z";
        }
        catch (IOException ex)
        {
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Cáp bị rút khi đang gửi SMS!" });
            return payloadSubmitted
                ? $"ERROR: {SmsPayloadSubmittedMarker} Rút cáp sau Ctrl+Z - {ex.Message}"
                : $"ERROR: Rút cáp trước Ctrl+Z - {ex.Message}";
        }
        catch (Exception ex)
        {
            return payloadSubmitted
                ? $"ERROR: {SmsPayloadSubmittedMarker} Lỗi sau Ctrl+Z - {ex.Message}"
                : $"ERROR: {ex.Message}";
        }
        finally
        {
            if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, tcs))
                _commandTcs.TryRemove(portName, out _);

            // Restore CSCS về UCS2 để nhận tin nhắn tiếng Việt đúng
            // KHÔNG reset AT+CSMP vì CSMP ảnh hưởng cả nhận tin (DCS field).
            // Modem init đã set CMGF=1 + CSCS=UCS2 là đủ cho receive.
            if (_serialPorts.TryGetValue(portName, out var sp2) && sp2.IsOpen)
            {
                if (GetModemProfile(portName)?.IsQuectel == true)
                    await SendInnerAsync("AT+CMGF=0");
                else
                    await SendInnerAsync("AT+CSCS=\"UCS2\"");
            }

            semaphore.Release();
        }
    }

    private sealed record TrustedPduSnapshot(
        bool Trusted,
        string RawResponse,
        IReadOnlySet<string> PresentIndices);

    private async Task<TrustedPduSnapshot> CaptureTrustedPduSnapshotAsync(
        string portName,
        string expectedScope,
        long generation)
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        if (!TryGetSmsScope(portName, generation, out string currentScope)
            || !string.Equals(
                expectedScope, currentScope, StringComparison.Ordinal)
            || !EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null
            || !_semaphores.TryGetValue(
                portName, out SemaphoreSlim? semaphore))
            return new TrustedPduSnapshot(false, string.Empty, empty);

        bool lockAcquired = await semaphore.WaitAsync(10000);
        if (!lockAcquired)
            return new TrustedPduSnapshot(false, string.Empty, empty);

        string rawResponse = string.Empty;
        bool snapshotParsed = false;
        bool restored = false;
        IReadOnlySet<string> presentIndices = empty;
        try
        {
            if (!TryGetSmsScope(portName, generation, out currentScope)
                || !string.Equals(
                    expectedScope, currentScope, StringComparison.Ordinal))
                return new TrustedPduSnapshot(false, string.Empty, empty);

            // Treat even an acknowledged-timeout as a possible mode change.
            // The finally block always restores and verifies text receive mode.
            string pduMode = await SendCommandWhilePortLockedAsync(
                portName,
                serialPort,
                "AT+CMGF=0",
                5000,
                CancellationToken.None);
            if (IsCommandFailure(pduMode))
                return new TrustedPduSnapshot(false, pduMode, empty);

            string freshCcid = await ReadFreshCcidWhilePortLockedAsync(
                portName,
                serialPort,
                CancellationToken.None);
            string expectedCcid = expectedScope.StartsWith(
                    "ccid:", StringComparison.Ordinal)
                ? expectedScope["ccid:".Length..]
                : string.Empty;
            if (string.IsNullOrWhiteSpace(freshCcid)
                || !string.Equals(
                    freshCcid, expectedCcid, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(freshCcid))
                    SetSmsSimIdentity(portName, freshCcid);
                return new TrustedPduSnapshot(false, string.Empty, empty);
            }

            rawResponse = await SendCommandWhilePortLockedAsync(
                portName,
                serialPort,
                "AT+CMGL=4",
                25000,
                CancellationToken.None);
            snapshotParsed = TryParseTrustedPduStoredSmsIndexSnapshot(
                rawResponse,
                out presentIndices);
        }
        finally
        {
            try
            {
                restored = await RestoreSmsReceiveModeWhilePortLockedAsync(
                    portName,
                    serialPort);
            }
            finally
            {
                semaphore.Release();
            }
        }

        bool identityStillCurrent = TryGetSmsScope(
                portName, generation, out currentScope)
            && string.Equals(
                expectedScope, currentScope, StringComparison.Ordinal);
        return new TrustedPduSnapshot(
            snapshotParsed && restored && identityStillCurrent,
            rawResponse,
            presentIndices);
    }

    private async Task ReconcileSimCleanupIntentsFromSweepAsync(
        string portName,
        long generation)
    {
        if (!TryGetSmsScope(portName, generation, out string scope))
            return;

        IReadOnlyList<SmsSimCleanupJournal.Intent> intents;
        try
        {
            intents = _simCleanupJournal.GetForScope(scope);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_SIM_CLEANUP_RECOVERY_BLOCKED] Không đọc được journal ý định xóa: {ex.Message}. Không suy đoán trạng thái SIM."
            });
            return;
        }

        if (intents.Count == 0) return;

        // Text-mode CMGL cannot prove absence safely because a stored message
        // whose body is exactly "OK" could look like an early command
        // terminator. PDU user data is hex-only, so a final standalone OK is
        // unambiguous. This extra snapshot runs only while crash-recovery
        // intents exist.
        TrustedPduSnapshot snapshot = await CaptureTrustedPduSnapshotAsync(
            portName,
            scope,
            generation);
        if (!snapshot.Trusted)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[SMS_SIM_CLEANUP_RECOVERY_RETRY] Chưa lấy được snapshot PDU hoàn chỉnh; giữ nguyên journal và không suy đoán slot."
            });
            return;
        }
        IReadOnlySet<string> presentIndices = snapshot.PresentIndices;

        foreach (SmsSimCleanupJournal.Intent intent in intents)
        {
            if (!TryGetSmsScope(portName, generation, out string currentScope)
                || !string.Equals(scope, currentScope, StringComparison.Ordinal))
                return;

            if (presentIndices.Contains(intent.SimIndex))
            {
                // The slot still exists. Read and fingerprint its current body;
                // an index may have been recycled, so never issue CMGD blindly.
                QueueStoredSmsRead(portName, intent.SimIndex);
                continue;
            }

            try
            {
                // A complete CMGL snapshot for this exact CCID proves the old
                // slot is absent. Finish the durable transition that may have
                // been interrupted after the modem acknowledged CMGD.
                _multipartJournal.MarkPartCleaned(
                    intent.MessageId,
                    intent.PartIdentity);
                _simCleanupJournal.Complete(
                    intent.IntentId,
                    intent.MessageId);
                if (_multipartJournal.IsDeliveryAcknowledged(intent.MessageId)
                    && _multipartJournal.IsSimCleanupConfirmed(intent.MessageId))
                    _multipartJournal.Complete(intent.MessageId);

                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SIM_CLEANUP_RECOVERED] index={intent.SimIndex} delivery={intent.MessageId}; CMGL xác nhận slot đã trống."
                });
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SIM_CLEANUP_RECOVERY_RETRY] index={intent.SimIndex} delivery={intent.MessageId}: {ex.Message}"
                });
            }
        }
    }

    public async Task SweepUnreadSmsAsync(string portName)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return;
        if (_suspendedBackgroundPorts.ContainsKey(portName)
            || IsCallInProgress(portName)
            || _commandTcs.ContainsKey(portName))
        {
            return;
        }

        SemaphoreSlim sweepLock = _smsSweepLocks.GetOrAdd(portName, static _ => new SemaphoreSlim(1, 1));
        if (!await sweepLock.WaitAsync(0)) return;

        long generation = CurrentSmsGeneration(portName);
        try
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Đang quét tin nhắn tồn đọng (Sweep)..." });

            // Re-assert receive mode on every recovery sweep. SMS sending and some
            // EC20 firmware revisions can leave CMGF/CNMI/URC routing changed; without
            // this, the SIM stores the message but no +CMTI reaches the application.
            await SendCommandAsync(portName, "AT+CMGF=1", 5000, silent: true);
            await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true);
            if (GetModemProfile(portName)?.IsQuectel == true)
                await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true);

            // ALL is intentional: CMGR marks a multipart segment REC READ before the remaining
            // segments arrive. Scanning only REC UNREAD loses that segment after restart.
            //
            // CMGF=1 above selects text mode for every modem.  Quectel's numeric
            // `AT+CMGL=4` form is PDU-mode syntax; using it here while still in text
            // mode made the recovery sweep return no records, so SMS stayed on the
            // SIM until a modem/app restart happened to flush the state.  Keep the
            // command consistent with the selected mode for all profiles.
            const string command = "AT+CMGL=\"ALL\"";
            string sweepResponse = await SendCommandAsync(portName, command, 25000, silent: true);
            if (IsCommandFailure(sweepResponse)
                && GetModemProfile(portName)?.IsQuectel == true)
            {
                // A few EC20 firmware banks reject the text-mode list command
                // after a previous PDU operation. Fall back once to PDU mode;
                // HandleDataReceived can route the returned PDU records through
                // the same QCMGR/CMGR decoder without dropping them.
                if (TryGetSmsScope(portName, generation, out string scope))
                {
                    TrustedPduSnapshot fallback =
                        await CaptureTrustedPduSnapshotAsync(
                            portName,
                            scope,
                            generation);
                    sweepResponse = fallback.RawResponse;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = fallback.Trusted
                            ? $"[SMS_SWEEP_PDU_FALLBACK] AT+CMGL=4: {sweepResponse.Trim()}"
                            : "[SMS_SWEEP_PDU_FALLBACK_BLOCKED] Snapshot hoặc trạng thái receive mode không xác minh được; không suy đoán bộ nhớ SIM."
                    });
                }
            }

            await ReconcileSimCleanupIntentsFromSweepAsync(
                portName,
                generation);
            if (TryGetSmsScope(
                    portName, generation, out string replayScope))
                ScheduleCompletedMultipartReplay(
                    replayScope,
                    portName,
                    delayMs: 1000);
            if (sweepResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || sweepResponse.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SWEEP_FAILED] {command}: {sweepResponse.Trim()}"
                });
            }
        }
        finally
        {
            sweepLock.Release();
        }
    }

    private void SignalOutgoingCallEnded(string portName, string reason)
    {
        if (_outgoingCallEndSignals.TryGetValue(portName, out var signal))
            signal.TrySetResult(reason);
    }

    internal static bool HasActiveOutgoingVoiceSession(string response) => Regex.IsMatch(
        response ?? string.Empty,
        @"\+CLCC:\s*\d+\s*,\s*0\s*,\s*0\s*,\s*0(?:\s*,|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    internal static bool IsSuccessfulCallHangupAcknowledgement(string response) =>
        Regex.IsMatch(
            response ?? string.Empty,
            @"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && !(response ?? string.Empty).Contains(
            "ERROR", StringComparison.OrdinalIgnoreCase)
        && !(response ?? string.Empty).Contains(
            "TIMEOUT", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTrustedNoVoiceCallSnapshot(string response)
    {
        string value = response ?? string.Empty;
        bool commandCompleted = Regex.IsMatch(
            value,
            @"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool hasVoiceCall = Regex.IsMatch(
            value,
            @"\+CLCC:\s*\d+\s*,\s*[01]\s*,\s*\d+\s*,\s*0(?:\s*,|\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return commandCompleted
            && !hasVoiceCall
            && !value.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HangUpAndConfirmCallEndedAsync(
        string portName,
        int durationSeconds)
    {
        string athResponse;
        try
        {
            athResponse = await SendCommandAsync(
                portName, "ATH", 3000, silent: true);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL_HANGUP_UNCONFIRMED] ATH exception: {ex.Message}"
            });
            return false;
        }

        bool athAcknowledged = IsSuccessfulCallHangupAcknowledgement(athResponse);
        string lastClcc = string.Empty;
        bool noVoiceCall = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                lastClcc = await SendCommandAsync(
                    portName, "AT+CLCC", 2000, silent: true);
                if (IsTrustedNoVoiceCallSnapshot(lastClcc))
                {
                    noVoiceCall = true;
                    break;
                }
            }
            catch
            {
                // A missing/failed snapshot is never proof that the call ended.
            }

            if (attempt < 3)
                await Task.Delay(250);
        }

        if (athAcknowledged && noVoiceCall)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL_HANGUP_CONFIRMED] duration={durationSeconds}; ATH=OK; CLCC=EMPTY."
            });
            return true;
        }

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[CALL_HANGUP_UNCONFIRMED] duration={durationSeconds}; ATH={Regex.Replace(athResponse.Trim(), @"\s+", " ")}; CLCC={Regex.Replace(lastClcc.Trim(), @"\s+", " ")}."
        });
        return false;
    }

    internal static IReadOnlyList<string> GetCallAudioPlaybackCommandOrder(string remoteFileName) =>
    [
        $"AT+QPSND=1,\"{remoteFileName}\",0,1,1",
        $"AT+QPSND=1,\"ufs:{remoteFileName}\",0,1,1"
    ];

    public async Task<bool> CallWithAudioAsync(
        string portName,
        string phoneNumber,
        string? wavPath,
        int durationSeconds = 30,
        bool record = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(portName)
            || !GsmDestination.TryNormalizeDial(phoneNumber, out string cleanPhone))
            return false;

        if (!_outgoingCallOperations.TryAdd(portName, 0))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[CALL] Cổng đang có một cuộc gọi khác."
            });
            return false;
        }

        durationSeconds = Math.Clamp(durationSeconds, 5, 300);
        var endSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outgoingCallEndSignals[portName] = endSignal;
        using IDisposable backgroundLease = SuspendPortBackgroundOperations(portName);
        bool recordingStarted = false;
        string? recordingRemoteName = null;
        try
        {
            // Giữ đúng luồng đã chạy ổn định ở nhánh dev: không đổi chế độ mạng,
            // không preflight CREG/CEREG/QNWINFO và không chờ CLCC 45 giây.
            string? remoteWavName = null;
            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
            {
                string extension = Path.GetExtension(wavPath).ToLowerInvariant();
                if (extension is not (".wav" or ".amr" or ".mp3")) extension = ".wav";
                string candidate = $"call-play{extension}";
                if (await UploadFileToModemAsync(portName, wavPath, candidate))
                    remoteWavName = candidate;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Đang gửi lệnh quay số ATD{cleanPhone}..."
            });

            string dialResp = await SendCommandAsync(portName, $"ATD{cleanPhone};", 15000);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Phản hồi ATD: {dialResp.Trim()}"
            });

            bool rejected = dialResp.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || dialResp.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase)
                || dialResp.Contains("BUSY", StringComparison.OrdinalIgnoreCase)
                || dialResp.Contains("NO ANSWER", StringComparison.OrdinalIgnoreCase)
                || dialResp.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
            if (rejected)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[CALL] Modem từ chối cuộc gọi: {dialResp.Trim()}"
                });
                await HangUpAndConfirmCallEndedAsync(portName, durationSeconds);
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(durationSeconds);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Đã quay số; tự dập sau tổng {durationSeconds} giây."
            });

            // Xác minh modem có thật sự tạo phiên thoại. Một số firmware vẫn trả OK cho ATD
            // nhưng không tạo CLCC thoại; khi đó đầu bên kia hoàn toàn không đổ chuông.
            // Deadline đã được tạo trước vòng lặp nên việc kiểm tra không kéo dài thời lượng gọi.
            bool sawOutgoingVoiceSession = false;
            bool sawActiveVoiceSession = false;
            string? lastCallState = null;
            int clccAttempts = 0;
            int maxNoVoiceClccAttempts = remoteWavName != null ? 60 : 8;
            string lastClcc = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (endSignal.Task.IsCompleted) break;

                clccAttempts++;
                string clcc = await SendCommandAsync(portName, "AT+CLCC", 1200, silent: true);
                lastClcc = Regex.Replace(clcc.Trim(), @"\s+", " ");
                Match voiceCall = Regex.Match(clcc,
                    @"\+CLCC:\s*\d+\s*,\s*0\s*,\s*(\d+)\s*,\s*0(?:\s*,[^\r\n]*)?",
                    RegexOptions.IgnoreCase);
                if (voiceCall.Success)
                {
                    sawOutgoingVoiceSession = true;
                    string state = voiceCall.Groups[1].Value switch
                    {
                        "0" => "ACTIVE",
                        "2" => "DIALING",
                        "3" => "ALERTING",
                        "4" => "INCOMING",
                        "5" => "WAITING",
                        _ => $"STATE_{voiceCall.Groups[1].Value}"
                    };
                    if (!string.Equals(lastCallState, state, StringComparison.Ordinal))
                    {
                        lastCallState = state;
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[CALL_STATE] Phiên thoại đã tạo: {state}."
                        });
                    }

                    if (state == "ACTIVE")
                    {
                        sawActiveVoiceSession = true;
                        if (remoteWavName != null)
                        {
                            bool playbackStarted = await PlayWavAsync(portName, remoteWavName, ct);
                            remoteWavName = null;
                            if (playbackStarted && record)
                                await WaitForAudioPlaybackCompleteAsync(
                                    portName, () => endSignal.Task.IsCompleted, ct);
                        }

                        if (record && !endSignal.Task.IsCompleted)
                        {
                            recordingRemoteName = $"call-{portName}-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
                            string recordResponse = await SendCommandAsync(
                                portName,
                                $"AT+QAUDRD=1,\"{recordingRemoteName}\",13,1",
                                5000,
                                silent: true,
                                ct: ct);
                            recordingStarted = recordResponse.Contains("OK", StringComparison.OrdinalIgnoreCase)
                                && !recordResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = recordingStarted
                                    ? $"[CALL_RECORDING] Recording downlink to {recordingRemoteName}"
                                    : $"[CALL_RECORDING_FAILED] {recordResponse.Trim()}"
                            });
                        }
                        break;
                    }
                }

                if (clcc.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase)
                    || clcc.Contains("BUSY", StringComparison.OrdinalIgnoreCase)
                    || clcc.Contains("NO ANSWER", StringComparison.OrdinalIgnoreCase))
                    break;

                if (ShouldStopWaitingForActiveCall(
                        sawOutgoingVoiceSession,
                        clccAttempts,
                        maxNoVoiceClccAttempts))
                    break;

                TimeSpan pollDelay = deadline - DateTime.UtcNow;
                if (pollDelay > TimeSpan.Zero)
                    await Task.Delay(pollDelay > TimeSpan.FromMilliseconds(500)
                        ? TimeSpan.FromMilliseconds(500) : pollDelay, ct);
            }

            if (!sawActiveVoiceSession)
            {
                if (sawOutgoingVoiceSession
                    && remoteWavName == null
                    && !record
                    && DateTime.UtcNow >= deadline)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[CALL_DURATION_COMPLETE] duration={durationSeconds}; voice=DIALING_OR_ALERTING; active=false; mode=no-audio."
                    });
                    return await HangUpAndConfirmCallEndedAsync(
                        portName, durationSeconds);
                }

                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[CALL_NO_ACTIVE_SESSION] ATD chưa tạo phiên ACTIVE (voiceSeen={sawOutgoingVoiceSession}; CLCC={lastClcc}). Cuộc gọi được đánh dấu thất bại."
                });
                await HangUpAndConfirmCallEndedAsync(portName, durationSeconds);
                _ = LogVoiceFailureDiagnosticsAsync(portName, "NO ACTIVE SESSION");
                return false;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                Task timer = Task.Delay(remaining, ct);
                Task completed = await Task.WhenAny(timer, endSignal.Task);
                if (completed == endSignal.Task)
                {
                    string reason = await endSignal.Task;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[CALL] Cuộc gọi kết thúc sớm: {reason}."
                    });
                    if (reason is "NO CARRIER" or "BUSY" or "NO ANSWER")
                        _ = LogVoiceFailureDiagnosticsAsync(portName, reason);
                    await HangUpAndConfirmCallEndedAsync(portName, durationSeconds);
                    return false;
                }
                await timer;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Hết {durationSeconds} giây → Dập máy (ATH)."
            });
            return await HangUpAndConfirmCallEndedAsync(
                portName, durationSeconds);
        }
        catch (OperationCanceledException)
        {
            try { await HangUpAndConfirmCallEndedAsync(portName, durationSeconds); }
            catch { }
            return false;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Lỗi: {ex.Message}"
            });
            try { await HangUpAndConfirmCallEndedAsync(portName, durationSeconds); }
            catch { }
            return false;
        }
        finally
        {
            if (recordingStarted && !string.IsNullOrWhiteSpace(recordingRemoteName))
            {
                try
                {
                    await SendCommandAsync(portName, "AT+QAUDRD=0", 5000, silent: true);
                    string recordingDirectory = Path.Combine(AppBootstrap.DataDir, "CallRecordings", portName);
                    Directory.CreateDirectory(recordingDirectory);
                    string localRecording = Path.Combine(recordingDirectory, recordingRemoteName);
                    string downloaded = await DownloadFileFromModemAsync(
                        portName, recordingRemoteName, localRecording);
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = string.IsNullOrWhiteSpace(downloaded)
                            ? $"[CALL_RECORDING_FAILED] Could not download {recordingRemoteName} from modem."
                            : $"[CALL_RECORDING_SAVED] {downloaded}"
                    });
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[CALL_RECORDING_FAILED] {ex.Message}"
                    });
                }
            }

            if (_outgoingCallEndSignals.TryGetValue(portName, out var currentSignal)
                && ReferenceEquals(currentSignal, endSignal))
                _outgoingCallEndSignals.TryRemove(portName, out _);
            _outgoingCallOperations.TryRemove(portName, out _);
        }
    }

    internal static bool ShouldStopWaitingForActiveCall(
        bool sawOutgoingVoiceSession,
        int clccAttempts,
        int maxNoVoiceClccAttempts) =>
        !sawOutgoingVoiceSession
        && clccAttempts >= maxNoVoiceClccAttempts;

    private async Task LogVoiceFailureDiagnosticsAsync(string portName, string reason)
    {
        try
        {
            string ceer = await SendCommandAsync(portName, "AT+CEER", 3000, silent: true);
            string ims = await SendCommandAsync(portName, "AT+QCFG=\"ims\"", 3000, silent: true);
            string scanMode = await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\"", 3000, silent: true);
            string scanSequence = await SendCommandAsync(portName, "AT+QCFG=\"nwscanseq\"", 3000, silent: true);
            string network = await SendCommandAsync(portName, "AT+QNWINFO", 3000, silent: true);
            string creg = await SendCommandAsync(portName, "AT+CREG?", 3000, silent: true);
            string cereg = await SendCommandAsync(portName, "AT+CEREG?", 3000, silent: true);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL_DIAG] reason={reason}; ceer={ceer.Trim()}; ims={ims.Trim()}; nwscanmode={scanMode.Trim()}; nwscanseq={scanSequence.Trim()}; network={network.Trim()}; creg={creg.Trim()}; cereg={cereg.Trim()}"
            });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL_DIAG] Không đọc được chẩn đoán thoại: {ex.Message}"
            });
        }
    }


    async Task<bool> PlayWavAsync(string portName, string remoteFileName, CancellationToken ct)
    {
        try
        {
            await SendCommandAsync(portName, "AT+CLVL=5", 2000, silent: true); // volume 0-5

            // EC20 requires repeat/ulmute/dlmute. 1,1 sends the WAV to the far
            // end while keeping both call directions audible. QAUDPLAY is not a
            // valid fallback here because it only plays to the local downlink.
            string resp = "ERROR";
            foreach (string playCmd in GetCallAudioPlaybackCommandOrder(remoteFileName))
            {
                resp = await SendCommandAsync(portName, playCmd, 8000, ct: ct);
                if (resp.Contains("OK", StringComparison.OrdinalIgnoreCase)
                    && !resp.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Play WAV: {resp}" });
            return resp.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !resp.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"PlayWav lỗi: {ex.Message}" });
            return false;
        }
    }

    async Task WaitForAudioPlaybackCompleteAsync(
        string portName,
        Func<bool> callEnded,
        CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !callEnded())
        {
            ct.ThrowIfCancellationRequested();
            string state = await SendCommandAsync(portName, "AT+QPSND?", 3000, silent: true, ct: ct);
            if (Regex.IsMatch(state, @"\+QPSND:\s*0\b", RegexOptions.IgnoreCase)) return;
            await Task.Delay(250, ct);
        }
    }

    async Task<bool> WaitForAnswerAsync(string portName, int timeoutSeconds, CancellationToken ct)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        bool callSeen = false;
        int noCallCount = 0;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var clcc = await SendCommandAsync(portName, "AT+CLCC", 2000, silent: false);
            bool hasClcc = clcc.Contains("+CLCC:");

            if (hasClcc)
            {
                callSeen = true;
                noCallCount = 0;

                if (Regex.IsMatch(clcc, @"\+CLCC:\s*\d+,\d+,0,"))
                {
                    return true;
                }
            }
            else
            {
                if (callSeen)
                {
                    noCallCount++;
                    if (noCallCount >= 2)
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Cuộc gọi bị cúp máy trước khi trả lời" });
                        return false;
                    }
                }
            }
            await Task.Delay(800, ct);
        }
        return false;
    }

    // ===================== INCOMING CALL HANDLING =====================
    void HandleIncomingCallUrcs(string portName, ref string currentData, StringBuilder buffer)
    {
        if (string.IsNullOrEmpty(currentData)) return;

        bool updated = false;

        // +CLIP: "+84901234567",145,...
        var clipMatches = Regex.Matches(currentData, @"\+CLIP:\s*""([^""]+)""");
        if (clipMatches.Count > 0)
        {
            foreach (Match m in clipMatches)
            {
                string caller = m.Groups[1].Value;
                OnIncomingRing(portName, caller);
                buffer.Replace(m.Value, "");
                updated = true;
            }
        }

        // RING hoặc +CRING: VOICE
        var ringMatches = Regex.Matches(currentData, @"RING|\+CRING:\s*VOICE");
        if (ringMatches.Count > 0)
        {
            foreach (Match m in ringMatches)
            {
                if (!_incomingCalls.ContainsKey(portName))
                    OnIncomingRing(portName, "Unknown");
                buffer.Replace(m.Value, "");
                updated = true;
            }
        }

        // NO CARRIER / BUSY / NO ANSWER → cuộc gọi kết thúc
        var endMatches = Regex.Matches(currentData, @"NO CARRIER|BUSY|NO ANSWER");
        if (endMatches.Count > 0 && _incomingCalls.ContainsKey(portName))
        {
            // The generic call-end block below must still see the terminal code
            // so outgoing-call waiters and CallEnded are completed as well.
            _ = OnIncomingCallEnded(portName);
        }

        if (updated)
        {
            currentData = buffer.ToString();
        }
    }

    void OnIncomingRing(string portName, string caller)
    {
        _activeCalls[portName] = true;

        var session = _incomingCalls.GetOrAdd(portName, _ => new gsm.Models.IncomingCallSession
        {
            Port = portName,
            Caller = caller,
            RingAt = DateTime.Now
        });

        if (session.Caller == "Unknown" && caller != "Unknown")
            session.Caller = caller;

        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"📞 Cuộc gọi đến từ {session.Caller}" });

        // Khôi phục event tương thích nhánh dev để UI, âm báo và Telegram nhận cuộc gọi đến.
        // Chỉ phát một lần cho mỗi phiên và đợi +CLIP nếu RING đến trước số gọi.
        if (!string.Equals(session.Caller, "Unknown", StringComparison.OrdinalIgnoreCase)
            && _incomingCallNotifications.TryAdd(portName, 0))
        {
            CallIncoming?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = session.Caller
            });
        }

        IncomingCallRinging?.Invoke(this, session);

        // Auto-answer and record only real voice-capable modem profiles. This is
        // deliberately started once per incoming session; repeated RING URCs do
        // not create another ATA/QAUDRD sequence.
        QuectelModemProfile? profile = GetModemProfile(portName);
        if (profile?.Supports(ModemCapability.VoiceCall) == true
            && profile.Supports(ModemCapability.AudioRecord))
        {
            string remoteFileName = $"incoming-{portName}-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
            var state = new IncomingCallRecordingState(remoteFileName);
            if (_incomingCallRecordings.TryAdd(portName, state))
                _ = AutoAnswerAndRecordIncomingCallAsync(portName, state);
        }
    }

    async Task OnIncomingCallEnded(string portName)
    {
        _activeCalls[portName] = false;
        _incomingCallNotifications.TryRemove(portName, out _);
        if (!_incomingCalls.TryRemove(portName, out var session))
        {
            ScheduleIncomingCallRecordingFinalization(portName);
            return;
        }

        session.EndedAt = DateTime.Now;
        IncomingCallEnded?.Invoke(this, session);
        ScheduleIncomingCallRecordingFinalization(portName);
        await Task.CompletedTask;
    }

    private async Task AutoAnswerAndRecordIncomingCallAsync(
        string portName,
        IncomingCallRecordingState state)
    {
        IDisposable? backgroundLease = null;
        try
        {
            backgroundLease = SuspendPortBackgroundOperations(portName);
            await Task.Delay(250);

            lock (state.Sync)
            {
                if (state.Ended) return;
            }

            string answerResponse = await SendCommandAsync(
                portName, "ATA", 10000, silent: true);
            bool answered = answerResponse.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !answerResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = answered
                    ? "[INCOMING_CALL] Tự động nghe máy (ATA) thành công."
                    : $"[INCOMING_CALL_FAILED] ATA: {answerResponse.Trim()}"
            });
            if (!answered) return;

            lock (state.Sync)
            {
                if (state.Ended) return;
                state.BackgroundLease = backgroundLease;
                backgroundLease = null;
            }

            _activeCalls[portName] = true;
            await Task.Delay(250);
            lock (state.Sync)
            {
                if (state.Ended) return;
            }

            string recordResponse = await SendCommandAsync(
                portName,
                $"AT+QAUDRD=1,\"{state.RemoteFileName}\",13,1",
                5000,
                silent: true);
            bool recordingStarted = recordResponse.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !recordResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
            lock (state.Sync)
            {
                state.RecordingStarted = recordingStarted;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = recordingStarted
                    ? $"[INCOMING_RECORDING] Đang ghi âm: {state.RemoteFileName}"
                    : $"[INCOMING_RECORDING_FAILED] {recordResponse.Trim()}"
            });
        }
        catch (OperationCanceledException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[INCOMING_CALL_FAILED] Tự động nghe máy bị hủy."
            });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[INCOMING_CALL_FAILED] {ex.Message}"
            });
        }
        finally
        {
            state.SetupCompleted.TrySetResult(true);
            backgroundLease?.Dispose();
        }
    }

    private void ScheduleIncomingCallRecordingFinalization(string portName)
    {
        if (!_incomingCallRecordings.TryGetValue(portName, out var state)) return;
        lock (state.Sync)
        {
            state.Ended = true;
            if (state.FinalizationStarted) return;
            state.FinalizationStarted = true;
        }

        _ = FinalizeIncomingCallRecordingAsync(portName, state);
    }

    private async Task FinalizeIncomingCallRecordingAsync(
        string portName,
        IncomingCallRecordingState state)
    {
        IDisposable? backgroundLease = null;
        try
        {
            try
            {
                await state.SetupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[INCOMING_RECORDING_FAILED] Hết thời gian chờ luồng nghe máy."
                });
            }

            bool recordingStarted;
            lock (state.Sync)
            {
                recordingStarted = state.RecordingStarted;
            }

            _incomingCallRecordings.TryRemove(portName, out _);
            if (!recordingStarted) return;

            await SendCommandAsync(portName, "AT+QAUDRD=0", 5000, silent: true);
            string recordingDirectory = Path.Combine(AppBootstrap.DataDir, "CallRecordings", portName);
            Directory.CreateDirectory(recordingDirectory);
            string localRecording = Path.Combine(recordingDirectory, state.RemoteFileName);
            string downloaded = await DownloadFileFromModemAsync(
                portName, state.RemoteFileName, localRecording);

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = string.IsNullOrWhiteSpace(downloaded)
                    ? $"[INCOMING_RECORDING_FAILED] Không tải được {state.RemoteFileName}."
                    : $"[INCOMING_RECORDING_SAVED] {downloaded}"
            });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[INCOMING_RECORDING_FAILED] {ex.Message}"
            });
        }
        finally
        {
            lock (state.Sync)
            {
                backgroundLease = state.BackgroundLease;
                state.BackgroundLease = null;
            }
            backgroundLease?.Dispose();
        }
    }
}



