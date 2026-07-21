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
    List<string> GetAvailablePorts();
    string ConnectAll(int baudRate = 115200);
    void Disconnect(string portName);
    void DisconnectAll();
    void StartHotplugWaitLoop(string portName);
    Task HandleSimInsertedAsync(string portName);
    Task<bool> ReinitializeSettingsAsync(string portName, CancellationToken ct = default);
    Task ReloadSimAsync(string portName);
    Task<bool> ReloadAndResumeSimAsync(string portName, CancellationToken ct = default);
    Task<bool> CallWithAudioAsync(string portName, string phoneNumber, string? wavPath, int durationSeconds = 30, bool record = false, CancellationToken ct = default);
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
    event EventHandler<gsm.Models.IncomingCallSession> IncomingCallAnswered;
    event EventHandler<gsm.Models.IncomingCallSession> IncomingCallEnded;
}

public class GsmDataEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string MsgIndex { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
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
    private readonly ConcurrentDictionary<string, byte> _incomingAnswerOperations = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simMonitorCts = new();
    private readonly ConcurrentDictionary<string, bool> _lastSimState = new();
    private readonly ConcurrentDictionary<string, bool> _simStackDisabledByTool = new();
    private readonly ConcurrentDictionary<string, int> _simRemovalEvidenceCounts = new();
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
    private readonly ConcurrentDictionary<string, DateTime> _deliveredStoredSms = new();
    private readonly ConcurrentDictionary<string, Channel<string>> _smsReadQueues = new();
    // Value 1 = one read is queued/running; value 2 = the same SIM index was
    // announced again while it was busy and must be read once more. EC20 can
    // recycle an index immediately after CMGD, so silently dropping the second
    // notification can postpone a new SMS until the recovery sweep.
    private readonly ConcurrentDictionary<string, int> _queuedSmsIndices = new();

    private async Task<string> ReadStoredSmsAsync(string port, string msgIndex)
    {
        if (GetModemProfile(port)?.IsQuectel == true)
            return await SendCommandAsync(port, $"AT+CMGR={msgIndex}", 25000, silent: true);

        // Quectel EC20/EC2x exposes uid, segment and total through QCMGR in text mode.
        // Fall back to standard CMGR for older firmware and non-Quectel modems.
        if (GetModemProfile(port)?.Supports(ModemCapability.QuectelStoredSms) == true)
        {
            string response = await SendCommandAsync(port, $"AT+QCMGR={msgIndex}", 15000, silent: true);
            if (IsCompleteStoredSmsResponse(response, "+QCMGR:")) return response;
        }
        return await SendCommandAsync(port, $"AT+CMGR={msgIndex}", 25000, silent: true);
    }

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

