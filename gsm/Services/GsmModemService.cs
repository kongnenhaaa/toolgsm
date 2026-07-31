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

public readonly record struct SautoImeiChangeResult(
    string ReadImei,
    bool ResetRequested);

public interface IGsmModemService
{
    Task<string> SendCommandAsync(
        string portName,
        string command,
        int timeoutMs = 5000,
        bool silent = false,
        CancellationToken ct = default);
    /// <summary>
    /// Runs the SAuto airplane sequence and advances only after the shared RX
    /// callback publishes a fresh +CFUN state report.
    /// </summary>
    Task<bool> EnterSautoAirplaneModeAsync(
        string portName,
        CancellationToken ct = default);
    /// <summary>
    /// Runs GSMController.ChangeImei while owning the UART continuously:
    /// write slot 7, wait for the shared RX callback to publish the readback,
    /// then send the CFUN reset without using terminal OK as a gate.
    /// </summary>
    Task<SautoImeiChangeResult> ChangeSautoImeiAsync(
        string portName,
        string targetImei,
        CancellationToken ct = default);
    /// <summary>
    /// Returns the latest slot-7 IMEI already parsed by the SAuto receive loop.
    /// This is a read-only memory lookup and does not transmit another AT command.
    /// </summary>
    string GetObservedImei(string portName);
    /// <summary>
    /// Returns the latest ICCID parsed by the SAuto receive loop. This is a
    /// read-only memory lookup and does not transmit another AT command.
    /// </summary>
    string GetObservedCcid(string portName);
    /// <summary>
    /// Runs GSMController.USSDCheck with the SAuto lock boundary: each AT
    /// command owns the UART only while it is written. The USSD stage advances
    /// only when the modem publishes a +CUSD payload.
    /// </summary>
    Task<string?> RunSautoManualUssdAsync(
        string portName,
        IReadOnlyList<string> stages,
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
    /// Stops the additional SMS maintenance layer before a SAuto IMEI workflow.
    /// Normal SAuto RX handling remains active; maintenance is re-enabled only
    /// after the post-reset automatic USSD stage has completed.
    /// </summary>
    Task<IDisposable> HoldSmsReceiveMaintenanceUntilSautoReadyAsync(
        string portName,
        CancellationToken ct = default);
    /// <summary>
    /// Bật/tắt xác nhận rút SIM nhanh. Cờ được bật ngay khi CCID của phiên hiện
    /// tại đã được xác nhận, kể cả khi SIM còn đang chờ thao tác IMEI.
    /// </summary>
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
    public DateTimeOffset? SmsTimestampUtc { get; set; }
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

    internal const string SautoImsUtQueryCommand = "AT+QCFG=\"ims/ut\"";
    internal const string SautoImsUtDisableCommand = "AT+QCFG=\"ims/ut\",0";
    internal const string SautoNetworkModeQueryCommand =
        "AT+QCFG=\"nwscanmode\"";
    internal const string SautoNetworkModeAutoCommand =
        "AT+QCFG=\"nwscanmode\",0,0";
    internal const string SautoServiceDomainQueryCommand =
        "AT+QCFG=\"servicedomain\"";
    internal const string SautoServiceDomainCsPsCommand =
        "AT+QCFG=\"servicedomain\",2,0";
    internal const string SautoMbnAutoSelQueryCommand =
        "AT+QMBNCFG=\"AutoSel\"";
    internal const string SautoMbnAutoSelEnableCommand =
        "AT+QMBNCFG=\"AutoSel\",1";

    internal static IReadOnlyList<string> SautoImsUtRepairCommandOrder { get; } =
    [
        SautoImsUtQueryCommand,
        SautoImsUtDisableCommand,
        SautoImsUtQueryCommand
    ];

    internal static IReadOnlyList<string> SautoInitializationCommandOrder { get; } =
    [
        "\u001b",
        "ATI",
        SautoImsUtQueryCommand,
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
        "AT+CMGD=1,4",
        "AT+CPMS=\"ME\",\"ME\",\"ME\"",
        "AT+CMGD=1,4",
        "AT+CPMS=\"SM\",\"SM\",\"SM\"",
        "AT+CPMS?",
        "AT+CNMI=1,1,0,0,0",
        SautoNetworkModeAutoCommand,
        "AT+QURCCFG=\"urcport\",\"uart1\"",
        "AT+CPIN?"
    ];

    internal static IReadOnlyList<string> SautoInitial111CommandOrder { get; } =
    [
        "AT+CUSD=2",
        "AT+CUSD=1,\"*111#\",15"
    ];

    internal static IReadOnlyList<string> SautoInitial101CommandOrder { get; } =
    [
        "AT+CSCS=\"GSM\"",
        "AT+CUSD=2",
        "AT+CUSD=1,\"*101#\",15"
    ];

    internal static IReadOnlyList<string> SautoNetworkPollingCommandOrder { get; } =
    [
        "AT+CPIN? \r",
        "AT+CSQ \r",
        "AT+COPS?"
    ];

    internal static TimeSpan SautoImeiResetGuardDelay { get; } =
        TimeSpan.FromSeconds(10);

    internal static TimeSpan SautoImeiWriteGuardDelay { get; } =
        TimeSpan.FromMilliseconds(500);

    internal const int SautoImeiReadMaxAttempts = 5;

    internal static TimeSpan SautoImeiReadInitialDelay { get; } =
        TimeSpan.FromMilliseconds(100);

    internal static TimeSpan SautoImeiReadPollDelay { get; } =
        TimeSpan.FromMilliseconds(100);

    internal static TimeSpan SautoImeiReadTimeout { get; } =
        TimeSpan.FromSeconds(12);

    internal static TimeSpan SautoImeiReadRetryDelay { get; } =
        TimeSpan.FromSeconds(1);

    internal const int SautoAirplaneMaxAttempts = 5;

    internal static TimeSpan SautoAirplanePreQueryDelay { get; } =
        TimeSpan.FromSeconds(1);

    internal static TimeSpan SautoAirplaneResponsePollDelay { get; } =
        TimeSpan.FromMilliseconds(200);

    internal static TimeSpan SautoAirplaneResponseTimeout { get; } =
        TimeSpan.FromSeconds(10);

    // Acknowledged USSD requests are asynchronous. VinaPhone may deliver the
    // +CUSD several seconds after OK; wait for the actual payload instead of
    // declaring failure and forcing the user to Refresh the port.
    internal static TimeSpan SautoManualUssdResponseTimeout { get; } =
        TimeSpan.FromSeconds(30);

    internal static TimeSpan SautoAirplaneRetryDelay { get; } =
        TimeSpan.FromSeconds(1);

    internal static TimeSpan SautoDataPortStepDelay { get; } =
        TimeSpan.FromMilliseconds(100);

    internal static TimeSpan SautoNetworkRecheckInterval { get; } =
        TimeSpan.FromSeconds(2);

    internal static TimeSpan SautoDataPortLoopDelay { get; } =
        TimeSpan.FromMilliseconds(400);

    internal static IReadOnlyList<string> SmsReceiveRestoreCommandOrder { get; } =
    [
        "AT+CMGF=1",
        "AT+CSCS=\"GSM\"",
        "AT+CPMS=\"SM\",\"SM\",\"SM\"",
        "AT+CNMI=1,1,0,0,0"
    ];

    internal const string SmsReceiveWatchdogCommand = "AT+CMGL=\"ALL\"";
    internal static TimeSpan SmsReceiveWatchdogInterval { get; } =
        TimeSpan.FromSeconds(60);
    internal static TimeSpan SmsReceiveWatchdogTurnGap { get; } =
        TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, SerialPort> _serialPorts = new();
    private readonly ConcurrentDictionary<string, gsm.Models.IncomingCallSession> _incomingCalls = new();
    private readonly ConcurrentDictionary<string, byte> _incomingCallNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IncomingCallRecordingState> _incomingCallRecordings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly PortOperationCoordinator _foregroundOperations = new();
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
    private sealed record SautoNetworkState(
        string Ccid,
        string Carrier,
        string NetworkType,
        bool AutomaticUssdCompleted,
        DateTimeOffset LastAutomaticUssdAttemptUtc);
    private sealed record SmsReceiveMaintenanceGate(
        string Ccid,
        long Generation);

    private sealed class SautoReceiveState
    {
        public object Sync { get; } = new();
        public StringBuilder LineBuffer { get; } = new();
        public long Revision { get; set; }
        public long CfunRevision { get; set; }
        public long UssdRevision { get; set; }
        public bool SimReady { get; set; }
        public bool SimLocked { get; set; }
        public bool ReadyTransitionPending { get; set; }
        public bool RestartRequired { get; set; }
        public int? CfunMode { get; set; }
        public string CpinResponse { get; set; } = string.Empty;
        public string Imei { get; set; } = string.Empty;
        public string Ccid { get; set; } = string.Empty;
        public string Carrier { get; set; } = string.Empty;
        public string NetworkType { get; set; } = string.Empty;
        public string CsqResponse { get; set; } = string.Empty;
        public string CopsResponse { get; set; } = string.Empty;
        public string UssdResponse { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Firmware { get; set; } = string.Empty;
    }

    private sealed record SautoReceiveSnapshot(
        long Revision,
        long CfunRevision,
        long UssdRevision,
        bool SimReady,
        bool SimLocked,
        bool RestartRequired,
        int? CfunMode,
        string CpinResponse,
        string Imei,
        string Ccid,
        string Carrier,
        string NetworkType,
        string CsqResponse,
        string CopsResponse,
        string UssdResponse,
        string Manufacturer,
        string Model,
        string Firmware);

    private readonly ConcurrentDictionary<string, NetworkPollingIdentity> _pollingExpectedIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SautoNetworkState> _sautoNetworkStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SautoReceiveState> _sautoReceiveStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sautoReceiveSignals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _sautoRestartOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _sautoImeiChangePorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _sautoResettingPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _sautoInitializingPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _suspendedBackgroundPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetworkPollingIdentity> _pendingNetworkPollingPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backgroundOperationSync = new();
    // GlobalSimMonitor chạy độc lập với vòng polling mạng. Bộ xác nhận này xử lý
    // URC rút SIM ngay, còn vòng quét 1 giây là fallback cho board thiếu URC.
    // CPIN/QSIMSTAT can report a short-lived absent state while the modem
    // changes CFUN or the CS/IMS domain. Require both consecutive probes and a
    // minimum elapsed window before clearing a live SIM from the UI.
    // An offline SIM-stack restart (CFUN=0 -> CFUN=4) can temporarily report
    // CPIN NOT READY / QSIMSTAT=0 while the card is still inserted. During that
    // window, removal monitors must not mistake the transient state for a hot-swap.
    // A modem can keep reporting CSQ while the SIM stack itself is wedged.
    // Keep this recovery per COM and bounded so a CME 13 cannot spin an
    // unbounded CFUN/COPS loop or stall all other ports.
    /// <summary>Guard chống race condition: đánh dấu port đang trong quá trình khởi tạo SIM đầu tiên.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portLifetimeCts = new();
    private readonly PortReconnectCoordinator _portReconnects = new();
    private readonly object _connectLock = new object();
    private static readonly TimeSpan PortReconnectDelay = TimeSpan.FromSeconds(1);


    public bool IsCallInProgress(string portName) =>
        _outgoingCallOperations.ContainsKey(portName)
        || (_activeCalls.TryGetValue(portName, out bool active) && active);

    public string GetObservedImei(string portName) =>
        GetSautoReceiveSnapshot(portName).Imei;

    public string GetObservedCcid(string portName) =>
        GetSautoReceiveSnapshot(portName).Ccid;

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

    private async Task<IDisposable> AcquireForegroundOperationAsync(
        string portName,
        string operation,
        CancellationToken ct)
    {
        AtCommandTraceLogger.State(
            portName,
            $"FOREGROUND_WAIT;operation={operation}");
        IDisposable lease = await _foregroundOperations
            .AcquireAsync(portName, ct)
            .ConfigureAwait(false);
        AtCommandTraceLogger.State(
            portName,
            $"FOREGROUND_BEGIN;operation={operation}");
        return new BackgroundOperationLease(() =>
        {
            AtCommandTraceLogger.State(
                portName,
                $"FOREGROUND_END;operation={operation}");
            lease.Dispose();
        });
    }

    /// <summary>
    /// Returns a COM to command mode before another foreground workflow starts.
    /// Progress is gated by terminal modem responses, not by guessed cooldowns:
    /// ESC leaves a stale CMGS prompt, AT proves command mode, CLCC proves there
    /// is no voice call, and CUSD=2 closes an earlier USSD session.
    /// </summary>
    private async Task<bool> PrepareForegroundChannelAsync(
        string portName,
        string nextOperation,
        CancellationToken ct)
    {
        if (!EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null
            || !_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
        {
            AtCommandTraceLogger.Error(
                portName,
                $"FOREGROUND_CLEANUP_FAILED;next={nextOperation};reason=PORT_NOT_OPEN");
            return false;
        }

        // ESC has no terminal response.  Keep it under the command semaphore,
        // then use acknowledged AT probes to prove that the modem left CMGS mode.
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!serialPort.IsOpen) return false;
            AtCommandTraceLogger.Tx(portName, "<ESC>");
            serialPort.Write(new byte[] { 27 }, 0, 1);
        }
        finally
        {
            semaphore.Release();
        }

        bool commandModeReady = false;
        string lastProbe = string.Empty;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            lastProbe = await SendCommandAsync(
                portName,
                "AT",
                3000,
                silent: true,
                ct: ct).ConfigureAwait(false);
            if (IsCleanSmsRecoveryProbe(lastProbe))
            {
                commandModeReady = true;
                break;
            }
        }

        if (!commandModeReady)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"FOREGROUND_CLEANUP_FAILED;next={nextOperation};step=COMMAND_MODE;result={GetSautoResponseOutcome(lastProbe)}");
            return false;
        }

        // Do not issue ATH when CLCC already proves that there is no voice call.
        // Data-mode CLCC entries are not voice sessions and are intentionally kept.
        string clcc = await SendCommandAsync(
            portName,
            "AT+CLCC",
            3000,
            silent: true,
            ct: ct).ConfigureAwait(false);
        bool voiceIdle = IsTrustedNoVoiceCallSnapshot(clcc);
        if (!voiceIdle)
        {
            for (int attempt = 1; attempt <= 3 && !voiceIdle; attempt++)
            {
                await SendCommandAsync(
                    portName,
                    attempt == 1 ? "ATH" : "AT+CHUP",
                    3000,
                    silent: true,
                    ct: ct).ConfigureAwait(false);
                clcc = await SendCommandAsync(
                    portName,
                    "AT+CLCC",
                    3000,
                    silent: true,
                    ct: ct).ConfigureAwait(false);
                voiceIdle = IsTrustedNoVoiceCallSnapshot(clcc);
            }
        }

        if (!voiceIdle)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"FOREGROUND_CLEANUP_FAILED;next={nextOperation};step=VOICE_IDLE;result={Regex.Replace(clcc.Trim(), @"\s+", " ")}");
            return false;
        }

        string cancelUssd = await SendCommandAsync(
            portName,
            "AT+CUSD=2",
            3000,
            silent: true,
            ct: ct).ConfigureAwait(false);
        if (!IsSautoOkResponse(cancelUssd))
        {
            AtCommandTraceLogger.Error(
                portName,
                $"FOREGROUND_CLEANUP_FAILED;next={nextOperation};step=USSD_CANCEL;result={GetSautoResponseOutcome(cancelUssd)}");
            return false;
        }

        string finalProbe = await SendCommandAsync(
            portName,
            "AT",
            3000,
            silent: true,
            ct: ct).ConfigureAwait(false);
        bool ready = IsCleanSmsRecoveryProbe(finalProbe);
        AtCommandTraceLogger.State(
            portName,
            ready
                ? $"FOREGROUND_CLEAN;next={nextOperation};sms=IDLE;voice=IDLE;ussd=IDLE"
                : $"FOREGROUND_CLEANUP_FAILED;next={nextOperation};step=FINAL_PROBE;result={GetSautoResponseOutcome(finalProbe)}");
        return ready;
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
    // A single owner performs the sweep, but every new request is retained here.
    // Previously TryAdd(owner)==false silently discarded the newer request. That
    // let an SMS remain in modem storage until another +CMTI happened to arrive.
    private readonly ConcurrentDictionary<string, long> _smsSweepPendingDueTicks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _smsSweepPendingReasons =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _smsReceiveWatchdogSchedulerSync = new();
    private CancellationTokenSource? _smsReceiveWatchdogSchedulerCts;
    private Task? _smsReceiveWatchdogSchedulerTask;
    private int _smsReceiveWatchdogCursor;
    private readonly ConcurrentDictionary<string, long> _smsReceiveWatchdogLastProbeTicks =
        new(StringComparer.OrdinalIgnoreCase);
    // Every periodic or recovery CMGL scan across all physical COM ports owns
    // this one gate. With 64 ports the modem never receives a 64-command burst.
    private readonly SemaphoreSlim _smsScanTurnGate = new(1, 1);
    private long _smsScanTurnCompletedTick;
    private readonly ConcurrentDictionary<string, SmsReceiveMaintenanceGate> _smsReceiveMaintenanceIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    // Every IMEI flow or SIM identity change advances this generation. An old
    // network loop may still publish a late +CUSD after cancellation; it must
    // never unlock SMS maintenance for the new SAuto lifecycle on the same COM.
    private readonly ConcurrentDictionary<string, long> _smsReceiveMaintenanceGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _smsReceiveMaintenanceActivationOwners =
        new(StringComparer.Ordinal);
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
            _sautoNetworkStates.TryRemove(portName, out _);
            UpdateSautoReceiveState(
                portName,
                static state =>
                {
                    state.Carrier = string.Empty;
                    state.NetworkType = string.Empty;
                    state.CopsResponse = string.Empty;
                });
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
                    InvalidateSmsReceiveMaintenance(portName);
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
            InvalidateSmsReceiveMaintenance(portName);
            InvalidateSmsQueueGeneration(portName);
        }
        else if (string.IsNullOrWhiteSpace(normalized))
        {
            _smsReceiveMaintenanceIdentities.TryRemove(portName, out _);
            StopSmsReceiveWatchdog(portName);
        }
        else if (IsSmsReceiveMaintenanceEnabled(portName))
        {
            EnsureSmsReceiveWatchdog(portName);
        }
    }

    public async Task<IDisposable> HoldSmsReceiveMaintenanceUntilSautoReadyAsync(
        string portName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        InvalidateSmsReceiveMaintenance(portName);
        IDisposable lifecycleLease = await AcquireForegroundOperationAsync(
                portName,
                "SAUTO_IMEI_LIFECYCLE",
                ct)
            .ConfigureAwait(false);

        // A reader that had already passed its gate may have owned the
        // foreground coordinator first. Invalidate once more after acquiring
        // the lease so every reader queued behind this lifecycle must re-check.
        long generation = InvalidateSmsReceiveMaintenance(portName);
        AtCommandTraceLogger.State(
            portName,
            $"SMS_MAINTENANCE_HELD;reason=SAUTO_IMEI_FLOW;generation={generation};next=AUTOMATIC_USSD_COMPLETE_AND_UART_RELEASED");
        return lifecycleLease;
    }

    private long CurrentSmsReceiveMaintenanceGeneration(string portName) =>
        _smsReceiveMaintenanceGenerations.GetOrAdd(portName, 0);

    private long InvalidateSmsReceiveMaintenance(string portName)
    {
        long generation = _smsReceiveMaintenanceGenerations.AddOrUpdate(
            portName,
            1,
            static (_, current) => unchecked(current + 1));
        _smsReceiveMaintenanceIdentities.TryRemove(portName, out _);
        StopSmsReceiveWatchdog(portName);
        return generation;
    }

    internal static bool CanOpenSmsReceiveMaintenanceGate(
        long expectedGeneration,
        long currentGeneration,
        string? expectedCcid,
        string? smsCcid,
        string? networkCcid,
        bool automaticUssdCompleted)
    {
        return automaticUssdCompleted
            && expectedGeneration == currentGeneration
            && !string.IsNullOrWhiteSpace(expectedCcid)
            && string.Equals(expectedCcid, smsCcid, StringComparison.Ordinal)
            && string.Equals(expectedCcid, networkCcid, StringComparison.Ordinal);
    }

    private bool CanOpenSmsReceiveMaintenanceGate(
        string portName,
        string expectedCcid,
        long expectedGeneration)
    {
        _smsSimIdentities.TryGetValue(portName, out string? smsCcid);
        _networkSimIdentities.TryGetValue(portName, out string? networkCcid);
        bool completed = _sautoNetworkStates.TryGetValue(
                portName, out SautoNetworkState? networkState)
            && string.Equals(
                networkState.Ccid,
                expectedCcid,
                StringComparison.Ordinal)
            && networkState.AutomaticUssdCompleted;
        return CanOpenSmsReceiveMaintenanceGate(
            expectedGeneration,
            CurrentSmsReceiveMaintenanceGeneration(portName),
            expectedCcid,
            smsCcid,
            networkCcid,
            completed);
    }

    private bool IsSmsReceiveMaintenanceEnabled(string portName)
    {
        return _smsReceiveMaintenanceIdentities.TryGetValue(
                portName,
                out SmsReceiveMaintenanceGate? gate)
            && CanOpenSmsReceiveMaintenanceGate(
                portName,
                gate.Ccid,
                gate.Generation);
    }

    private void EnableSmsReceiveMaintenanceAfterSauto(
        string portName,
        string expectedCcid,
        long expectedGeneration,
        string reason)
    {
        string normalized = Regex.Match(
            expectedCcid ?? string.Empty,
            @"(?<!\d)89\d{16,20}(?!\d)").Value;
        if (string.IsNullOrWhiteSpace(normalized)
            || !CanOpenSmsReceiveMaintenanceGate(
                portName,
                normalized,
                expectedGeneration))
            return;

        string activationKey =
            $"{portName}\u001f{expectedGeneration}";
        if (!_smsReceiveMaintenanceActivationOwners.TryAdd(
                activationKey,
                0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!_portLifetimeCts.TryGetValue(
                        portName,
                        out CancellationTokenSource? lifetime)
                    || lifetime.IsCancellationRequested
                    || !_semaphores.TryGetValue(
                        portName,
                        out SemaphoreSlim? semaphore))
                    return;

                // This request is raised from inside DataPort's UART critical
                // section. Acquiring the same semaphore here is the condition
                // that proves the completed *111# owner has actually released
                // the COM; no guessed post-USSD millisecond delay is used.
                await semaphore.WaitAsync(lifetime.Token).ConfigureAwait(false);
                bool opened = false;
                bool firstEnable = false;
                try
                {
                    if (!CanOpenSmsReceiveMaintenanceGate(
                            portName,
                            normalized,
                            expectedGeneration))
                        return;

                    var nextGate = new SmsReceiveMaintenanceGate(
                        normalized,
                        expectedGeneration);
                    firstEnable =
                        !_smsReceiveMaintenanceIdentities.TryGetValue(
                            portName,
                            out SmsReceiveMaintenanceGate? current)
                        || current != nextGate;
                    _smsReceiveMaintenanceIdentities[portName] = nextGate;
                    opened = true;
                }
                finally
                {
                    semaphore.Release();
                }

                if (!opened || !IsSmsReceiveMaintenanceEnabled(portName))
                    return;

                if (firstEnable)
                {
                    AtCommandTraceLogger.State(
                        portName,
                        $"SMS_MAINTENANCE_ENABLED;ccid={normalized};generation={expectedGeneration};reason={reason};after=SAUTO_AUTO_USSD_AND_UART_RELEASE");
                }

                EnsureSmsReceiveWatchdog(portName);
                ScheduleSafeUnreadSmsSweep(
                    portName,
                    $"sauto-post-ussd:{reason}");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _smsReceiveMaintenanceActivationOwners.TryRemove(
                    activationKey,
                    out _);
            }
        });
    }

    private void EnsureSmsReceiveWatchdog(string portName)
    {
        if (!_portLifetimeCts.TryGetValue(
                portName, out CancellationTokenSource? lifetime)
            || lifetime.IsCancellationRequested)
            return;

        bool newlyRegistered = _smsReceiveWatchdogLastProbeTicks.TryAdd(
            portName,
            Environment.TickCount64);
        lock (_smsReceiveWatchdogSchedulerSync)
        {
            if (_smsReceiveWatchdogSchedulerCts is { } existing
                && !existing.IsCancellationRequested
                && _smsReceiveWatchdogSchedulerTask is { IsCompleted: false })
            {
                if (newlyRegistered)
                    LogSmsReceiveWatchdogRegistration(portName);
                return;
            }

            _smsReceiveWatchdogSchedulerCts?.Dispose();
            var scheduler = new CancellationTokenSource();
            _smsReceiveWatchdogSchedulerCts = scheduler;
            _smsReceiveWatchdogSchedulerTask =
                RunSmsReceiveWatchdogSchedulerAsync(scheduler);
        }

        if (newlyRegistered)
            LogSmsReceiveWatchdogRegistration(portName);
    }

    private void LogSmsReceiveWatchdogRegistration(string portName) =>
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[SMS_WATCHDOG_REGISTERED] mode=ROUND_ROBIN; per_port_min={SmsReceiveWatchdogInterval.TotalSeconds:0}s; turn_gap={SmsReceiveWatchdogTurnGap.TotalSeconds:0}s."
        });

    private void StopSmsReceiveWatchdog(string portName)
    {
        _smsReceiveWatchdogLastProbeTicks.TryRemove(portName, out _);
        CancellationTokenSource? schedulerToCancel = null;
        lock (_smsReceiveWatchdogSchedulerSync)
        {
            bool anyEnabledPort = _smsReceiveMaintenanceIdentities.Keys.Any(
                IsSmsReceiveMaintenanceEnabled);
            if (!anyEnabledPort)
                schedulerToCancel = _smsReceiveWatchdogSchedulerCts;
        }
        try { schedulerToCancel?.Cancel(); } catch { }
    }

    private void StopAllSmsReceiveWatchdogs()
    {
        CancellationTokenSource? scheduler;
        lock (_smsReceiveWatchdogSchedulerSync)
            scheduler = _smsReceiveWatchdogSchedulerCts;
        try { scheduler?.Cancel(); } catch { }
        _smsReceiveWatchdogLastProbeTicks.Clear();
    }

    internal static int GetSmsReceiveWatchdogPortOrder(string? portName)
    {
        Match match = Regex.Match(
            portName ?? string.Empty,
            @"(\d+)$",
            RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out int number)
            ? number
            : int.MaxValue;
    }

    private bool CanRunSmsScanTurn(string portName)
    {
        return _serialPorts.TryGetValue(portName, out SerialPort? serialPort)
            && serialPort.IsOpen
            && _smsSimIdentities.ContainsKey(portName)
            && IsSmsReceiveMaintenanceEnabled(portName)
            && !_suspendedBackgroundPorts.ContainsKey(portName)
            && !IsCallInProgress(portName)
            && !_sautoInitializingPorts.ContainsKey(portName)
            && !_sautoImeiChangePorts.ContainsKey(portName)
            && !_sautoResettingPorts.ContainsKey(portName);
    }

    private async Task<IDisposable> AcquireSmsScanTurnAsync(
        CancellationToken token)
    {
        await _smsScanTurnGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long lastCompleted = Volatile.Read(
                ref _smsScanTurnCompletedTick);
            long gapMilliseconds = checked(
                (long)SmsReceiveWatchdogTurnGap.TotalMilliseconds);
            long elapsed = Environment.TickCount64 - lastCompleted;
            if (lastCompleted != 0 && elapsed < gapMilliseconds)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(gapMilliseconds - elapsed),
                        token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            _smsScanTurnGate.Release();
            throw;
        }

        return new BackgroundOperationLease(() =>
        {
            Volatile.Write(
                ref _smsScanTurnCompletedTick,
                Environment.TickCount64);
            _smsScanTurnGate.Release();
        });
    }

    private async Task RunSmsReceiveWatchdogSchedulerAsync(
        CancellationTokenSource scheduler)
    {
        CancellationToken token = scheduler.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                string[] ports = _smsReceiveMaintenanceIdentities.Keys
                    .Where(IsSmsReceiveMaintenanceEnabled)
                    .OrderBy(GetSmsReceiveWatchdogPortOrder)
                    .ThenBy(static port => port, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (ports.Length == 0)
                {
                    await Task.Delay(SmsReceiveWatchdogTurnGap, token)
                        .ConfigureAwait(false);
                    continue;
                }

                long now = Environment.TickCount64;
                long intervalMilliseconds = checked(
                    (long)SmsReceiveWatchdogInterval.TotalMilliseconds);
                string? selectedPort = null;
                int start = Math.Abs(_smsReceiveWatchdogCursor) % ports.Length;
                for (int offset = 0; offset < ports.Length; offset++)
                {
                    int index = (start + offset) % ports.Length;
                    string candidate = ports[index];
                    long lastProbe = _smsReceiveWatchdogLastProbeTicks.GetOrAdd(
                        candidate,
                        now);
                    if (now - lastProbe < intervalMilliseconds)
                        continue;

                    selectedPort = candidate;
                    _smsReceiveWatchdogCursor = (index + 1) % ports.Length;
                    _smsReceiveWatchdogLastProbeTicks[candidate] = now;
                    break;
                }

                if (selectedPort == null)
                {
                    await Task.Delay(SmsReceiveWatchdogTurnGap, token)
                        .ConfigureAwait(false);
                    continue;
                }

                await ProbeStoredSmsForWatchdogAsync(selectedPort, token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_smsReceiveWatchdogSchedulerSync)
            {
                if (ReferenceEquals(
                        _smsReceiveWatchdogSchedulerCts,
                        scheduler))
                {
                    _smsReceiveWatchdogSchedulerCts = null;
                    _smsReceiveWatchdogSchedulerTask = null;
                    _smsReceiveWatchdogCursor = 0;
                }
            }
            scheduler.Dispose();
        }
    }

    private async Task ProbeStoredSmsForWatchdogAsync(
        string portName,
        CancellationToken token)
    {
        if (!CanRunSmsScanTurn(portName))
            return;

        try
        {
            using IDisposable scanTurn =
                await AcquireSmsScanTurnAsync(token).ConfigureAwait(false);
            if (!CanRunSmsScanTurn(portName))
                return;

            using IDisposable foregroundLease =
                await AcquireForegroundOperationAsync(
                        portName,
                        "SMS_WATCHDOG_ROUND_ROBIN",
                        token)
                    .ConfigureAwait(false);
            if (!CanRunSmsScanTurn(portName))
                return;

            string response = await SendCommandAsync(
                    portName,
                    SmsReceiveWatchdogCommand,
                    timeoutMs: 5000,
                    silent: true,
                    ct: token)
                .ConfigureAwait(false);
            string safeResponse = response ?? string.Empty;
            int storedCount = Regex.Matches(
                    safeResponse,
                    @"\+CMGL:\s*\d+",
                    RegexOptions.IgnoreCase)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (storedCount > 0)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_WATCHDOG_DRAIN] mode=ROUND_ROBIN; phát hiện {storedCount} slot dù không cần +CMTI; đã chuyển vào hàng đợi đọc."
                });
            }
            else if (IsCommandFailure(safeResponse)
                     && !safeResponse.Contains(
                         "Another command",
                         StringComparison.OrdinalIgnoreCase))
            {
                ScheduleSafeUnreadSmsSweep(
                    portName,
                    "watchdog-receive-mode-repair",
                    initialDelayMs: 1000);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_WATCHDOG_RETRY] mode=ROUND_ROBIN; {ex.Message}"
            });
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

        if (!string.IsNullOrWhiteSpace(deliveryId)
            && _deliveredStoredSms.ContainsKey(deliveryId))
        {
            // Slot này thuộc một tin đã phát xong. Đọc lại (do sweep hoặc CMGL
            // trùng lượt) chỉ để dọn slot; ghi lại vào journal sẽ tạo một nhóm
            // ghép dở mới không bao giờ đủ mảnh.
            if (!string.IsNullOrWhiteSpace(msgIndex))
                indicesToDelete.Add(msgIndex);
            return null;
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
                    {
                        ScheduleSafeUnreadSmsSweep(
                            port,
                            "sms-queue-writer-recovery");
                    }
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

            // Preserve the +CMTI index, but do not emit CMGR/QCMGR while the
            // captured SAuto IMEI -> reset -> automatic *111# lifecycle owns
            // this COM. The queued index resumes as soon as that RX milestone
            // enables SMS maintenance for the same CCID.
            while (!IsSmsReceiveMaintenanceEnabled(port))
            {
                if (!IsCurrentSmsGeneration(port, state.Generation))
                    return;
                await Task.Delay(100, token).ConfigureAwait(false);
            }

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
        if (!TryBeginCommandTransaction(portName, tcs))
            return "ERROR: Another command is already in progress";

        try
        {
            if (!serialPort.IsOpen) return "ERROR: Port not open";
            AtCommandTraceLogger.Tx(portName, command + "\r\n");
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
                AtCommandTraceLogger.Timeout(portName, command);
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
            foreach (string command in SmsReceiveRestoreCommandOrder)
            {
                await SendCommandWhilePortLockedAsync(
                    portName,
                    serialPort,
                    command,
                    5000,
                    CancellationToken.None);
            }

            string cmgf = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CMGF?", 5000, CancellationToken.None);
            string cscs = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CSCS?", 5000, CancellationToken.None);
            string cnmi = await SendCommandWhilePortLockedAsync(
                portName, serialPort, "AT+CNMI?", 5000, CancellationToken.None);
            if (Regex.IsMatch(cmgf, @"\+CMGF:\s*1\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(
                    cscs, @"\+CSCS:\s*""GSM""", RegexOptions.IgnoreCase)
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
        string contentIdentity = decoded.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .Normalize(NormalizationForm.FormC);
        string timestampIdentity = decoded.SmsTimestampUtc?.ToUniversalTime()
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        string concatIdentity = decoded.Concatenation == null
            ? "single"
            : $"{decoded.Concatenation.Reference}:{decoded.Concatenation.Sequence}:{decoded.Concatenation.Total}";

        // A CMGR read changes REC UNREAD to REC READ. EC20 can also return the
        // first read as a PDU and the verification read as QCMGR text. Identity
        // must therefore come from the decoded SMS, not the transport envelope
        // or sender representation. Otherwise one physical slot is delivered
        // twice before CMGD.
        return BuildDeliveryId(
            "stored-v2",
            scope,
            msgIndex,
            timestampIdentity,
            concatIdentity,
            contentIdentity);
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
                            Data = "[SMS_RECEIVE_MODE_RESTORE_BLOCKED] Chưa xác minh lại được CMGF=1/CSCS=GSM/CNMI=1,1; giữ cleanup intent để tự phục hồi lần sau."
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
        using IDisposable foregroundLease =
            await AcquireForegroundOperationAsync(
                    port,
                    "SMS_STORED_READ",
                    ct)
                .ConfigureAwait(false);
        if (!IsSmsReceiveMaintenanceEnabled(port)
            || !IsCurrentSmsGeneration(port, generation))
            return false;

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
        DateTimeOffset? smsTimestampUtc = decoded.SmsTimestampUtc;
        if (smsTimestampUtc == null
            && TryParseSmsTimestamp(
                smsContent,
                out DateTimeOffset parsedSmsTimestamp))
        {
            smsTimestampUtc = parsedSmsTimestamp;
        }

        string sender = ParseSenderFromCmgr(smsContent);
        if (sender == "Unknown" && !string.IsNullOrWhiteSpace(decoded.Sender))
            sender = DecodeSmsSender(decoded.Sender);
        if (string.IsNullOrWhiteSpace(sender))
            sender = "Unknown";

        string storedDeliveryId = BuildStoredSmsDeliveryId(
            scope,
            msgIndex,
            smsContent);
        if (string.IsNullOrWhiteSpace(storedDeliveryId))
            return false;
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
                DeliveryId = completedDeliveryId,
                SmsTimestampUtc = smsTimestampUtc
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
                // Nhớ theo từng mảnh, không chỉ theo tin: sau khi tin đã ra,
                // slot của bất kỳ mảnh nào được đọc lại cũng phải bị nhận ra là
                // đã phát để không sinh nhóm ghép dở trùng lặp.
                foreach (string partIdentity in
                         _multipartJournal.GetPartIdentities(completedDeliveryId))
                {
                    RememberDeliveredSms(partIdentity);
                }
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

    internal static bool TryParseSmsTimestamp(
        string? raw,
        out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        Match match = Regex.Match(
            raw ?? string.Empty,
            @"(?<stamp>\d{2}/\d{2}/\d{2},\d{2}:\d{2}:\d{2})(?<zone>[+-]\d{2})?",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !DateTime.TryParseExact(
                match.Groups["stamp"].Value,
                "yy/MM/dd,HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateTime localTime))
        {
            return false;
        }

        TimeSpan offset;
        if (match.Groups["zone"].Success
            && int.TryParse(
                match.Groups["zone"].Value,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out int quarterHours)
            && Math.Abs(quarterHours) <= 56)
        {
            offset = TimeSpan.FromMinutes(quarterHours * 15);
        }
        else
        {
            offset = TimeZoneInfo.Local.GetUtcOffset(localTime);
        }

        try
        {
            timestampUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified),
                    offset)
                .ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool HasReadableModemIdentity(string? atiResponse)
    {
        string response = atiResponse ?? string.Empty;
        if (response.Contains("Quectel", StringComparison.OrdinalIgnoreCase))
            return true;
        return Regex.IsMatch(
            response,
            @"\b(?:EC|EG|BG|RG|RM|EM|EP|UC)[A-Z0-9-]{2,}\b",
            RegexOptions.IgnoreCase);
    }

    internal static bool IsStaleCmsErrorTerminator(
        string terminator,
        string? pendingCommand) =>
        terminator.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase)
        && !CanCommandReturnCmsError(pendingCommand);

    /// <summary>
    /// Chỉ các lệnh thuộc dịch vụ tin nhắn/USSD mới có thể trả +CMS ERROR. Lệnh
    /// nào không thuộc nhóm này mà nhận +CMS ERROR thì đó là phản hồi về muộn
    /// của một lệnh SMS trước đó. Không xác định được lệnh đang chờ thì giữ
    /// hành vi cũ (nhận làm phản hồi) để không bao giờ treo lệnh vô hạn.
    /// </summary>
    internal const string SmsPayloadCommandState = "SMS_PAYLOAD";

    internal static bool CanCommandReturnCmsError(string? pendingCommand)
    {
        string command = pendingCommand?.Trim() ?? string.Empty;
        if (command.Length == 0) return true;
        // Bước ghi payload sau dấu nhắc '>' không phải là một lệnh AT nhưng
        // chính +CMS ERROR mới là câu trả lời của nó (ví dụ 350 khi nhà mạng
        // chặn chiều đi). Bỏ qua ở đây sẽ làm mọi lần gửi lỗi phải chờ hết
        // timeout thay vì báo lỗi ngay.
        if (string.Equals(
                command, SmsPayloadCommandState, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Regex.IsMatch(
            command,
            @"^AT\+(?:Q?CMG[SWDRLC]|CMSS|CNMA|CNMI|CPMS|CSCA|CSCB|CSDH|CSMS|CMMS|CUSD)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool IsAtTerminalLine(string line) =>
        Regex.IsMatch(
            line,
            @"^(?:OK|ERROR|\+(?:CMS|CME)\s+ERROR\s*:[^\r\n]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsKnownUnownedCommandResponseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || IsAtTerminalLine(line))
            return true;

        // Echoed AT commands and synchronous payloads below have already been
        // published to ProcessSautoReceiveChunk. They are not SMS/call/USSD URCs
        // and are safe to discard once they no longer have a command owner.
        if (Regex.IsMatch(
                line,
                @"^AT(?:$|[+I])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            line,
            @"^\+(?:CPIN|CSQ|COPS|CFUN|CNMI|CPMS|QCCID|CCID|ICCID|EGMR|CGMI|CGMM|CGMR|GSN|CNUM|CSCA|QNWINFO|QCFG|CLCC|QSIMSTAT)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                line,
                @"^(?:RDY|APP RDY|PB DONE|SMS DONE|Call Ready)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                line,
                @"^\+QIND:\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Removes complete synchronous response frames left by SAuto-style
    /// write-only commands. Async SMS/call/USSD frames are intentionally not in
    /// the allow-list and therefore stop cleanup instead of being discarded.
    /// </summary>
    internal static string RemoveLeadingUnownedCommandResponseFrames(
        string? data,
        out IReadOnlyList<string> removedFrames)
    {
        string remaining = data ?? string.Empty;
        var removed = new List<string>();
        while (true)
        {
            Match terminal = Regex.Match(
                remaining,
                @"(?:\A|\r?\n)(?<terminal>OK|ERROR|\+(?:CMS|CME)\s+ERROR\s*:[^\r\n]*)(?:[\t ]*(?:\r?\n|$))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!terminal.Success) break;

            int end = terminal.Index + terminal.Length;
            string frame = remaining.Substring(0, end);
            string[] lines = Regex.Split(frame, @"\r?\n")
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0)
                .ToArray();
            if (lines.Length == 0
                || lines.Any(line => !IsKnownUnownedCommandResponseLine(line)))
            {
                break;
            }

            string owner = lines.FirstOrDefault(static line =>
                    !IsAtTerminalLine(line)
                    && !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                ?? "BARE";
            string terminalText = terminal.Groups["terminal"].Value.Trim();
            removed.Add(owner == "BARE"
                ? terminalText
                : $"{owner.Split(':')[0]}+{terminalText}");
            remaining = remaining.Substring(end);
        }

        removedFrames = removed;
        return remaining;
    }

    private static IReadOnlyList<string> RemoveLeadingUnownedCommandResponseFrames(
        StringBuilder buffer)
    {
        string remaining = RemoveLeadingUnownedCommandResponseFrames(
            buffer.ToString(),
            out IReadOnlyList<string> removedFrames);
        if (removedFrames.Count == 0) return removedFrames;

        buffer.Clear();
        buffer.Append(remaining);
        return removedFrames;
    }

    private static void TraceRemovedUnownedCommandResponseFrames(
        string portName,
        string? boundaryCommand,
        IReadOnlyList<string> removedFrames)
    {
        if (removedFrames.Count == 0) return;

        AtCommandTraceLogger.State(
            portName,
            $"AT_UNOWNED_RESPONSE_DRAINED;boundary={boundaryCommand?.Trim() ?? "NONE"};frames={string.Join(",", removedFrames)}");
    }

    private bool TryBeginCommandTransaction(
        string portName,
        TaskCompletionSource<string> transaction)
    {
        object gate = _portBufferLocks.GetOrAdd(
            portName,
            static _ => new object());
        lock (gate)
        {
            if (_portBuffers.TryGetValue(portName, out StringBuilder? buffer))
            {
                IReadOnlyList<string> drained =
                    RemoveLeadingUnownedCommandResponseFrames(buffer);
                TraceRemovedUnownedCommandResponseFrames(
                    portName,
                    transaction.Task.AsyncState as string,
                    drained);
            }

            return _commandTcs.TryAdd(portName, transaction);
        }
    }

    internal static bool CanTerminalFrameCompletePendingCommand(
        string? responseFrame,
        string? terminator,
        string? pendingCommand)
    {
        string frame = responseFrame ?? string.Empty;
        string terminal = terminator?.Trim() ?? string.Empty;
        string command = pendingCommand?.Trim() ?? string.Empty;
        if (command.Length == 0) return true;

        if (terminal.StartsWith(">", StringComparison.Ordinal))
            return command.StartsWith("AT+CMGS", StringComparison.OrdinalIgnoreCase);
        if (terminal.Contains("CONNECT", StringComparison.OrdinalIgnoreCase))
            return true;

        if (terminal.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return !IsStaleCmsErrorTerminator(terminal, command);
        if (!terminal.Equals("OK", StringComparison.OrdinalIgnoreCase))
            return true;

        string? requiredMarker = null;
        if (command.StartsWith("AT+CPIN?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CPIN:";
        else if (command.StartsWith("AT+CPMS?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CPMS:";
        else if (command.StartsWith("AT+CNMI?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CNMI:";
        else if (command.StartsWith("AT+CFUN?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CFUN:";
        else if (command.StartsWith("AT+COPS?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+COPS:";
        else if (Regex.IsMatch(
                     command,
                     @"^AT\+C(?:G|E)?REG\?",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string name = Regex.Match(
                command,
                @"^AT\+(C(?:G|E)?REG)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Groups[1].Value;
            requiredMarker = $"+{name}:";
        }
        else if (Regex.IsMatch(
                     command,
                     @"^AT\+CSQ(?:\s|$)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            requiredMarker = "+CSQ:";
        else if (command.StartsWith("AT+CNUM", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CNUM:";
        else if (command.StartsWith("AT+CSCA?", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+CSCA:";
        else if (command.StartsWith("AT+QNWINFO", StringComparison.OrdinalIgnoreCase))
            requiredMarker = "+QNWINFO:";
        else if (command.StartsWith("AT+QCFG", StringComparison.OrdinalIgnoreCase)
                 && !command.Contains(",", StringComparison.Ordinal))
        {
            Match query = Regex.Match(
                command,
                @"^AT\+QCFG\s*=\s*""(?<key>[^""]+)""\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!query.Success)
            {
                requiredMarker = "+QCFG:";
            }
            else
            {
                return Regex.IsMatch(
                    frame,
                    $@"\+QCFG:\s*""{Regex.Escape(query.Groups["key"].Value)}""\s*,",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        else if (command.StartsWith("AT+QCCID", StringComparison.OrdinalIgnoreCase)
                 || command.StartsWith("AT+CCID", StringComparison.OrdinalIgnoreCase)
                 || command.StartsWith("AT+ICCID", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                frame,
                @"\+(?:Q?CCID|ICCID)\s*:\s*89\d{16,20}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        else if (command.StartsWith("AT+EGMR=0,7", StringComparison.OrdinalIgnoreCase)
                 || command.StartsWith("AT+CGSN", StringComparison.OrdinalIgnoreCase)
                 || command.StartsWith("AT+GSN", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(frame, @"(?<!\d)\d{15}(?!\d)")
                || frame.Contains("+EGMR:", StringComparison.OrdinalIgnoreCase);
        }
        else if (command.Equals("ATI", StringComparison.OrdinalIgnoreCase))
            return HasReadableModemIdentity(frame);

        return requiredMarker == null
            || frame.Contains(requiredMarker, StringComparison.OrdinalIgnoreCase);
    }

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

    public static string DecodeSmsSender(string? rawSender) =>
        // Some EC20C firmware renders an alphanumeric sender as concatenated decimal ASCII:
        // 86 105 110 97 80 104 111 110 101 => "VinaPhone".
        // One shared implementation keeps every read path (live +CMT, +CMGR,
        // CMGL, PDU) and the multipart journal on the same sender string.
        SmsSenderText.Canonicalize(rawSender);
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
                ReadTimeout = 20000,
                WriteTimeout = 20000,
                Handshake = Handshake.RequestToSend,
                DtrEnable = true,
                RtsEnable = true,
                NewLine = "\r\n",
                Encoding = Encoding.UTF8,
                WriteBufferSize = 1024
            };

            SerialDataReceivedEventHandler handler =
                (s, e) => HandleDataReceived(portName, serialPort);
            serialPort.DataReceived += handler;
            serialPort.ErrorReceived +=
                (s, e) => HandleErrorReceived(portName, serialPort, e);
            serialPort.Open();
            AtCommandTraceLogger.Open(portName);

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
            _sautoReceiveStates[portName] = new SautoReceiveState();
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

    private void HandleErrorReceived(
        string portName,
        SerialPort sp,
        SerialErrorReceivedEventArgs args)
    {
        // UART overrun/frame/parity events are transient and do not prove USB removal.
        // SAuto keeps the handle alive; the next AT command determines real connectivity.
        // Actual unplugging is still handled by IOException/UnauthorizedAccessException.
        AtCommandTraceLogger.Error(
            portName,
            $"SERIAL:{args.EventType}");
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

    public Task<bool> VerifyExpectedCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = portName;
        _ = expectedCcid;
        // SAuto uses the CCID already parsed by its DataPort state. Do not send
        // an extra ICCID transaction before every SMS/call.
        return Task.FromResult(true);
    }

    internal static bool IsRadioDisabledResponse(string? response) =>
        Regex.IsMatch(response ?? string.Empty, @"\+CFUN:\s*(?:0|4)\b", RegexOptions.IgnoreCase);

    internal static int? ParseSautoCfunMode(string? response)
    {
        Match match = Regex.Match(
            response ?? string.Empty,
            @"\+CFUN:\s*(\d+)",
            RegexOptions.IgnoreCase);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out int mode)
            ? mode
            : null;
    }

    internal static (int First, int Second, int Third)?
        ParseSautoImsUtConfiguration(string? response)
    {
        Match match = Regex.Match(
            response ?? string.Empty,
            @"\+QCFG:[ \t]*""ims/ut""[ \t]*,[ \t]*([01])[ \t]*,[ \t]*([01])[ \t]*,[ \t]*([01])[ \t]*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out int first)
            || !int.TryParse(match.Groups[2].Value, out int second)
            || !int.TryParse(match.Groups[3].Value, out int third))
        {
            return null;
        }

        return (first, second, third);
    }

    internal static bool IsSautoImsUtDisabledResponse(string? response)
    {
        (int First, int Second, int Third)? config =
            ParseSautoImsUtConfiguration(response);
        return IsSautoOkResponse(response)
            && config is { First: 0, Second: 0, Third: 0 };
    }

    internal static bool RequiresSautoImsUtDisable(string? response)
    {
        (int First, int Second, int Third)? config =
            ParseSautoImsUtConfiguration(response);
        return IsSautoOkResponse(response)
            && config is { First: 1 };
    }

    internal static bool NetworkRecoveryImeiMatches(
        string? observedImei,
        string? expectedImei) =>
        ImeiManagementService.AreEquivalentImei(
            observedImei,
            expectedImei);

    internal static bool RequiresSautoControllerRestart(string? cpinResponse)
    {
        string response = cpinResponse ?? string.Empty;
        return response.Contains("CPIN: NOT READY", StringComparison.OrdinalIgnoreCase)
            || (response.Contains("+CME ERROR: 10", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("+CME ERROR: 100", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSautoCpinReadyResponse(string? response) =>
        IsSautoOkResponse(response)
        && (response?.Contains(
                "+CPIN: READY",
                StringComparison.OrdinalIgnoreCase) ?? false);

    internal static bool IsSautoSimAbsentResponse(string? response)
    {
        string value = response ?? string.Empty;
        return value.Contains(
                "+CME ERROR: 13",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "SIM NOT INSERTED",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "+CPIN: NOT INSERTED",
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendEscapeWithoutResponseAsync(
        string portName,
        CancellationToken ct)
    {
        if (!EnsurePortOpen(portName, out SerialPort? sp) || sp == null)
            throw new IOException($"Không mở được {portName} để gửi ESC.");
        if (!_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
            throw new IOException($"Không tìm thấy khóa UART của {portName}.");

        await semaphore.WaitAsync(ct);
        try
        {
            AtCommandTraceLogger.Tx(portName, "<ESC>");
            sp.Write("\u001b");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<bool> EnterSautoAirplaneModeAsync(
        string portName,
        CancellationToken ct = default)
    {
        if (!EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null)
        {
            return false;
        }
        if (!_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
            return false;

        // GSMController.airplane() calls sendAT separately for each command.
        // Therefore the DataPort loop may acquire this COM during the 1-second
        // guard or the RX wait, exactly as seen in the SAuto duplex capture.
        for (int attempt = 1; attempt <= SautoAirplaneMaxAttempts; attempt++)
        {
            long cfunRevisionAtAttemptStart =
                GetSautoReceiveSnapshot(portName).CfunRevision;
            UpdateSautoReceiveState(
                portName,
                static state => state.CfunMode = null);

            await SendSautoWriteOnlyAsync(
                portName,
                serialPort,
                semaphore,
                "AT+CFUN=4" + Environment.NewLine,
                ct);

            await Task.Delay(SautoAirplanePreQueryDelay, ct);

            await SendSautoWriteOnlyAsync(
                portName,
                serialPort,
                semaphore,
                "AT+CFUN?" + Environment.NewLine,
                ct);

            int? reportedMode = await WaitForFreshSautoCfunModeAsync(
                portName,
                cfunRevisionAtAttemptStart,
                ct);
            if (reportedMode == 4)
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"SAUTO_CFUN_CONFIRMED;attempt={attempt}/{SautoAirplaneMaxAttempts};mode=4;source=RX");
                return true;
            }

            AtCommandTraceLogger.State(
                portName,
                $"SAUTO_STEP_HOLD;step=CFUN_QUERY_4;attempt={attempt}/{SautoAirplaneMaxAttempts};mode={(reportedMode?.ToString() ?? "NO_REPORT")};next_retry_seconds=1");
            await Task.Delay(SautoAirplaneRetryDelay, ct);
        }

        return false;
    }

    private async Task<bool> EnterSautoAirplaneModeWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        bool sendEc20CnmiCallback,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= SautoAirplaneMaxAttempts; attempt++)
        {
            long cfunRevisionAtAttemptStart =
                GetSautoReceiveSnapshot(portName).CfunRevision;
            UpdateSautoReceiveState(
                portName,
                static state => state.CfunMode = null);

            // GSMController.airplane() uses sendAT, which is write-only. A bare
            // OK must never complete this state transition: only the shared RX
            // callback seeing a fresh +CFUN report can release the step.
            await WriteSautoCommandWhileLockedAsync(
                portName,
                serialPort,
                "AT+CFUN=4" + Environment.NewLine,
                ct);

            // On EC20, SAuto's asynchronous ATI callback publishes this command
            // while airplane() is inside its one-second guard interval.
            if (sendEc20CnmiCallback && attempt == 1)
            {
                await WriteSautoCommandWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+CNMI=1,1,0,0,0\r",
                    ct);
            }

            await Task.Delay(SautoAirplanePreQueryDelay, ct);

            await WriteSautoCommandWhileLockedAsync(
                portName,
                serialPort,
                "AT+CFUN?" + Environment.NewLine,
                ct);

            int? reportedMode = await WaitForFreshSautoCfunModeAsync(
                portName,
                cfunRevisionAtAttemptStart,
                ct);
            if (reportedMode == 4)
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"SAUTO_CFUN_CONFIRMED;attempt={attempt}/{SautoAirplaneMaxAttempts};mode=4;source=RX");
                return true;
            }

            AtCommandTraceLogger.State(
                portName,
                $"SAUTO_STEP_HOLD;step=CFUN_QUERY_4;attempt={attempt}/{SautoAirplaneMaxAttempts};mode={(reportedMode?.ToString() ?? "NO_REPORT")};next_retry_seconds=1");
            await Task.Delay(SautoAirplaneRetryDelay, ct);
        }

        return false;
    }

    private async Task<int?> WaitForFreshSautoCfunModeAsync(
        string portName,
        long revisionAtAttemptStart,
        CancellationToken ct)
    {
        int pollMilliseconds =
            checked((int)SautoAirplaneResponsePollDelay.TotalMilliseconds);
        int remainingMilliseconds =
            checked((int)SautoAirplaneResponseTimeout.TotalMilliseconds);

        while (remainingMilliseconds > 0)
        {
            SautoReceiveSnapshot snapshot =
                GetSautoReceiveSnapshot(portName);
            if (snapshot.CfunRevision > revisionAtAttemptStart)
                return snapshot.CfunMode;

            remainingMilliseconds -= pollMilliseconds;
            await Task.Delay(SautoAirplaneResponsePollDelay, ct);
        }

        SautoReceiveSnapshot finalSnapshot =
            GetSautoReceiveSnapshot(portName);
        return finalSnapshot.CfunRevision > revisionAtAttemptStart
            ? finalSnapshot.CfunMode
            : null;
    }

    public async Task<SautoImeiChangeResult> ChangeSautoImeiAsync(
        string portName,
        string targetImei,
        CancellationToken ct = default)
    {
        if (!EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"IMEI_CHANGE:{targetImei}: Port not open");
            return new SautoImeiChangeResult(string.Empty, false);
        }

        if (!_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
        {
            AtCommandTraceLogger.Error(
                portName,
                $"IMEI_CHANGE:{targetImei}: Semaphore missing");
            return new SautoImeiChangeResult(string.Empty, false);
        }

        string readImei = "ERROR";
        bool resetRequested = false;
        bool needAirplane = false;

        await semaphore.WaitAsync(ct);
        _sautoImeiChangePorts[portName] = 0;
        try
        {
            // GSMController.ChangeImei writes slot 7 directly and deliberately
            // ignores the terminal OK. The only progression evidence is the
            // IMEI later published by HandlePort from AT+EGMR=0,7.
            WriteSautoRawWhileLocked(
                portName,
                serialPort,
                $"AT+EGMR=1,7,\"{targetImei}\"{Environment.NewLine}",
                ct);
            AtCommandTraceLogger.State(
                portName,
                $"SAUTO_IMEI_WRITE_SENT;target={targetImei};next=RX_READBACK;guard_ms=500");
            await Task.Delay(SautoImeiWriteGuardDelay, ct);

            UpdateSautoReceiveState(
                portName,
                static state => state.Imei = string.Empty);

            for (int attempt = 1;
                 attempt <= SautoImeiReadMaxAttempts;
                 attempt++)
            {
                WriteSautoRawWhileLocked(
                    portName,
                    serialPort,
                    $"AT+EGMR=0,7{Environment.NewLine}",
                    ct);
                await Task.Delay(SautoImeiReadInitialDelay, ct);

                int remainingMilliseconds = checked(
                    (int)SautoImeiReadTimeout.TotalMilliseconds);
                int pollMilliseconds = checked(
                    (int)SautoImeiReadPollDelay.TotalMilliseconds);
                while (remainingMilliseconds > 0)
                {
                    string observedImei =
                        GetSautoReceiveSnapshot(portName).Imei;
                    if (!string.IsNullOrWhiteSpace(observedImei))
                    {
                        readImei = observedImei;
                        break;
                    }

                    remainingMilliseconds -= pollMilliseconds;
                    await Task.Delay(SautoImeiReadPollDelay, ct);
                }

                if (!string.Equals(
                        readImei,
                        "ERROR",
                        StringComparison.Ordinal))
                {
                    AtCommandTraceLogger.State(
                        portName,
                        $"SAUTO_IMEI_READBACK;attempt={attempt}/{SautoImeiReadMaxAttempts};imei={readImei};source=RX");
                    break;
                }

                AtCommandTraceLogger.State(
                    portName,
                    $"SAUTO_STEP_HOLD;step=EGMR_READBACK;attempt={attempt}/{SautoImeiReadMaxAttempts};next_retry_seconds=1");
                await Task.Delay(SautoImeiReadRetryDelay, ct);
            }

            if (!string.Equals(
                    readImei,
                    targetImei,
                    StringComparison.Ordinal))
            {
                needAirplane = true;
            }
            else
            {
                Guid resetGeneration = Guid.NewGuid();
                _sautoResettingPorts[portName] = resetGeneration;
                try
                {
                    // ResetModemAsync uses SerialPort.Write and then sleeps for
                    // ten seconds. It does not wait for OK/RDY/CPIN.
                    WriteSautoRawWhileLocked(
                        portName,
                        serialPort,
                        $"AT+CFUN=1,1{Environment.NewLine}",
                        ct);
                    resetRequested = true;
                    AtCommandTraceLogger.State(
                        portName,
                        "SAUTO_RESET_SENT;command=AT+CFUN=1,1;terminal_gate=OFF;guard_seconds=10");
                    await Task.Delay(SautoImeiResetGuardDelay, ct);
                }
                finally
                {
                    RemoveSautoResetGeneration(
                        portName,
                        resetGeneration);
                }

                UpdateSautoReceiveState(
                    portName,
                    static state =>
                    {
                        state.RestartRequired = false;
                        state.CpinResponse = string.Empty;
                        state.Carrier = string.Empty;
                        state.NetworkType = string.Empty;
                        state.CsqResponse = string.Empty;
                        state.CopsResponse = string.Empty;
                    });
                _sautoNetworkStates.TryRemove(portName, out _);
            }
        }
        finally
        {
            _sautoImeiChangePorts.TryRemove(portName, out _);
            semaphore.Release();
        }

        // This is the final needAirplane branch in ChangeImei. It executes only
        // after releasing the ChangeImei UART ownership, exactly like SAuto.
        if (needAirplane)
            await EnterSautoAirplaneModeAsync(portName, ct);

        return new SautoImeiChangeResult(readImei, resetRequested);
    }

    private void RemoveSautoResetGeneration(
        string portName,
        Guid resetGeneration)
    {
        if (_sautoResettingPorts.TryGetValue(
                portName,
                out Guid currentGeneration)
            && currentGeneration == resetGeneration)
        {
            _sautoResettingPorts.TryRemove(portName, out _);
        }
    }

    private async Task EnsureSautoImsUtDisabledWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        CancellationToken ct)
    {
        string beforeResponse;
        try
        {
            beforeResponse =
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    SautoImsUtQueryCommand,
                    TimeSpan.FromSeconds(5),
                    ct);
        }
        catch (TimeoutException ex)
        {
            AtCommandTraceLogger.State(
                portName,
                $"IMS_UT_CHECK_TIMEOUT;action=SKIP_WRITE_CONTINUE;message={ex.Message}");
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[IMS_UT_CHECK_WARN] Không đọc được ims/ut; bỏ qua ghi để tránh đổi cấu hình mù."
            });
            return;
        }

        (int First, int Second, int Third)? before =
            ParseSautoImsUtConfiguration(beforeResponse);
        if (IsSautoImsUtDisabledResponse(beforeResponse))
        {
            AtCommandTraceLogger.State(
                portName,
                "IMS_UT_CHECK;state=0,0,0;action=SKIP_WRITE;next=SAUTO_INITIALIZATION");
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[IMS_UT_READY] ims/ut=0,0,0; đã đúng nên không ghi lại."
            });
            return;
        }

        string beforeState = before is { } beforeValue
            ? $"{beforeValue.First},{beforeValue.Second},{beforeValue.Third}"
            : GetSautoResponseOutcome(beforeResponse);
        if (!RequiresSautoImsUtDisable(beforeResponse))
        {
            AtCommandTraceLogger.State(
                portName,
                $"IMS_UT_CHECK;state={beforeState};action=SKIP_UNRECOGNIZED_OR_ALREADY_DISABLED");
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[IMS_UT_CHECK_WARN] ims/ut={beforeState}; không ghi vì phản hồi không phải trạng thái bật hợp lệ."
            });
            return;
        }

        AtCommandTraceLogger.State(
            portName,
            $"IMS_UT_CHECK;state={beforeState};action=SET_0");
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[IMS_UT_REPAIR] ims/ut={beforeState}; đang đặt về 0,0,0."
        });

        string setResponse;
        try
        {
            setResponse =
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    SautoImsUtDisableCommand,
                    TimeSpan.FromSeconds(5),
                    ct);
        }
        catch (TimeoutException ex)
        {
            // The modem may have accepted the write even when its terminal OK
            // arrived late. Never repeat the write blindly; verify once below.
            setResponse = string.Empty;
            AtCommandTraceLogger.State(
                portName,
                $"IMS_UT_SET_ACK_TIMEOUT;action=VERIFY;message={ex.Message}");
        }

        string verificationResponse;
        try
        {
            verificationResponse =
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    SautoImsUtQueryCommand,
                    TimeSpan.FromSeconds(5),
                    ct);
        }
        catch (TimeoutException ex)
        {
            AtCommandTraceLogger.State(
                portName,
                $"IMS_UT_VERIFY_TIMEOUT;action=CONTINUE_WITH_WARNING;message={ex.Message}");
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[IMS_UT_REPAIR_WARN] Đã gửi lệnh tắt nhưng chưa đọc lại được 0,0,0."
            });
            return;
        }
        if (!IsSautoImsUtDisabledResponse(verificationResponse))
        {
            (int First, int Second, int Third)? after =
                ParseSautoImsUtConfiguration(verificationResponse);
            string afterState = after is { } afterValue
                ? $"{afterValue.First},{afterValue.Second},{afterValue.Third}"
                : GetSautoResponseOutcome(verificationResponse);
            AtCommandTraceLogger.State(
                portName,
                $"IMS_UT_VERIFY_FAILED;before={beforeState};set={GetSautoResponseOutcome(setResponse)};after={afterState};action=CONTINUE_WITH_WARNING");
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[IMS_UT_REPAIR_WARN] Chưa xác minh được 0,0,0 (sau={afterState})."
            });
            return;
        }

        AtCommandTraceLogger.State(
            portName,
            $"IMS_UT_VERIFIED;state=0,0,0;set={GetSautoResponseOutcome(setResponse)};next=SAUTO_INITIALIZATION");
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = "[IMS_UT_READY] Đã đặt và xác minh ims/ut=0,0,0."
        });
    }

    private async Task EnsureSautoOptionalFirmwareSettingsWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        CancellationToken ct)
    {
        // These settings are optional across EC20 firmware revisions. Always
        // query first, and only write when the response is readable and wrong.
        // An unsupported optional key must never abort IMEI or SIM handling.
        await TryEnsureSautoOptionalSettingWhileLockedAsync(
            portName,
            serialPort,
            SautoNetworkModeQueryCommand,
            SautoNetworkModeAutoCommand,
            "nwscanmode",
            response => ParseSautoQcfgFirstValue(response, "nwscanmode") is not null,
            response => ParseSautoQcfgFirstValue(response, "nwscanmode") == 0,
            ct);
        await TryEnsureSautoOptionalSettingWhileLockedAsync(
            portName,
            serialPort,
            SautoServiceDomainQueryCommand,
            SautoServiceDomainCsPsCommand,
            "servicedomain",
            response => ParseSautoQcfgFirstValue(response, "servicedomain") is not null,
            response => ParseSautoQcfgFirstValue(response, "servicedomain") == 2,
            ct);
        await TryEnsureSautoOptionalSettingWhileLockedAsync(
            portName,
            serialPort,
            SautoMbnAutoSelQueryCommand,
            SautoMbnAutoSelEnableCommand,
            "mbn-autosel",
            response => ParseSautoMbnAutoSelValue(response) is not null,
            response => ParseSautoMbnAutoSelValue(response) == 1,
            ct);
    }

    private async Task TryEnsureSautoOptionalSettingWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        string queryCommand,
        string setCommand,
        string settingName,
        Func<string, bool> isReadable,
        Func<string, bool> isConfigured,
        CancellationToken ct)
    {
        string queryResponse;
        try
        {
            queryResponse = await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                queryCommand,
                TimeSpan.FromSeconds(5),
                ct);
        }
        catch (TimeoutException ex)
        {
            AtCommandTraceLogger.State(
                portName,
                $"FIRMWARE_SETTING_SKIP;setting={settingName};reason=QUERY_TIMEOUT;message={ex.Message}");
            return;
        }

        if (!IsSautoOkResponse(queryResponse) || !isReadable(queryResponse))
        {
            AtCommandTraceLogger.State(
                portName,
                $"FIRMWARE_SETTING_SKIP;setting={settingName};reason=UNSUPPORTED_OR_UNREADABLE");
            return;
        }
        if (isConfigured(queryResponse))
        {
            AtCommandTraceLogger.State(
                portName,
                $"FIRMWARE_SETTING_READY;setting={settingName};action=SKIP_WRITE");
            return;
        }

        string setResponse;
        try
        {
            setResponse = await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                setCommand,
                TimeSpan.FromSeconds(8),
                ct);
        }
        catch (TimeoutException ex)
        {
            AtCommandTraceLogger.State(
                portName,
                $"FIRMWARE_SETTING_WARN;setting={settingName};reason=SET_TIMEOUT;message={ex.Message}");
            return;
        }

        AtCommandTraceLogger.State(
            portName,
            $"FIRMWARE_SETTING_SET;setting={settingName};result={GetSautoResponseOutcome(setResponse)}");
    }

    internal static int? ParseSautoQcfgFirstValue(
        string? response,
        string key)
    {
        Match match = Regex.Match(
            response ?? string.Empty,
            $@"\+QCFG:\s*""{Regex.Escape(key)}""\s*,\s*(-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out int value)
            ? value
            : null;
    }

    internal static int? ParseSautoMbnAutoSelValue(string? response)
    {
        Match match = Regex.Match(
            response ?? string.Empty,
            @"\+QMBNCFG:\s*""AutoSel""\s*,\s*([01])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out int value)
            ? value
            : null;
    }

    private async Task<SautoInitializationResult> RunSautoInitializationSequenceAsync(
        string portName,
        CancellationToken ct)
    {
        if (!EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null)
        {
            throw new IOException(
                $"Không mở được {portName} để khởi tạo modem.");
        }
        if (!_semaphores.TryGetValue(
                portName,
                out SemaphoreSlim? semaphore))
        {
            throw new IOException(
                $"Không tìm thấy khóa UART của {portName}.");
        }

        await semaphore.WaitAsync(ct);
        _sautoInitializingPorts[portName] = 0;
        try
        {
            AtCommandTraceLogger.Tx(portName, "<ESC>");
            serialPort.Write(
                new byte[] { 27 },
                0,
                1);

            // ESC has no terminal response. SAuto gives the modem command
            // parser one guard interval before ATI; without it EC20 ignores
            // the first ATI and every port enters an artificial timeout/retry.
            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
            // Never discard inbound UART bytes here: a +CMTI or direct +CMT may
            // arrive during the startup guard. Feed every pending byte through
            // the normal durable receive parser before issuing ATI.
            if (serialPort.BytesToRead > 0)
                HandleDataReceived(portName, serialPort);
            serialPort.DiscardOutBuffer();
            AtCommandTraceLogger.State(
                portName,
                "SAUTO_START_GUARD_DONE;rx=preserved;next=ATI");

            string atiResponse = string.Empty;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    atiResponse =
                        await WriteSautoCommandForResponseWhileLockedAsync(
                            portName,
                            serialPort,
                            "ATI \r",
                            TimeSpan.FromSeconds(3),
                            ct);
                }
                catch (TimeoutException exception)
                {
                    AtCommandTraceLogger.State(
                        portName,
                        $"SAUTO_STEP_HOLD;step=ATI_IDENTITY;attempt={attempt}/5;result=TIMEOUT;message={exception.Message}");
                    continue;
                }

                if (IsSautoOkResponse(atiResponse)
                    && HasReadableModemIdentity(atiResponse))
                {
                    break;
                }

                AtCommandTraceLogger.State(
                    portName,
                    $"SAUTO_STEP_HOLD;step=ATI_IDENTITY;attempt={attempt}/5;result={GetSautoResponseOutcome(atiResponse)}");
                atiResponse = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(atiResponse))
            {
                throw new TimeoutException(
                    $"{portName} không trả danh tính ATI hợp lệ sau 5 phản hồi/thời hạn.");
            }
            SautoReceiveSnapshot identity =
                GetSautoReceiveSnapshot(portName);
            bool isEc20 =
                atiResponse.Contains(
                    "EC20",
                    StringComparison.OrdinalIgnoreCase)
                || identity.Model.Contains(
                    "EC20",
                    StringComparison.OrdinalIgnoreCase)
                || identity.Firmware.Contains(
                    "EC20",
                    StringComparison.OrdinalIgnoreCase);

            if (isEc20)
            {
                await EnsureSautoImsUtDisabledWhileLockedAsync(
                    portName,
                    serialPort,
                    ct);
                await EnsureSautoOptionalFirmwareSettingsWhileLockedAsync(
                    portName,
                    serialPort,
                    ct);
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+CPMS=\"ME\",\"SM\",\"MT\"\r",
                    TimeSpan.FromSeconds(10),
                    ct);
            }

            bool radioLocked =
                await EnterSautoAirplaneModeWhileLockedAsync(
                    portName,
                    serialPort,
                    sendEc20CnmiCallback: isEc20,
                    ct);

            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+EGMR=0,7; \r",
                TimeSpan.FromSeconds(12),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CNMI? \r",
                TimeSpan.FromSeconds(10),
                ct);

            if (isEc20)
            {
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+CSCS=\"GSM\"\r",
                    TimeSpan.FromSeconds(10),
                    ct);
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+QURCCFG=\"urcport\",\"uart1\"\r",
                    TimeSpan.FromSeconds(10),
                    ct);
            }
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CMGF=1\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CPMS=\"SM\",\"SM\",\"SM\"\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CMGD=1,4\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CPMS=\"ME\",\"ME\",\"ME\"\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CMGD=1,4\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CPMS=\"SM\",\"SM\",\"SM\"\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CPMS?\r",
                TimeSpan.FromSeconds(10),
                ct);
            await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                "AT+CNMI=1,1,0,0,0\r",
                TimeSpan.FromSeconds(10),
                ct);

            if (isEc20)
            {
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    SautoNetworkModeAutoCommand,
                    TimeSpan.FromSeconds(10),
                    ct);
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+QURCCFG=\"urcport\",\"uart1\"",
                    TimeSpan.FromSeconds(10),
                    ct);
            }

            UpdateSautoReceiveState(
                portName,
                static state =>
                {
                    state.SimReady = false;
                    state.SimLocked = false;
                    state.ReadyTransitionPending = false;
                    state.CpinResponse = string.Empty;
                });
            string initialCpinResponse =
                await WriteSautoCommandForResponseWhileLockedAsync(
                    portName,
                    serialPort,
                    "AT+CPIN?",
                    TimeSpan.FromSeconds(10),
                    ct);
            UpdateSautoReceiveState(
                portName,
                state =>
                    state.CpinResponse = initialCpinResponse);

            SautoReceiveSnapshot snapshot =
                GetSautoReceiveSnapshot(portName);
            QuectelModemProfile profile =
                QuectelModemProfile.FromIdentity(
                    snapshot.Manufacturer,
                    snapshot.Model,
                    snapshot.Firmware);
            _modemProfiles[portName] = profile;
            _portVendors[portName] = profile.IsQuectel
                ? "QUECTEL"
                : snapshot.Manufacturer.ToUpperInvariant();
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[MODEM_PROFILE] manufacturer={profile.Manufacturer}; model={profile.Model}; firmware={profile.Firmware}; capabilities={profile.CapabilityText}"
            });

            return new SautoInitializationResult(
                profile,
                snapshot.Imei,
                snapshot.CpinResponse,
                radioLocked);
        }
        finally
        {
            _sautoInitializingPorts.TryRemove(portName, out _);
            semaphore.Release();
        }
    }

    private async Task InitializeModemCoreAsync(
        string portName,
        CancellationToken ct)
    {
        SautoInitializationResult result = await RunSautoInitializationSequenceAsync(
            portName,
            ct);
        if (!result.RadioLocked)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[STATUS_NO_RESPONSE] SAuto không xác nhận được CFUN=4 sau 5 lần; tiếp tục DataPort."
            });
        }

        string cleanImei = Regex.Match(
            result.ImeiResponse,
            @"(?<!\d)\d{15}(?!\d)").Value;
        if (!string.IsNullOrWhiteSpace(cleanImei))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[PARSE_IMEI] {cleanImei}"
            });
        }

        string cpinResponse = result.CpinResponse;
        if (RequiresSautoControllerRestart(cpinResponse))
        {
            return;
        }

        SautoReceiveSnapshot state = GetSautoReceiveSnapshot(portName);
        bool simReady = state.SimReady;
        if (!simReady)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[NO_SIM_READY] imei={cleanImei}"
            });
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[WAITING_FOR_SIM] Không đọc được SIM"
            });
        }

        StartHotplugWaitLoop(
            portName,
            simReady,
            completeInitialProbe: true);
    }

    private async Task InitializeModemAsync(string portName, CancellationToken ct)
    {
        try
        {
            await InitializeModemCoreAsync(portName, ct);
        }
        catch (OperationCanceledException) { }
    }


    private static bool IsCommandFailure(string response) =>
        string.IsNullOrWhiteSpace(response)
        || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || response.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

    internal static bool HasReadableCcid(string response) =>
        !IsCommandFailure(response)
        && Regex.IsMatch(response, @"(?<!\d)89\d{16,20}(?!\d)");

    public async Task ReloadSimAsync(string portName)
    {
        await ReconnectPortAsync(portName, 115200);
    }

    public Task<bool> ReloadAndResumeSimAsync(
        string portName,
        CancellationToken ct = default) =>
        ReconnectPortAsync(portName, 115200, ct);

    public async Task<bool> ReinitializeSettingsAsync(
        string portName,
        CancellationToken ct = default)
    {
        if (!_serialPorts.ContainsKey(portName)) return false;
        await InitializeModemCoreAsync(portName, ct);
        return true;
    }
    public void StartHotplugWaitLoop(string portName) =>
        StartHotplugWaitLoop(
            portName,
            simReadyInitially: false,
            completeInitialProbe: false);

    private void StartHotplugWaitLoop(
        string portName,
        bool simReadyInitially,
        bool completeInitialProbe)
    {
        if (_suspendedBackgroundPorts.ContainsKey(portName)) return;

        // A completed CFUN=1,1 starts a fresh DataPort cycle. A CME 10 /
        // CPIN NOT READY flag from the previous modem lifetime must not make
        // this new hot-plug loop exit before it can query CPIN and ICCID.
        UpdateSautoReceiveState(
            portName,
            static state =>
            {
                state.RestartRequired = false;
                state.CpinResponse = string.Empty;
            });

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
            bool simReady = simReadyInitially;
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[WAITING_FOR_SIM] Đang chờ SIM theo vòng DataPort của SAuto"
            });

            if (completeInitialProbe)
            {
                try
                {
                    await SendSautoCommandForResponseAsync(
                        portName,
                        "AT+EGMR=0,7; \r",
                        TimeSpan.FromSeconds(12),
                        token);
                    simReady = GetSautoReceiveSnapshot(portName).SimReady;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            while (IsCurrentLoop() && _serialPorts.ContainsKey(portName))
            {
                try
                {
                    if (!EnsurePortOpen(
                            portName,
                            out SerialPort? hotplugPort)
                        || hotplugPort == null
                        || !_semaphores.TryGetValue(
                            portName,
                            out SemaphoreSlim? hotplugSemaphore))
                    {
                        break;
                    }

                    await hotplugSemaphore.WaitAsync(token);
                    try
                    {
                        SautoReceiveSnapshot beforeProbe =
                            GetSautoReceiveSnapshot(portName);
                        if (beforeProbe.RestartRequired) break;
                        simReady = beforeProbe.SimReady;

                        if (!simReady)
                        {
                            UpdateSautoReceiveState(
                                portName,
                                static state =>
                                {
                                    state.SimReady = false;
                                    state.SimLocked = false;
                                    state.ReadyTransitionPending = false;
                                    state.Ccid = string.Empty;
                                    state.CpinResponse = string.Empty;
                                });
                            string cpinResponse =
                                await WriteSautoCommandForResponseWhileLockedAsync(
                                    portName,
                                    hotplugPort,
                                    "AT+CPIN?",
                                    TimeSpan.FromSeconds(10),
                                    token);

                            SautoReceiveSnapshot afterCpin =
                                GetSautoReceiveSnapshot(portName);
                            if (afterCpin.RestartRequired) break;
                            simReady =
                                IsSautoCpinReadyResponse(cpinResponse)
                                && afterCpin.SimReady;
                            bool simAbsent =
                                IsSautoSimAbsentResponse(cpinResponse);
                            if (!simReady && !simAbsent)
                            {
                                AtCommandTraceLogger.State(
                                    portName,
                                    $"SAUTO_STEP_HOLD;step=CPIN_DECISION;result={GetSautoResponseOutcome(cpinResponse)}");
                                continue;
                            }

                            string imeiResponse =
                                await WriteSautoCommandForResponseWhileLockedAsync(
                                    portName,
                                    hotplugPort,
                                    "AT+EGMR=0,7; \r",
                                    TimeSpan.FromSeconds(12),
                                    token);
                            bool imeiAccepted =
                                IsSautoOkResponse(imeiResponse)
                                && Regex.IsMatch(
                                    imeiResponse,
                                    @"(?<!\d)\d{15}(?!\d)");
                            if (!imeiAccepted)
                            {
                                AtCommandTraceLogger.State(
                                    portName,
                                    $"SAUTO_STEP_HOLD;step=READ_IMEI;result={GetSautoResponseOutcome(imeiResponse)}");
                            }

                            if (simReady
                                && imeiAccepted
                                && string.IsNullOrWhiteSpace(
                                    GetSautoReceiveSnapshot(portName).Ccid))
                            {
                                string ccidResponse =
                                    await WriteSautoCommandForResponseWhileLockedAsync(
                                        portName,
                                        hotplugPort,
                                        "AT+ICCID \r",
                                        TimeSpan.FromSeconds(10),
                                        token);
                                if (!HasReadableCcid(ccidResponse))
                                {
                                    AtCommandTraceLogger.State(
                                        portName,
                                        $"SAUTO_STEP_HOLD;step=READ_ICCID;result={GetSautoResponseOutcome(ccidResponse)}");
                                }
                            }

                            SautoReceiveSnapshot completed =
                                GetSautoReceiveSnapshot(portName);
                            if (imeiAccepted
                                && completed.SimReady
                                && !string.IsNullOrWhiteSpace(completed.Imei)
                                && !string.IsNullOrWhiteSpace(completed.Ccid))
                            {
                                break;
                            }
                        }
                        else
                        {
                            UpdateSautoReceiveState(
                                portName,
                                static state =>
                                    state.CpinResponse = string.Empty);
                            string cpinResponse =
                                await WriteSautoCommandForResponseWhileLockedAsync(
                                    portName,
                                    hotplugPort,
                                    "AT+CPIN? \r",
                                    TimeSpan.FromSeconds(10),
                                    token);

                            SautoReceiveSnapshot afterCpin =
                                GetSautoReceiveSnapshot(portName);
                            if (afterCpin.RestartRequired) break;
                            simReady =
                                IsSautoCpinReadyResponse(cpinResponse)
                                && afterCpin.SimReady;
                            bool imeiReady =
                                !string.IsNullOrWhiteSpace(afterCpin.Imei);
                            if (simReady && !imeiReady)
                            {
                                string imeiResponse =
                                    await WriteSautoCommandForResponseWhileLockedAsync(
                                        portName,
                                        hotplugPort,
                                        "AT+EGMR=0,7; \r",
                                        TimeSpan.FromSeconds(12),
                                        token);
                                imeiReady =
                                    IsSautoOkResponse(imeiResponse)
                                    && Regex.IsMatch(
                                        imeiResponse,
                                        @"(?<!\d)\d{15}(?!\d)");
                            }

                            if (simReady
                                && imeiReady
                                && string.IsNullOrWhiteSpace(afterCpin.Ccid))
                            {
                                string ccidResponse =
                                    await WriteSautoCommandForResponseWhileLockedAsync(
                                        portName,
                                        hotplugPort,
                                        "AT+ICCID \r",
                                        TimeSpan.FromSeconds(10),
                                        token);
                                if (HasReadableCcid(ccidResponse)
                                    && !string.IsNullOrWhiteSpace(
                                        GetSautoReceiveSnapshot(portName).Ccid))
                                {
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        hotplugSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[WAITING_FOR_SIM] Lặp DataPort lỗi: {ex.Message}"
                    });
                }

                try { await Task.Delay(400, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
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
        long smsReceiveMaintenanceGeneration =
            CurrentSmsReceiveMaintenanceGeneration(portName);
        CancellationTokenSource loopCts;

        lock (_backgroundOperationSync)
        {
            if (!IsNetworkSimIdentityCurrent(portName, normalizedExpectedCcid))
                return;

            if (_suspendedBackgroundPorts.ContainsKey(portName))
            {
                _pendingNetworkPollingPorts[portName] = expectedIdentity;
                return;
            }

            _pendingNetworkPollingPorts.TryRemove(portName, out _);
            lock (_pollingCts)
            {
                if (_pollingCts.TryGetValue(portName, out CancellationTokenSource? oldCts))
                {
                    try { oldCts.Cancel(); } catch { }
                    oldCts.Dispose();
                }

                loopCts = new CancellationTokenSource();
                _pollingCts[portName] = loopCts;
                _pollingExpectedIdentities[portName] = expectedIdentity;
            }
        }

        CancellationToken token = loopCts.Token;
        string carrier = string.Empty;
        string networkType = string.Empty;
        bool automaticUssdCompleted = false;
        DateTimeOffset lastNetworkCheckUtc = DateTimeOffset.MinValue;
        DateTimeOffset lastAutomaticUssdAttemptUtc = DateTimeOffset.MinValue;
        long lastObservedUssdRevision =
            GetSautoReceiveSnapshot(portName).UssdRevision;
        if (_sautoNetworkStates.TryGetValue(portName, out SautoNetworkState? cached)
            && string.Equals(cached.Ccid, normalizedExpectedCcid, StringComparison.Ordinal))
        {
            carrier = cached.Carrier;
            networkType = cached.NetworkType;
            automaticUssdCompleted = cached.AutomaticUssdCompleted;
            lastAutomaticUssdAttemptUtc =
                cached.LastAutomaticUssdAttemptUtc;
        }

        if (automaticUssdCompleted)
        {
            EnableSmsReceiveMaintenanceAfterSauto(
                portName,
                normalizedExpectedCcid,
                smsReceiveMaintenanceGeneration,
                "cached-automatic-ussd-complete");
        }

        _ = Task.Run(async () =>
        {
            int consecutiveCopsNoCarrierResponses = 0;
            while (!token.IsCancellationRequested
                   && _serialPorts.ContainsKey(portName)
                   && IsNetworkPollingIdentityCurrent(portName, expectedIdentity))
            {
                try
                {
                    if (!EnsurePortOpen(
                            portName,
                            out SerialPort? networkPort)
                        || networkPort == null
                        || !_semaphores.TryGetValue(
                            portName,
                            out SemaphoreSlim? networkSemaphore))
                    {
                        break;
                    }

                    await networkSemaphore.WaitAsync(token);
                    try
                    {
                        // This is GSMController.DataPort: sendAT is write-only,
                        // then the receive callback updates simReady/networkGSM.
                        // Do not turn terminal OK/ERROR into extra progression
                        // gates that SAuto does not have.
                        await WriteSautoCommandWhileLockedAsync(
                            portName,
                            networkPort,
                            SautoNetworkPollingCommandOrder[0],
                            token);
                        await Task.Delay(SautoDataPortStepDelay, token);

                        SautoReceiveSnapshot afterCpin =
                            GetSautoReceiveSnapshot(portName);
                        if (afterCpin.RestartRequired)
                        {
                            _sautoNetworkStates.TryRemove(portName, out _);
                            break;
                        }

                        if (!afterCpin.SimReady)
                        {
                            AtCommandTraceLogger.State(
                                portName,
                                $"SAUTO_STEP_HOLD;step=CPIN_READY;result={GetSautoResponseOutcome(afterCpin.CpinResponse)}");
                        }
                        else
                        {
                            await WriteSautoCommandWhileLockedAsync(
                                portName,
                                networkPort,
                                SautoNetworkPollingCommandOrder[1],
                                token);
                            await Task.Delay(SautoDataPortStepDelay, token);

                            SautoReceiveSnapshot afterCsq =
                                GetSautoReceiveSnapshot(portName);
                            if (IsSautoCarrierRegistered(afterCsq.Carrier))
                            {
                                carrier = afterCsq.Carrier;
                                networkType = afterCsq.NetworkType;
                            }

                            if (ShouldQuerySautoNetwork(
                                    carrier,
                                    DateTimeOffset.UtcNow,
                                    lastNetworkCheckUtc))
                            {
                                await WriteSautoCommandWhileLockedAsync(
                                    portName,
                                    networkPort,
                                    SautoNetworkPollingCommandOrder[2],
                                    token);
                                await Task.Delay(
                                    SautoDataPortStepDelay,
                                    token);
                                lastNetworkCheckUtc = DateTimeOffset.UtcNow;

                                SautoReceiveSnapshot afterCops =
                                    GetSautoReceiveSnapshot(portName);
                                carrier = afterCops.Carrier;
                                networkType = afterCops.NetworkType;
                                if (IsSautoCarrierRegistered(carrier))
                                {
                                    consecutiveCopsNoCarrierResponses = 0;
                                    _sautoNetworkStates[portName] =
                                        new SautoNetworkState(
                                            normalizedExpectedCcid,
                                            carrier,
                                            networkType,
                                            AutomaticUssdCompleted:
                                                automaticUssdCompleted,
                                            LastAutomaticUssdAttemptUtc:
                                                lastAutomaticUssdAttemptUtc);
                                    LogMessage?.Invoke(
                                        this,
                                        new GsmDataEventArgs
                                        {
                                            PortName = portName,
                                            Data =
                                                $"[SAUTO_NETWORK_READY] ccid={normalizedExpectedCcid}; carrier={carrier}; type={networkType}"
                                        });
                                }
                                else
                                {
                                    consecutiveCopsNoCarrierResponses++;
                                    AtCommandTraceLogger.State(
                                        portName,
                                        $"SAUTO_STEP_HOLD;step=COPS_REGISTERED;result={GetSautoResponseOutcome(afterCops.CopsResponse)};next_retry_seconds=2;no_carrier_count={consecutiveCopsNoCarrierResponses}");
                                    // Sau 5 lần COPS liên tiếp không có carrier (~12s), gửi AT+COPS=0,0
                                    // để force firmware tự đăng ký mạng (giúp EC20CEFAGR08A03M4G boot chậm).
                                    if (consecutiveCopsNoCarrierResponses >= 5)
                                    {
                                        consecutiveCopsNoCarrierResponses = 0;
                                        AtCommandTraceLogger.State(
                                            portName,
                                            "SAUTO_NETWORK_REREGISTER;reason=COPS_NO_CARRIER;action=AT+COPS=0,0");
                                        try
                                        {
                                            await WriteSautoCommandWhileLockedAsync(
                                                portName,
                                                networkPort,
                                                "AT+COPS=0,0",
                                                token);
                                            await Task.Delay(SautoDataPortStepDelay, token);
                                        }
                                        catch (Exception reregEx)
                                            when (reregEx is TimeoutException
                                                          or IOException
                                                          or InvalidOperationException)
                                        {
                                            AtCommandTraceLogger.Error(
                                                portName,
                                                $"SAUTO_NETWORK_REREGISTER_FAILED;detail={reregEx.Message}");
                                        }
                                    }
                                }
                            }

                            // A COPS response can arrive just after the 100 ms
                            // receive window. SAuto observes the shared field on
                            // the next statement/loop, so synchronize it here too.
                            SautoReceiveSnapshot latestUssdState =
                                GetSautoReceiveSnapshot(portName);
                            if (!IsSautoCarrierRegistered(carrier)
                                && IsSautoCarrierRegistered(
                                    latestUssdState.Carrier))
                            {
                                carrier = latestUssdState.Carrier;
                                networkType = latestUssdState.NetworkType;
                            }

                            if (!automaticUssdCompleted
                                && latestUssdState.UssdRevision
                                    > lastObservedUssdRevision
                                && IsSautoAutomatic111Completion(
                                    latestUssdState.UssdResponse))
                            {
                                automaticUssdCompleted = true;
                                _sautoNetworkStates[portName] =
                                    new SautoNetworkState(
                                        normalizedExpectedCcid,
                                        carrier,
                                        networkType,
                                        AutomaticUssdCompleted: true,
                                        LastAutomaticUssdAttemptUtc:
                                            lastAutomaticUssdAttemptUtc);
                                EnableSmsReceiveMaintenanceAfterSauto(
                                    portName,
                                    normalizedExpectedCcid,
                                    smsReceiveMaintenanceGeneration,
                                    "late-111-rx");
                            }
                            lastObservedUssdRevision = Math.Max(
                                lastObservedUssdRevision,
                                latestUssdState.UssdRevision);

                            if (!automaticUssdCompleted
                                && IsSautoCarrierRegistered(carrier))
                            {
                                string automaticUssd =
                                    GetSautoAutomaticUssdCode(carrier);
                                if (string.IsNullOrWhiteSpace(
                                        automaticUssd))
                                {
                                    automaticUssdCompleted = true;
                                    _sautoNetworkStates[portName] =
                                        new SautoNetworkState(
                                            normalizedExpectedCcid,
                                            carrier,
                                            networkType,
                                            AutomaticUssdCompleted: true,
                                            LastAutomaticUssdAttemptUtc:
                                                lastAutomaticUssdAttemptUtc);
                                    EnableSmsReceiveMaintenanceAfterSauto(
                                        portName,
                                        normalizedExpectedCcid,
                                        smsReceiveMaintenanceGeneration,
                                        "carrier-has-no-automatic-ussd");
                                }
                                else if (DateTimeOffset.UtcNow
                                             - lastAutomaticUssdAttemptUtc
                                         >= TimeSpan.FromSeconds(30))
                                {
                                    lastAutomaticUssdAttemptUtc =
                                        DateTimeOffset.UtcNow;
                                    AtCommandTraceLogger.State(
                                        portName,
                                        $"SAUTO_AUTO_USSD_BEGIN;ccid={normalizedExpectedCcid};carrier={carrier};code={automaticUssd}");
                                    string ussdResult =
                                        await RunSautoAutomaticUssdWhileLockedAsync(
                                            portName,
                                            networkPort,
                                            automaticUssd,
                                            token);
                                    SautoReceiveSnapshot afterAutomaticUssd =
                                        GetSautoReceiveSnapshot(portName);
                                    automaticUssdCompleted =
                                        IsSautoAutomatic111Completion(ussdResult)
                                        || (afterAutomaticUssd.UssdRevision
                                                > lastObservedUssdRevision
                                            && IsSautoAutomatic111Completion(
                                                afterAutomaticUssd
                                                    .UssdResponse));
                                    lastObservedUssdRevision =
                                        afterAutomaticUssd.UssdRevision;
                                    _sautoNetworkStates[portName] =
                                        new SautoNetworkState(
                                            normalizedExpectedCcid,
                                            carrier,
                                            networkType,
                                            AutomaticUssdCompleted:
                                                automaticUssdCompleted,
                                            LastAutomaticUssdAttemptUtc:
                                                lastAutomaticUssdAttemptUtc);
                                    AtCommandTraceLogger.State(
                                        portName,
                                        $"SAUTO_AUTO_USSD_SEQUENCE_DONE;code={automaticUssd};phone_found={automaticUssdCompleted};result={GetSautoResponseOutcome(ussdResult)}");
                                    LogMessage?.Invoke(
                                        this,
                                        new GsmDataEventArgs
                                        {
                                            PortName = portName,
                                            Data =
                                                $"[SAUTO_AUTO_USSD_RESULT] ccid={normalizedExpectedCcid}; carrier={carrier}; code={automaticUssd}; phone_found={automaticUssdCompleted}; result={GetSautoResponseOutcome(ussdResult)}; cusd={ussdResult.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)}"
                                        });
                                    if (!automaticUssdCompleted)
                                    {
                                        AtCommandTraceLogger.State(
                                            portName,
                                            $"SAUTO_AUTO_USSD_WAITING;code={automaticUssd};next_retry_seconds=30");
                                    }
                                    else
                                    {
                                        EnableSmsReceiveMaintenanceAfterSauto(
                                            portName,
                                            normalizedExpectedCcid,
                                            smsReceiveMaintenanceGeneration,
                                            "111-rx-complete");
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        networkSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_WAITING] {ex.Message}"
                    });
                }

                try { await Task.Delay(SautoDataPortLoopDelay, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }
    internal static string ResolveSautoCarrier(string? response)
    {
        string value = (response ?? string.Empty).ToUpperInvariant();
        if (value.Contains("VIETTEL", StringComparison.Ordinal)) return "VIETTEL";
        if (value.Contains("MOBIFONE", StringComparison.Ordinal)) return "MOBIFONE";
        if (value.Contains("VINAPHONE", StringComparison.Ordinal)) return "VINAPHONE";
        if (value.Contains("VIETNAMOBILE", StringComparison.Ordinal)) return "VIETNAMOBILE";
        if (value.Contains("VNSKY", StringComparison.Ordinal)) return "VNSKY";
        return "No Signal";
    }

    internal static bool IsSautoCarrierRegistered(string? carrier) =>
        !string.IsNullOrWhiteSpace(carrier)
        && !string.Equals(
            carrier,
            "No Signal",
            StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldQuerySautoNetwork(
        string? carrier,
        DateTimeOffset nowUtc,
        DateTimeOffset lastCheckUtc) =>
        !IsSautoCarrierRegistered(carrier)
        && nowUtc - lastCheckUtc > SautoNetworkRecheckInterval;

    internal static string GetSautoAutomaticUssdCode(string? carrier)
    {
        string value = carrier?.Trim().ToUpperInvariant() ?? string.Empty;
        return value switch
        {
            "VINAPHONE" => "*111#",
            _ => string.Empty
        };
    }

    internal static bool IsSautoSuccessfulUssdResponse(
        string? response) =>
        !Regex.IsMatch(
            response ?? string.Empty,
            @"\+(?:CME|CMS) ERROR:|\bERROR\b",
            RegexOptions.IgnoreCase)
        && Regex.IsMatch(
            response ?? string.Empty,
            @"\+CUSD:\s*[01](?:\s*,|\s*(?:\r?\n|$))",
            RegexOptions.IgnoreCase);

    internal static bool ContainsSautoPhoneNumber(string? response) =>
        Regex.IsMatch(
            response ?? string.Empty,
            @"(?<!\d)(?:0\d{9}|84\d{9})(?!\d)",
            RegexOptions.CultureInvariant);

    internal static bool IsSautoAutomatic111Completion(string? response) =>
        HasSautoManualUssdPayloadForStage(response, "*111#")
        && ContainsSautoPhoneNumber(response);

    internal static string MapSautoCopsAccessTechnology(string? act) => act?.Trim() switch
    {
        "" or null => "...",
        "0" => "2G",
        "1" => "GSM",
        "2" => "3G",
        "3" => "2G",
        "4" or "5" or "6" => "3G",
        "7" => "4G",
        _ => "Unknown"
    };

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

            AtCommandTraceLogger.Tx(portName, $"AT+QFOPEN=\"{remoteFile}\",2");
            sp.Write($"AT+QFOPEN=\"{remoteFile}\",2\r");

            string res = await ReadUntilAsync(sp, "OK", 3000);
            if (string.IsNullOrWhiteSpace(res)) return string.Empty;

            var match = Regex.Match(res, @"\+QFOPEN:\s*(\d+)");
            if (!match.Success) return string.Empty;
            int handleId = int.Parse(match.Groups[1].Value);

            using var fs = new FileStream(localFile, FileMode.Create, FileAccess.Write);

            while(true)
            {
                AtCommandTraceLogger.Tx(portName, $"AT+QFREAD={handleId},4096");
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
                if (!string.IsNullOrEmpty(line))
                    AtCommandTraceLogger.Rx(portName, line);
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
                if (!string.IsNullOrEmpty(lenStr))
                    AtCommandTraceLogger.Rx(portName, $"{lenStr}\r\n");

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
                AtCommandTraceLogger.Rx(
                    portName,
                    $"[FILE_PAYLOAD bytes={total}]");
                fs.Write(buf, 0, total);

                await ReadUntilAsync(sp, "OK", 1000);
            }

            AtCommandTraceLogger.Tx(portName, $"AT+QFCLOSE={handleId}");
            sp.Write($"AT+QFCLOSE={handleId}\r");
            await ReadUntilAsync(sp, "OK", 1000);

            // Delete file from RAM to free up memory
            AtCommandTraceLogger.Tx(portName, $"AT+QFDEL=\"{remoteFile}\"");
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

            AtCommandTraceLogger.Tx(
                portName,
                $"AT+QFUPL=\"{remoteFile}\",{fileSize},{uploadTimeoutSeconds}");
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
            if (!string.IsNullOrEmpty(resp)) AtCommandTraceLogger.Rx(portName, resp);
            if (!resp.Contains("CONNECT", StringComparison.OrdinalIgnoreCase)) return false;

            // Write raw bytes
            using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[1024];
                int bytesRead = 0;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    AtCommandTraceLogger.Tx(portName, $"[FILE_PAYLOAD bytes={bytesRead}]");
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
            if (!string.IsNullOrEmpty(finalResp)) AtCommandTraceLogger.Rx(portName, finalResp);

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
        string msgIndex = "",
        DateTimeOffset? smsTimestampUtc = null)
    {
        var delivery = new GsmDataEventArgs
        {
            PortName = portName,
            Data = content,
            MsgIndex = msgIndex,
            Sender = sender,
            Otp = ExtractOtp(content) ?? string.Empty,
            DeliveryId = deliveryId,
            SmsTimestampUtc = smsTimestampUtc
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

    private long _lastMultipartSalvageTicks;
    private long _lastMultipartStalledReportTicks;
    private static readonly TimeSpan MultipartSalvageInterval =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MultipartStalledReportInterval =
        TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MultipartStalledThreshold =
        TimeSpan.FromMinutes(10);
    // Do not keep a carrier segment invisible for hours. The remaining part may
    // still arrive later and can complete the journal, but the part already
    // received must become visible after this bounded grace period.
    private static readonly TimeSpan MultipartPartialFallbackThreshold =
        TimeSpan.FromMinutes(2);

    /// <summary>
    /// Một tin nhiều mảnh có thể bị chẻ thành nhiều nhóm ghép dở khi firmware
    /// trả người gửi ở hai dạng khác nhau; nhóm nào cũng thiếu mảnh nên tin
    /// không bao giờ ra. Quét lại journal theo nhịp và báo cả những nhóm vẫn
    /// thiếu mảnh thật để chúng không im lặng nằm mãi trên đĩa.
    /// </summary>
    private void TryRepairMultipartJournal(string portName)
    {
        if (!ShouldRunThrottledJournalPass(
                ref _lastMultipartSalvageTicks, MultipartSalvageInterval))
        {
            return;
        }

        try
        {
            int salvaged = _multipartJournal.SalvageSplitSenderGroups();
            if (salvaged > 0)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_MULTIPART_SALVAGED] Đã ghép lại {salvaged} tin bị chẻ nhóm theo người gửi; replay sẽ đẩy vào inbox."
                });
            }

            if (!ShouldRunThrottledJournalPass(
                    ref _lastMultipartStalledReportTicks,
                    MultipartStalledReportInterval))
            {
                return;
            }
            foreach (string stalled in _multipartJournal.DescribeStalledGroups(
                         MultipartStalledThreshold, DateTimeOffset.Now))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_MULTIPART_STALLED] {stalled}"
                });
            }

            foreach (SmsMultipartJournal.StalledSnapshot snapshot in
                     _multipartJournal.GetStalledSnapshots(
                         MultipartPartialFallbackThreshold,
                         DateTimeOffset.Now))
            {
                string targetPort = string.IsNullOrWhiteSpace(snapshot.PortName)
                    ? portName
                    : snapshot.PortName;
                if (!DirectScopeStillCurrent(targetPort, snapshot.Scope))
                    continue;

                string partialDeliveryId =
                    $"{snapshot.MessageId}:partial:{snapshot.PresentParts}/{snapshot.Concatenation.Total}";
                bool accepted = DispatchDecodedSms(
                    targetPort,
                    snapshot.Sender,
                    snapshot.Content,
                    partialDeliveryId);
                if (!accepted)
                    continue;

                // This releases the visible delivery from the retry loop while
                // retaining the durable journal. If the missing part arrives
                // later, the original message can still complete safely.
                _multipartJournal.MarkPartialDeliveryAcknowledged(
                    snapshot.MessageId);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = targetPort,
                    Data = $"[SMS_MULTIPART_PARTIAL] delivery={snapshot.MessageId}; "
                         + $"hiển thị {snapshot.PresentParts}/{snapshot.Concatenation.Total} phần "
                         + "sau thời gian chờ; không để SMS bị kẹt trong journal."
                });
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[SMS_MULTIPART_SALVAGE_RETRY] {ex.Message}"
            });
        }
    }

    private readonly ConcurrentDictionary<string, long> _simStorageReportTicks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SimStorageReportInterval =
        TimeSpan.FromMinutes(10);
    internal const int SimStorageWarnPercent = 50;
    internal const int SimStorageCriticalPercent = 80;

    /// <summary>
    /// Bộ nhớ SIM đầy là đường mất tin duy nhất mà ứng dụng không tự thấy: modem
    /// từ chối tin mới trước khi có URC nào để xử lý. Đọc AT+CPMS? theo nhịp và
    /// chỉ log khi mức dùng đã đáng lo, để trạng thái bình thường không gây nhiễu.
    /// </summary>
    private async Task ReportSimStorageUsageAsync(string portName)
    {
        long now = Environment.TickCount64;
        long previous = _simStorageReportTicks.GetOrAdd(portName, 0);
        bool firstPass = previous == 0;
        if (!firstPass
            && now - previous < (long)SimStorageReportInterval.TotalMilliseconds)
        {
            return;
        }
        if (!_simStorageReportTicks.TryUpdate(portName, now, previous)) return;

        string response = await SendCommandAsync(
            portName, "AT+CPMS?", 5000, silent: true);
        if (!TryParseSimStorageUsage(response, out int used, out int total))
        {
            // Lượt đọc đầu có thể chỉ nhận được 'OK' khi dòng +CPMS: bị lệch
            // nhịp với lượt đọc trước. Thử lại một lần trước khi kết luận là
            // không giám sát được.
            await Task.Delay(500);
            response = await SendCommandAsync(
                portName, "AT+CPMS?", 5000, silent: true);
        }
        if (!TryParseSimStorageUsage(response, out used, out total))
        {
            if (firstPass)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[SMS_SIM_STORAGE_UNKNOWN] Không đọc được AT+CPMS?: {response.Trim()}; không giám sát được mức dùng bộ nhớ SIM."
                });
            }
            return;
        }

        int percent = (int)Math.Round(used * 100d / total, MidpointRounding.AwayFromZero);
        // Lượt đầu mỗi cổng luôn ghi một dòng mốc: im lặng phải có nghĩa là
        // "đã kiểm tra và còn chỗ", không phải "chưa từng kiểm tra".
        if (percent < SimStorageWarnPercent && !firstPass) return;

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = percent >= SimStorageCriticalPercent
                ? $"[SMS_SIM_STORAGE_CRITICAL] Bộ nhớ SIM {used}/{total} ({percent}%); tin mới có thể bị modem từ chối trước khi vào tool."
                : $"[SMS_SIM_STORAGE] Bộ nhớ SIM {used}/{total} ({percent}%)."
        });
    }

    internal static bool TryParseSimStorageUsage(
        string? cpmsResponse,
        out int used,
        out int total)
    {
        used = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(cpmsResponse)) return false;

        // +CPMS: "SM",3,50,"SM",3,50,"SM",3,50 — bộ đọc là cặp số đầu tiên.
        Match match = Regex.Match(
            cpmsResponse,
            @"\+CPMS:\s*""[^""]*""\s*,\s*(?<used>\d+)\s*,\s*(?<total>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["used"].Value, out used)
            && int.TryParse(match.Groups["total"].Value, out total)
            && total > 0;
    }

    private static bool ShouldRunThrottledJournalPass(
        ref long lastTicks,
        TimeSpan interval)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref lastTicks);
        if (previous != 0 && now - previous < (long)interval.TotalMilliseconds)
            return false;
        return Interlocked.CompareExchange(ref lastTicks, now, previous)
            == previous;
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
        long requestedDue = Environment.TickCount64
            + Math.Max(0, initialDelayMs);
        _smsSweepPendingDueTicks.AddOrUpdate(
            portName,
            requestedDue,
            (_, currentDue) => Math.Min(currentDue, requestedDue));
        _smsSweepPendingReasons[portName] = reason;

        // The captured SAuto IMEI lifecycle owns the COM through reset,
        // CPIN/CSQ/COPS and automatic *111#. Keep recovery requests pending,
        // but never emit CMGL or receive-mode repair commands before that RX
        // milestone has completed for this exact CCID.
        if (!IsSmsReceiveMaintenanceEnabled(portName))
            return;

        // One worker per COM is enough, but a request arriving while that worker
        // exists remains in _smsSweepPendingDueTicks and is never discarded.
        if (!_smsSweepRetryOwners.TryAdd(portName, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                DateTime reportAfter = DateTime.UtcNow.AddMinutes(2);
                while (_serialPorts.ContainsKey(portName))
                {
                    if (!IsSmsReceiveMaintenanceEnabled(portName))
                        break;

                    if (!_smsSweepPendingDueTicks.TryGetValue(
                            portName, out long dueTick))
                        break;

                    long remainingMs = dueTick - Environment.TickCount64;
                    if (remainingMs > 0)
                    {
                        // Poll the deadline in short slices so a newer urgent
                        // request can shorten an older 30-second deferred sweep.
                        await Task.Delay((int)Math.Min(remainingMs, 250))
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (!_smsSweepPendingDueTicks.TryRemove(
                            new KeyValuePair<string, long>(portName, dueTick)))
                        continue;

                    bool busy = _commandTcs.ContainsKey(portName)
                        || _suspendedBackgroundPorts.ContainsKey(portName)
                        || IsCallInProgress(portName)
                        || _sautoInitializingPorts.ContainsKey(portName)
                        || _sautoImeiChangePorts.ContainsKey(portName)
                        || _sautoResettingPorts.ContainsKey(portName);
                    if (!busy)
                    {
                        // Any request that arrived while waiting for this COM is
                        // covered by the sweep about to run. A request arriving
                        // after this removal remains pending for the next pass.
                        _smsSweepPendingDueTicks.TryRemove(portName, out _);
                        string sweepReason = _smsSweepPendingReasons.TryRemove(
                            portName, out string? latestReason)
                                ? latestReason
                                : reason;
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[SMS_RECEIVE_RECOVERY] reason={sweepReason}; khôi phục chế độ nhận và vét SMS đang lưu trong modem."
                        });
                        await SweepUnreadSmsAsync(portName).ConfigureAwait(false);
                        reportAfter = DateTime.UtcNow.AddMinutes(2);
                        continue;
                    }

                    // Keep the request live while a foreground workflow/reset is
                    // using the COM; it will be retried as soon as that owner exits.
                    _smsSweepPendingDueTicks.AddOrUpdate(
                        portName,
                        Environment.TickCount64 + 250,
                        static (_, currentDue) => currentDue);

                    if (DateTime.UtcNow >= reportAfter)
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = "[SMS_SWEEP_WAITING] COM đang bận; giữ yêu cầu quét và tiếp tục thử lại."
                        });
                        reportAfter = DateTime.UtcNow.AddMinutes(2);
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }
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
                // Close the race where a request is inserted after the loop's
                // final check but before this owner flag is released.
                if (_serialPorts.ContainsKey(portName)
                    && _smsSweepPendingDueTicks.ContainsKey(portName))
                {
                    string pendingReason = _smsSweepPendingReasons.TryGetValue(
                        portName, out string? latestReason)
                            ? latestReason
                            : reason;
                    ScheduleSafeUnreadSmsSweep(portName, pendingReason);
                }
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
        DateTimeOffset? smsTimestampUtc = TryParseSmsTimestamp(
            rawDirect,
            out DateTimeOffset parsedSmsTimestamp)
            ? parsedSmsTimestamp
            : null;

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
                deliveryId,
                smsTimestampUtc: smsTimestampUtc);
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
            messageId,
            smsTimestampUtc: smsTimestampUtc);
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
                string chunk = sp.ReadExisting();
                current += chunk;
                if (!string.IsNullOrEmpty(chunk))
                    AtCommandTraceLogger.Rx(sp.PortName, chunk);
                if (current.Contains(keyword)) return current;
            }
            await Task.Delay(10);
        }
        return current;
    }

    private SautoReceiveSnapshot GetSautoReceiveSnapshot(string portName)
    {
        SautoReceiveState state = _sautoReceiveStates.GetOrAdd(
            portName,
            static _ => new SautoReceiveState());
        lock (state.Sync)
        {
            return new SautoReceiveSnapshot(
                state.Revision,
                state.CfunRevision,
                state.UssdRevision,
                state.SimReady,
                state.SimLocked,
                state.RestartRequired,
                state.CfunMode,
                state.CpinResponse,
                state.Imei,
                state.Ccid,
                state.Carrier,
                state.NetworkType,
                state.CsqResponse,
                state.CopsResponse,
                state.UssdResponse,
                state.Manufacturer,
                state.Model,
                state.Firmware);
        }
    }

    private void SignalSautoReceiveStateChanged(string portName)
    {
        SemaphoreSlim signal = _sautoReceiveSignals.GetOrAdd(
            portName,
            static _ => new SemaphoreSlim(0, 1));
        if (signal.CurrentCount == 0)
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task<SautoReceiveSnapshot?> WaitForSautoReceiveStateAsync(
        string portName,
        Func<SautoReceiveSnapshot, bool> condition,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        SemaphoreSlim signal = _sautoReceiveSignals.GetOrAdd(
            portName,
            static _ => new SemaphoreSlim(0, 1));

        try
        {
            while (true)
            {
                SautoReceiveSnapshot snapshot =
                    GetSautoReceiveSnapshot(portName);
                if (condition(snapshot))
                    return snapshot;

                await signal.WaitAsync(timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void UpdateSautoReceiveState(
        string portName,
        Action<SautoReceiveState> update)
    {
        SautoReceiveState state = _sautoReceiveStates.GetOrAdd(
            portName,
            static _ => new SautoReceiveState());
        lock (state.Sync)
            update(state);
    }

    internal static bool IsSmsStorageReadyUrc(string? line) =>
        Regex.IsMatch(
            line ?? string.Empty,
            @"^(?:\+QIND:\s*)?""?SMS\s+(?:DONE|READY)""?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsRegisteredSmsCarrier(string? carrier) =>
        !string.IsNullOrWhiteSpace(carrier)
        && !carrier.Equals("No Signal", StringComparison.OrdinalIgnoreCase)
        && !carrier.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private void ProcessSautoReceiveChunk(string portName, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        SautoReceiveState state = _sautoReceiveStates.GetOrAdd(
            portName,
            static _ => new SautoReceiveState());
        var publishedEvents = new List<string>();
        bool queueReadyTransition = false;
        bool runEc20AtiCallback = false;
        bool runCnmiCorrection = false;
        bool runCallReadyCallback = false;
        bool runSmsStorageReadyRecovery = false;
        bool runNetworkRegisteredSmsRecovery = false;
        bool stateChanged = false;
        string restartReason = string.Empty;

        lock (state.Sync)
        {
            state.LineBuffer.Append(chunk);
            while (true)
            {
                int newlineIndex = IndexOf(
                    state.LineBuffer,
                    '\n');
                if (newlineIndex < 0) break;

                string line = state.LineBuffer
                    .ToString(0, newlineIndex + 1)
                    .Trim('\r', '\n', ' ');
                state.LineBuffer.Remove(0, newlineIndex + 1);
                if (line.Length == 0) continue;
                state.Revision++;
                stateChanged = true;

                if (line.Contains(
                        "CPIN: READY",
                        StringComparison.OrdinalIgnoreCase))
                {
                    bool becameReady = !state.SimReady;
                    state.CpinResponse = line;
                    state.SimReady = true;
                    state.SimLocked = false;
                    state.RestartRequired = false;
                    if (becameReady)
                    {
                        state.Ccid = string.Empty;
                        // SAuto sends ESC only on the NOT-READY -> READY edge.
                        // Keep it pending until the CPIN command owner still holding
                        // the per-COM semaphore can send it. Writing ESC directly from
                        // DataReceived races the next EGMR/ICCID command and was seen
                        // dropping that command's response on all 32 ports.
                        state.ReadyTransitionPending =
                            !_sautoResettingPorts.ContainsKey(portName);
                        queueReadyTransition = true;
                    }
                }
                else if (line.Contains(
                             "CPIN: NOT READY",
                             StringComparison.OrdinalIgnoreCase))
                {
                    state.CpinResponse = line;
                    state.SimReady = false;
                    state.ReadyTransitionPending = false;
                    state.RestartRequired = true;
                    restartReason = line;
                }
                else if (line.Contains(
                             "CPIN: SIM PIN",
                             StringComparison.OrdinalIgnoreCase)
                         || line.Contains(
                             "CPIN: SIM PUK",
                             StringComparison.OrdinalIgnoreCase))
                {
                    state.CpinResponse = line;
                    state.SimReady = false;
                    state.SimLocked = true;
                    state.ReadyTransitionPending = false;
                    publishedEvents.Add($"[STATUS_SIM_LOCKED] {line}");
                }

                if (RequiresSautoControllerRestart(line)
                    && !line.Contains(
                        "CPIN: NOT READY",
                        StringComparison.OrdinalIgnoreCase)
                    && !_sautoImeiChangePorts.ContainsKey(portName))
                {
                    state.SimReady = false;
                    state.ReadyTransitionPending = false;
                    state.RestartRequired = true;
                    restartReason = line;
                }

                int? cfunMode = ParseSautoCfunMode(line);
                if (cfunMode.HasValue)
                {
                    state.CfunMode = cfunMode.Value;
                    state.CfunRevision++;
                }

                if (line.Contains(
                    "+EGMR:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    string imei = Regex.Match(
                        line,
                        @"(?<!\d)\d{15}(?!\d)").Value;
                    if (!string.IsNullOrWhiteSpace(imei)
                        && !string.Equals(
                            state.Imei,
                            imei,
                            StringComparison.Ordinal))
                    {
                        state.Imei = imei;
                        publishedEvents.Add($"[PARSE_IMEI] {imei}");
                    }
                }

                Match iccid = Regex.Match(
                    line,
                    @"\+?(?:ICCID|QCCID):\s*(89\d{16,20})",
                    RegexOptions.IgnoreCase);
                if (iccid.Success && state.SimReady)
                {
                    string value = iccid.Groups[1].Value;
                    if (!string.Equals(
                        state.Ccid,
                        value,
                        StringComparison.Ordinal))
                    {
                        state.Ccid = value;
                        publishedEvents.Add($"[PARSE_CCID] {value}");
                        publishedEvents.Add(
                            "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận SIM theo vòng DataPort của SAuto");
                    }
                }

                if (line.Contains(
                    "+CSQ:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    bool signalChanged = !string.Equals(
                        state.CsqResponse,
                        line,
                        StringComparison.Ordinal);
                    state.CsqResponse = line;
                    // SAuto updates the grid only when the RSSI value changes.
                    // The complete TX/RX stream remains in at_commands.log, while
                    // identical polling samples no longer flood system_log/UI.
                    if (state.SimReady && signalChanged)
                        publishedEvents.Add(line);
                }

                if (line.Contains(
                    "+COPS:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    string previousCarrier = state.Carrier;
                    state.CopsResponse = line;
                    state.Carrier = ResolveSautoCarrier(line);
                    if (!IsRegisteredSmsCarrier(previousCarrier)
                        && IsRegisteredSmsCarrier(state.Carrier))
                    {
                        runNetworkRegisteredSmsRecovery = true;
                    }
                    _ = TryParseCopsResponse(
                        line,
                        out _,
                        out string accessTechnology);
                    state.NetworkType =
                        MapSautoCopsAccessTechnology(accessTechnology);
                    publishedEvents.Add(
                        $"[NETWORK_TYPE] {state.NetworkType}");
                    publishedEvents.Add(line);
                }

                if (line.Contains(
                    "+CUSD:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    state.UssdResponse = line;
                    state.UssdRevision++;
                }

                if (line.Contains(
                    "QUECTEL",
                    StringComparison.OrdinalIgnoreCase))
                {
                    state.Manufacturer = "Quectel";
                }

                Match revision = Regex.Match(
                    line,
                    @"^Revision:\s*(?<firmware>.+)$",
                    RegexOptions.IgnoreCase);
                if (revision.Success)
                {
                    state.Firmware =
                        revision.Groups["firmware"].Value.Trim();
                    runEc20AtiCallback = state.Firmware.Contains(
                        "EC20",
                        StringComparison.OrdinalIgnoreCase);
                }
                else if (Regex.IsMatch(
                    line,
                    @"^(?:EC|EG|BG|RG|RM|EM|EP|UC)[A-Z0-9-]{2,}$",
                    RegexOptions.IgnoreCase))
                {
                    state.Model = line;
                }

                Match cnmi = Regex.Match(
                    line,
                    @"\+CNMI:\s*\d+\s*,\s*(\d+)",
                    RegexOptions.IgnoreCase);
                if (cnmi.Success
                    && !string.Equals(
                        cnmi.Groups[1].Value,
                        "1",
                        StringComparison.Ordinal))
                {
                    runCnmiCorrection = true;
                }

                if (line.Contains(
                        "+CUSD: 1",
                        StringComparison.OrdinalIgnoreCase)
                    || line.Contains(
                        "Call Ready",
                        StringComparison.OrdinalIgnoreCase))
                {
                    runCallReadyCallback = true;
                }

                if (IsSmsStorageReadyUrc(line))
                    runSmsStorageReadyRecovery = true;
            }

            if (state.LineBuffer.Length > 8192)
                state.LineBuffer.Remove(0, state.LineBuffer.Length - 4096);
        }

        if (stateChanged)
            SignalSautoReceiveStateChanged(portName);

        foreach (string data in publishedEvents)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = data
            });
        }

        bool initializing =
            _sautoInitializingPorts.ContainsKey(portName);
        if (runEc20AtiCallback && !initializing)
            QueueSautoEc20AtiCallback(portName);
        if (runCnmiCorrection && !initializing)
            QueueSautoCnmiCorrection(portName);
        if (queueReadyTransition)
            QueueSautoReadyTransition(portName);
        if (runCallReadyCallback)
            QueueSautoCallReadyCallback(portName);

        if (runSmsStorageReadyRecovery
            || runNetworkRegisteredSmsRecovery)
        {
            ScheduleSafeUnreadSmsSweep(
                portName,
                runSmsStorageReadyRecovery
                    ? "modem-sms-storage-ready"
                    : "network-registered",
                initialDelayMs: 250);
        }

        if (!string.IsNullOrWhiteSpace(restartReason))
            QueueSautoControllerRestart(portName, restartReason);
    }

    private void QueueSautoReadyTransition(string portName)
    {
        if (GetSautoReceiveSnapshot(portName).SimReady)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = "[STATUS_SIM_READY] Đã nhận SIM; DataPort đang đọc CCID."
            });
        }
    }

    private void QueueSautoCallReadyCallback(string portName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                SautoReceiveSnapshot? ready =
                    await WaitForSautoReceiveStateAsync(
                        portName,
                        snapshot =>
                            !_sautoImeiChangePorts.ContainsKey(portName),
                        TimeSpan.FromSeconds(10),
                        CancellationToken.None);
                if (ready != null)
                {
                    await SendSautoCommandForResponseAsync(
                        portName,
                        "AT+CNMI?",
                        TimeSpan.FromSeconds(10),
                        CancellationToken.None);
                }
            }
            catch
            {
                // HandlePort treats the Call Ready/+CUSD callback as best effort.
            }
        });
    }

    private void QueueSautoEc20AtiCallback(string portName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendSautoCommandForResponseAsync(
                    portName,
                    "AT+CPMS=\"ME\",\"SM\",\"MT\"\r",
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
                await SendSautoCommandForResponseAsync(
                    portName,
                    "AT+CNMI=1,1,0,0,0\r",
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
            }
            catch
            {
                // GSMController.HandlePort also treats this ATI callback as
                // best effort and lets DataPort continue independently.
            }
        });
    }

    private void QueueSautoCnmiCorrection(string portName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string correction =
                    await SendSautoCommandForResponseAsync(
                    portName,
                    "AT+CNMI=1,1,0,0,0",
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
                if (IsSautoOkResponse(correction))
                {
                    await SendSautoCommandForResponseAsync(
                        portName,
                        "AT+CNMI?",
                        TimeSpan.FromSeconds(10),
                        CancellationToken.None);
                }
            }
            catch
            {
            }
        });
    }

    private void QueueSautoControllerRestart(
        string portName,
        string reason)
    {
        if (!_sautoRestartOwners.TryAdd(portName, 0)) return;

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data =
                $"[WAITING_FOR_SIM] SAuto nhận {reason.Trim()} và mở lại riêng cổng."
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (_sautoRestartOwners.ContainsKey(portName))
                {
                    bool reconnected = await ReconnectPortAsync(
                        portName,
                        115200,
                        CancellationToken.None);
                    if (!reconnected)
                        break;

                    SautoReceiveSnapshot state =
                        GetSautoReceiveSnapshot(portName);
                    if (!RequiresSautoControllerRestart(
                            state.CpinResponse))
                    {
                        break;
                    }

                    AtCommandTraceLogger.State(
                        portName,
                        $"SAUTO_CONTROLLER_RESTART_REPEAT;result={GetSautoResponseOutcome(state.CpinResponse)}");
                }
            }
            finally
            {
                _sautoRestartOwners.TryRemove(portName, out _);
            }
        });
    }

    private static int IndexOf(StringBuilder value, char target)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == target) return index;
        }

        return -1;
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

            if (!string.IsNullOrEmpty(chunk))
            {
                AtCommandTraceLogger.Rx(portName, chunk);
                ProcessSautoReceiveChunk(portName, chunk);
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
                        int completionEnd = match.Index + match.Length;
                        string completion = currentData
                            .Substring(0, completionEnd)
                            .Trim();
                        buffer.Remove(0, completionEnd);
                        IReadOnlyList<string> orphanFrames =
                            RemoveLeadingUnownedCommandResponseFrames(buffer);
                        TraceRemovedUnownedCommandResponseFrames(
                            portName,
                            c,
                            orphanFrames);
                        t.TrySetResult(completion);
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
                string? pendingCommand = tcs.Task.AsyncState as string;
                Match match = Match.Empty;
                while (true)
                {
                    match = Regex.Match(currentData, @"(?:\r?\nOK\r?\n?|\r?\nERROR\r?\n?|\+CMS ERROR:[^\r\n]*\r?\n?|\+CME ERROR:[^\r\n]*\r?\n?|>\s*|\r?\nCONNECT\r?\n?)");
                    if (!match.Success) break;

                    int candidateEnd = match.Index + match.Length;
                    string candidate = currentData.Substring(0, candidateEnd);
                    if (CanTerminalFrameCompletePendingCommand(
                            candidate,
                            match.Value,
                            pendingCommand))
                    {
                        break;
                    }

                    // SAuto's write-only poller can leave CPIN/CSQ/COPS frames
                    // behind. The next transaction must not consume those as its
                    // own response merely because they end in OK/ERROR.
                    AtCommandTraceLogger.State(
                        portName,
                        $"AT_RESPONSE_NOT_FOR_PENDING;waiting={pendingCommand?.Trim() ?? "UNKNOWN"};observed={match.Value.Trim()}");
                    buffer.Remove(0, candidateEnd);
                    currentData = buffer.ToString();
                }
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
                            string completion = currentData.Substring(
                                0,
                                ackEndIndex);
                            buffer.Remove(0, ackEndIndex);
                            IReadOnlyList<string> orphanFrames =
                                RemoveLeadingUnownedCommandResponseFrames(buffer);
                            currentData = buffer.ToString();
                            TraceRemovedUnownedCommandResponseFrames(
                                portName,
                                cmd,
                                orphanFrames);
                            tcs.TrySetResult(completion);
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
                            string completion = currentData.Substring(
                                0,
                                endIndex);
                            buffer.Remove(0, endIndex);
                            IReadOnlyList<string> orphanFrames =
                                RemoveLeadingUnownedCommandResponseFrames(buffer);
                            currentData = buffer.ToString();
                            TraceRemovedUnownedCommandResponseFrames(
                                portName,
                                cmd,
                                orphanFrames);
                            tcs.TrySetResult(completion);
                        }
                    }
                    else
                    {
                        int endIndex = match.Index + match.Length;
                        string completion = currentData.Substring(
                            0,
                            endIndex);
                        buffer.Remove(0, endIndex);
                        IReadOnlyList<string> orphanFrames =
                            RemoveLeadingUnownedCommandResponseFrames(buffer);
                        currentData = buffer.ToString();
                        TraceRemovedUnownedCommandResponseFrames(
                            portName,
                            pendingCommand,
                            orphanFrames);
                        tcs.TrySetResult(completion);
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
                IReadOnlyList<string> orphanFrames =
                    RemoveLeadingUnownedCommandResponseFrames(buffer);
                if (orphanFrames.Count > 0)
                {
                    TraceRemovedUnownedCommandResponseFrames(
                        portName,
                        boundaryCommand: null,
                        orphanFrames);
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
        StopAllSmsReceiveWatchdogs();
        foreach (var cts in _portLifetimeCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        foreach (var pending in _commandTcs.Values)
            pending.TrySetResult("ERROR: Port disconnected");
        foreach (var kvp in _serialPorts)
        {
            try
            {
                bool wasOpen = kvp.Value.IsOpen;
                kvp.Value.Close();
                kvp.Value.Dispose();
                if (wasOpen) AtCommandTraceLogger.Close(kvp.Key);
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
        _sautoNetworkStates.Clear();
        _sautoReceiveStates.Clear();
        _sautoReceiveSignals.Clear();
        _sautoRestartOwners.Clear();
        _sautoImeiChangePorts.Clear();
        _sautoResettingPorts.Clear();
        _sautoInitializingPorts.Clear();
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
        _smsSweepPendingDueTicks.Clear();
        _smsSweepPendingReasons.Clear();
        _smsReceiveWatchdogLastProbeTicks.Clear();
        _smsReceiveMaintenanceIdentities.Clear();
        _smsReceiveMaintenanceGenerations.Clear();
        _smsReceiveMaintenanceActivationOwners.Clear();
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
        InvalidateSmsReceiveMaintenance(portName);
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
        if (_serialPorts.TryGetValue(portName, out var sp))
        {
            try
            {
                bool wasOpen = sp.IsOpen;
                sp.Close();
                sp.Dispose();
                if (wasOpen) AtCommandTraceLogger.Close(portName);
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
        }

        _portBuffers.TryRemove(portName, out _);
        _portBufferLocks.TryRemove(portName, out _);
        _sautoNetworkStates.TryRemove(portName, out _);
        _sautoReceiveStates.TryRemove(portName, out _);
        _sautoReceiveSignals.TryRemove(portName, out _);
        _sautoImeiChangePorts.TryRemove(portName, out _);
        _sautoResettingPorts.TryRemove(portName, out _);
        _sautoInitializingPorts.TryRemove(portName, out _);

        // Dọn cancellation state kể cả khi kết nối bị lỗi giữa chừng trước lúc tạo semaphore.
        if (_pollingCts.TryRemove(portName, out var polling)) { try { polling.Cancel(); polling.Dispose(); } catch { } }
        if (_smsSweepLocks.TryRemove(portName, out _))
        {
            // Do not dispose here: a sweep already holding this lock may still
            // execute its finally/Release after the COM is disconnected.
        }
        _smsSweepPendingDueTicks.TryRemove(portName, out _);
        _smsSweepPendingReasons.TryRemove(portName, out _);
    }

    private bool EnsurePortOpen(string portName, out SerialPort? sp)
    {
        if (_serialPorts.TryGetValue(portName, out sp))
        {
            if (sp.IsOpen) return true;
            try
            {
                sp.Open();
                if (sp.IsOpen)
                {
                    AtCommandTraceLogger.Open(portName);
                    return true;
                }

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

    private static void WriteSautoRawWhileLocked(
        string portName,
        SerialPort serialPort,
        string data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!serialPort.IsOpen)
            throw new IOException($"Port {portName} is not open.");

        AtCommandTraceLogger.Tx(portName, data);
        serialPort.Write(data);
    }

    private static async Task WriteSautoCommandWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        string command,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!serialPort.IsOpen)
        {
            AtCommandTraceLogger.Error(portName, $"WRITE:{command}: Port not open");
            return;
        }

        Task writeTask = Task.Run(() =>
        {
            AtCommandTraceLogger.Tx(
                portName,
                command + serialPort.NewLine);
            serialPort.WriteLine(command);
        });

        Task completed = await Task.WhenAny(
            writeTask,
            Task.Delay(TimeSpan.FromSeconds(3), ct));
        if (ReferenceEquals(completed, writeTask))
        {
            await writeTask;
            return;
        }

        ct.ThrowIfCancellationRequested();
        AtCommandTraceLogger.Timeout(portName, $"WRITE:{command}");
        try
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
                AtCommandTraceLogger.Close(portName);
            }
        }
        catch (Exception ex)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"WRITE_TIMEOUT_CLOSE:{command}: {ex.Message}");
        }

        try
        {
            await writeTask;
        }
        catch (Exception ex)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"WRITE_TIMEOUT_TASK:{command}: {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        try
        {
            if (!serialPort.IsOpen)
            {
                serialPort.Open();
                AtCommandTraceLogger.Open(portName);
            }
        }
        catch (Exception ex)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"WRITE_TIMEOUT_REOPEN:{command}: {ex.Message}");
        }
    }

    private async Task<string> WriteSautoCommandForResponseWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        string command,
        TimeSpan timeout,
        CancellationToken ct,
        bool appendConfiguredNewLine = true)
    {
        string logicalCommand = command.TrimEnd('\r', '\n', ' ');
        var response = new TaskCompletionSource<string>(
            logicalCommand,
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryBeginCommandTransaction(portName, response))
            return "ERROR: Another command is already in progress";

        try
        {
            if (appendConfiguredNewLine)
            {
                await WriteSautoCommandWhileLockedAsync(
                    portName,
                    serialPort,
                    command,
                    ct);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                if (!serialPort.IsOpen)
                    return "ERROR: Port not open";
                AtCommandTraceLogger.Tx(portName, command);
                serialPort.Write(command);
            }

            try
            {
                string result = await response.Task.WaitAsync(timeout, ct);
                if (logicalCommand.Equals(
                        "AT+CPIN?",
                        StringComparison.OrdinalIgnoreCase)
                    && IsSautoCpinReadyResponse(result))
                {
                    await CompleteSautoReadyTransitionWhileLockedAsync(
                        portName,
                        serialPort,
                        ct);
                }
                AtCommandTraceLogger.State(
                    portName,
                    $"SAUTO_STEP_RESULT;command={logicalCommand};result={GetSautoResponseOutcome(result)}");
                return result.Trim();
            }
            catch (TimeoutException)
            {
                response.TrySetCanceled();
                AtCommandTraceLogger.Timeout(
                    portName,
                    $"RESPONSE:{logicalCommand}");
                throw new TimeoutException(
                    $"GSM không trả terminal frame cho '{logicalCommand}' trong {timeout.TotalSeconds:0.#} giây.");
            }
        }
        finally
        {
            if (_commandTcs.TryGetValue(portName, out var current)
                && ReferenceEquals(current, response))
            {
                _commandTcs.TryRemove(portName, out _);
            }
        }
    }

    private async Task CompleteSautoReadyTransitionWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        CancellationToken ct)
    {
        SautoReceiveState state = _sautoReceiveStates.GetOrAdd(
            portName,
            static _ => new SautoReceiveState());
        bool shouldSendEsc;
        lock (state.Sync)
        {
            shouldSendEsc = state.ReadyTransitionPending
                && !_sautoResettingPorts.ContainsKey(portName);
            state.ReadyTransitionPending = false;
        }

        if (!shouldSendEsc) return;

        ct.ThrowIfCancellationRequested();
        if (!serialPort.IsOpen)
        {
            AtCommandTraceLogger.Error(
                portName,
                "CPIN_READY_ESC: Port not open");
            return;
        }

        AtCommandTraceLogger.Tx(portName, "<ESC>");
        serialPort.Write(new byte[] { 27 }, 0, 1);

        // ESC has no terminal frame. This is the same guard interval used by
        // SAuto before it allows the DataPort loop to send EGMR/ICCID next.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        AtCommandTraceLogger.State(
            portName,
            "SAUTO_SIM_READY_TRANSITION;esc=sent;next=GSM_RESPONSE_CHAIN");
    }

    private async Task<string> SendSautoCommandForResponseAsync(
        string portName,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!EnsurePortOpen(portName, out SerialPort? serialPort)
            || serialPort == null)
        {
            return "ERROR: Port not open";
        }

        if (!_semaphores.TryGetValue(portName, out SemaphoreSlim? semaphore))
            return "ERROR: Semaphore missing";

        await semaphore.WaitAsync(ct);
        try
        {
            return await WriteSautoCommandForResponseWhileLockedAsync(
                portName,
                serialPort,
                command,
                timeout,
                ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task SendSautoWriteOnlyAsync(
        string portName,
        SerialPort serialPort,
        SemaphoreSlim semaphore,
        string command,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            // This is GSMController.sendAT: release the per-COM lock as soon as
            // SerialPort.WriteLine completes. OK/ERROR is not a USSD gate.
            await WriteSautoCommandWhileLockedAsync(
                portName,
                serialPort,
                command,
                ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal static bool HasSautoManualUssdPayloadForStage(
        string? response,
        string stage)
    {
        string value = response ?? string.Empty;
        if (!Regex.IsMatch(
                value,
                @"\+CUSD:\s*\d+\s*,",
                RegexOptions.IgnoreCase))
            return false;

        string normalizedStage = stage.Trim();
        // VinaPhone's automatic *111# menu and manual *101# balance response
        // can arrive close together.  Do not let a late *111# menu complete a
        // *101# waiter (the exact race observed after Refresh on COM89/98/99).
        if (string.Equals(normalizedStage, "*101#", StringComparison.Ordinal))
            return Regex.IsMatch(value, @"\+CUSD:\s*0\s*,", RegexOptions.IgnoreCase);
        if (string.Equals(normalizedStage, "*111#", StringComparison.Ordinal))
            return Regex.IsMatch(value, @"\+CUSD:\s*1\s*,", RegexOptions.IgnoreCase);

        return true;
    }

    private static string GetSautoResponseOutcome(string? response)
    {
        string value = response ?? string.Empty;
        Match error = Regex.Match(
            value,
            @"\+(?:CME|CMS) ERROR:[^\r\n]*|\bERROR\b",
            RegexOptions.IgnoreCase);
        if (error.Success)
            return error.Value.Trim();
        if (Regex.IsMatch(value, @"(?:^|\r?\n)OK(?:\r?\n|$)"))
            return "OK";
        return string.IsNullOrWhiteSpace(value) ? "EMPTY" : "DATA";
    }

    private static bool IsSautoOkResponse(string? response) =>
        Regex.IsMatch(
            response ?? string.Empty,
            @"(?:^|\r?\n)OK(?:\r?\n|$)")
        && !Regex.IsMatch(
            response ?? string.Empty,
            @"\+(?:CME|CMS) ERROR:|\bERROR\b",
            RegexOptions.IgnoreCase);

    private async Task<bool> SetAndConfirmRfFunctionalModeAsync(
        string portName,
        string operation,
        int expectedMode,
        CancellationToken ct)
    {
        string command = $"AT+CFUN={expectedMode}";
        int commandTimeoutMs = expectedMode == 1 ? 15000 : 10000;
        string lastCommandResponse = string.Empty;
        string lastQueryResponse = string.Empty;

        for (int attempt = 1;
             attempt <= SautoAirplaneMaxAttempts;
             attempt++)
        {
            lastCommandResponse = await SendCommandAsync(
                portName,
                command,
                commandTimeoutMs,
                silent: true,
                ct: ct).ConfigureAwait(false);
            AtCommandTraceLogger.State(
                portName,
                IsSautoOkResponse(lastCommandResponse)
                    ? $"{operation}_RF_COMMAND_ACK;command={command};attempt={attempt}/{SautoAirplaneMaxAttempts}"
                    : $"{operation}_RF_STEP_HOLD;step=CFUN{expectedMode}_ACK;attempt={attempt}/{SautoAirplaneMaxAttempts};result={GetSautoResponseOutcome(lastCommandResponse)};action=VERIFY_STATE");

            // OK acknowledges receipt, not completion of the RF transition.
            // Query only after the SAuto guard and advance solely on +CFUN: n.
            await Task.Delay(SautoAirplanePreQueryDelay, ct)
                .ConfigureAwait(false);
            lastQueryResponse = await SendCommandAsync(
                portName,
                "AT+CFUN?",
                5000,
                silent: true,
                ct: ct).ConfigureAwait(false);
            int? mode = ParseSautoCfunMode(lastQueryResponse);
            if (IsSautoOkResponse(lastQueryResponse)
                && mode == expectedMode)
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"{operation}_RF_CFUN_CONFIRMED;mode={expectedMode};attempt={attempt}/{SautoAirplaneMaxAttempts}");
                return true;
            }

            AtCommandTraceLogger.State(
                portName,
                $"{operation}_RF_STEP_HOLD;step=CFUN_QUERY_{expectedMode};attempt={attempt}/{SautoAirplaneMaxAttempts};mode={(mode?.ToString() ?? "NO_REPORT")};result={GetSautoResponseOutcome(lastQueryResponse)};action=RETRY_{command}");
            if (attempt < SautoAirplaneMaxAttempts)
            {
                await Task.Delay(SautoAirplaneRetryDelay, ct)
                    .ConfigureAwait(false);
            }
        }

        AtCommandTraceLogger.Error(
            portName,
            $"{operation}_RF_RECOVERY_FAILED;step=CFUN{expectedMode};command_result={GetSautoResponseOutcome(lastCommandResponse)};query_result={GetSautoResponseOutcome(lastQueryResponse)}");
        return false;
    }

    private async Task<bool> RecoverSautoRadioServiceAsync(
        string portName,
        string operation,
        string reason,
        CancellationToken ct)
    {
        AtCommandTraceLogger.State(
            portName,
            $"{operation}_RF_RECOVERY_BEGIN;reason={reason}");
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[{operation}_RF_RECOVERY] Đang khởi động lại riêng dịch vụ RF của cổng ({reason})."
        });

        string cancelUssd = await SendCommandAsync(
            portName,
            "AT+CUSD=2",
            5000,
            silent: true,
            ct: ct).ConfigureAwait(false);
        if (!IsSautoOkResponse(cancelUssd))
        {
            // Some firmware returns ERROR when no USSD session is open. That is
            // already a terminal answer; RF cycling is still the deterministic
            // way to clear the service, so do not abort recovery here.
            AtCommandTraceLogger.State(
                portName,
                $"{operation}_RF_STEP_HOLD;step=CUSD2;result={GetSautoResponseOutcome(cancelUssd)};action=CONTINUE_CFUN4");
        }
        else
        {
            AtCommandTraceLogger.State(
                portName,
                $"{operation}_RF_COMMAND_ACK;command=AT+CUSD=2");
        }

        if (!await SetAndConfirmRfFunctionalModeAsync(
                portName,
                operation,
                expectedMode: 4,
                ct).ConfigureAwait(false))
            return false;

        if (!await SetAndConfirmRfFunctionalModeAsync(
                portName,
                operation,
                expectedMode: 1,
                ct).ConfigureAwait(false))
            return false;

        for (int attempt = 1; attempt <= 15; attempt++)
        {
            string cops = await SendCommandAsync(
                portName,
                "AT+COPS?",
                5000,
                silent: true,
                ct: ct).ConfigureAwait(false);
            if (IsSautoOkResponse(cops)
                && TryParseCopsResponse(cops, out _, out _))
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"{operation}_RF_RECOVERY_READY;cops_attempt={attempt}");
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[{operation}_RF_RECOVERY] Mạng đã đăng ký lại; cổng đã sẵn sàng."
                });
                return true;
            }

            AtCommandTraceLogger.State(
                portName,
                $"{operation}_RF_STEP_HOLD;step=COPS_REGISTERED;attempt={attempt}/15;result={GetSautoResponseOutcome(cops)}");

            if (attempt < 15)
                await Task.Delay(TimeSpan.FromSeconds(1), ct)
                    .ConfigureAwait(false);
        }

        AtCommandTraceLogger.Error(
            portName,
            $"{operation}_RF_RECOVERY_FAILED;step=COPS_REGISTERED");
        return false;
    }

    private async Task<string> RunSautoAutomaticUssdWhileLockedAsync(
        string portName,
        SerialPort serialPort,
        string ussdCode,
        CancellationToken ct)
    {
        // Keep the UART lock for the whole sequence, but never gate progress
        // on terminal OK/ERROR. +CUSD is asynchronous and is consumed by the
        // shared receive parser. Automatic lookup remains the captured 111
        // flow; 101 is an explicit manual lookup from the UI.
        await WriteSautoCommandWhileLockedAsync(
            portName,
            serialPort,
            "AT+CSCS=\"GSM\"",
            ct);
        long ussdRevision =
            GetSautoReceiveSnapshot(portName).UssdRevision;
        await WriteSautoCommandWhileLockedAsync(
            portName,
            serialPort,
            "AT+CUSD=2",
            ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await WriteSautoCommandWhileLockedAsync(
            portName,
            serialPort,
            $"AT+CUSD=1,\"{ussdCode}\",15\r",
            ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        SautoReceiveSnapshot completed =
            GetSautoReceiveSnapshot(portName);
        return completed.UssdRevision > ussdRevision
            ? completed.UssdResponse
            : "OK";
    }

    public async Task<string?> RunSautoManualUssdAsync(
        string portName,
        IReadOnlyList<string> stages,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0
            || stages.Any(string.IsNullOrWhiteSpace))
            return null;

        using IDisposable foregroundLease =
            await AcquireForegroundOperationAsync(portName, "USSD", ct)
                .ConfigureAwait(false);
        using IDisposable backgroundLease =
            SuspendPortBackgroundOperations(portName);
        bool channelPrepared = false;
        try
        {
            for (int cleanupAttempt = 1;
                 cleanupAttempt <= 2 && !channelPrepared;
                 cleanupAttempt++)
            {
                channelPrepared = await PrepareForegroundChannelAsync(
                        portName,
                        cleanupAttempt == 1 ? "USSD" : "USSD_CLEANUP_RETRY",
                        ct)
                    .ConfigureAwait(false);
                if (!channelPrepared && cleanupAttempt == 1)
                {
                    AtCommandTraceLogger.State(
                        portName,
                        "USSD_CLEANUP_RETRY;reason=SMS_TERMINAL_PENDING");
                }
            }

            if (!channelPrepared)
                return "ERROR: Modem channel cleanup failed before USSD";

            string? lastResponse = null;
            bool ussdSessionAlreadyCancelled = true;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                // Initial cleanup and successful RF recovery both acknowledge
                // CUSD=2. Other retries close the previous request first.
                if (!ussdSessionAlreadyCancelled)
                {
                    string cancelResponse = await SendCommandAsync(
                        portName,
                        "AT+CUSD=2",
                        3000,
                        silent: true,
                        ct: ct).ConfigureAwait(false);
                    if (!IsSautoOkResponse(cancelResponse))
                    {
                        lastResponse =
                            $"ERROR: USSD cleanup failed ({GetSautoResponseOutcome(cancelResponse)})";
                        continue;
                    }
                }
                ussdSessionAlreadyCancelled = false;

                bool timedOutWaitingForCusd = false;
                for (int stageIndex = 0;
                     stageIndex < stages.Count;
                     stageIndex++)
                {
                    string stage = stages[stageIndex];
                    long ussdRevision =
                        GetSautoReceiveSnapshot(portName).UssdRevision;
                    string requestResponse = await SendCommandAsync(
                        portName,
                        $"AT+CUSD=1,\"{stage}\",15",
                        10000,
                        silent: true,
                        ct: ct).ConfigureAwait(false);
                    if (!IsSautoOkResponse(requestResponse))
                    {
                        lastResponse =
                            $"ERROR: USSD request rejected ({GetSautoResponseOutcome(requestResponse)})";
                        break;
                    }

                    SautoReceiveSnapshot? completed =
                        await WaitForSautoReceiveStateAsync(
                            portName,
                            snapshot =>
                                snapshot.UssdRevision > ussdRevision
                                && HasSautoManualUssdPayloadForStage(
                                    snapshot.UssdResponse,
                                    stage),
                            SautoManualUssdResponseTimeout,
                            ct);
                    bool ussdStatus = completed != null;
                    lastResponse = completed?.UssdResponse
                        ?? "ERROR: Timeout waiting for +CUSD";

                    if (!ussdStatus)
                    {
                        timedOutWaitingForCusd = true;
                        break;
                    }

                    if (ussdStatus
                        && stageIndex == stages.Count - 1)
                    {
                        return lastResponse;
                    }

                }

                if (timedOutWaitingForCusd && attempt == 0)
                {
                    bool radioRecovered = await RecoverSautoRadioServiceAsync(
                            portName,
                            "USSD",
                            "ACK_WITHOUT_CUSD",
                            ct)
                        .ConfigureAwait(false);
                    if (!radioRecovered)
                    {
                        lastResponse = "ERROR: USSD RF recovery failed";
                        break;
                    }

                    channelPrepared = await PrepareForegroundChannelAsync(
                            portName,
                            "USSD_AFTER_RF_RECOVERY",
                            ct)
                        .ConfigureAwait(false);
                    if (!channelPrepared)
                    {
                        lastResponse = "ERROR: Modem channel cleanup failed after USSD RF recovery";
                        break;
                    }

                    ussdSessionAlreadyCancelled = true;
                }
            }

            return lastResponse;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AtCommandTraceLogger.Error(
                portName,
                $"MANUAL_USSD: {ex.Message}");
            return null;
        }
        finally
        {
            // Cleanup is part of the same per-COM workflow and therefore finishes
            // before SMS/call/background polling can use this channel again.
            try
            {
                if (channelPrepared)
                {
                    await PrepareForegroundChannelAsync(
                        portName,
                        "IDLE_AFTER_USSD",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // The primary result is preserved; the next queued workflow will
                // run the same fail-closed cleanup before transmitting anything.
            }
        }
    }

    public async Task<string> SendCommandAsync(
        string portName,
        string command,
        int timeoutMs = 5000,
        bool silent = false,
        CancellationToken ct = default)
    {
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
            AtCommandTraceLogger.Timeout(portName, $"LOCK:{command}");
            return "ERROR: Timeout waiting for lock";
        }

        var tcs = new TaskCompletionSource<string>(command, TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryBeginCommandTransaction(portName, tcs))
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

            AtCommandTraceLogger.Tx(portName, command + "\r\n");
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
                AtCommandTraceLogger.Timeout(portName, command);
                return "ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)";
            }

            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (IOException ex)
        {
            AtCommandTraceLogger.Error(portName, $"{command}: {ex.Message}");
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
            AtCommandTraceLogger.Timeout(portName, "LOCK:[RAW]");
            return "ERROR: Timeout waiting for lock";
        }

        var tcs = new TaskCompletionSource<string>("RAW_DATA", TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryBeginCommandTransaction(portName, tcs))
        {
            semaphore.Release();
            return "ERROR: Another command is already in progress";
        }

        try
        {
            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> [RAW] {data}" });

            AtCommandTraceLogger.Tx(portName, $"[RAW] {data}");
            sp.Write(data);
            
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                tcs.TrySetCanceled();
                AtCommandTraceLogger.Timeout(portName, "[RAW]");
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
    private const int SmsStopTerminalDrainTimeoutMs = 15_000;
    internal const string SmsPayloadSubmittedMarker = "[SMS_PAYLOAD_SUBMITTED]";
    internal const string SmsChannelRecoveryRequiredMarker =
        "[SMS_CHANNEL_RECOVERY_REQUIRED]";

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

        using IDisposable foregroundLease =
            await AcquireForegroundOperationAsync(portName, "SMS", ct)
                .ConfigureAwait(false);
        using IDisposable backgroundLease =
            SuspendPortBackgroundOperations(portName);
        bool channelPrepared = false;
        bool radioRecoveryRequired = false;
        string TrackSmsResult(string result)
        {
            if (result.Contains(
                    SmsChannelRecoveryRequiredMarker,
                    StringComparison.Ordinal))
            {
                radioRecoveryRequired = true;
            }
            return result;
        }
        try
        {
            channelPrepared = await PrepareForegroundChannelAsync(
                    portName,
                    "SMS",
                    ct).ConfigureAwait(false);
            if (!channelPrepared)
                return "ERROR: Modem channel cleanup failed before SMS";

        // Kiểm tra xem message có ký tự nằm ngoài bảng mã GSM cơ bản hay không
        // (Sử dụng cách kiểm tra đơn giản: nếu có bất kỳ ký tự nào > 127 thì coi là Unicode)
        bool isGsm = (message ?? "").All(c => c <= 127);
        int maxLen = isGsm ? MaxGsmPartLength : MaxUcs2PartLength;
        int maxChunk = isGsm ? MaxGsmChunkBodyLength : MaxUcs2ChunkBodyLength;

        if (string.IsNullOrEmpty(message) || message.Length <= maxLen)
        {
            return TrackSmsResult(await SendSmsPartAsync(
                portName, phoneNumber, message ?? "", isGsm, timeoutMs, ct));
        }

        var chunks = SplitMessageIntoChunks(message, maxChunk);
        int total = chunks.Count;
        var results = new List<string>();
        int confirmedParts = 0;

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested)
            {
                return TrackSmsResult(confirmedParts > 0
                    ? $"ERROR: {SmsPayloadSubmittedMarker} Multipart SMS cancelled after {confirmedParts}/{total} confirmed parts; do not retry the whole message"
                    : "ERROR: SMS operation cancelled");
            }
            string partBody = $"[{i + 1}/{total}] {chunks[i]}";
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SMS_MULTIPART] Đang gửi đoạn {i + 1}/{total}..." });

            string resp = await SendSmsPartAsync(portName, phoneNumber, partBody, isGsm, timeoutMs, ct);
            results.Add(resp);

            if (resp.Contains("ERROR"))
            {
                if (confirmedParts > 0
                    || resp.Contains(
                        SmsPayloadSubmittedMarker,
                        StringComparison.Ordinal))
                {
                    return TrackSmsResult($"ERROR: {SmsPayloadSubmittedMarker} Multipart SMS stopped after {confirmedParts}/{total} confirmed parts; part {i + 1}: {resp}");
                }
                return TrackSmsResult($"ERROR: Gửi thất bại ở đoạn {i + 1}/{total} - {resp}");
            }
            confirmedParts++;

            // Chờ 1.5s giữa các đoạn để mạng có thể nhận đúng thứ tự
            if (i < total - 1)
            {
                try { await Task.Delay(1500, ct); }
                catch (OperationCanceledException)
                {
                    return TrackSmsResult($"ERROR: {SmsPayloadSubmittedMarker} Multipart SMS cancelled after {confirmedParts}/{total} confirmed parts; do not retry the whole message");
                }
            }
        }

        return $"OK (Đã gửi {total} đoạn thành công)";
        }
        finally
        {
            try
            {
                if (channelPrepared)
                {
                    if (radioRecoveryRequired)
                    {
                        bool recovered = await RecoverSautoRadioServiceAsync(
                                portName,
                                "SMS",
                                "CANCELLED_AFTER_CTRL_Z",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!recovered)
                        {
                            AtCommandTraceLogger.Error(
                                portName,
                                "SMS_STOP_RECOVERY_FAILED;next_operation_must_clean=true");
                        }
                    }

                    await PrepareForegroundChannelAsync(
                        portName,
                        radioRecoveryRequired
                            ? "IDLE_AFTER_SMS_STOP_RECOVERY"
                            : "IDLE_AFTER_SMS",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Never resend an irreversible payload. The next queued
                // operation must pass its own cleanup before transmitting.
            }
        }
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
        bool channelRecoveryRequired = false;

        async Task<string> SendInnerAsync(
            string cmd,
            CancellationToken token = default,
            int commandTimeoutMs = 5000)
        {
            token.ThrowIfCancellationRequested();
            var innerTcs = new TaskCompletionSource<string>(cmd, TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryBeginCommandTransaction(portName, innerTcs))
                return "ERROR: Another command is already in progress";
            try
            {
                AtCommandTraceLogger.Tx(portName, cmd);
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

            // Ctrl+Z has already ended text-entry mode. Sending ESC here cannot
            // recall the submitted payload and can make a late +CMS response leak
            // into the next workflow. Probe command mode directly and let the
            // outer workflow reset RF only if acknowledged probes cannot clean it.

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
            if (!TryBeginCommandTransaction(portName, tcs))
            {
                return "ERROR: Another command is already in progress";
            }

            AtCommandTraceLogger.Tx(portName, $"AT+CMGS=\"{phoneNumber}\"");
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
                try
                {
                    if (sp.IsOpen)
                    {
                        AtCommandTraceLogger.Tx(portName, "<ESC>");
                        sp.Write("\x1B");
                    }
                }
                catch { }
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
            if (!TryBeginCommandTransaction(portName, tcs))
                return "ERROR: Another command is already in progress";

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {message}" });
            ct.ThrowIfCancellationRequested();

            if (isGsm)
            {
                AtCommandTraceLogger.Tx(
                    portName,
                    $"[SMS_PAYLOAD chars={message.Length}]<CTRL-Z>");
                sp.Write(message + "\x1A");
            }
            else
            {
                string hexMessage = BitConverter.ToString(Encoding.BigEndianUnicode.GetBytes(message)).Replace("-", "");
                AtCommandTraceLogger.Tx(
                    portName,
                    $"[SMS_UCS2_PAYLOAD hex_chars={hexMessage.Length}]<CTRL-Z>");
                sp.Write(hexMessage + "\x1A");
            }
            // SerialPort.Write returning after Ctrl+Z is the irreversible
            // ownership boundary. From this point, timeout/cancel/disconnect is
            // ambiguous and must never be retried automatically.
            payloadSubmitted = true;

            // Sau Ctrl+Z, nhà mạng/modem có thể cần lâu mới trả +CMGS/OK. Chờ tối thiểu
            // 90 giây; nếu vẫn quá hạn thì không retry vì SMS có thể đã được nhận.
            // Cancellation stops future queue items, but Ctrl+Z is irreversible.
            // Keep this COM lease and drain the terminal SMS response briefly so
            // +CMGS/+CMS can never be consumed by the following USSD transaction.
            Task payloadTimeoutTask = Task.Delay(GetSmsPayloadTimeoutMs(timeoutMs));
            Task cancellationSignal = ct.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                : Task.Delay(Timeout.InfiniteTimeSpan);
            completedTask = await Task.WhenAny(
                tcs.Task,
                payloadTimeoutTask,
                cancellationSignal);

            bool cancelledAfterSubmit = completedTask == cancellationSignal;
            if (cancelledAfterSubmit)
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"SMS_STOP_AFTER_CTRL_Z_WAIT_TERMINAL;timeout_ms={SmsStopTerminalDrainTimeoutMs}");
                completedTask = await Task.WhenAny(
                    tcs.Task,
                    Task.Delay(SmsStopTerminalDrainTimeoutMs));
            }

            if (completedTask != tcs.Task)
            {
                tcs.TrySetCanceled();

                // Gỡ TCS cũ trước khi phục hồi. Nếu để nguyên, ERROR/OK do AT
                // có thể bị nhận nhầm thành kết quả của payload đã quá hạn.
                if (_commandTcs.TryGetValue(portName, out var pendingPayload)
                    && ReferenceEquals(pendingPayload, tcs))
                {
                    _commandTcs.TryRemove(portName, out _);
                }

                (bool recovered, string? lateSubmitConfirmation) = await RecoverSmsChannelAsync();
                if (!string.IsNullOrWhiteSpace(lateSubmitConfirmation))
                    return lateSubmitConfirmation;

                string terminalReason = cancelledAfterSubmit
                    ? "SMS operation cancelled after Ctrl+Z without terminal response"
                    : "Timeout sending SMS payload";
                if (!recovered)
                {
                    channelRecoveryRequired = true;
                    return $"ERROR: {SmsPayloadSubmittedMarker} {SmsChannelRecoveryRequiredMarker} {terminalReason}; SMS channel recovery failed";
                }

                return $"ERROR: {SmsPayloadSubmittedMarker} {terminalReason}";
            }

            string finalResp = await tcs.Task;
            if (cancelledAfterSubmit)
            {
                AtCommandTraceLogger.State(
                    portName,
                    $"SMS_STOP_AFTER_CTRL_Z_TERMINAL;result={GetSautoResponseOutcome(finalResp)}");
            }
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (OperationCanceledException)
        {
            if (!payloadSubmitted)
            {
                try
                {
                    if (sp.IsOpen)
                    {
                        AtCommandTraceLogger.Tx(portName, "<ESC>");
                        sp.Write("\x1B");
                    }
                }
                catch { }
            }
            else
            {
                // Ctrl+Z already handed the payload to the modem. ESC cannot
                // recall it and was the cause of late +CMS frames corrupting
                // the next call/USSD workflow. The outer SMS workflow performs
                // a bounded RF recovery before releasing this COM.
                channelRecoveryRequired = true;
            }
            return payloadSubmitted
                ? $"ERROR: {SmsPayloadSubmittedMarker} {SmsChannelRecoveryRequiredMarker} SMS operation cancelled after Ctrl+Z"
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
            if (payloadSubmitted)
                channelRecoveryRequired = true;
            return payloadSubmitted
                ? $"ERROR: {SmsPayloadSubmittedMarker} {SmsChannelRecoveryRequiredMarker} Lỗi sau Ctrl+Z - {ex.Message}"
                : $"ERROR: {ex.Message}";
        }
        finally
        {
            if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, tcs))
                _commandTcs.TryRemove(portName, out _);

            // Always return the modem to the same live receive mode used by the
            // SAuto initialization. Leaving Quectel in CMGF=0 after an outgoing
            // SMS made later inbound records wait in storage or take the slower
            // PDU fallback path until another +CMTI happened to wake the reader.
            if (!channelRecoveryRequired
                && _serialPorts.TryGetValue(portName, out var sp2)
                && sp2.IsOpen)
            {
                foreach (string restoreCommand in SmsReceiveRestoreCommandOrder)
                    await SendInnerAsync(
                        restoreCommand,
                        CancellationToken.None);
                if (GetModemProfile(portName)?.IsQuectel == true)
                {
                    await SendInnerAsync(
                        "AT+QURCCFG=\"urcport\",\"uart1\"",
                        CancellationToken.None);
                }
            }

            semaphore.Release();
            // This worker waits for the SMS foreground/background leases to be
            // released, then drains anything that arrived while CMGS owned UART.
            ScheduleSafeUnreadSmsSweep(
                portName,
                channelRecoveryRequired
                    ? "outgoing-sms-channel-recovery"
                    : "outgoing-sms-finished",
                initialDelayMs: 250);
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
        if (!IsSmsReceiveMaintenanceEnabled(portName))
        {
            ScheduleSafeUnreadSmsSweep(
                portName,
                "sweep-held-until-sauto-ready");
            return;
        }
        if (_suspendedBackgroundPorts.ContainsKey(portName)
            || IsCallInProgress(portName))
        {
            ScheduleSafeUnreadSmsSweep(
                portName,
                "sweep-deferred-busy-port",
                initialDelayMs: 250);
            return;
        }

        // One global turn across every COM. Recovery sweeps and periodic
        // watchdog probes cannot issue CMGL concurrently on a 64-port rig.
        using IDisposable scanTurn =
            await AcquireSmsScanTurnAsync(CancellationToken.None)
                .ConfigureAwait(false);
        if (!CanRunSmsScanTurn(portName))
        {
            ScheduleSafeUnreadSmsSweep(
                portName,
                "sweep-deferred-until-next-round-robin-turn",
                initialDelayMs: 1000);
            return;
        }

        SemaphoreSlim sweepLock = _smsSweepLocks.GetOrAdd(portName, static _ => new SemaphoreSlim(1, 1));
        await sweepLock.WaitAsync();

        try
        {
            using IDisposable foregroundLease =
                await AcquireForegroundOperationAsync(
                    portName,
                    "SMS_SWEEP",
                    CancellationToken.None).ConfigureAwait(false);
            // State may have changed while this recovery waited behind a user
            // operation. Defer without dropping the request.
            if (!IsSmsReceiveMaintenanceEnabled(portName)
                || _suspendedBackgroundPorts.ContainsKey(portName)
                || IsCallInProgress(portName))
            {
                ScheduleSafeUnreadSmsSweep(
                    portName,
                    "sweep-deferred-after-foreground-wait",
                    initialDelayMs: 250);
                return;
            }

            using IDisposable backgroundLease =
                SuspendPortBackgroundOperations(portName);
            if (!_serialPorts.TryGetValue(portName, out sp)
                || !sp.IsOpen
                || !IsSmsReceiveMaintenanceEnabled(portName))
                return;

            long generation = CurrentSmsGeneration(portName);

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Đang quét tin nhắn tồn đọng (Sweep)..." });

            // Re-assert receive mode on every recovery sweep. SMS sending and some
            // EC20 firmware revisions can leave CMGF/CNMI/URC routing changed; without
            // this, the SIM stores the message but no +CMTI reaches the application.
            foreach (string restoreCommand in SmsReceiveRestoreCommandOrder)
            {
                if (!IsSmsReceiveMaintenanceEnabled(portName))
                    return;
                await SendCommandAsync(
                    portName,
                    restoreCommand,
                    5000,
                    silent: true);
            }
            if (GetModemProfile(portName)?.IsQuectel == true)
            {
                if (!IsSmsReceiveMaintenanceEnabled(portName))
                    return;
                await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true);
            }

            // ALL is intentional: CMGR marks a multipart segment REC READ before the remaining
            // segments arrive. Scanning only REC UNREAD loses that segment after restart.
            //
            // CMGF=1 above selects text mode for every modem.  Quectel's numeric
            // `AT+CMGL=4` form is PDU-mode syntax; using it here while still in text
            // mode made the recovery sweep return no records, so SMS stayed on the
            // SIM until a modem/app restart happened to flush the state.  Keep the
            // command consistent with the selected mode for all profiles.
            const string command = "AT+CMGL=\"ALL\"";
            if (!IsSmsReceiveMaintenanceEnabled(portName))
                return;
            string sweepResponse = await SendCommandAsync(portName, command, 25000, silent: true);
            if (IsCommandFailure(sweepResponse)
                && GetModemProfile(portName)?.IsQuectel == true)
            {
                // A few EC20 firmware banks reject the text-mode list command
                // after a previous PDU operation. Fall back once to PDU mode;
                // HandleDataReceived can route the returned PDU records through
                // the same QCMGR/CMGR decoder without dropping them.
                if (IsSmsReceiveMaintenanceEnabled(portName)
                    && TryGetSmsScope(portName, generation, out string scope))
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
            // Cứu các tin bị chẻ nhóm theo người gửi trước khi hẹn replay: nhóm
            // vừa được ghép đủ mảnh sẽ được chính lượt replay này đẩy vào inbox.
            TryRepairMultipartJournal(portName);
            if (IsSmsReceiveMaintenanceEnabled(portName))
                await ReportSimStorageUsageAsync(portName);
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

        using IDisposable foregroundLease =
            await AcquireForegroundOperationAsync(portName, "CALL", ct)
                .ConfigureAwait(false);

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
        bool channelPrepared = false;
        bool recordingStarted = false;
        string? recordingRemoteName = null;
        try
        {
            channelPrepared = await PrepareForegroundChannelAsync(
                    portName,
                    "CALL",
                    ct).ConfigureAwait(false);
            if (!channelPrepared)
                return false;

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

            try
            {
                if (channelPrepared)
                {
                    await PrepareForegroundChannelAsync(
                        portName,
                        "IDLE_AFTER_CALL",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Keep the original call result. A queued workflow still has to
                // pass its own cleanup before it can transmit.
            }
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
        // Generic NO CARRIER/BUSY/NO ANSWER reaches this method for outgoing
        // calls too. Queue receive recovery before checking _incomingCalls;
        // previously outgoing-only calls returned here and never restored SMS.
        ScheduleSafeUnreadSmsSweep(portName, "voice-call-ended", 250);
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



