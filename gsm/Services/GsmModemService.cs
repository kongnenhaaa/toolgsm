using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

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
    Task SweepUnreadSmsAsync(string portName);
    Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile);
    Task<bool> UploadFileToModemAsync(string portName, string localFile, string remoteFile);
    void StartPollingNetwork(string portName);
    /// <summary>
    /// Bật/tắt xác nhận rút SIM nhanh. Cờ được bật ngay khi CCID của phiên hiện
    /// tại đã được xác nhận, kể cả khi SIM còn đang chờ thao tác IMEI.
    /// </summary>
    void SetSimRemovalWatchEnabled(string portName, bool enabled);
    List<string> GetAvailablePorts();
    string ConnectAll(int baudRate = 115200);
    void Disconnect(string portName);
    void DisconnectAll();
    IDisposable SuspendPortBackgroundOperations(string portName);
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
    public bool DeliveryAccepted { get; set; }
}

public class GsmModemService : IGsmModemService
{
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
        "AT+CMGD=1,4",
        "AT+CPMS=\"ME\",\"ME\",\"ME\"",
        "AT+CMGD=1,4",
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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _keepAliveCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portHealthCts = new();
    private readonly ConcurrentDictionary<string, byte> _portHealthRecoveryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _portHealthFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simMonitorCts = new();
    private readonly ConcurrentDictionary<string, int> _suspendedBackgroundPorts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingNetworkPollingPorts =
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
    /// <summary>Guard chống race condition: đánh dấu port đang trong quá trình khởi tạo SIM đầu tiên.</summary>
    private readonly ConcurrentDictionary<string, bool> _simInitInProgress = new();
    private readonly ConcurrentDictionary<string, bool> _simInsertInProgress = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portLifetimeCts = new();
    private readonly object _connectLock = new object();


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

    public IDisposable SuspendPortBackgroundOperations(string portName)
    {
        lock (_backgroundOperationSync)
        {
            int leaseCount = _suspendedBackgroundPorts.AddOrUpdate(
                portName, 1, static (_, current) => current + 1);
            if (leaseCount == 1)
            {
                if (_pollingCts.ContainsKey(portName))
                    _pendingNetworkPollingPorts[portName] = 0;
                CancelLoop(_pollingCts, portName);
                CancelLoop(_keepAliveCts, portName);
                CancelLoop(_simMonitorCts, portName);
            }
        }

        return new BackgroundOperationLease(() =>
        {
            bool resumeNetworkPolling = false;
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
                resumeNetworkPolling = _pendingNetworkPollingPorts.TryRemove(portName, out _);
            }

            // CompleteSautoResetAsync reaches Active while the IMEI operation still owns
            // this lease. Its StartPollingNetwork request must run after the lease opens;
            // dropping it here left the UI with only IMEI/CCID/CSQ and no COPS/USSD data.
            if (resumeNetworkPolling)
                StartPollingNetwork(portName);
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

    private readonly SmsMultipartAssembler _exactMultipartAssembler = new();
    private readonly SmsImplicitMultipartAssembler _implicitMultipartAssembler = new();
    private readonly SmsMultipartJournal _multipartJournal = new(
        Path.Combine(AppBootstrap.DataDir, "sms_multipart_journal.json"));
    private readonly ConcurrentDictionary<string, DateTime> _deliveredStoredSms = new();
    private readonly ConcurrentDictionary<string, Channel<string>> _smsReadQueues = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSweepLocks = new(StringComparer.OrdinalIgnoreCase);
    // Value 1 = one read is queued/running; value 2 = the same SIM index was
    // announced again while it was busy and must be read once more. EC20 can
    // recycle an index immediately after CMGD, so silently dropping the second
    // notification can postpone a new SMS until the recovery sweep.
    private readonly ConcurrentDictionary<string, int> _queuedSmsIndices = new();

    private async Task<string> ReadStoredSmsAsync(string port, string msgIndex)
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
            string response = await SendCommandAsync(port, command, 25000, silent: true);
            if (command.StartsWith("AT+QCMGR=", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCompleteStoredSmsResponse(response, "+QCMGR:")) return response;
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
        if (string.IsNullOrWhiteSpace(response) || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return false;
        if (requiredHeader != null && !response.Contains(requiredHeader, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Regex.IsMatch(response, @"(?:^|\r?\n)OK\s*$", RegexOptions.IgnoreCase)) return false;
        return !string.IsNullOrWhiteSpace(SmsBodyDecoder.Decode(response).Content);
    }

    private string? TryAssembleMultipartExact(string port, string sender, DecodedSmsBody decoded, string msgIndex, string rawStoredSms, out List<string> indicesToDelete)
    {
        indicesToDelete = new List<string>();
        if (decoded.Concatenation == null)
        {
            SmsAssemblyResult implicitResult = _implicitMultipartAssembler.Add(port, sender, decoded.Content, msgIndex);
            if (implicitResult.Status == SmsAssemblyStatus.Waiting)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_FALLBACK] sender={sender} index={msgIndex} chars={decoded.Content.Length}; firmware không trả UDH, đang giữ SMS và chờ phần cuối." });
                return null;
            }
            if (implicitResult.Status == SmsAssemblyStatus.Completed)
            {
                indicesToDelete.AddRange(implicitResult.MessageIndices);
                return implicitResult.Content;
            }
            if (implicitResult.Status == SmsAssemblyStatus.Duplicate) return null;
            if (implicitResult.Status == SmsAssemblyStatus.Conflict)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_FALLBACK] Index không liên tục từ {sender}; giữ nguyên SMS trên SIM để quét lại, không phát đoạn rời." });
                return null;
            }

            DateTime now = DateTime.UtcNow;
            foreach (var item in _deliveredStoredSms.Where(x => now - x.Value > TimeSpan.FromMinutes(10)).ToArray())
                _deliveredStoredSms.TryRemove(item.Key, out _);
            string deliveryKey = $"{port}\u001f{msgIndex}\u001f{rawStoredSms}";
            // Mark only after the consumer accepts the SMS and CMGD succeeds.
            // A failed UI dispatch must be retried by the next recovery sweep.
            if (_deliveredStoredSms.ContainsKey(deliveryKey)) return null;
            if (!string.IsNullOrWhiteSpace(msgIndex)) indicesToDelete.Add(msgIndex);
            return decoded.Content;
        }

        IReadOnlyList<SmsMultipartJournal.Part> durableParts;
        try
        {
            // Persist before CMGD. EC20 SIM storage commonly has only 10 records while
            // carrier notices can contain 11-12 parts. This journal lets us release each
            // recyclable SIM slot without losing already decoded parts on app restart.
            durableParts = _multipartJournal.RecordAndGetParts(
                port, sender, decoded.Concatenation, decoded.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[MULTIPART_JOURNAL_ERROR] Không lưu an toàn được phần {decoded.Concatenation.Sequence}/{decoded.Concatenation.Total}: {ex.Message}. Giữ nguyên SMS trên SIM."
            });
            return null;
        }