        SmsAssemblyResult result = _exactMultipartAssembler.Add(port, sender, decoded.Concatenation, decoded.Content, msgIndex);
        // EC20 SIM storage commonly has only 10 records. A carrier SMS can be
        // 11-12 parts, so waiting for all parts before CMGD deadlocks the SIM.
        // The assembler already owns a safe in-memory copy of this decoded part;
        // release only the current record so the next part/OTP has room to arrive.
        if (!string.IsNullOrWhiteSpace(msgIndex)
            && result.Status is SmsAssemblyStatus.Waiting or SmsAssemblyStatus.Completed or SmsAssemblyStatus.Duplicate)
            indicesToDelete.Add(msgIndex);
        if (result.Status == SmsAssemblyStatus.Completed)
        {
            return result.Content;
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
            sender = decoded.Sender;
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

        try
        {
            SmsReceived?.Invoke(this, new GsmDataEventArgs
            {
                PortName = port,
                Data = fullContent,
                MsgIndex = msgIndex,
                Sender = sender,
                Otp = ExtractOtp(fullContent) ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Không phát được SMS index {msgIndex}; giữ nguyên trên SIM: {ex.Message}" });
            return;
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
    public event EventHandler<gsm.Models.IncomingCallSession>? IncomingCallAnswered;
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

        // Registry enumeration order is not physical USB order. Sort by the USB
        // topology first so separate GSM boxes/hubs stay together. XR21V1414-based
        // GSM banks are wired on the front panel as C, B, A, D (not A, B, C, D).
        // This maps the first connected bank, for example, to COM3, COM12, COM4,
        // COM6 and places the next physical bank immediately below it in the UI.
        var filtered = filteredCandidates
            .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.LocationInformation) ? 1 : 0)
            .ThenBy(candidate => candidate.LocationInformation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetPhysicalInterfaceRank)
            .ThenBy(candidate => GetPortNumber(candidate.PortName))
            .Select(candidate => candidate.PortName)
            .ToList();

        // Fallback nếu danh sách lọc trống
        if (filtered.Count == 0)
        {
            foreach (var p in allSystemPorts.OrderBy(GetPortNumber))
            {
                if (!bluetoothPorts.Contains(p))
                {
                    filtered.Add(p);
                }
            }
        }

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
            0x04 => 0, // Channel C - first socket on the physical GSM bank
            0x02 => 1, // Channel B
            0x00 => 2, // Channel A
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
                        sp.ErrorReceived += (s, e) => HandleErrorReceived(p, sp);
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

    private void HandleErrorReceived(string portName, SerialPort sp)
    {
        Disconnect(portName);
        PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi phần cứng (Có thể bị rút cáp)" });
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

    private async Task<QuectelModemProfile> DetectModemProfileAsync(string portName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string manufacturer = await SendCommandAsync(portName, "AT+CGMI", 5000, silent: true);
        string model = await SendCommandAsync(portName, "AT+CGMM", 5000, silent: true);
        string firmware = await SendCommandAsync(portName, "AT+CGMR", 5000, silent: true);
        if (IsCommandFailure(manufacturer) || IsCommandFailure(model))
        {
            string ati = await SendCommandAsync(portName, "ATI", 5000, silent: true);
            if (IsCommandFailure(manufacturer) && ati.Contains("QUECTEL", StringComparison.OrdinalIgnoreCase))
                manufacturer = "Quectel";
            if (IsCommandFailure(model))
            {
                Match match = Regex.Match(ati, @"\b((?:EC|EG|BG|RG|RM|EM|EP|UC)[A-Z0-9-]{2,})\b", RegexOptions.IgnoreCase);
                if (match.Success) model = match.Groups[1].Value;
            }
            if (IsCommandFailure(firmware)) firmware = ati;
        }

        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(manufacturer, model, firmware);
        if (profile.IsQuectel)
        {
            ModemCapability detected = profile.Capabilities;
            var probes = new (string Command, ModemCapability Capability)[]
            {
                ("AT+QCFG=\"nwscanmode\"", ModemCapability.NetworkScanConfig),
                ("AT+QCFG=\"ims\"", ModemCapability.ImsConfig),
                ("AT+QSIMSTAT?", ModemCapability.SimStatusUrc),
                ("AT+QURCCFG?", ModemCapability.UrcPortRouting),
                ("AT+QCMGR=?", ModemCapability.QuectelStoredSms),
                ("AT+QAUDRD=?", ModemCapability.AudioRecord),
                ("AT+QHTTPCFG=?", ModemCapability.HttpData),
                ("AT+QTONEDET=?", ModemCapability.DtmfDetection)
            };
            foreach ((string command, ModemCapability capability) in probes)
            {
                ct.ThrowIfCancellationRequested();
                string response = await SendCommandAsync(portName, command, 3000, silent: true);
                if (!IsCommandFailure(response)) detected |= capability;
            }
            profile = profile with { Capabilities = detected };
        }

        _portVendors[portName] = profile.Manufacturer.ToUpperInvariant();
        _modemProfiles[portName] = profile;
        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[MODEM_PROFILE] manufacturer={profile.Manufacturer}; model={profile.Model}; firmware={profile.Firmware}; capabilities={profile.CapabilityText}"
        });
        return profile;
    }

    private static bool IsCommandFailure(string response) =>
        string.IsNullOrWhiteSpace(response)
        || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || response.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

    internal static bool HasReadableCcid(string response) =>
        !IsCommandFailure(response)
        && Regex.IsMatch(response, @"(?<!\d)89\d{16,20}(?!\d)");

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

    private async Task<SautoInitializationResult> RunSautoInitializationSequenceAsync(
        string portName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await SendEscapeWithoutResponseAsync(portName, ct);
        await Task.Delay(600, ct);

        string ati = await SendCommandAsync(portName, "ATI", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CPMS=\"ME\",\"SM\",\"MT\"", 5000, silent: true, ct: ct);
        await Task.Delay(300, ct);

        string cfun4 = await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct: ct);
        await Task.Delay(200, ct);
        await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true, ct: ct);
        await Task.Delay(800, ct);
        string cfunState = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true, ct: ct);

        bool radioLocked = !IsCommandFailure(cfun4)
            && Regex.IsMatch(cfunState, @"\+CFUN:\s*4\b", RegexOptions.IgnoreCase);
        if (!radioLocked)
        {
            await Task.Delay(300, ct);
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

        await Task.Delay(700, ct);
        string imei = await SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true, ct: ct);
        await Task.Delay(100, ct);
        await SendCommandAsync(portName, "AT+CNMI?", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, silent: true, ct: ct);
        await Task.Delay(300, ct);
        await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true, ct: ct);
        await Task.Delay(300, ct);
        await SendCommandAsync(portName, "AT+CMGF=1", 5000, silent: true, ct: ct);
        await Task.Delay(300, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CMGD=1,4", 5000, silent: true, ct: ct);
        await Task.Delay(500, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"ME\",\"ME\",\"ME\"", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CMGD=1,4", 5000, silent: true, ct: ct);
        await Task.Delay(500, ct);
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CPMS?", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true, ct: ct);
        await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 5000, silent: true, ct: ct);
        await Task.Delay(500, ct);
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
            Data = $"[MODEM_PROFILE] manufacturer={result.Profile.Manufacturer}; model={result.Profile.Model}; firmware={result.Profile.Firmware}; capabilities={result.Profile.CapabilityText}"
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

        // [SECURITY CRITICAL] Tắt radio NGAY khi modem online sau reboot.
        // EC20 boot mặc định CFUN=1 → expose IMEI gốc lên mạng nếu không tắt sớm.
        // Caller (CompletePortInitializationAsync) sẽ bật CFUN=1 SAU KHI ghi IMEI mới.
        await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
        await Task.Delay(500, ct);

        await SendCommandAsync(portName, "ATE0", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CMGF=1", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CLIP=1", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CMEE=2", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CREG=2", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CGREG=2", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CEREG=2", 5000, silent: true);
        await SendCommandAsync(portName, "AT+CRC=1", 5000, silent: true);
        
        QuectelModemProfile profile = await DetectModemProfileAsync(portName, ct);
        if (profile.IsQuectel)
        {
            await SendCommandAsync(portName, "AT+CMGF=0", 5000, silent: true);
            if (profile.Supports(ModemCapability.UrcPortRouting))
                await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true);
            // Không ghi đè nwscanmode/nwscanseq khi reconnect; nhánh dev giữ nguyên
            // cấu hình mạng của thiết bị và nhận/gọi ổn định hơn.
            if (gsm.Services.SettingsService.Current?.EnableVolte == true
                && profile.Supports(ModemCapability.ImsConfig))
                await SendCommandAsync(portName, "AT+QCFG=\"ims\",1", 5000, silent: true);
            if (profile.Supports(ModemCapability.VoiceCall))
            {
                await ConfigureVoiceAudioAsync(portName);
            }
            // Giữ nguyên QSIMDET hiện có của bo mạch; không ép polarity chung cho mọi khay.
            if (profile.Supports(ModemCapability.SimStatusUrc))
                await SendCommandAsync(portName, "AT+QSIMSTAT=1", 5000, silent: true);
            if (profile.Supports(ModemCapability.DtmfDetection))
                await SendCommandAsync(portName, "AT+QTONEDET=1", 5000, silent: true);
        } 
        string cnmi = await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 5000, silent: true);
        if (cnmi.Contains("ERROR")) 
        {
            cnmi = await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true);
            if (cnmi.Contains("ERROR"))
            {
                await SendCommandAsync(portName, "AT+CNMI=2,2,0,0,0", 5000, silent: true);
            }
        }
        
        // Chỉ cấu hình offline. Caller duy nhất chịu trách nhiệm xác minh CCID/IMEI,
        // bật CFUN=1 và khởi động network polling sau khi xác minh thành công.
        return true;
    }

    public void StartGlobalSimMonitor(string portName)
    {
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
                    _simRemovalEvidenceCounts.TryRemove(portName, out _);
                    _lastSimState[portName] = false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }

                if (isSimPresent && !lastState)
                {
                    _simRemovalEvidenceCounts.TryRemove(portName, out _);
                    // Guard: Nếu InitializeModemAsync đang chạy (trong 20s đầu) hoặc đang handle SIM khác → bỏ qua
                    if (_simInitInProgress.ContainsKey(portName)) continue;

                    _lastSimState[portName] = true;
                    _ = HandleSimInsertedSafelyAsync(portName);
                }
                else if (isSimPresent)
                {
                    _simRemovalEvidenceCounts.TryRemove(portName, out _);
                }
                else if (isSimRemoved && lastState)
                {
                    // Require three consecutive, identity-confirmed removal cycles before
                    // clearing CCID/phone/balance and entering the CFUN=4 hot-plug loop.
                    int evidence = _simRemovalEvidenceCounts.AddOrUpdate(portName, 1, (_, old) => old + 1);
                    if (evidence < 3) continue;
                    _simRemovalEvidenceCounts.TryRemove(portName, out _);
                    _lastSimState[portName] = false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (Quét nền)!" });
                    _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    StartHotplugWaitLoop(portName);
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
                    if (!result.RadioLocked) continue;

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
                        continue;

                    string ccidResponse = await SendCommandAsync(portName, "AT+ICCID", 5000, silent: true, ct: token);
                    if (!HasReadableCcid(ccidResponse)) continue;

                    _lastSimState[portName] = true;
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

    private void StartHotplugWaitLoopLegacy(string portName)
    {
        if (_keepAliveCts.TryRemove(portName, out var oldKeepAlive))
        {
            try { oldKeepAlive.Cancel(); oldKeepAlive.Dispose(); } catch { }
        }

        CancellationToken token;
        CancellationTokenSource loopCts;
        lock (_pollingCts)
        {
            if (_pollingCts.TryGetValue(portName, out var oldCts))
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch {}
            }
            loopCts = new CancellationTokenSource();
            _pollingCts[portName] = loopCts;
            token = loopCts.Token;
        }

        bool IsCurrentLoop() => !token.IsCancellationRequested
            && _pollingCts.TryGetValue(portName, out var current)
            && ReferenceEquals(current, loopCts);

        _ = Task.Run(async () =>
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs 
            { 
                PortName = portName, 
                Data = "[WAITING_FOR_SIM] Đang chờ cắm SIM (Hot-plug) – giữ CFUN=4" 
            });

            // Ép tắt sóng ngay
            await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);

            int cfunCheckCounter = 0;
            int failedActiveProbeCycles = 0;
            bool contactErrorReported = false;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(2000, token); } catch { break; }
                if (!IsCurrentLoop()) break;
                if (!_serialPorts.ContainsKey(portName)) break;

                // Kiểm tra CFUN ở mọi vòng 2 giây. Bộ đếm riêng chỉ dùng cho probe CCID.
                cfunCheckCounter++;
                bool runCcidProbe = cfunCheckCounter >= 5;
                if (runCcidProbe)
                    cfunCheckCounter = 0;

                string cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
                if (!IsRadioDisabledResponse(cfunStatus))
                {
                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                    cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
                    if (!IsRadioDisabledResponse(cfunStatus))
                    {
                        await SendCommandAsync(portName, "AT+CFUN=0", 8000, silent: true);
                        cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
                        if (!IsRadioDisabledResponse(cfunStatus))
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = "[STATUS_NO_RESPONSE] Không xác nhận được RF đã tắt; dừng probe SIM trên cổng này."
                            });
                            continue;
                        }
                    }
                }

                // Chỉ dùng trạng thái vật lý để phát hiện SIM. Việc đọc CCID/IMEI được gom
                // vào HandleSimInsertedAsync nhằm tránh hai luồng cùng xử lý một lần cắm.
                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                bool hasSim = Regex.IsMatch(cpin, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                if (cpin.Contains("SIM PIN") || cpin.Contains("SIM PUK"))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }
                if (!hasSim)
                {
                    string qsim = GetModemProfile(portName)?.Supports(ModemCapability.SimStatusUrc) == true
                        ? await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true)
                        : string.Empty;
                    if (!IsCurrentLoop()) break;
                    hasSim = Regex.IsMatch(qsim, @"\+QSIMSTAT:\s*1\s*,\s*1");

                    // Firmware EC20 có thể giấu trạng thái SIM khi CFUN=4. Mỗi 10 giây
                    // thử đọc CCID rồi khởi tạo lại SIM stack bằng CFUN=0 -> 4 nếu cần.
                    // Tuyệt đối không dùng CFUN=1/CFUN=1,1 trước khi IMEI đã được ghi.
                    if (!hasSim && runCcidProbe)
                    {
                        string safeCcid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true);
                        hasSim = HasReadableCcid(safeCcid);

                        if (!hasSim)
                        {
                            if (!IsCurrentLoop()) break;
                            if (await RestartSimStackOfflineAsync(portName, token))
                            {
                                string offlineCpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                                hasSim = Regex.IsMatch(offlineCpin, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase);
                                if (!hasSim)
                                {
                                    safeCcid = await ReadCcidWithFallbackAsync(portName, 4000, silent: true);
                                    hasSim = HasReadableCcid(safeCcid);
                                }
                            }
                        }

                        if (!hasSim)
                        {
                            failedActiveProbeCycles++;
                        }
                        else
                        {
                            // SIM phát hiện thành công — reset bộ đếm
                            failedActiveProbeCycles = 0;
                            contactErrorReported = false;
                        }
                    }
                }

                if (hasSim)
                {
                    if (!IsCurrentLoop()) break;
                    // [SECURITY FIX] Tắt radio NGAY KHI phát hiện SIM — trước delay 3s.
                    // Nếu không, modem attach mạng với IMEI gốc trong suốt 3 giây chờ.
                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                    // Cho SIM stack của EC20 ổn định sau hot-plug
                    try { await Task.Delay(1500, token); } catch { break; }
                    if (!IsCurrentLoop()) break;
                    _lastSimState[portName] = true;
                    await HandleSimInsertedAsync(portName);
                    break;
                }

                if (!contactErrorReported && failedActiveProbeCycles >= 6)
                {
                    contactErrorReported = true;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = "[SIM_CONTACT_ERROR] Không đọc được SIM khi RF tắt. Đã fail-closed tại CFUN=4; kiểm tra USIM_PRESENCE/QSIMDET, polarity và tiếp điểm khe SIM."
                    });
                }
            }
        });
    }

    public async Task HandleSimInsertedAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return;

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

        // Recover messages received while the tool was closed/configuring. Do not bulk-delete at
        // startup: each stored index must pass through CMGR and is deleted only after successful
        // decoding. Multipart parts are retained in the assembler before their SIM slots are freed.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                if (!token.IsCancellationRequested
                    && _serialPorts.ContainsKey(portName)
                    && !IsCallInProgress(portName))
                    await SweepUnreadSmsAsync(portName);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SWEEP] Lỗi quét SMS tồn khi Active: {ex.Message}" });
            }
        }, token);

        // Recovery sweep is independent from network/operator detection. +CMTI can be lost
        // while a long AT command is running or while the USB serial driver reconnects.
        // CMGL=ALL also recovers multipart segments already marked REC READ by CMGR before
        // a restart. Successfully delivered messages are deleted, so subsequent sweeps do
        // not emit duplicates.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _serialPorts.ContainsKey(portName))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
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
                    await Task.Delay(500, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                if (IsCallInProgress(portName)) continue;

                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true, ct: token);
                if (cpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                    || cpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }

                await Task.Delay(100, token);
                string csqStr = await SendCommandAsync(portName, "AT+CSQ", 5000, silent: true, ct: token);
                if (csqStr.Contains("+CSQ:", StringComparison.OrdinalIgnoreCase))
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = csqStr.Trim() });

                cycles++;
                if (cycles % 5 != 0) continue;

                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true, ct: token);
                var match = Regex.Match(copsStr, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)""(?:,\s*(\d+))?");
                if (copsStr.Contains("+COPS:") && match.Success)
                {
                    string act = match.Groups[2].Success ? match.Groups[2].Value : "?";
                    string netType = MapCopsAccessTechnology(act);
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_TYPE] {netType}" });
                    
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    operatorReported = true;
                    continue;
                }

                if (operatorReported) continue;

                // Chỉ quan sát COPS. Không phát COPS=0/COPS=2 và không ép 2G/3G/4G;
                // EC20 tự đăng ký sau CFUN=1,1 giống luồng đã capture từ SAuto.
                if (cycles == 150)
                {
                    waitingNoticeCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_WAITING] Chưa có COPS sau ~2.5 phút (lần {waitingNoticeCount}); chỉ tiếp tục theo dõi, không phát lệnh đăng ký mạng."
                    });
                }
                // Sau ~5 phút vẫn chỉ báo trạng thái; không thay đổi lựa chọn nhà mạng.
                else if (cycles >= 300)
                {
                    cycles = 0;
                    waitingNoticeCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = $"[NETWORK_WAITING] Vẫn chưa có COPS sau ~5 phút (lần {waitingNoticeCount}); chỉ tiếp tục theo dõi, không COPS=2/COPS=0."
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
                var copsMatch = Regex.Match(cops, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)""(?:,\s*(\d+))?");
                if (copsMatch.Success)
                {
                    string act = copsMatch.Groups[2].Success ? copsMatch.Groups[2].Value : "?";
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

            sp.DiscardInBuffer();
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

            sp.DiscardInBuffer();
            sp.Write($"AT+QFUPL=\"{remoteFile}\",{fileSize}\r");

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
            while ((DateTime.Now - start).TotalSeconds < 5)
            {
                if (sp.BytesToRead > 0)
                {
                    finalResp += (char)sp.ReadChar();
                    if (finalResp.Contains("OK")) break;
                }
                else
                {
                    await Task.Delay(10);
                }
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
        }
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
            var regMatches = Regex.Matches(currentData, @"\+(C(?:G|E)?REG):\s*([0-9])(?:[^\r\n]*)");
            if (regMatches.Count > 0)
            {
                foreach (Match match in regMatches)
                {
                    string regType = match.Groups[1].Value;
                    string stat = match.Groups[2].Value;
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
                    bool isRequestedResponse = pendingRegistrationCommand.Equals(
                        $"AT+{regType}?", StringComparison.OrdinalIgnoreCase);
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
            if (currentData.Contains("+CLIP:"))
            {
                var clipMatch = Regex.Match(currentData, @"\+CLIP:\s*""([^""]+)""");
                if (clipMatch.Success)
                {
                    string callerNumber = clipMatch.Groups[1].Value;
                    _activeCalls[portName] = true;
                    CallIncoming?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = callerNumber });
                    
                    // Cắt bỏ khỏi buffer
                    buffer.Replace(clipMatch.Value, "");
                    buffer.Replace("RING", ""); 
                    currentData = buffer.ToString();
                }
            }

            if (currentData.Contains("NO CARRIER"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "NO CARRIER");
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "NO CARRIER" });
                buffer.Replace("NO CARRIER", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("BUSY"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "BUSY");
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "BUSY" });
                buffer.Replace("BUSY", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("NO ANSWER"))
            {
                _activeCalls[portName] = false;
                SignalOutgoingCallEnded(portName, "NO ANSWER");
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
                
                _lastSimState.TryGetValue(portName, out bool lastState);
                if (!lastState)
                {
                    _lastSimState[portName] = true;
                    // Khởi động luồng đọc CCID và IMEI, sau đó báo UI
                    _ = HandleSimInsertedSafelyAsync(portName);
                }
            }

            bool stackDisabledByTool = _simStackDisabledByTool.TryGetValue(portName, out bool stackDisabled) && stackDisabled;
            bool hasUnsolicitedCpinRemoval = !stackDisabledByTool && !isCpinQueryResponse &&
                (currentData.Contains("+CPIN: NOT READY") || currentData.Contains("+CPIN: NOT INSERTED"));
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
                        // QSIMSTAT polarity phụ thuộc bo mạch và CPIN có thể NOT READY
                        // tạm thời khi CS/IMS đổi trạng thái. URC chỉ là bằng chứng đầu;
                        // global monitor sẽ xác minh thêm QSIMSTAT + CCID qua nhiều vòng.
                        _simRemovalEvidenceCounts.AddOrUpdate(portName, 1, (_, old) => Math.Max(old, 1));
                        LogMessage?.Invoke(this, new GsmDataEventArgs
                        {
                            PortName = portName,
                            Data = "[SIM_REMOVAL_PENDING] Modem báo mất SIM; đang xác minh lại trước khi đổi trạng thái."
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
                        // Đợi USSD từ tổng đài. Nếu chỉ mới có OK/phản hồi trung gian mà chưa có +CUSD: và chưa có lỗi, thoát ra tiếp tục đợi
                        if (!currentData.Contains("+CUSD:") && 
                            !currentData.Contains("ERROR") && 
                            !currentData.Contains("+CME ERROR") && 
                            !currentData.Contains("+CMS ERROR"))
                        {
                            return; // Tiếp tục chờ phản hồi từ nhà mạng
                        }

                        // VNSKY có lỗi gửi "+CME ERROR: 100" trước "+CUSD:"
                        if (currentData.Contains("+CME ERROR: 100"))
                        {
                            buffer.Replace("+CME ERROR: 100", ""); 
                            currentData = buffer.ToString();
                        }
                        else
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
                var match = Regex.Match(currentData, @"(?:\r?\nOK\r?\n?|\r?\nERROR\r?\n?|\+CMS ERROR:[^\r\n]*\r?\n?|\+CME ERROR:[^\r\n]*\r?\n?)");
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
        foreach (var cts in _simMonitorCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
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
        _simMonitorCts.Clear();
        _lastSimState.Clear();
        _simRemovalEvidenceCounts.Clear();
        _rebootRecoveryInProgress.Clear();
        _simInitInProgress.Clear();
        _simInsertInProgress.Clear();
        _portLifetimeCts.Clear();
        _dataReceivedHandlers.Clear();
        _isDownloading.Clear();
        _incomingCalls.Clear();
        _incomingCallNotifications.Clear();
        _incomingAnswerOperations.Clear();
        foreach (var signal in _outgoingCallEndSignals.Values)
            signal.TrySetResult("Port disconnected");
        _outgoingCallEndSignals.Clear();
        foreach (Channel<string> queue in _smsReadQueues.Values) queue.Writer.TryComplete();
        _smsReadQueues.Clear();
        _queuedSmsIndices.Clear();
    }

    public void Disconnect(string portName)
    {
        _incomingCalls.TryRemove(portName, out _);
        _incomingCallNotifications.TryRemove(portName, out _);
        _incomingAnswerOperations.TryRemove(portName, out _);
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
            _simRemovalEvidenceCounts.TryRemove(portName, out _);
            _rebootRecoveryInProgress.TryRemove(portName, out _);
            _simInitInProgress.TryRemove(portName, out _);
            _simInsertInProgress.TryRemove(portName, out _);
        }

        _portBuffers.TryRemove(portName, out _);
        _portBufferLocks.TryRemove(portName, out _);

        // Dọn cancellation state kể cả khi kết nối bị lỗi giữa chừng trước lúc tạo semaphore.
        if (_pollingCts.TryRemove(portName, out var polling)) { try { polling.Cancel(); polling.Dispose(); } catch { } }
        if (_keepAliveCts.TryRemove(portName, out var keepAlive)) { try { keepAlive.Cancel(); keepAlive.Dispose(); } catch { } }
        if (_simMonitorCts.TryRemove(portName, out var simMonitor)) { try { simMonitor.Cancel(); simMonitor.Dispose(); } catch { } }
        _lastSimState.TryRemove(portName, out _);
        _simRemovalEvidenceCounts.TryRemove(portName, out _);
        _rebootRecoveryInProgress.TryRemove(portName, out _);
        _simInitInProgress.TryRemove(portName, out _);
        _simInsertInProgress.TryRemove(portName, out _);
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
            timeoutMs = Math.Max(timeoutMs, 30000);
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

        async Task SendInnerAsync(string cmd, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var innerTcs = new TaskCompletionSource<string>(cmd, TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, innerTcs)) return;
            try
            {
                sp.Write(cmd + "\r");
                await Task.WhenAny(innerTcs.Task, Task.Delay(2000, token));
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, innerTcs))
                    _commandTcs.TryRemove(portName, out _);
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> AT+CMGS=\"{phoneNumber}\"" });

            await SendInnerAsync("AT+CMGF=1", ct);
            
            if (isGsm)
            {
                await SendInnerAsync("AT+CSMP=17,167,0,0", ct);
                await SendInnerAsync("AT+CSCS=\"GSM\"", ct);
            }
            else
            {
                await SendInnerAsync("AT+CSMP=17,167,0,8", ct);
                await SendInnerAsync("AT+CSCS=\"UCS2\"", ct);
            }

            tcs = new TaskCompletionSource<string>("AT+CMGS", TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, tcs))
            {
                return "ERROR: Another command is already in progress";
            }

            sp.Write($"AT+CMGS=\"{phoneNumber}\"\r");

            var timeoutTask = Task.Delay(5000, ct);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetCanceled();
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
            _commandTcs.TryAdd(portName, tcs);

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

            timeoutTask = Task.Delay(timeoutMs, ct);
            completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetCanceled();
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
        
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Đang quét tin nhắn tồn đọng (Sweep)..." });
        // ALL is intentional: CMGR marks a multipart segment REC READ before the remaining
        // segments arrive. Scanning only REC UNREAD loses that segment after restart.
        string command = GetModemProfile(portName)?.IsQuectel == true ? "AT+CMGL=4" : "AT+CMGL=\"ALL\"";
        await SendCommandAsync(portName, command, 25000, silent: true);
    }

    private void SignalOutgoingCallEnded(string portName, string reason)
    {
        if (_outgoingCallEndSignals.TryGetValue(portName, out var signal))
            signal.TrySetResult(reason);
    }

    private async Task ConfigureVoiceAudioAsync(string portName)
    {
        string pcmVoice = await SendCommandAsync(portName, "AT+QPCMV=0,0", 5000, silent: true);
        string audioMode = await SendCommandAsync(portName, "AT+QAUDMOD=0", 5000, silent: true);
        static string Compact(string value) => Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");

        LogMessage?.Invoke(this, new GsmDataEventArgs
        {
            PortName = portName,
            Data = $"[VOICE_INIT] QPCMV={Compact(pcmVoice)}; QAUDMOD={Compact(audioMode)}"
        });
    }

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
        try
        {
            // Giữ đúng luồng đã chạy ổn định ở nhánh dev: không đổi chế độ mạng,
            // không preflight CREG/CEREG/QNWINFO và không chờ CLCC 45 giây.
            string? remoteWavName = null;
            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
                remoteWavName = await UploadWavAsync(portName, wavPath, ct);

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

                    if (state == "ACTIVE" && remoteWavName != null)
                    {
                        await PlayWavAsync(portName, remoteWavName, ct);
                        remoteWavName = null;
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


    async Task<string?> UploadWavAsync(string portName, string localPath, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(localPath);
            if (!fi.Exists || fi.Length == 0) return null;

            string remoteName = "play.wav";
            long size = fi.Length;

            await SendCommandAsync(portName, $"AT+QFDEL=\"{remoteName}\"", 2000, silent: true);

            int uploadTimeoutSec = Math.Max(30, (int)(size / 1024) + 20);
            string cmd = $"AT+QFUPL=\"{remoteName}\",{size},{uploadTimeoutSec}";

            var resp = await SendCommandAsync(portName, cmd, 10000);
            if (!resp.Contains("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!resp.Contains("OK", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"QFUPL không nhận CONNECT: {resp}" });
                    return null;
                }
            }

            byte[] data = await File.ReadAllBytesAsync(localPath, ct);
            bool written = await WriteRawAsync(portName, data, ct);
            if (!written)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Ghi binary WAV thất bại" });
                return null;
            }

            await Task.Delay(500, ct);
            var final = await SendCommandAsync(portName, "AT", 5000, silent: true); // flush

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Upload WAV OK → {remoteName} ({size} bytes)" });
            return remoteName;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"UploadWav lỗi: {ex.Message}" });
            return null;
        }
    }

    async Task<bool> WriteRawAsync(string portName, byte[] data, CancellationToken ct)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || sp == null || !sp.IsOpen)
            return false;

        try
        {
            await sp.BaseStream.WriteAsync(data, 0, data.Length, ct);
            await sp.BaseStream.FlushAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"WriteRaw lỗi: {ex.Message}" });
            return false;
        }
    }

    async Task PlayWavAsync(string portName, string remoteFileName, CancellationToken ct)
    {
        try
        {
            await SendCommandAsync(portName, "AT+CLVL=5", 2000, silent: true); // volume 0-5

            var playCmd = $"AT+QPSND=1,\"{remoteFileName}\"";
            var resp = await SendCommandAsync(portName, playCmd, 8000);

            if (resp.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                playCmd = $"AT+QAUDPLAY=\"{remoteFileName}\",0,1";
                resp = await SendCommandAsync(portName, playCmd, 8000);
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Play WAV: {resp}" });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"PlayWav lỗi: {ex.Message}" });
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
            foreach (Match m in endMatches)
            {
                _ = OnIncomingCallEnded(portName);
                buffer.Replace(m.Value, "");
                updated = true;
            }
        }

        if (updated)
        {
            currentData = buffer.ToString();
        }
    }

    void OnIncomingRing(string portName, string caller)
    {
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

        var cfg = gsm.Services.SettingsService.Current;
        if (cfg?.AutoAnswerIncoming == true && session.AnsweredAt == null)
        {
            _ = AnswerAndRecordAsync(portName);
        }
        else
        {
            IncomingCallRinging?.Invoke(this, session);
        }
    }

    public async Task AnswerAndRecordAsync(string portName)
    {
        if (!_incomingAnswerOperations.TryAdd(portName, 0))
            return;

        if (!_incomingCalls.TryGetValue(portName, out var session))
        {
            session = new gsm.Models.IncomingCallSession { Port = portName, Caller = "Unknown", RingAt = DateTime.Now };
            _incomingCalls[portName] = session;
        }

        try
        {
            // 1. Nhấc máy
            var ata = await SendCommandAsync(portName, "ATA", 8000);
            if (ata.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"ATA fail: {ata}" });
                return;
            }

            session.AnsweredAt = DateTime.Now;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đã nhấc máy từ {session.Caller}" });

            var cfg = gsm.Services.SettingsService.Current;
            if (cfg?.RecordIncoming == true
                && GetModemProfile(portName)?.Supports(ModemCapability.AudioRecord) == true)
            {
                // 2. Bắt đầu ghi âm (Quectel)
                string remoteName = $"in_{DateTime.Now:HHmmss}.wav";
                session.Recording = true;

                // Xóa file cũ
                await SendCommandAsync(portName, $"AT+QFDEL=\"{remoteName}\"", 2000, silent: true);

                var rec = await SendCommandAsync(portName, $"AT+QAUDRD=1,\"{remoteName}\",1", 5000);
                if (rec.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    rec = await SendCommandAsync(portName, $"AT+QAUDRD=1,\"{remoteName}\"", 5000);
                }

                session.LocalWavPath = remoteName; // tạm giữ tên remote
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đang ghi âm → {remoteName} | {rec}" });
            }

            IncomingCallAnswered?.Invoke(this, session);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"AnswerAndRecord lỗi: {ex.Message}" });
        }
        finally
        {
            _incomingAnswerOperations.TryRemove(portName, out _);
        }
    }

    async Task OnIncomingCallEnded(string portName)
    {
        _incomingCallNotifications.TryRemove(portName, out _);
        _incomingAnswerOperations.TryRemove(portName, out _);
        if (!_incomingCalls.TryRemove(portName, out var session))
            return;

        session.EndedAt = DateTime.Now;

        try
        {
            // 1. Dập (phòng hờ)
            await SendCommandAsync(portName, "ATH", 3000, silent: true);

            var cfg = gsm.Services.SettingsService.Current;

            // 2. Dừng ghi âm
            if (session.Recording)
            {
                await SendCommandAsync(portName, "AT+QAUDRD=0", 5000);
                session.Recording = false;
            }

            string remoteName = session.LocalWavPath ?? "";
            if (string.IsNullOrEmpty(remoteName) || cfg?.RecordIncoming != true)
            {
                IncomingCallEnded?.Invoke(this, session);
                return;
            }

            // 3. Tải file về PC
            string localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ToolGSM_Recordings");
            Directory.CreateDirectory(localDir);

            string localPath = Path.Combine(localDir,
                $"{portName}_{session.Caller.Replace("+", "")}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            bool ok = await DownloadBinaryFileFromModemAsync(portName, remoteName, localPath);
            if (ok)
            {
                session.LocalWavPath = localPath;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đã tải ghi âm → {localPath}" });

                // 4. Xóa file trên modem
                await SendCommandAsync(portName, $"AT+QFDEL=\"{remoteName}\"", 3000, silent: true);

                // 5. STT
                if (cfg?.SttIncoming == true)
                {
                    await ProcessRecordingSttAsync(session);
                }
                else
                {
                    IncomingCallEnded?.Invoke(this, session);
                }
            }
            else
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Tải ghi âm thất bại" });
                IncomingCallEnded?.Invoke(this, session);
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"OnIncomingCallEnded lỗi: {ex.Message}" });
            IncomingCallEnded?.Invoke(this, session);
        }
    }

    async Task<bool> DownloadBinaryFileFromModemAsync(string portName, string remoteName, string localPath)
    {
        try
        {
            // Lấy kích thước file để download
            var sizeResp = await SendCommandAsync(portName, $"AT+QFLST=\"{remoteName}\"", 5000);
            long expectedSize = 0;
            var match = Regex.Match(sizeResp, @"\+QFLST:\s*""[^""]+"",(\d+)");
            if (match.Success)
            {
                expectedSize = long.Parse(match.Groups[1].Value);
            }

            _isDownloading[portName] = true;
            try
            {
                if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return false;

                // Send QFDWL command rawly
                string cmd = $"AT+QFDWL=\"{remoteName}\"\r";
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmd);
                await sp.BaseStream.WriteAsync(cmdBytes, 0, cmdBytes.Length);

                // Read until CONNECT
                long startTicks = DateTime.UtcNow.Ticks;
                string header = "";
                while (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks).TotalSeconds < 10)
                {
                    if (sp.BytesToRead > 0)
                    {
                        byte[] buf = new byte[sp.BytesToRead];
                        int r = await sp.BaseStream.ReadAsync(buf, 0, buf.Length);
                        header += Encoding.ASCII.GetString(buf, 0, r);
                        if (header.Contains("CONNECT")) break;
                        if (header.Contains("ERROR")) return false;
                    }
                    await Task.Delay(50);
                }

                if (!header.Contains("CONNECT")) return false;

                using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write);
                long totalRead = 0;
                startTicks = DateTime.UtcNow.Ticks;
                
                while (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks).TotalSeconds < 60)
                {
                    if (sp.BytesToRead > 0)
                    {
                        byte[] buf = new byte[sp.BytesToRead];
                        int r = await sp.BaseStream.ReadAsync(buf, 0, buf.Length);
                        if (r > 0)
                        {
                            await fs.WriteAsync(buf, 0, r);
                            totalRead += r;
                            startTicks = DateTime.UtcNow.Ticks; // reset timeout
                        }
                    }
                    else
                    {
                        await Task.Delay(50);
                    }
                    
                    // Stop condition
                    if (expectedSize > 0 && totalRead >= expectedSize) break;
                    // Otherwise rely on timeout
                }
                
                return File.Exists(localPath) && new FileInfo(localPath).Length > 100;
            }
            finally
            {
                _isDownloading[portName] = false;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"DownloadFile lỗi: {ex.Message}" });
            return false;
        }
    }

    async Task ProcessRecordingSttAsync(gsm.Models.IncomingCallSession session)
    {
        if (string.IsNullOrEmpty(session.LocalWavPath) || !File.Exists(session.LocalWavPath))
        {
            IncomingCallEnded?.Invoke(this, session);
            return;
        }

        try
        {
            var cfg = gsm.Services.SettingsService.Current;
            string text = "";

            if (cfg?.SttEngine == "whisper" && !string.IsNullOrWhiteSpace(cfg.WhisperApiUrl))
            {
                text = await RecognizeWhisperHttp(session.LocalWavPath, cfg.WhisperApiUrl);
            }
            else
            {
                text = await Task.Run(() => RecognizeWindows(session.LocalWavPath));
            }

            session.Transcript = text ?? "";
            session.Otp = ExtractOtp(session.Transcript);

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = session.Port, Data = $"STT: {session.Transcript} | OTP={session.Otp ?? "—"}" });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = session.Port, Data = $"STT lỗi: {ex.Message}" });
        }
        finally
        {
            IncomingCallEnded?.Invoke(this, session);
        }
    }

    string RecognizeWindows(string wavPath)
    {
        try
        {
            using var recognizer = new System.Speech.Recognition.SpeechRecognitionEngine(new System.Globalization.CultureInfo("vi-VN"));
            recognizer.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
            recognizer.SetInputToWaveFile(wavPath);
            var result = recognizer.Recognize();
            return result?.Text ?? "";
        }
        catch
        {
            try
            {
                using var en = new System.Speech.Recognition.SpeechRecognitionEngine(new System.Globalization.CultureInfo("en-US"));
                en.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
                en.SetInputToWaveFile(wavPath);
                var result = en.Recognize();
                return result?.Text ?? "";
            }
            catch { return ""; }
        }
    }

    async Task<string> RecognizeWhisperHttp(string wavPath, string apiUrl)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var form = new System.Net.Http.MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(wavPath);
            form.Add(new System.Net.Http.ByteArrayContent(bytes), "file", Path.GetFileName(wavPath));
            form.Add(new System.Net.Http.StringContent("vi"), "language");
            form.Add(new System.Net.Http.StringContent("json"), "response_format");

            var resp = await http.PostAsync(apiUrl, form);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
            return json;
        }
        catch
        {
            return "";
        }
    }
}



