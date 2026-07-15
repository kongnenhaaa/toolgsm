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

namespace gsm.Services;

public interface IGsmModemService
{
    Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000, bool silent = false);
    Task<string> SendRawAsync(string portName, string data, int timeoutMs = 5000, bool silent = false);
    Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 15000);
    Task SweepUnreadSmsAsync(string portName);
    Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile);
    Task<bool> UploadFileToModemAsync(string portName, string localFile, string remoteFile);
    void StartPollingNetwork(string portName);
    List<string> GetAvailablePorts();
    string ConnectAll(int baudRate = 115200);
    void Disconnect(string portName);
    void DisconnectAll();
    void StartHotplugWaitLoop(string portName);
    Task<bool> ReinitializeSettingsAsync(string portName, CancellationToken ct = default);
    Task ReloadSimAsync(string portName);
    Task<bool> CallWithAudioAsync(string portName, string phoneNumber, string? wavPath, int durationSeconds = 30, bool record = false, CancellationToken ct = default);
    bool IsCallInProgress(string portName);

    Func<string, string, bool>? RequiresSimAcceptanceCheck { get; set; }

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
    private readonly ConcurrentDictionary<string, SerialPort> _serialPorts = new();
    private readonly ConcurrentDictionary<string, gsm.Models.IncomingCallSession> _incomingCalls = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _portBuffers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _commandTcs = new();
    private readonly ConcurrentDictionary<string, int> _connectionErrors = new();
    private readonly ConcurrentDictionary<string, DateTime> _sleepingPorts = new();
    private readonly ConcurrentDictionary<string, string> _portVendors = new();
    private readonly ConcurrentDictionary<string, SerialDataReceivedEventHandler> _dataReceivedHandlers = new();
    private readonly ConcurrentDictionary<string, bool> _isDownloading = new();
    private readonly ConcurrentDictionary<string, bool> _activeCalls = new();
    private readonly ConcurrentDictionary<string, byte> _outgoingCallOperations = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _keepAliveCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simMonitorCts = new();
    private readonly ConcurrentDictionary<string, bool> _lastSimState = new();
    private readonly ConcurrentDictionary<string, bool> _simStackDisabledByTool = new();
    /// <summary>Guard chống race condition: đánh dấu port đang trong quá trình khởi tạo SIM đầu tiên.</summary>
    private readonly ConcurrentDictionary<string, bool> _simInitInProgress = new();
    private readonly ConcurrentDictionary<string, bool> _simInsertInProgress = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _portLifetimeCts = new();
    private readonly object _connectLock = new object();

    public Func<string, string, bool>? RequiresSimAcceptanceCheck { get; set; }

    public bool IsCallInProgress(string portName) =>
        _outgoingCallOperations.ContainsKey(portName)
        || (_activeCalls.TryGetValue(portName, out bool active) && active);

    // ===================== SMS DECODE + MULTIPART =====================
    static readonly Regex OtpRegex = new(
        // Nhóm 1: Có từ khoá rõ ràng trước số OTP (độ chính xác cao nhất)
        @"(?:otp|m\xE3\s*otp|ma\s*otp|m\xE3\s*x\xE1c\s*th\u1EF1c|ma\s*xac\s*thuc|" +
        @"m\xE3\s*x\xE1c\s*nh\u1EADn|ma\s*xac\s*nhan|" +
        @"verification\s*code|auth(?:entication)?\s*code|m\xE3\s*pin|" +
        @"code\s*(?:is|:)|l\xE0\s*:?\s*|la\s*:?\s*|" +
        @"m\u1EADt\s*kh\u1EA9u|mat\s*khau|" +
        @"token|pin)[^\d]{0,12}(\d{4,8})\b" +
        // Nhóm 2: Fallback – số đứng độc lập 4-8 chữ số
        @"|(?<!\d)(\d{4,8})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractOtp(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        
        // Tiền xử lý: xóa các chuỗi dạng *số* (số điện thoại ẩn)
        string text = Regex.Replace(content.Trim(), @"\*+\d+", "");
        
        var m = OtpRegex.Match(text);
        if (!m.Success) return null;
        
        if (m.Groups[1].Success) return m.Groups[1].Value;
        
        if (m.Groups[2].Success)
        {
            var num = m.Groups[2].Value;
            // Loại bỏ năm (2024-2030), hotline (1800, 1900), SĐT dài >= 9 chữ số
            if (num.Length >= 9) return null;                                        // SĐT
            if (num.Length == 4 && (num.StartsWith("19") || num.StartsWith("20"))) return null; // Năm / 1900
            if (num.StartsWith("1800") || num.StartsWith("1900")) return null;       // Hotline
            return num;
        }
        return null;
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
    private readonly ConcurrentDictionary<string, byte> _queuedSmsIndices = new();

    private async Task<string> ReadStoredSmsAsync(string port, string msgIndex)
    {
        // Quectel EC20/EC2x exposes uid, segment and total through QCMGR in text mode.
        // Fall back to standard CMGR for older firmware and non-Quectel modems.
        string response = await SendCommandAsync(port, $"AT+QCMGR={msgIndex}", 15000, silent: true);
        if (IsCompleteStoredSmsResponse(response, "+QCMGR:")) return response;
        return await SendCommandAsync(port, $"AT+CMGR={msgIndex}", 25000, silent: true);
    }

    private static bool IsCompleteStoredSmsResponse(string response, string? requiredHeader = null)
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
            if (!_deliveredStoredSms.TryAdd(deliveryKey, now)) return null;
            if (!string.IsNullOrWhiteSpace(msgIndex)) indicesToDelete.Add(msgIndex);
            return decoded.Content;
        }

        SmsAssemblyResult result = _exactMultipartAssembler.Add(port, sender, decoded.Concatenation, decoded.Content, msgIndex);
        if (result.Status == SmsAssemblyStatus.Completed)
        {
            indicesToDelete.AddRange(result.MessageIndices);
            return result.Content;
        }
        if (result.Status is SmsAssemblyStatus.Invalid or SmsAssemblyStatus.Conflict)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART] UDH không hợp lệ hoặc xung đột từ {sender}; giữ SMS trên SIM để quét lại." });
        return null;
    }

    private void QueueStoredSmsRead(string port, string msgIndex)
    {
        string queueKey = $"{port}\u001f{msgIndex}";
        if (!_queuedSmsIndices.TryAdd(queueKey, 0)) return;

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
            finally { _queuedSmsIndices.TryRemove($"{port}\u001f{msgIndex}", out _); }
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
        string? fullContent = TryAssembleMultipartExact(port, sender, decoded, msgIndex, smsContent, out var indicesToDelete);
        if (fullContent == null)
        {
            if (decoded.Concatenation != null)
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART] sender={sender} ref={decoded.Concatenation.Reference} seq={decoded.Concatenation.Sequence}/{decoded.Concatenation.Total} index={msgIndex} chars={decoded.Content.Length}; đang chờ đủ phần." });
            return;
        }

        if (decoded.Concatenation != null)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_COMPLETE] sender={sender} ref={decoded.Concatenation.Reference} total={decoded.Concatenation.Total} chars={fullContent.Length}" });
        else if (indicesToDelete.Count > 1)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[MULTIPART_FALLBACK_COMPLETE] sender={sender} total={indicesToDelete.Count} chars={fullContent.Length}" });

        foreach (string index in indicesToDelete)
        {
            string deleteResponse = await SendCommandAsync(port, $"AT+CMGD={index},0", 5000, silent: true);
            if (deleteResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = port, Data = $"[SMS_QUEUE] Không xóa được index {index}; bộ chống trùng sẽ ngăn phát lại trong phiên này." });
        }

        SmsReceived?.Invoke(this, new GsmDataEventArgs
        {
            PortName = port,
            Data = fullContent,
            MsgIndex = msgIndex,
            Sender = sender,
            Otp = ExtractOtp(fullContent) ?? string.Empty
        });
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
        @"\+Q?CMGR:\s*""[^""]*"",\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    string ParseSenderFromCmgr(string raw)
    {
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
        var usbPorts = new List<string>();
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
                                                usbPorts.Add(portName);
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
        var filtered = new List<string>();
        foreach (var p in usbPorts)
        {
            if (allSystemPorts.Contains(p) && !bluetoothPorts.Contains(p) && !filtered.Contains(p))
            {
                filtered.Add(p);
            }
        }

        // Fallback nếu danh sách lọc trống
        if (filtered.Count == 0)
        {
            foreach (var p in allSystemPorts)
            {
                if (!bluetoothPorts.Contains(p))
                {
                    filtered.Add(p);
                }
            }
        }

        return filtered;
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
        
        if (ccid.Contains("ERROR") || string.IsNullOrWhiteSpace(ccid))
        {
            ccid = await SendCommandAsync(portName, "AT+CCID", timeoutMs, silent);
        }

        if (ccid.Contains("ERROR") || string.IsNullOrWhiteSpace(ccid))
        {
            string crsm = await SendCommandAsync(portName, "AT+CRSM=176,12258,0,0,10", timeoutMs, silent);
            if (!crsm.Contains("ERROR") && crsm.Contains("+CRSM:"))
            {
                ccid = crsm; // Lấy luôn chuỗi raw để logic parse phía trên tự xử lý
            }
        }

        return ccid;
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

    private async Task InitializeModemCoreAsync(string portName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // [SECURITY] Gửi lệnh ngắt sóng NGAY LẬP TỨC ngay khi mở cổng COM, không chờ đợi PING AT.
        // Ngăn chặn tối đa việc modem kịp đăng ký vào mạng bằng IMEI phần cứng khi vừa khởi động.
        await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
        ct.ThrowIfCancellationRequested();
        
        // Kiểm tra kết nối cơ bản
        string ping = "ERROR";
        for (int i = 0; i < 5; i++)
        {
            ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
            if (!ping.Contains("Timeout") && !ping.Contains("ERROR")) break;
            await Task.Delay(500, ct);
        }
        
        if (ping.Contains("Timeout") || ping.Contains("ERROR"))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Bỏ qua cổng vì không phản hồi lệnh AT cơ bản: {ping}" });
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_NO_RESPONSE]" });
            return;
        }

        // Ngắt sóng ngay, chặn đăng ký mạng bằng IMEI cũ
        bool cfunOffSuccess = false;
        for (int i = 0; i < 5; i++)
        {
            // Kiểm tra trạng thái hiện tại trước - nếu đã CFUN=4 hoặc CFUN=0 thì OK luôn
            string cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true);
            if (Regex.IsMatch(cfunStatus, @"\+CFUN:\s*[04]"))
            {
                cfunOffSuccess = true;
                break;
            }

            string cfunResp = await SendCommandAsync(portName, "AT+CFUN=4", 15000, silent: true);
            // FIX: Điều kiện đúng: thành công khi KHÔNG có ERROR (hoặc modem đã ở CFUN=4 rồi)
            if (!cfunResp.Contains("ERROR"))
            {
                cfunOffSuccess = true;
                break;
            }
            // Nếu +CME ERROR: 303 (operation not allowed) → modem có thể đã ở CFUN=4 sẵn, kiểm tra lại
            if (cfunResp.Contains("+CME ERROR"))
            {
                string reCheck = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
                if (Regex.IsMatch(reCheck, @"\+CFUN:\s*[04]"))
                {
                    cfunOffSuccess = true;
                    break;
                }
            }
            await Task.Delay(1000, ct);
        }
        
        if (!cfunOffSuccess)
        {
            // Fallback an toàn: CFUN=0 vẫn tắt RF. Nếu cả hai đều thất bại thì
            // dừng khởi tạo, không tiếp tục trên một modem có thể đang online.
            string minimumMode = await SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true);
            cfunOffSuccess = !minimumMode.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
            if (!cfunOffSuccess)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_NO_RESPONSE] Không thể tắt radio an toàn (CFUN=4/0 đều thất bại)" });
                return;
            }
        }
        await Task.Delay(500, ct);

        await SendCommandAsync(portName, "ATE0", 30000); // Turn off echo
        await SendCommandAsync(portName, "AT+CMGF=1", 30000); // Set SMS to text mode
        await SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 30000); // Đọc được tiếng Việt
        await SendCommandAsync(portName, "AT+CLIP=1", 30000); // Hiển thị thông tin người gọi
        
        // Cấu hình nâng cao
        await SendCommandAsync(portName, "AT+CMEE=2", 10000, silent: true); 
        await SendCommandAsync(portName, "AT+CPMS=\"SM\",\"SM\",\"SM\"", 10000, silent: true);
        await SendCommandAsync(portName, "AT+CREG=2", 10000, silent: true);
        await SendCommandAsync(portName, "AT+CGREG=2", 10000, silent: true);
        await SendCommandAsync(portName, "AT+CEREG=2", 10000, silent: true);
        await SendCommandAsync(portName, "AT+CRC=1", 10000, silent: true);
        
        // Lấy hãng sản xuất (Vendor)
        string cgmi = await SendCommandAsync(portName, "AT+CGMI", 10000, silent: true);
        string vendor = cgmi.ToUpper();
        _portVendors[portName] = vendor;

        if (vendor.Contains("QUECTEL"))
        {
            await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 10000, silent: true); // Định tuyến URC đúng cổng UART1 để nhận +CUSD và +CMTI
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 10000, silent: true); // 0 = Auto (2G/3G/4G)
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanseq\",030201,1", 10000, silent: true); // Ưu tiên 4G -> 3G -> 2G
            var settings = gsm.Services.SettingsService.Current;
            int imsVal = (settings != null && settings.EnableVolte) ? 1 : 0;
            await SendCommandAsync(portName, $"AT+QCFG=\"ims\",{imsVal}", 10000, silent: true); // Cấu hình VoLTE theo Settings
            await SendCommandAsync(portName, "AT+QSIMDET=1,0", 10000, silent: true); // Bật phát hiện chân SIM vật lý
            await SendCommandAsync(portName, "AT+QSIMSTAT=1", 10000, silent: true); // Bật báo cáo trạng thái SIM URC
            await SendCommandAsync(portName, "AT&W", 10000, silent: true); // Lưu cấu hình vào bộ nhớ profile modem
            await SendCommandAsync(portName, "AT+QTONEDET=1", 30000); // Bật bộ phát hiện âm tần DTMF
        }
        
        string imei = await SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
        if (!imei.Contains("ERROR") && !string.IsNullOrWhiteSpace(imei))
        {
            string cleanImei = imei.Replace("OK", "").Trim();
            if (!string.IsNullOrWhiteSpace(cleanImei))
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {cleanImei}" });
        }
        
        // Ưu tiên đọc SIM khi RF vẫn tắt. Chỉ dùng CFUN=1 ngắn hạn như fallback
        // cho firmware EC20 không cho đọc CCID ở CFUN=0/4.
        // Giữ CFUN=4: EC20 vẫn tắt RF nhưng SIM stack còn hoạt động. CFUN=0 trên một số
        // firmware tắt cả SIM stack và làm SIM đã cắm sẵn bị nhận nhầm thành "SIM failure".
        await SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true);
        await Task.Delay(500, ct);
        string ccid = "ERROR";
        string initialCpin = await SendCommandAsync(portName, "AT+CPIN?", 3000, silent: true);
        bool simLocked = initialCpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                      || initialCpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
        bool definitelyNoSim = initialCpin.Contains("NOT INSERTED", StringComparison.OrdinalIgnoreCase)
                            || initialCpin.Contains("NOT READY", StringComparison.OrdinalIgnoreCase)
                            || initialCpin.Contains("CME ERROR: 10", StringComparison.OrdinalIgnoreCase);
        if (simLocked)
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {initialCpin.Trim()}" });

        if (!definitelyNoSim && !simLocked)
        {
            ccid = await ReadCcidWithFallbackAsync(portName, 5000, false);
            if (ccid.Contains("ERROR") || string.IsNullOrWhiteSpace(ccid))
            {
                string qsim = await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true);
                bool physicallyPresent = initialCpin.Contains("READY", StringComparison.OrdinalIgnoreCase)
                                      || Regex.IsMatch(qsim, @"\+QSIMSTAT:\s*1\s*,\s*1");
                // Một số EC20 trả SIM failure/QSIMSTAT=0 khi CFUN=4 dù SIM đã cắm sẵn.
                // Với lỗi chung (không phải NOT INSERTED), bật stack ngắn hạn để xác minh thật.
                if (physicallyPresent || !definitelyNoSim)
                {
                    await SendCommandAsync(portName, "AT+CFUN=1", 8000, silent: true);
                    await Task.Delay(1800, ct);
                    string enabledCpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                    simLocked = enabledCpin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
                             || enabledCpin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase);
                    ccid = await ReadCcidWithFallbackAsync(portName, 5000, false);
                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                }
            }
        }

        if (!ccid.Contains("ERROR"))
        {
            _lastSimState[portName] = true;
            
            // Giữ radio tắt; ViewModel chỉ bật lại sau khi xác minh CCID/IMEI cuối cùng.
            await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
            await Task.Delay(500, ct);
            
            // Cấu hình đẩy SMS: 2,1 để lưu vào SIM và gửi +CMTI (phù hợp với Regex lấy msgIndex)
            string cnmi = await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 10000, silent: true); 
            if (cnmi.Contains("ERROR")) 
            {
                cnmi = await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 10000, silent: true);
                if (cnmi.Contains("ERROR"))
                {
                    await SendCommandAsync(portName, "AT+CNMI=2,2,0,0,0", 10000, silent: true);
                }
            } 
            
            string cnum = await SendCommandAsync(portName, "AT+CNUM", 10000, silent: true);

            // PARSE_IMEI đã được emit ở trên (trước CFUN=1) - không emit lại ở đây
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
            if (!cnum.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CNUM] {cnum.Replace("OK", "").Trim()}" });
        }
        else
        {
            _lastSimState[portName] = false;
            if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
            if (!simLocked)
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Không đọc được SIM" });
            StartHotplugWaitLoop(portName);
        }
    }

    public async Task ReloadSimAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return;
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[INFO] Đang khởi động lại phần cứng để nhận SIM..." });
        
        // Gửi lệnh khởi động lại mềm (Reset module)
        await SendCommandAsync(portName, "AT+CFUN=1,1", 10000);
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

        await Task.Delay(1000, ct);
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
        
        string vendor = _portVendors.TryGetValue(portName, out var v) ? v : "";
        if (vendor.Contains("QUECTEL"))
        {
            await SendCommandAsync(portName, "AT+QURCCFG=\"urcport\",\"uart1\"", 5000, silent: true);
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 5000, silent: true);
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanseq\",030201,1", 5000, silent: true);
            var settings = gsm.Services.SettingsService.Current;
            int imsVal = (settings != null && settings.EnableVolte) ? 1 : 0;
            await SendCommandAsync(portName, $"AT+QCFG=\"ims\",{imsVal}", 5000, silent: true); // Cấu hình VoLTE theo Settings
            await SendCommandAsync(portName, "AT+QSIMDET=1,0", 5000, silent: true); // Bật phát hiện SIM vật lý
            await SendCommandAsync(portName, "AT+QSIMSTAT=1", 5000, silent: true); // Bật báo cáo trạng thái SIM URC
            await SendCommandAsync(portName, "AT&W", 5000, silent: true);
            await SendCommandAsync(portName, "AT+QTONEDET=1", 5000, silent: true); // Bật bộ phát hiện âm tần DTMF
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

                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                
                // Nếu timeout (modem đang bận gọi điện) thì bỏ qua vòng lặp này
                if (string.IsNullOrWhiteSpace(cpin)) continue;

                bool isSimPresent = cpin.Contains("READY");
                bool isSimLocked = cpin.Contains("SIM PIN") || cpin.Contains("SIM PUK");
                bool stackDisabledByTool = _simStackDisabledByTool.TryGetValue(portName, out bool stackDisabled) && stackDisabled;
                // EC20C reports CPIN NOT READY / CME 10 while CFUN=0/4 even when the card is
                // physically still inserted. Never turn that tool-induced radio transition into
                // a physical-removal event; the unsolicited QSIMSTAT/CPIN handler will detect a
                // real removal once the SIM stack is enabled again.
                bool isSimRemoved = !stackDisabledByTool &&
                                    (cpin.Contains("NOT INSERTED") || cpin.Contains("NOT READY") ||
                                     cpin.Contains("ERROR: 10") || cpin.Contains("ERROR: 13") || cpin.Contains("ERROR: 14"));

                // Quectel sometimes returns generic ERROR when SIM is removed if CMEE=2 drops
                if (!isSimPresent && !isSimRemoved && cpin.Contains("ERROR"))
                {
                    string qsimstat = await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true);
                    // QSIMSTAT=0 ở CFUN=4 không chứng minh SIM đã bị rút trên EC20C.
                    // Chỉ cập nhật PRESENT khi cảm biến báo chắc chắn; removal thật do URC
                    // hoặc CPIN NOT INSERTED đảm nhiệm.
                    if (Regex.IsMatch(qsimstat, @"\+QSIMSTAT:\s*1\s*,\s*1"))
                        isSimPresent = true;
                }

                _lastSimState.TryGetValue(portName, out bool lastState);

                if (isSimLocked)
                {
                    _lastSimState[portName] = false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }

                if (isSimPresent && !lastState)
                {
                    // Guard: Nếu InitializeModemAsync đang chạy (trong 20s đầu) hoặc đang handle SIM khác → bỏ qua
                    if (_simInitInProgress.ContainsKey(portName)) continue;

                    _lastSimState[portName] = true;
                    _ = HandleSimInsertedSafelyAsync(portName);
                }
                else if (isSimRemoved && lastState)
                {
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
            int hardRecoveryAttempts = 0;
            bool contactErrorReported = false;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(2000, token); } catch { break; }
                if (!IsCurrentLoop()) break;
                if (!_serialPorts.ContainsKey(portName)) break;

                // Kiểm tra CFUN mỗi 10 giây
                cfunCheckCounter++;
                if (cfunCheckCounter >= 5)
                {
                    cfunCheckCounter = 0;
                    string cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
                    if (!cfunStatus.Contains("+CFUN: 4") && !cfunStatus.Contains("+CFUN: 0"))
                    {
                        await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    }
                }

                // Chỉ dùng trạng thái vật lý để phát hiện SIM. Việc đọc CCID/IMEI được gom
                // vào HandleSimInsertedAsync nhằm tránh hai luồng cùng xử lý một lần cắm.
                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                bool hasSim = cpin.Contains("READY");
                if (cpin.Contains("SIM PIN") || cpin.Contains("SIM PUK"))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpin.Trim()}" });
                    continue;
                }
                if (!hasSim)
                {
                    string qsim = await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true);
                    if (!IsCurrentLoop()) break;
                    hasSim = Regex.IsMatch(qsim, @"\+QSIMSTAT:\s*1\s*,\s*1");

                    // Firmware EC20 có thể giấu trạng thái SIM khi CFUN=4. Mỗi 10 giây bật
                    // SIM stack ngắn hạn để phát hiện chắc chắn SIM đã cắm sẵn/hot-plug.
                    if (!hasSim && cfunCheckCounter == 0)
                    {
                        await SendCommandAsync(portName, "AT+CFUN=1", 8000, silent: true);
                        if (!IsCurrentLoop()) break;
                        for (int attempt = 0; attempt < 3 && !hasSim; attempt++)
                        {
                            await Task.Delay(attempt == 0 ? 1800 : 1200, token);
                            string enabledCpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                            hasSim = enabledCpin.Contains("READY", StringComparison.OrdinalIgnoreCase);
                            if (!hasSim)
                            {
                                string enabledQsim = await SendCommandAsync(portName, "AT+QSIMSTAT?", 3000, silent: true);
                                hasSim = Regex.IsMatch(enabledQsim, @"\+QSIMSTAT:\s*1\s*,\s*1");
                            }
                        }
                        if (!hasSim)
                        {
                            failedActiveProbeCycles++;
                            await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);

                            // Nếu SIM_DET/stack của riêng module bị kẹt sau khi thay SIM nhanh,
                            // reboot riêng EC20 tối đa hai lần. Không reboot vô hạn khi khe thực sự
                            // không tiếp xúc hoặc không có SIM.
                            if (failedActiveProbeCycles >= 6 && hardRecoveryAttempts < 2)
                            {
                                if (!IsCurrentLoop()) break;
                                failedActiveProbeCycles = 0;
                                hardRecoveryAttempts++;
                                LogMessage?.Invoke(this, new GsmDataEventArgs
                                {
                                    PortName = portName,
                                    Data = $"[SIM_RECOVERY] COM vẫn phản hồi nhưng chưa đọc được SIM; reboot riêng module lần {hardRecoveryAttempts}/2..."
                                });

                                await SendCommandAsync(portName, "AT+QSIMDET=1,0", 5000, silent: true);
                                await SendCommandAsync(portName, "AT+CFUN=1,1", 10000, silent: true);
                                try { await Task.Delay(12000, token); } catch { break; }
                                if (!IsCurrentLoop()) break;

                                for (int bootProbe = 0; bootProbe < 5 && !hasSim; bootProbe++)
                                {
                                    string ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
                                    if (!ping.Contains("OK", StringComparison.OrdinalIgnoreCase))
                                    {
                                        await Task.Delay(1000, token);
                                        continue;
                                    }

                                    string rebootCpin = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
                                    hasSim = rebootCpin.Contains("READY", StringComparison.OrdinalIgnoreCase)
                                          && !rebootCpin.Contains("NOT READY", StringComparison.OrdinalIgnoreCase);
                                    if (!hasSim) await Task.Delay(1200, token);
                                }

                                if (!hasSim)
                                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                            }
                        }
                        else
                        {
                            failedActiveProbeCycles = 0;
                        }
                    }
                }

                if (hasSim)
                {
                    if (!IsCurrentLoop()) break;
                    _lastSimState[portName] = true;
                    await HandleSimInsertedAsync(portName);
                    break;
                }

                if (!contactErrorReported && hardRecoveryAttempts >= 2 && failedActiveProbeCycles >= 3)
                {
                    // Chỉ phát một lần cho mỗi chu kỳ chờ, tránh spam log/UI.
                    contactErrorReported = true;
                    LogMessage?.Invoke(this, new GsmDataEventArgs
                    {
                        PortName = portName,
                        Data = "[SIM_CONTACT_ERROR] COM vẫn sống nhưng EC20 báo không có SIM sau hai lần recovery. Kiểm tra chiều SIM/tiếp điểm/khe. Tool vẫn tiếp tục tự dò."
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
            // Đợi thẻ SIM khởi động bên trong modem và URC QSIMSTAT đến tay handler.
            await Task.Delay(2000);

            string cpinState = await SendCommandAsync(portName, "AT+CPIN?", 4000, silent: true);
            if (cpinState.Contains("SIM PIN") || cpinState.Contains("SIM PUK"))
            {
                _lastSimState[portName] = false;
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[STATUS_SIM_LOCKED] {cpinState.Trim()}" });
                return;
            }
        
            // Đảm bảo tắt sóng trước khi làm việc với CCID/IMEI.
            await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);

        // Đọc IMEI hiện tại
            string currentImei = await SendCommandAsync(portName, "AT+CGSN", 5000, silent: true);
            string cleanImei = "";
            if (!string.IsNullOrWhiteSpace(currentImei) && !currentImei.Contains("ERROR"))
            {
                cleanImei = currentImei.Replace("OK", "").Trim();
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {cleanImei}" });
            }

        // Đọc CCID
            string pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
            bool hasSim = !pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp);

            // Một số EC20 không cho đọc CCID khi CFUN=4. Chỉ bật SIM stack trong thời gian
            // ngắn để đọc danh tính, rồi tắt radio lại trước khi phát sự kiện xử lý IMEI.
            if (!hasSim)
            {
                await SendCommandAsync(portName, "AT+CFUN=1", 8000, silent: true);
                for (int attempt = 0; attempt < 3 && !hasSim; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 1800 : 1200);
                    pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
                    hasSim = !pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp);
                }
                await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
            }

            if (hasSim)
            {
            string ccid = pollResp.Replace("OK", "").Trim();
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid}" });

            bool isNewSim = true;
            if (RequiresSimAcceptanceCheck != null)
            {
                isNewSim = RequiresSimAcceptanceCheck(ccid, cleanImei);
            }

            var settings = gsm.Services.SettingsService.Current;
            if (settings != null && settings.EnableNewSimIntakeMode && !settings.AutoAccept && isNewSim)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_WAITING_ACCEPT] SIM mới đã cắm – CHỜ USER CHẤP NHẬN" });
            }
            else 
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận diện SIM thay nóng qua URC, đang cấu hình..." });
            }
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
        // startup: each stored index must pass through QCMGR and is deleted only after successful
        // decoding, or after every segment of a concatenated SMS has been assembled.
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

        // Tạo luồng ngầm chờ thiết bị đăng ký mạng thành công để lấy nhà mạng (Tránh việc AT+COPS? chạy quá sớm lúc chưa có sóng)
        // Lặp vô hạn cho đến khi có mạng hoặc cổng bị rút
        _ = Task.Run(async () =>
        {
            int attempts = 0;
            int recoveryCount = 0;
            while (true)
            {
                try
                {
                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                if (IsCallInProgress(portName)) continue;
                
                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true);
                var match = Regex.Match(copsStr, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)""(?:,\s*(\d+))?");
                if (copsStr.Contains("+COPS:") && match.Success)
                {
                    string act = match.Groups[2].Success ? match.Groups[2].Value : "?";
                    string netType = act switch
                    {
                        "0" => "2G",
                        "2" => "3G",
                        "7" => "LTE/4G",
                        _ => $"Unknown({act})"
                    };
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_TYPE] {netType}" });
                    
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    StartKeepAliveLoop(portName);
                    break;
                }

                attempts++;

                // Khôi phục sóng nếu kẹt quá lâu (15 lần = 30 giây)
                if (attempts > 15)
                {
                    attempts = 0;

                    // Modem/SIM vẫn phản hồi nhưng nhà mạng chưa đăng ký. Chỉ reset RF tối đa
                    // ba lần; sau đó chuyển sang dò thụ động để không làm cổng Active quay lại
                    // Connecting và không CFUN lặp vô hạn.
                    if (recoveryCount >= 3)
                    {
                        if (recoveryCount == 3)
                        {
                            recoveryCount++;
                            LogMessage?.Invoke(this, new GsmDataEventArgs
                            {
                                PortName = portName,
                                Data = "[NETWORK_FAILED] Modem/SIM vẫn phản hồi nhưng chưa đăng ký được nhà mạng; chuyển sang dò thụ động."
                            });
                        }
                        try { await Task.Delay(30000, token); } catch { break; }
                        continue;
                    }

                    recoveryCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_RECOVERY] Không tìm thấy sóng, đang thử khôi phục mạng lần {recoveryCount}..." });
                    
                    // Toggle chế độ máy bay để reset cọc sóng
                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                    try { await Task.Delay(1200, token); } catch { break; }
                    await SendCommandAsync(portName, "AT+CFUN=1", 12000, silent: true);
                    try { await Task.Delay(1500, token); } catch { break; }
                    
                    string vendor = _portVendors.TryGetValue(portName, out var v) ? v : "";
                    if (vendor.Contains("QUECTEL"))
                    {
                        // Đặt lại chuẩn ưu tiên sau khi AT+COPS=0 vì một số FW Qualcomm reset giá trị này
                        await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 8000, silent: true);
                        await SendCommandAsync(portName, "AT+QCFG=\"nwscanseq\",030201,1", 8000, silent: true);
                    }
                    
                    // Ép tự động quét lại trạm sóng mạng
                    await SendCommandAsync(portName, "AT+COPS=0", 10000, silent: true);
                }
            }
        }, token);
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
                
                string vendor = _portVendors.TryGetValue(portName, out var v) ? v : "";
                if (vendor.Contains("QUECTEL"))
                {
                    await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                    await SendCommandAsync(portName, "AT+CREG?", 5000, silent: true);
                    await SendCommandAsync(portName, "AT+QCSQ", 5000, silent: true);
                }
                else
                {
                    await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                    await SendCommandAsync(portName, "AT+CREG?", 5000, silent: true);
                    await SendCommandAsync(portName, "AT+CSQ", 5000, silent: true);
                }
                
                // Sweep bù (quét tin nhắn kẹt định kỳ)
                string cmgl = await SendCommandAsync(portName, "AT+CMGL=\"REC UNREAD\"", 25000, silent: true);
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
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[WARNING] Buffer overflow ({buffer.Length} chars) — có thể bị mất dữ liệu. Đang làm sạch..." });
                buffer.Clear();
                currentData = "";
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
                    buffer.Replace(match.Value, "");
                }
                currentData = buffer.ToString();
            }

            HandleIncomingCallUrcs(portName, ref currentData, buffer);

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
                    
                    // Lấy chi tiết thông tin cuộc gọi đang diễn ra
                    _ = SendCommandAsync(portName, "AT+CLCC", 5000, silent: true);
                    
                    // Đếm ngược 60s để tự động dập máy tránh ngâm port
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(60000);
                        if (_serialPorts.ContainsKey(portName) && _activeCalls.TryGetValue(portName, out bool isActive) && isActive)
                        {
                            await SendCommandAsync(portName, "ATH", 5000, silent: true);
                            _activeCalls[portName] = false;
                        }
                    });
                    
                    // Cắt bỏ khỏi buffer
                    buffer.Replace(clipMatch.Value, "");
                    buffer.Replace("RING", ""); 
                    currentData = buffer.ToString();
                }
            }

            if (currentData.Contains("NO CARRIER"))
            {
                _activeCalls[portName] = false;
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "NO CARRIER" });
                buffer.Replace("NO CARRIER", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("BUSY"))
            {
                _activeCalls[portName] = false;
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "BUSY" });
                buffer.Replace("BUSY", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("NO ANSWER"))
            {
                _activeCalls[portName] = false;
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
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra!" });
                    _lastSimState[portName] = false;
                    _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    StartHotplugWaitLoop(portName);
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
                    if (_commandTcs.TryGetValue(portName, out var t) && t.Task.AsyncState is string c && c.StartsWith("AT+CUSD"))
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
                    if (tcs.Task.AsyncState is string cmd && cmd.StartsWith("AT+CUSD"))
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
        _commandTcs.Clear();
        _connectionErrors.Clear();
        _sleepingPorts.Clear();
        _portVendors.Clear();
        _pollingCts.Clear();
        _keepAliveCts.Clear();
        _simMonitorCts.Clear();
        _lastSimState.Clear();
        _simInitInProgress.Clear();
        _simInsertInProgress.Clear();
        _portLifetimeCts.Clear();
        _dataReceivedHandlers.Clear();
        _isDownloading.Clear();
        foreach (Channel<string> queue in _smsReadQueues.Values) queue.Writer.TryComplete();
        _smsReadQueues.Clear();
        _queuedSmsIndices.Clear();
    }

    public void Disconnect(string portName)
    {
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
            _portBuffers.TryRemove(portName, out _);
            if (_commandTcs.TryRemove(portName, out var pendingCommand))
                pendingCommand.TrySetResult("ERROR: Port disconnected");
            _connectionErrors.TryRemove(portName, out _);
            _dataReceivedHandlers.TryRemove(portName, out _);
            _isDownloading.TryRemove(portName, out _);
            _sleepingPorts.TryRemove(portName, out _);
            _portVendors.TryRemove(portName, out _);

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
            _simInitInProgress.TryRemove(portName, out _);
            _simInsertInProgress.TryRemove(portName, out _);
        }

        // Dọn cancellation state kể cả khi kết nối bị lỗi giữa chừng trước lúc tạo semaphore.
        if (_pollingCts.TryRemove(portName, out var polling)) { try { polling.Cancel(); polling.Dispose(); } catch { } }
        if (_keepAliveCts.TryRemove(portName, out var keepAlive)) { try { keepAlive.Cancel(); keepAlive.Dispose(); } catch { } }
        if (_simMonitorCts.TryRemove(portName, out var simMonitor)) { try { simMonitor.Cancel(); simMonitor.Dispose(); } catch { } }
        _lastSimState.TryRemove(portName, out _);
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

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000, bool silent = false)
    {
        if (Regex.IsMatch(command, @"^AT\+CFUN\s*=\s*[04](?:\D|$)", RegexOptions.IgnoreCase))
            _simStackDisabledByTool[portName] = true;
        else if (Regex.IsMatch(command, @"^AT\+CFUN\s*=\s*1(?:\D|$)", RegexOptions.IgnoreCase))
            _simStackDisabledByTool[portName] = false;

        // Kéo dài thời gian chờ cho các lệnh đặc biệt
        if (command.StartsWith("AT+CUSD")) timeoutMs = 45000;
        else if (command.StartsWith("AT+CMGR")) timeoutMs = 25000;

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

        var tcs = new TaskCompletionSource<string>(command, TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandTcs.TryAdd(portName, tcs))
        {
            semaphore.Release();
            return "ERROR: Another command is already in progress";
        }

        try
        {
            // Xóa sạch bộ đệm trước khi gửi lệnh mới để tránh nhận rác/OK sót từ lệnh trước
            try
            {
                sp.DiscardInBuffer();
            }
            catch {}

            if (_portBuffers.TryGetValue(portName, out var buf))
            {
                lock (buf)
                {
                    buf.Clear();
                }
            }

            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {command}" });
            
            sp.Write(command + "\r\n");
            
            // Đứng chờ HandleDataReceived bơm dữ liệu vào TCS, hoặc bị quá giờ (Timeout)
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                tcs.TrySetCanceled();
                _commandTcs.TryRemove(portName, out _);
                return "ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)";
            }
            
            string finalResp = await tcs.Task;
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

    public async Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 30000)
    {
        // Kiểm tra xem message có ký tự nằm ngoài bảng mã GSM cơ bản hay không
        // (Sử dụng cách kiểm tra đơn giản: nếu có bất kỳ ký tự nào > 127 thì coi là Unicode)
        bool isGsm = (message ?? "").All(c => c <= 127);
        int maxLen = isGsm ? MaxGsmPartLength : MaxUcs2PartLength;
        int maxChunk = isGsm ? MaxGsmChunkBodyLength : MaxUcs2ChunkBodyLength;

        if (string.IsNullOrEmpty(message) || message.Length <= maxLen)
        {
            return await SendSmsPartAsync(portName, phoneNumber, message ?? "", isGsm, timeoutMs);
        }

        var chunks = SplitMessageIntoChunks(message, maxChunk);
        int total = chunks.Count;
        var results = new List<string>();

        for (int i = 0; i < total; i++)
        {
            string partBody = $"[{i + 1}/{total}] {chunks[i]}";
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[SMS_MULTIPART] Đang gửi đoạn {i + 1}/{total}..." });

            string resp = await SendSmsPartAsync(portName, phoneNumber, partBody, isGsm, timeoutMs);
            results.Add(resp);

            if (resp.Contains("ERROR"))
            {
                return $"ERROR: Gửi thất bại ở đoạn {i + 1}/{total} - {resp}";
            }

            // Chờ 1.5s giữa các đoạn để mạng có thể nhận đúng thứ tự
            if (i < total - 1) await Task.Delay(1500);
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

    private async Task<string> SendSmsPartAsync(string portName, string phoneNumber, string message, bool isGsm, int timeoutMs = 30000)
    {
        if (!EnsurePortOpen(portName, out var sp) || sp == null) return "ERROR: Port not open";
        if (!_semaphores.TryGetValue(portName, out var semaphore)) return "ERROR: Semaphore missing";

        bool lockAcquired = await semaphore.WaitAsync(timeoutMs);
        if (!lockAcquired) return "ERROR: Timeout waiting for lock";

        TaskCompletionSource<string>? tcs = null;

        async Task SendInnerAsync(string cmd)
        {
            var innerTcs = new TaskCompletionSource<string>(cmd, TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, innerTcs)) return;
            try
            {
                sp.Write(cmd + "\r");
                await Task.WhenAny(innerTcs.Task, Task.Delay(2000));
            }
            finally
            {
                if (_commandTcs.TryGetValue(portName, out var existing) && ReferenceEquals(existing, innerTcs))
                    _commandTcs.TryRemove(portName, out _);
            }
        }

        try
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> AT+CMGS=\"{phoneNumber}\"" });

            await SendInnerAsync("AT+CMGF=1");
            
            if (isGsm)
            {
                await SendInnerAsync("AT+CSMP=17,167,0,0");
                await SendInnerAsync("AT+CSCS=\"GSM\"");
            }
            else
            {
                await SendInnerAsync("AT+CSMP=17,167,0,8");
                await SendInnerAsync("AT+CSCS=\"UCS2\"");
            }

            tcs = new TaskCompletionSource<string>("AT+CMGS", TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandTcs.TryAdd(portName, tcs))
            {
                return "ERROR: Another command is already in progress";
            }

            sp.Write($"AT+CMGS=\"{phoneNumber}\"\r");

            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
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
            
            if (isGsm)
            {
                sp.Write(message + "\x1A");
            }
            else
            {
                string hexMessage = BitConverter.ToString(Encoding.BigEndianUnicode.GetBytes(message)).Replace("-", "");
                sp.Write(hexMessage + "\x1A");
            }

            timeoutTask = Task.Delay(timeoutMs);
            completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                tcs.TrySetCanceled();
                return "ERROR: Timeout sending SMS payload";
            }

            string finalResp = await tcs.Task;
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp.Trim()}" });
            return finalResp.Trim();
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
                await SendInnerAsync("AT+CSCS=\"UCS2\""); // Restore charset để nhận đúng tiếng Việt
            }

            semaphore.Release();
        }
    }

    public async Task SweepUnreadSmsAsync(string portName)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return;
        
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Đang quét tin nhắn tồn đọng (Sweep)..." });
        await SendCommandAsync(portName, "AT+CMGL=\"REC UNREAD\"", 25000, silent: true);
    }

    public async Task<bool> CallWithAudioAsync(
        string portName,
        string phoneNumber,
        string? wavPath,
        int durationSeconds = 30,
        bool record = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(phoneNumber))
            return false;

        if (!_outgoingCallOperations.TryAdd(portName, 0))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Cổng đang có một cuộc gọi khác." });
            return false;
        }

        try
        {
            // === CHẨN ĐOÁN TRƯỚC KHI GỌI (tối ưu cho Quectel EC20C - LTE/CSFB) ===

            // 1. Kiểm tra radio; nếu tắt thì fail-closed để state machine recovery xử lý
            string cfunCheck = await SendCommandAsync(portName, "AT+CFUN?", 3000, silent: true);
            bool radioOff = cfunCheck.Contains("+CFUN: 4") || cfunCheck.Contains("+CFUN:4") ||
                            cfunCheck.Contains("+CFUN: 0") || cfunCheck.Contains("+CFUN:0");
            if (radioOff)
            {
                // Không tự bật radio ở tầng gọi điện: chỉ state machine CCID/IMEI mới có
                // quyền đưa modem lên CFUN=1. Caller sẽ báo lỗi để người dùng recovery an toàn.
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Từ chối gọi vì radio đang tắt; cần recovery và xác minh lại SIM." });
                return false;
            }

            // 2. Đảm bảo network scan mode = AUTO để CSFB (LTE→3G/2G fallback) hoạt động
            //    AT+QCFG="nwscanmode",0 = AUTO (cho phép fallback xuống 2G/3G khi gọi)
            //    Không cần nếu đã AUTO, nhưng gọi lại không hại
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0", 3000, silent: true);

            // 3. Kiểm tra đăng ký mạng — EC20C dùng AT+CEREG (LTE EPS) và AT+CREG (CS domain)
            //    Cần cả hai: CEREG=1 = LTE data OK, CREG=1 = CS voice OK (cho CSFB)
            
            // Enable CREG unsolicited reporting rồi query
            await SendCommandAsync(portName, "AT+CREG=2", 2000, silent: true);
            string cregResp = await SendCommandAsync(portName, "AT+CREG?", 3000, silent: true);
            var cregMatch = System.Text.RegularExpressions.Regex.Match(cregResp, @"\+CREG:\s*\d+,(\d+)");
            int cregStat = cregMatch.Success && int.TryParse(cregMatch.Groups[1].Value, out int st1) ? st1 : -1;

            // Query LTE EPS registration (quan trọng với EC20C)
            string ceregResp = await SendCommandAsync(portName, "AT+CEREG?", 3000, silent: true);
            var ceregMatch = System.Text.RegularExpressions.Regex.Match(ceregResp, @"\+CEREG:\s*\d+,(\d+)");
            int ceregStat = ceregMatch.Success && int.TryParse(ceregMatch.Groups[1].Value, out int st2) ? st2 : -1;

            // Query loại mạng hiện tại (EC20C specific)
            string qnwInfo = await SendCommandAsync(portName, "AT+QNWINFO", 3000, silent: true);
            // AT+QNWINFO trả: +QNWINFO: "LTE","46001","LTE BAND 3",1850 hoặc "WCDMA"/"GSM"
            bool isOnLte = qnwInfo.Contains("LTE", StringComparison.OrdinalIgnoreCase);
            bool isOnWcdma = qnwInfo.Contains("WCDMA", StringComparison.OrdinalIgnoreCase) || qnwInfo.Contains("HSPA", StringComparison.OrdinalIgnoreCase);
            bool isOnGsm = qnwInfo.Contains("GSM", StringComparison.OrdinalIgnoreCase) || qnwInfo.Contains("EDGE", StringComparison.OrdinalIgnoreCase);
            string netType = isOnLte ? "LTE (sẽ CSFB xuống 3G/2G khi gọi)" : isOnWcdma ? "WCDMA/3G" : isOnGsm ? "GSM/2G" : "Không rõ";

            string cregLabel = cregStat switch { 1 => "CS đã đăng ký (Home)", 5 => "CS đã đăng ký (Roaming)", 0 => "CS chưa đăng ký", 2 => "CS đang tìm mạng", 3 => "CS bị từ chối", _ => $"CS={cregStat}" };
            string ceregLabel = ceregStat switch { 1 => "LTE OK (Home)", 5 => "LTE OK (Roaming)", 0 => "LTE chưa đăng ký", _ => $"LTE={ceregStat}" };
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Mạng: {netType} | {cregLabel} | {ceregLabel}" });

            // Nếu cả CS và LTE đều chưa đăng ký → không gọi được
            bool csOk  = cregStat  == 1 || cregStat  == 5;
            bool lteOk = ceregStat == 1 || ceregStat == 5;
            if (!csOk && !lteOk && cregStat != -1) // Chỉ chặn nếu có dữ liệu rõ ràng
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] ⚠️ Không thể gọi — SIM chưa đăng ký mạng ({cregLabel}, {ceregLabel})" });
                return false;
            }

            // Nếu đang trên LTE: cần CSFB → chờ thêm sau ATD để modem rớt xuống 3G/2G
            int csfbWaitMs = isOnLte ? 5000 : 0; // Chờ 5s cho CSFB nếu đang LTE

            // Bật CLIP để hiện số gọi đến (Caller ID)
            await SendCommandAsync(portName, "AT+CLIP=1", 2000, silent: true);

            // Upload WAV lên modem TRƯỚC khi dial (tránh delay sau khi bắt máy)
            string? remoteWavName = null;
            bool hasWav = !string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath);
            if (hasWav)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đang upload WAV ({Path.GetFileName(wavPath)}) lên modem..." });
                remoteWavName = await UploadWavAsync(portName, wavPath!, ct);
                if (remoteWavName == null)
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Upload WAV thất bại — cuộc gọi vẫn tiến hành (không có nhạc)." });
                else
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Upload WAV OK: {remoteWavName}" });
            }

            // Gửi ATDxxx; để quay số
            string cleanPhone = (phoneNumber.StartsWith("+") ? "+" : "") + new string(phoneNumber.Where(char.IsDigit).ToArray());
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Đang gửi lệnh quay số ATD{cleanPhone}..." });
            var dialResp = await SendCommandAsync(portName, $"ATD{cleanPhone};", 15000);
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Phản hồi ATD: {dialResp.Trim()}" });

            if (dialResp.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                dialResp.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase))
            {
                // Phân tích chi tiết lỗi ATD
                string errDetail = dialResp.Contains("+CME ERROR") ? "Lỗi modem phần cứng (CME)" :
                                   dialResp.Contains("NO CARRIER")  ? "Không có sóng/mạng" :
                                   dialResp.Contains("BUSY")        ? "Máy bận" : "Lỗi không xác định";
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Gọi thất bại ({errDetail}): {dialResp.Trim()}" });
                return false;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] ATD gửi thành công → Đang chờ kết nối tới {cleanPhone}..." });

            // Nếu đang LTE: chờ CSFB fallback xuống 3G/2G trước khi poll CLCC
            if (csfbWaitMs > 0)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Đang LTE → Chờ CSFB fallback xuống 3G/2G ({csfbWaitMs / 1000}s)..." });
                await Task.Delay(csfbWaitMs, ct);
            }

            // Polling AT+CLCC để xác nhận cuộc gọi thực sự đang rung chuông hoặc đã kết nối
            bool callConfirmed  = false; // Đã từng thấy CLCC có dữ liệu
            bool seenRinging    = false; // Đã từng thấy stat=2 (Dialing) hoặc stat=3 (Alerting = rung)
            bool answered       = false;
            var clccDeadline = DateTime.UtcNow.AddSeconds(45); // tối đa 45s chờ bắt máy
            while (DateTime.UtcNow < clccDeadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(800, ct);
                string clcc = await SendCommandAsync(portName, "AT+CLCC", 2000, silent: true);

                // Log raw để debug nếu chưa confirmed
                if (!callConfirmed)
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL][CLCC] {clcc.Trim()}" });

                if (clcc.Contains("+CLCC:"))
                {
                    callConfirmed = true;
                    var clccLines = clcc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Where(l => l.Contains("+CLCC:")).ToList();

                    // [FIX] Chỉ lấy entry MO (outgoing, dir=0) — KHÔNG fallback sang dir=1 (incoming)
                    // Nếu chỉ còn entry dir=1 mà dir=0 biến mất → cuộc gọi MO đã kết thúc
                    // Format: +CLCC: <idx>,<dir>,<stat>,<mode>,<mpty>[,<number>,<type>]
                    string? clccLine = clccLines
                        .FirstOrDefault(l =>
                        {
                            var p = l.Replace("+CLCC:", "").Trim().Split(',');
                            return p.Length > 1 && p[1].Trim() == "0"; // dir=0 = MO (outgoing)
                        });

                    if (clccLine == null)
                    {
                        // Có CLCC nhưng không có entry MO (dir=0) → cuộc gọi đi đã kết thúc
                        if (seenRinging)
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Cuộc gọi kết thúc — không bắt máy." });
                            await SendCommandAsync(portName, "ATH", 2000, silent: true);
                            return false;
                        }
                        continue; // Chưa thấy ringing, tiếp tục chờ
                    }

                    if (clccLine != null)
                    {
                        var parts = clccLine.Replace("+CLCC:", "").Trim().Split(',');
                        int dir = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int d) ? d : -1;
                        int callStatus = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int s) ? s : -1;

                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL][CLCC] dir={dir} stat={callStatus} raw={clccLine}" });

                        // stat: 0=Active, 2=Dialing (MO), 3=Alerting (đang rung ở đầu kia)
                        if (callStatus == 2 || callStatus == 3)
                        {
                            if (!seenRinging)
                            {
                                seenRinging = true;
                                string statusText = callStatus == 3 ? "Đang rung ở đầu kia..." : "Đang quay số...";
                                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] {statusText}" });
                            }
                            continue; // Tiếp tục poll
                        }
                        else if (callStatus == 0)
                        {
                            // stat=0 (Active) chỉ tin là nhấc máy THẬT nếu đã từng thấy ringing trước
                            if (seenRinging)
                            {
                                answered = true;
                                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Đối phương đã nhấc máy!" });
                                if (remoteWavName != null)
                                    await PlayWavAsync(portName, remoteWavName, ct);
                                break;
                            }
                            else
                            {
                                // Modem trả stat=0 ngay mà chưa qua ringing
                                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL][WARN] stat=0 ngay (chưa qua ringing, dir={dir}) — chờ thêm..." });
                                seenRinging = true;
                                continue;
                            }
                        }
                    }
                }
                else if (clcc.Contains("NO CARRIER") || clcc.Contains("BUSY"))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[CALL] Cuộc gọi kết thúc sớm: {clcc.Trim()}" });
                    return false;
                }
                else if (callConfirmed)
                {
                    // Đã từng thấy CLCC, giờ không còn → đầu kia dập máy
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Cuộc gọi đã kết thúc từ phía đối phương." });
                    return answered;
                }
            }

            if (!callConfirmed)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] ⚠️ AT+CLCC không trả về cuộc gọi nào — ATD có thể không ra mạng! (Kiểm tra SIM/anten)" });
                await SendCommandAsync(portName, "ATH", 3000, silent: true);
                return false;
            }

            if (!answered)
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[CALL] Không ai nhấc máy trong 45s — tiếp tục giữ theo duration..." });

            // Giữ cuộc gọi theo duration
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(durationSeconds), ct);
            }
            catch (TaskCanceledException) { }

            // Dập máy
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Hết thời gian {durationSeconds}s → Dập máy (ATH)" });
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
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"CallWithAudio lỗi: {ex.Message}" });
            try { await SendCommandAsync(portName, "ATH", 3000, silent: true); } catch { }
            return false;
        }
        finally
        {
            // EC20C cần một khoảng ngắn để quay lại chế độ mạng bình thường sau CSFB/ATH.
            // Giữ cờ call trong lúc xác nhận để CPIN/COPS/CSQ nền không đánh dấu nhầm mất SIM.
            bool responsive = false;
            bool simReady = false;
            try
            {
                await Task.Delay(1200);
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    string ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
                    if (!ping.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                        && !ping.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                    {
                        responsive = true;
                        string cpin = await SendCommandAsync(portName, "AT+CPIN?", 3000, silent: true);
                        if (cpin.Contains("READY", StringComparison.OrdinalIgnoreCase)
                            && !cpin.Contains("NOT READY", StringComparison.OrdinalIgnoreCase))
                        {
                            simReady = true;
                            break;
                        }
                    }
                    await Task.Delay(800);
                }
            }
            catch { }
            finally
            {
                _outgoingCallOperations.TryRemove(portName, out _);
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs
            {
                PortName = portName,
                Data = responsive && simReady
                    ? "[CALL_RECOVERED] Modem đã phản hồi lại sau cuộc gọi."
                    : "[CALL_RECOVERY_PENDING] Modem/SIM chưa ổn định ngay sau cuộc gọi; chờ vòng giám sát kế tiếp."
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
        if (endMatches.Count > 0)
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

        var cfg = gsm.Services.SettingsService.Current;
        if (cfg?.AutoAnswerIncoming == true)
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
            if (cfg?.RecordIncoming == true)
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
    }

    async Task OnIncomingCallEnded(string portName)
    {
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