        SmsAssemblyResult result = _exactMultipartAssembler.Add(port, sender, decoded.Concatenation, decoded.Content, msgIndex);
        // EC20 SIM storage commonly has only 10 records. A carrier SMS can be
        // 11-12 parts, so waiting for all parts before CMGD deadlocks the SIM. The
        // durable journal now owns a safe copy, so release only the current record.
        if (!string.IsNullOrWhiteSpace(msgIndex)
            && result.Status is SmsAssemblyStatus.Waiting or SmsAssemblyStatus.Completed or SmsAssemblyStatus.Duplicate)
            indicesToDelete.Add(msgIndex);

        bool durableComplete = durableParts.Count == decoded.Concatenation.Total
            && Enumerable.Range(1, decoded.Concatenation.Total)
                .SequenceEqual(durableParts.Select(part => part.Sequence));
        if (durableComplete)
        {
            // Prefer the journal result. It also enables a delivery retry after the UI
            // rejected/failed the first completed event and the in-memory assembler has
            // already marked the final segment as a duplicate.
            return string.Concat(durableParts.Select(part => part.Content));
        }
        if (result.Status == SmsAssemblyStatus.Duplicate) return null;
        if (result.Status is SmsAssemblyStatus.Invalid or SmsAssemblyStatus.Conflict)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART] UDH không hợp lệ hoặc xung đột từ {sender}; giữ SMS trên SIM để quét lại." });
        return null;
    }

    private void QueueStoredSmsRead(string port, string msgIndex)
    {
        string queueKey = $"{port}\u001f{msgIndex}";
        if (!_queuedSmsIndices.TryAdd(queueKey, 1))
        {
            _queuedSmsIndices.AddOrUpdate(queueKey, 2, static (_, _) => 2);
            return;
        }

        Channel<string> channel = _smsReadQueues.GetOrAdd(port, p =>
        {
            var created = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _ = Task.Run(() => ProcessStoredSmsQueueAsync(p, created.Reader));
            return created;
        });
        if (!channel.Writer.TryWrite(msgIndex)) _queuedSmsIndices.TryRemove(queueKey, out _);
    }

    private async Task ProcessStoredSmsQueueAsync(string port, ChannelReader<string> reader)
    {
        await foreach (string msgIndex in reader.ReadAllAsync())
        {
            try { await ProcessStoredSmsAsync(port, msgIndex); }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Lỗi đọc index {msgIndex}: {ex.Message}. SMS vẫn được giữ trên SIM." });
            }
            finally
            {
                string queueKey = $"{port}\u001f{msgIndex}";
                while (_queuedSmsIndices.TryGetValue(queueKey, out int pending))
                {
                    if (pending > 1)
                    {
                        if (!_queuedSmsIndices.TryUpdate(queueKey, 1, pending)) continue;
                        if (!reader.Completion.IsCompleted
                            && _smsReadQueues.TryGetValue(port, out Channel<string>? channel)
                            && channel.Writer.TryWrite(msgIndex))
                            break;
                        _queuedSmsIndices.TryRemove(queueKey, out _);
                        break;
                    }

                    if (_queuedSmsIndices.TryRemove(
                        new KeyValuePair<string, int>(queueKey, pending))) break;
                }
            }
        }
    }

    private async Task ProcessStoredSmsAsync(string port, string msgIndex)
    {
        string smsContent = string.Empty;
        bool success = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            smsContent = await ReadStoredSmsAsync(port, msgIndex);
            if (IsCompleteStoredSmsResponse(smsContent)) { success = true; break; }
            await Task.Delay(750);
        }

        if (!success)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Index {msgIndex} chưa trả đủ header/body/OK sau 3 lần; giữ SMS và sẽ quét lại." });
            _ = Task.Run(async () => { await Task.Delay(2000); await SweepUnreadSmsAsync(port); });
            return;
        }

        DecodedSmsBody decoded = SmsBodyDecoder.Decode(smsContent);
        if (string.IsNullOrWhiteSpace(decoded.Content))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Index {msgIndex} có body rỗng; không xóa." });
            return;
        }

        string sender = ParseSenderFromCmgr(smsContent);
        if (sender == "Unknown" && !string.IsNullOrWhiteSpace(decoded.Sender))
            sender = DecodeSmsSender(decoded.Sender);
        if (decoded.RecoveredMislabelledUcs2)
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_ENCODING_RECOVERED] sender={sender} index={msgIndex} chars={decoded.Content.Length}; payload UCS2 gắn nhầm DCS GSM-7 đã được khôi phục."
            });
        string? fullContent = TryAssembleMultipartExact(port, sender, decoded, msgIndex, smsContent, out var indicesToDelete);
        if (fullContent == null)
        {
            foreach (string bufferedIndex in indicesToDelete.Distinct(StringComparer.Ordinal))
            {
                string deleteResponse = await SendCommandAsync(port, $"AT+CMGD={bufferedIndex},0", 5000, silent: true);
                if (deleteResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART] Đã giữ phần {bufferedIndex} trong RAM nhưng chưa giải phóng được ô SIM; sweep sẽ thử lại." });
            }
            if (decoded.Concatenation != null)
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART] sender={sender} ref={decoded.Concatenation.Reference} seq={decoded.Concatenation.Sequence}/{decoded.Concatenation.Total} index={msgIndex} chars={decoded.Content.Length}; đang chờ đủ phần." });
            return;
        }

        if (decoded.Concatenation != null)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_COMPLETE] sender={sender} ref={decoded.Concatenation.Reference} total={decoded.Concatenation.Total} chars={fullContent.Length}" });
        else if (indicesToDelete.Count > 1)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_FALLBACK_COMPLETE] sender={sender} total={indicesToDelete.Count} chars={fullContent.Length}" });

        var delivery = new GsmDataEventArgs
        {
            PortName = port,
            Data = fullContent,
            MsgIndex = msgIndex,
            Sender = sender,
            Otp = ExtractOtp(fullContent) ?? string.Empty
        };
        try
        {
            SmsReceived?.Invoke(this, delivery);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Không phát được SMS index {msgIndex}; giữ nguyên trên SIM: {ex.Message}" });
            return;
        }

        if (!delivery.DeliveryAccepted)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = $"[SMS_DELIVERY_REJECTED] UI chưa xác nhận sở hữu SMS index {msgIndex}; giữ bản hoàn chỉnh để sweep phát lại."
            });
            return;
        }

        if (decoded.Concatenation != null)
        {
            try
            {
                _multipartJournal.Complete(port, sender, decoded.Concatenation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The inbox already owns the complete SMS. Keep running and retain the
                // journal entry for TTL cleanup; the in-memory fingerprint prevents a
                // duplicate delivery during this session.
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = port,
                    Data = $"[MULTIPART_JOURNAL_WARN] Đã nhận đủ SMS nhưng chưa dọn được journal: {ex.Message}"
                });
            }
        }

        // SmsReceived handlers synchronously take ownership of the decoded
        // content before returning. Delete immediately afterwards so a small
        // EC20 SIM store cannot fill while the UI is busy. Only this service is
        // allowed to issue CMGD for received messages; consumers must never
        // delete the same recyclable index a second time.
        foreach (string index in indicesToDelete)
        {
            string deleteResponse = await SendCommandAsync(port, $"AT+CMGD={index},0", 5000, silent: true);
            if (deleteResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Không xóa được index {index}; bộ chống trùng sẽ ngăn phát lại trong phiên này." });
            if (!deleteResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                && decoded.Concatenation == null)
                _deliveredStoredSms[$"{port}\u001f{index}\u001f{smsContent}"] = DateTime.UtcNow;
        }

    }

    private class PendingMultipart
    {
        public string Port { get; set; } = "";
        public string Sender { get; set; } = "";
        public List<string> Parts { get; set; } = new();
        public List<string> MsgIndices { get; set; } = new();
        public DateTime LastAt { get; set; } = DateTime.Now;
        public string? Combined => Parts.Count == 0 ? null : string.Join("", Parts);
    }

    private readonly ConcurrentDictionary<string, PendingMultipart> _multipartBuffer = new();
    private static readonly TimeSpan MultipartTimeout = TimeSpan.FromSeconds(45);
    string MultipartKey(string port, string sender) => $"{port}|{sender}";

    string? TryAssembleMultipart(string port, string sender, string partContent, string msgIndex, out List<string> indicesToDelete)
    {
        indicesToDelete = new List<string>();
        var key = MultipartKey(port, sender);
        var now = DateTime.Now;

        foreach (var kv in _multipartBuffer.ToArray())
        {
            if (now - kv.Value.LastAt > MultipartTimeout)
            {
                if (_multipartBuffer.TryRemove(kv.Key, out var timedOut))
                {
                    string? fullText = timedOut.Combined;
                    if (!string.IsNullOrEmpty(fullText))
                    {
                        string? otp = ExtractOtp(fullText);
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = timedOut.Port, Data = $"[MULTIPART] Ghép nối tin dài từ {timedOut.Sender} quá hạn, tự động xuất phần đã nhận." });
                        
                        // Xóa các phần tin nhắn cũ trên SIM
                        foreach (var idx in timedOut.MsgIndices)
                        {
                            _ = SendCommandAsync(timedOut.Port, $"AT+CMGD={idx}", 3000, silent: true);
                        }

                        SmsReceived?.Invoke(this, new GsmDataEventArgs 
                        { 
                            PortName = timedOut.Port, 
                            Data = fullText, 
                            MsgIndex = "", 
                            Sender = timedOut.Sender,
                            Otp = otp ?? ""
                        });
                    }
                }
            }
        }

        // KHÔNG dùng heuristic ghép tin text-mode.
        // Lý do: mỗi AT+CMGR trả về 1 tin hoàn chỉnh đã được modem decode.
        // Multipart PDU thật (UDH header) được xử lý đúng bởi MainViewModel.TryBufferConcatenatedSms.
        // Heuristic length==160 gây lỗi: 2 tin riêng biệt từ cùng sender (tin 1 đúng 160 ký tự)
        // sẽ bị buffer rồi merge sai thành 1 tin.
        // → Mỗi tin đọc từ AT+CMGR là 1 tin độc lập, trả về ngay lập tức.
        if (!string.IsNullOrEmpty(msgIndex)) indicesToDelete.Add(msgIndex);
        return partContent;
    }

    static readonly Regex CmgrHeaderRegex = new(
        @"\+(?:Q?CMGR|CMT):\s*""[^""]*"",\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    string ParseSenderFromCmgr(string raw)
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
            // Xóa cache ngủ và đếm lỗi để quét mới hoàn toàn
            _sleepingPorts.Clear();
            _connectionErrors.Clear();

            var ports = GetAvailablePorts();
            BackendConcurrency.ConfigureThreadPool(ports.Count);
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = "SYSTEM", Data = $"[HỆ THỐNG] Quét cổng COM: Phát hiện {ports.Count} cổng trong Windows ({string.Join(", ", ports)})" });

            Parallel.ForEach(ports, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, ports.Count)
            }, p =>
            {
                if (!_serialPorts.ContainsKey(p))
                {
                    if (_sleepingPorts.TryGetValue(p, out var sleepUntil))
                    {
                        if (DateTime.Now < sleepUntil)
                            return; // Đang trong thời gian ngủ, bỏ qua
                        else
                            _sleepingPorts.TryRemove(p, out _); // Đã hết thời gian ngủ
                    }

                    try
                    {
                        var sp = new SerialPort(p, baudRate, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = 5000,
                            WriteTimeout = 5000,
                            DtrEnable = true,
                            RtsEnable = true
                        };
                        
                        SerialDataReceivedEventHandler handler = (s, e) => HandleDataReceived(p, sp);
                        sp.DataReceived += handler;
                        sp.ErrorReceived += (s, e) => HandleErrorReceived(p, sp, e);
                        sp.Open();
                        
                        _serialPorts.TryAdd(p, sp);
                        _dataReceivedHandlers.TryAdd(p, handler);
                        _semaphores.TryAdd(p, new SemaphoreSlim(1, 1));
                        _portBuffers.TryAdd(p, new StringBuilder());
                        _portBufferLocks.TryAdd(p, new object());
                        _connectionErrors.TryRemove(p, out _); // Reset lỗi khi kết nối thành công
                        if (_portLifetimeCts.TryRemove(p, out var staleLifetime))
                        {
                            try { staleLifetime.Cancel(); staleLifetime.Dispose(); } catch { }
                        }
                        _portLifetimeCts[p] = new CancellationTokenSource();
                        
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Đã kết nối thành công {p} (Baud: {baudRate})" });
                        // Báo UI ngay khi COM đã mở, không chờ quá trình đọc SIM/CCID hoàn tất.
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = "[PORT_OPENED]" });
                        
                        // Khởi động luồng giám sát SIM nền tảng toàn cầu (Global SIM Monitor) đảm bảo theo dõi 100% thời gian thực
                        StartGlobalSimMonitor(p);
                        StartPortHealthSupervisor(p);
                        
                        newlyOpenedPorts.Add(p);
                    }
                    catch (Exception ex)
                    {
                        int errors = _connectionErrors.AddOrUpdate(p, 1, (key, old) => old + 1);
                        if (errors >= 3)
                        {
                            _sleepingPorts[p] = DateTime.Now.AddSeconds(30); // Cho cổng ngủ 30 giây
                            _connectionErrors.TryRemove(p, out _);
                            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Lỗi kết nối {p} quá 3 lần: {ex.Message}. Tạm ngưng kết nối cổng này trong 30 giây để tránh spam log." });
                        }
                        else
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Lỗi kết nối {p}: {ex.Message}" });
                        }
                        failedPorts.Add(p);
                    }
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
                    await Task.Delay(TimeSpan.FromSeconds(15), token);
                    if (token.IsCancellationRequested) break;

                    if (_suspendedBackgroundPorts.ContainsKey(portName)
                        || IsCallInProgress(portName)
                        || _commandTcs.ContainsKey(portName))
                    {
                        continue;
                    }

                    bool healthy = _serialPorts.TryGetValue(portName, out var serialPort)
                        && serialPort.IsOpen;
                    if (healthy)
                    {
                        string probe = await SendCommandAsync(
                            portName, "AT", 3000, silent: true, ct: token);
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

                    consecutiveFailures++;
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
                        Disconnect(portName);
                        PortDisconnected?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = "COM không phản hồi; hệ thống đang tự kết nối lại."
                        });
                        // Disconnect cancels this supervisor's token by design;
                        // use an uncancelled handoff delay so the reconnect still
                        // runs after the old handle has been removed.
                        await Task.Delay(1500);
                        ConnectAll(115200);
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

    private async Task<string> ReadCcidWithFallbackAsync(string portName, int timeoutMs = 5000, bool silent = true)
    {
        string vendor = _portVendors.TryGetValue(portName, out var v) ? v : "";
        string ccid = "ERROR";

        if (vendor.Contains("QUECTEL"))
        {
            ccid = await SendCommandAsync(portName, "AT+QCCID", timeoutMs, silent);
        }
        
        if (!HasReadableCcid(ccid))
        {
            ccid = await SendCommandAsync(portName, "AT+CCID", timeoutMs, silent);
        }

        if (!HasReadableCcid(ccid))
        {
            string crsm = await SendCommandAsync(portName, "AT+CRSM=176,12258,0,0,10", timeoutMs, silent);
            if (!crsm.Contains("ERROR") && crsm.Contains("+CRSM:"))
            {
                ccid = crsm; // Lấy luôn chuỗi raw để logic parse phía trên tự xử lý
            }
        }

        return ccid;
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
    private async Task<bool> RestartSimStackOfflineAsync(string portName, CancellationToken ct = default)
    {
        string minimum = await SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true, ct: ct);
        if (IsCommandFailure(minimum) || !await ConfirmCfunAsync(portName, 0, ct)) return false;

        await Task.Delay(500, ct);
        string airplane = await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct: ct);
        if (IsCommandFailure(airplane) || !await ConfirmCfunAsync(portName, 4, ct)) return false;

        await Task.Delay(800, ct);
        return true;
    }

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

            // CPIN: NOT INSERTED là tín hiệu chắc chắn nhất. Không cần chờ thêm
            // QSIMSTAT/CCID (các lệnh đó có thể timeout khi khay đã rỗng), nên
            // chuyển UI sang Chờ cắm SIM ngay sau mốc xác nhận 5 giây.
            if (cpinText.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase))
            {
                _lastSimState[portName] = false;
                ClearSimRemovalEvidence(portName);
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (xác nhận sau 5 giây)."
                });
                await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true, ct: token);
                StartHotplugWaitLoop(portName);
                return;
            }

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
            bool ccidPresent = HasReadableCcid(ccidText);
            bool explicitNotInserted = cpinText.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase);
            bool confirmedAbsent = !cpinPresent && !sensorPresent && !ccidPresent
                && (explicitNotInserted || IsConfirmedSimAbsentDuringPolling(
                    cpinText, qsimText, ccidText, cfunText, stackDisabledByTool: false));

            if (!confirmedAbsent) return;

            // Tách task xác nhận khỏi dictionary trước khi phát log. MainViewModel
            // sẽ vô hiệu hóa cờ khi nhận WAITING_FOR_SIM; không được hủy chính task
            // đang gửi CFUN=4 và khởi động vòng chờ SIM.
            if (_simRemovalConfirmationCts.TryGetValue(portName, out var currentConfirmation)
                && ReferenceEquals(currentConfirmation, confirmationCts))
                _simRemovalConfirmationCts.TryRemove(portName, out _);
            _lastSimState[portName] = false;
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
        await SendCommandAsync(portName, "AT+CMGD=1,4", 5000, silent: true, ct: ct);
        await Task.Delay(200, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"ME\",\"ME\",\"ME\"", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CMGD=1,4", 5000, silent: true, ct: ct);
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

        bool simLocked = result.CpinResponse.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                      || result.CpinResponse.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
        bool simReady = Regex.IsMatch(result.CpinResponse, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
        if (simLocked)
        {
            _lastSimState[portName] = false;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {result.CpinResponse.Trim()}" });
            return;
        }

        string ccid = simReady
            ? await SendCommandAsync(portName, "AT+ICCID", 5000, silent: true, ct: ct)
            : "ERROR";
        if (HasReadableCcid(ccid))
        {
            _lastSimState[portName] = true;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
            return;
        }

        _lastSimState[portName] = false;
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Không đọc được SIM" });
        StartHotplugWaitLoop(portName);
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
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (Quét nền)!" });
                    _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    StartHotplugWaitLoop(portName);
                }
                else if (isSimRemoved && lastState)
                {
                    // Chỉ bật theo dõi rút SIM sau khi *111# và *101# đã cùng OK.
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
                        await ReopenSerialHandleBetweenSautoPassesAsync(portName, token);
                        continue;
                    }

                    string ccidResponse = await SendCommandAsync(portName, "AT+ICCID", 5000, silent: true, ct: token);
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

    public void StartPollingNetwork(string portName)
    {
        CancellationToken token;
        lock (_backgroundOperationSync)
        {
            if (_suspendedBackgroundPorts.ContainsKey(portName))
            {
                _pendingNetworkPollingPorts[portName] = 0;
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
                token = newCts.Token;
            }
        }

        // Recovery sweep is independent from network/operator detection. +CMTI can be lost
        // while a long AT command is running or while the USB serial driver reconnects.
        // CMGL=ALL also recovers multipart segments already marked REC READ by CMGR before
        // a restart. Delay the first bulk sweep until SAuto's CPIN/CSQ/COPS/*111# startup
        // window has completed; live +CMTI/+CMT is still processed immediately.
        _ = Task.Run(async () =>
        {
            bool firstSweep = true;
            while (!token.IsCancellationRequested && _serialPorts.ContainsKey(portName))
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
            while (true)
            {
                try
                {
                    await Task.Delay(
                        operatorReported
                            ? GsmBackgroundSupervisor.GetSignalScanInterval(
                                SettingsService.Current.SignalScanIntervalSeconds)
                            : TimeSpan.FromMilliseconds(500),
                        token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;
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
                // While registration is pending, query COPS every pass so a COM
                // does not sit in a 50-second blind gap. Once COPS succeeds,
                // keep the lighter five-cycle cadence for health monitoring.
                if (operatorReported && cycles % 5 != 0)
                {
                    // CSQ is a health/UI probe; do not let it delay the first
                    // post-IMEI COPS query or the first *111# activation.
                    await Task.Delay(100, token);
                    string liveCsq = await SendCommandAsync(
                        portName, "AT+CSQ", 2000, silent: true, ct: token);
                    if (liveCsq.Contains("+CSQ:", StringComparison.OrdinalIgnoreCase))
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = liveCsq.Trim() });
                    continue;
                }

                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true, ct: token);
                if (TryParseCopsResponse(copsStr, out _, out string act))
                {
                    string netType = MapCopsAccessTechnology(act);
                    if (!string.IsNullOrWhiteSpace(netType))
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_TYPE] {netType}" });
                    
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    operatorReported = true;
                    continue;
                }

                // Only probe CSQ after a COPS miss. A slow CSQ response must not
                // postpone the first network registration/USSD attempt.
                await Task.Delay(100, token);
                string csqStr = await SendCommandAsync(
                    portName, "AT+CSQ", 2000, silent: true, ct: token);
                if (csqStr.Contains("+CSQ:", StringComparison.OrdinalIgnoreCase))
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = csqStr.Trim() });

                if (operatorReported)
                {
                    // COPS disappeared after a previously healthy registration.
                    // Re-enter the recovery path instead of silently continuing
                    // with stale network/UI data.
                    operatorReported = false;
                    waitingNoticeCount++;
                    // Re-enter the per-port recovery probe quickly after a
                    // previously healthy registration disappears.
                    cycles = 29;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = "[NETWORK_LOST] COPS biến mất sau khi đang hoạt động; bắt đầu khôi phục đăng ký mạng."
                    });
                }

                // Nếu modem có CSQ nhưng không tự hoàn tất COPS, khởi động lại
                // auto-selection giống SAuto. Không dùng COPS=2/CFUN vì có thể
                // làm rơi phiên thoại hoặc nạp lại danh tính trong lúc chạy.
                // Re-arm auto-selection early on COMs that have CSQ but no COPS.
                if (cycles == 30)
                {
                    waitingNoticeCount++;
                    string creg = await SendCommandAsync(
                        portName, "AT+CREG?", 4000, silent: true, ct: token);
                    string cgreg = await SendCommandAsync(
                        portName, "AT+CGREG?", 4000, silent: true, ct: token);
                    string cereg = await SendCommandAsync(
                        portName, "AT+CEREG?", 4000, silent: true, ct: token);
                    if (IsNetworkRegistered(creg))
                    {
                        string registeredType = IsNetworkRegistered(cereg)
                            ? "4G"
                            : IsNetworkRegistered(cgreg) ? "3G" : "2G";
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = $"[NETWORK_FALLBACK] type={registeredType}; CREG đã đăng ký nhưng COPS không trả tên nhà mạng."
                        });
                        operatorReported = true;
                        continue;
                    }

                    string copsAuto = await SendCommandAsync(
                        portName, "AT+COPS=0", 15000, silent: true, ct: token);
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_RECOVERY] Có CSQ nhưng COPS chưa trả nhà mạng; đã kích hoạt lại auto-select (lần {waitingNoticeCount}): {copsAuto.Trim()}"
                    });
                    cycles = 0;
                    continue;
                }
                // Nếu vẫn chưa đăng ký sau nhiều chu kỳ, tiếp tục để vòng lặp
                // tự kiểm tra trạng thái modem ở mốc kế tiếp.
                else if (cycles >= 60)
                {
                    cycles = 0;
                    waitingNoticeCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_WAITING] Vẫn chưa có COPS sau nhiều chu kỳ (lần {waitingNoticeCount}); tiếp tục theo dõi."
                    });
                }
                else if (cycles % 75 == 0)
                {
                    waitingNoticeCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_WAITING] Modem vẫn phản hồi nhưng chưa có COPS (lần chờ {waitingNoticeCount}); tiếp tục dò."
                    });
                }
            }
        }, token);
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
        if (string.IsNullOrWhiteSpace(response)) return false;
        Match match = Regex.Match(
            response,
            @"\+(?:C|CG|CE)REG:\s*(?:\d+\s*,\s*)?(?<stat>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success && match.Groups["stat"].Value is "1" or "5";
    }

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
                @"\+CMTI:\s*""[^""]+"",\s*(\d+)",
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

            if (string.IsNullOrWhiteSpace(chunk)) return;

            if (!_portBuffers.TryGetValue(portName, out var buffer)) return;
            buffer.Append(chunk);
            
            string currentData = buffer.ToString();
            
            // Buffer giới hạn 32 KB — đủ chứa cả PDU SMS Unicode dài nhất (thường < 600 hex chars/phần)
            // Không reset buffer khi còn dữ liệu hợp lệ đang được xử lý. Chỉ reset khi thực sự overflow.
            if (buffer.Length > 32000)
            {
                // Cuu cac +CMTI chua xu ly truoc khi xoa buffer de khong miss SMS
                var salvageCmti = Regex.Matches(buffer.ToString(), @"\+CMTI:\s*""[^""]*"",\s*(\d+)");
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[WARNING] Buffer overflow ({buffer.Length} chars) - dang lam sach; cuu {salvageCmti.Count} CMTI." });
                buffer.Clear();
                currentData = "";
                foreach (Match m in salvageCmti) QueueStoredSmsRead(portName, m.Groups[1].Value);
                if (salvageCmti.Count == 0) _ = SweepUnreadSmsAsync(portName);
            }

            if (currentData.Contains("+CMS ERROR: 302") || currentData.Contains("memory full"))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi đầy bộ nhớ SIM (+CMS ERROR: 302). Đang quét từng SMS; không xóa hàng loạt để tránh mất đoạn." });
                _ = SweepUnreadSmsAsync(portName);
                buffer.Replace("+CMS ERROR: 302", "");
                buffer.Replace("memory full", "");
                currentData = buffer.ToString();
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
                var matches = Regex.Matches(currentData, @"\+CMTI:\s*""[^""]+"",\s*(\d+)");
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
                var directMatches = Regex.Matches(
                    currentData,
                    @"\+CMT:[^\r\n]*(?:\r?\n)([^\r\n]+)(?:\r?\n|$)",
                    RegexOptions.IgnoreCase);
                foreach (Match direct in directMatches)
                {
                    string rawDirect = direct.Value;
                    DecodedSmsBody decodedDirect = SmsBodyDecoder.Decode(rawDirect);
                    if (string.IsNullOrWhiteSpace(decodedDirect.Content)) continue;
                    string senderDirect = ParseSenderFromCmgr(rawDirect);
                    if (senderDirect == "Unknown" && !string.IsNullOrWhiteSpace(decodedDirect.Sender))
                        senderDirect = decodedDirect.Sender;
                    string? completeDirect;
                    if (decodedDirect.Concatenation != null)
                    {
                        SmsAssemblyResult assembled = _exactMultipartAssembler.Add(
                            portName, senderDirect, decodedDirect.Concatenation, decodedDirect.Content, "");
                        completeDirect = assembled.Status == SmsAssemblyStatus.Completed ? assembled.Content : null;
                    }
                    else completeDirect = decodedDirect.Content;

                    if (!string.IsNullOrWhiteSpace(completeDirect))
                    {
                        SmsReceived?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = completeDirect,
                            Sender = senderDirect,
                            Otp = ExtractOtp(completeDirect) ?? string.Empty
                        });
                    }
                    buffer.Replace(rawDirect, "");
                }
                currentData = buffer.ToString();
            }

            // Xử lý kết quả quét AT+CMGL="REC UNREAD"
            // Định dạng: +CMGL: <index>,"REC UNREAD",...
            if (currentData.Contains("+CMGL:"))
            {
                var matches = Regex.Matches(currentData, @"\+CMGL:\s*(\d+)");
                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        string msgIndex = match.Groups[1].Value;
                        // Chỉ log nếu không trùng với các index đang đọc từ +CMTI
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[Sweep] Đã vét được tin nhắn kẹt ở vị trí {msgIndex}, đang đọc..." });
                        
                        // Cắt bỏ phần thông báo này khỏi bộ đệm
                        buffer.Replace(match.Value, ""); 
                    }
                    currentData = buffer.ToString();
                    
                    foreach (Match match in matches) QueueStoredSmsRead(portName, match.Groups[1].Value);
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
            if (_commandTcs.TryGetValue(portName, out var tcs))
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
            // ---------------------------------------------------------
            // 3. DỌN DẸP RÁC BỘ ĐỆM AN TOÀN
            // ---------------------------------------------------------
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
        foreach (Channel<string> queue in _smsReadQueues.Values) queue.Writer.TryComplete();
        _smsReadQueues.Clear();
        _queuedSmsIndices.Clear();
        _smsSweepLocks.Clear();
    }

    public void Disconnect(string portName)
    {
        _incomingCalls.TryRemove(portName, out _);
        _incomingCallNotifications.TryRemove(portName, out _);
        if (_outgoingCallEndSignals.TryRemove(portName, out var callEndSignal))
            callEndSignal.TrySetResult("Port disconnected");
        if (_smsReadQueues.TryRemove(portName, out var smsQueue)) smsQueue.Writer.TryComplete();
        foreach (string key in _queuedSmsIndices.Keys.Where(k => k.StartsWith(portName + "\u001f", StringComparison.Ordinal)))
            _queuedSmsIndices.TryRemove(key, out _);
        _exactMultipartAssembler.ClearPort(portName);
        _implicitMultipartAssembler.ClearPort(portName);
        foreach (string key in _deliveredStoredSms.Keys.Where(k => k.StartsWith(portName + "\u001f", StringComparison.Ordinal)))
            _deliveredStoredSms.TryRemove(key, out _);

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
        else if (command.StartsWith("AT+CMGR")) timeoutMs = 25000;

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
                    return "ERROR: Timeout sending SMS payload; SMS channel recovery failed";

                return "ERROR: Timeout sending SMS payload";
            }

            string finalResp = await tcs.Task;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
        }
        catch (OperationCanceledException)
        {
            try { if (sp.IsOpen) sp.Write("\x1B"); } catch { }
            return "ERROR: SMS operation cancelled";
        }
        catch (IOException ex)
        {
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Cáp bị rút khi đang gửi SMS!" });
            return $"ERROR: Rút cáp đột ngột - {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
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
            string command = GetModemProfile(portName)?.IsQuectel == true ? "AT+CMGL=4" : "AT+CMGL=\"ALL\"";
            await SendCommandAsync(portName, command, 25000, silent: true);
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
            string? lastCallState = null;
            int clccAttempts = 0;
            string lastClcc = string.Empty;
            while (DateTime.UtcNow < deadline && clccAttempts < (remoteWavName != null ? 60 : 8))
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

                if (!sawOutgoingVoiceSession && clccAttempts >= 8)
                    break;

                TimeSpan pollDelay = deadline - DateTime.UtcNow;
                if (pollDelay > TimeSpan.Zero)
                    await Task.Delay(pollDelay > TimeSpan.FromMilliseconds(500)
                        ? TimeSpan.FromMilliseconds(500) : pollDelay, ct);
            }

            if (!sawOutgoingVoiceSession && !endSignal.Task.IsCompleted)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs
                {
                    PortName = portName,
                    Data = $"[CALL_NO_VOICE_SESSION] ATD trả OK nhưng modem không tạo phiên thoại CLCC (CLCC={lastClcc}). Cuộc gọi được đánh dấu thất bại."
                });
                await SendCommandAsync(portName, "ATH", 3000, silent: true);
                _ = LogVoiceFailureDiagnosticsAsync(portName, "NO VOICE SESSION");
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
                    return false;
                }
                await timer;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Hết {durationSeconds} giây → Dập máy (ATH)."
            });
            await SendCommandAsync(portName, "ATH", 3000, silent: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { await SendCommandAsync(portName, "ATH", 3000, silent: true); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = $"[CALL] Lỗi: {ex.Message}"
            });
            try { await SendCommandAsync(portName, "ATH", 3000, silent: true); } catch { }
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



