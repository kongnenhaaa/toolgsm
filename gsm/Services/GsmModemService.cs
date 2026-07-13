using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    Task ReinitializeSettingsAsync(string portName);
    Task<bool> ResetNetworkAsync(string portName);
    Task<bool> AcceptNewSimAndPaintImeiAsync(string portName, string targetImei);
    Task<bool> CallWithAudioAsync(string portName, string phoneNumber, string? wavPath, int durationSeconds = 30, bool record = false, CancellationToken ct = default);

    
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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _keepAliveCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _simMonitorCts = new();
    private readonly ConcurrentDictionary<string, bool> _lastSimState = new();
    private readonly object _connectLock = new object();

    // ===================== SMS DECODE + MULTIPART =====================
    static readonly Regex OtpRegex = new(
        @"(?:otp|mã\s*otp|ma\s*otp|mã\s*xác\s*thực|ma\s*xac\s*thuc|verification\s*code|auth(?:entication)?\s*code|mã\s*pin|code\s*is|la\s*:?\s*)[^\d]{0,12}(\d{4,8})\b" +
        @"|(?<!\d)(\d{4,8})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractOtp(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var m = OtpRegex.Match(content.Trim());
        if (!m.Success) return null;
        if (m.Groups[1].Success) return m.Groups[1].Value;
        if (m.Groups[2].Success)
        {
            var num = m.Groups[2].Value;
            if (num.Length == 4 && (num.StartsWith("19") || num.StartsWith("20"))) return null;
            return num;
        }
        return null;
    }

    public static string DecodeSmsBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        var lines = raw.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                       .Select(l => l.Trim())
                       .Where(l => !string.IsNullOrEmpty(l) &&
                                   !l.StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase) &&
                                   !l.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                                   !l.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                       .ToList();

        if (lines.Count == 0) return raw;
        var body = lines.OrderByDescending(l => l.Length).First();

        if (IsHexString(body) && body.Length >= 4 && body.Length % 2 == 0)
        {
            try { return DecodeUcs2Hex(body); } catch { }
        }
        return body;
    }

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

        bool looksLikePart = partContent.Length <= 160 &&
                             (partContent.EndsWith("...") ||
                              partContent.EndsWith("…") ||
                              (!partContent.EndsWith(".") && !partContent.EndsWith("!") && !partContent.EndsWith("?") && partContent.Length > 50));

        if (!_multipartBuffer.TryGetValue(key, out var pending))
        {
            if (!looksLikePart)
            {
                if (!string.IsNullOrEmpty(msgIndex)) indicesToDelete.Add(msgIndex);
                return partContent;
            }
            pending = new PendingMultipart { Port = port, Sender = sender };
            _multipartBuffer[key] = pending;
        }

        pending.Parts.Add(partContent);
        if (!string.IsNullOrEmpty(msgIndex)) pending.MsgIndices.Add(msgIndex);
        pending.LastAt = now;

        bool looksComplete = partContent.EndsWith(".") || partContent.EndsWith("!") ||
                             partContent.EndsWith("?") || partContent.Length < 40;

        if (looksComplete || pending.Parts.Count >= 5)
        {
            _multipartBuffer.TryRemove(key, out _);
            indicesToDelete.AddRange(pending.MsgIndices);
            return string.Join("", pending.Parts);
        }

        return null;
    }

    static readonly Regex CmgrHeaderRegex = new(
        @"\+CMGR:\s*""[^""]*"",\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    string ParseSenderFromCmgr(string raw)
    {
        var m = CmgrHeaderRegex.Match(raw);
        if (m.Success)
        {
            string val = m.Groups[1].Value.Trim();
            if (IsHexString(val))
            {
                if (Regex.IsMatch(val, @"^\d+$") && !Regex.IsMatch(val, @"^(00[2-7][0-9])+$")) return val;
                try { return DecodeUcs2Hex(val); } catch { }
            }
            return val;
        }
        return "Unknown";
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
        List<string> newlyOpenedPorts = new List<string>();
        List<string> failedPorts = new List<string>();

        lock (_connectLock)
        {
            // Xóa cache ngủ và đếm lỗi để quét mới hoàn toàn
            _sleepingPorts.Clear();
            _connectionErrors.Clear();

            var ports = GetAvailablePorts();
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = "SYSTEM", Data = $"[HỆ THỐNG] Quét cổng COM: Phát hiện {ports.Count} cổng trong Windows ({string.Join(", ", ports)})" });

            foreach (var p in ports)
            {
                if (!_serialPorts.ContainsKey(p))
                {
                    if (_sleepingPorts.TryGetValue(p, out var sleepUntil))
                    {
                        if (DateTime.Now < sleepUntil)
                            continue; // Đang trong thời gian ngủ, bỏ qua
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
                        
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Đã kết nối thành công {p} (Baud: {baudRate})" });
                        
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
            }
        }

        // CHỈ gửi lệnh khởi tạo SAU KHI đã mở kết nối xong toàn bộ các cổng COM.
        // Điều này đảm bảo quá trình đọc/ghi USB (AT commands) không xung đột với quá trình OS nhận diện cổng COM mới.
        if (newlyOpenedPorts.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var p in newlyOpenedPorts)
                {
                    _ = InitializeModemAsync(p);
                    await Task.Delay(10); // Giãn cách cực ngắn (10ms) để tải đồng loạt theo yêu cầu
                }
            });
        }

        string result = "";
        if (newlyOpenedPorts.Count > 0) result += $"Mới: {string.Join(", ", newlyOpenedPorts)}. ";
        if (failedPorts.Count > 0) result += $"Lỗi: {string.Join(", ", failedPorts)}.";
        return string.IsNullOrWhiteSpace(result) ? "Không có cổng mới cần kết nối" : result.Trim();
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

    private async Task InitializeModemAsync(string portName)
    {
        // [SECURITY] Gửi lệnh ngắt sóng NGAY LẬP TỨC ngay khi mở cổng COM, không chờ đợi PING AT.
        // Ngăn chặn tối đa việc modem kịp đăng ký vào mạng bằng IMEI phần cứng khi vừa khởi động.
        await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
        
        // Kiểm tra kết nối cơ bản
        string ping = "ERROR";
        for (int i = 0; i < 5; i++)
        {
            ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
            if (!ping.Contains("Timeout") && !ping.Contains("ERROR")) break;
            await Task.Delay(500);
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
            // Kiểm tra trạng thái hiện tại trước
            string cfunStatus = await SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true);
            if (Regex.IsMatch(cfunStatus, @"\+CFUN:\s*[04]"))
            {
                cfunOffSuccess = true;
                break;
            }

            string cfunResp = await SendCommandAsync(portName, "AT+CFUN=4", 15000);
            if (!cfunResp.Contains("ERROR") || cfunResp.Contains("+CME ERROR"))
            {
                cfunOffSuccess = true;
                break;
            }
            await Task.Delay(1000);
        }
        
        if (!cfunOffSuccess)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "ERROR: Không thể ngắt sóng (AT+CFUN=4 thất bại). Hủy khởi tạo để bảo vệ IMEI." });
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_NO_RESPONSE]" });
            return; // Dừng lập tức, không đọc CCID hay thực hiện gì thêm để đảm bảo an toàn 100%
        }
        await Task.Delay(1000);

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
            // Bật xuất âm thanh cuộc gọi ra cổng USB (UAC) cho Quectel EC20
            await SendCommandAsync(portName, "AT+QPCMV=0,0", 30000); 
            await SendCommandAsync(portName, "AT+QAUDMOD=0", 30000); 
        }
        
        string imei = await SendCommandAsync(portName, "AT+CGSN", 30000);
        
        // Thử đọc CCID nhiều lần (SIM có thể cần vài giây để khởi tạo)
        string ccid = "ERROR";
        for (int i = 0; i < 15; i++)
        {
            string resp = await ReadCcidWithFallbackAsync(portName, 5000, false);
            if (!resp.Contains("ERROR") && !string.IsNullOrWhiteSpace(resp))
            {
                ccid = resp;
                break;
            }
            await Task.Delay(1000);
        }

        if (!ccid.Contains("ERROR"))
        {
            // Xóa toàn bộ SMS cũ trong SIM để tránh bị đầy bộ nhớ khiến không nhận được CMTI mới
            await SendCommandAsync(portName, "AT+CMGD=1,4", 30000); 
            
            // Cấu hình đẩy SMS: 2,1 để lưu vào SIM và gửi +CMTI (phù hợp với Regex lấy msgIndex)
            string cnmi = await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 15000); 
            if (cnmi.Contains("ERROR")) 
            {
                cnmi = await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 15000);
                if (cnmi.Contains("ERROR"))
                {
                    await SendCommandAsync(portName, "AT+CNMI=2,2,0,0,0", 15000);
                }
            } 
            
            string cnum = await SendCommandAsync(portName, "AT+CNUM", 30000);

            // Gửi thông tin sang ViewModel qua event log với Prefix đặc biệt
            if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
            if (!cnum.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CNUM] {cnum.Replace("OK", "").Trim()}" });
        }
        else
        {
            if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
            
            // Lấy cài đặt
            var settings = gsm.Services.SettingsService.Current;
            if (settings != null && settings.EnableNewSimIntakeMode)
            {
                // Không tự động tráng IMEI. Đẩy vào luồng chờ chấp nhận
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_WAITING_ACCEPT] SIM mới – đang chờ user chấp nhận" });
                StartHotplugWaitLoop(portName);
                return;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_NO_RESPONSE]" });

            StartHotplugWaitLoop(portName);
        }
    }

    public async Task<bool> ResetNetworkAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return false;
        
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[PROXY] Đang ngắt sóng để lấy IP mới..." });
        
        // Gửi lệnh ngắt sóng
        string respOff = await SendCommandAsync(portName, "AT+CFUN=4", 10000);
        if (respOff.Contains("ERROR"))
        {
            // Thử ngắt sóng kiểu khác (với một số modem là CFUN=0)
            respOff = await SendCommandAsync(portName, "AT+CFUN=0", 10000);
        }
        
        await Task.Delay(3000); // Chờ 3 giây để mạng ngắt hoàn toàn
        
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[PROXY] Đang bật lại sóng, vui lòng đợi..." });
        // Gửi lệnh bật lại sóng
        string respOn = await SendCommandAsync(portName, "AT+CFUN=1", 10000);
        
        return !respOn.Contains("ERROR");
    }

    public async Task ReinitializeSettingsAsync(string portName)
    {
        // Chờ modem boot lên (AT trả về OK)
        bool ready = false;
        while (true)
        {
            if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
            string ping = await SendCommandAsync(portName, "AT", 3000, silent: true);
            if (!ping.Contains("Timeout") && !ping.Contains("ERROR"))
            {
                ready = true;
                break;
            }
            await Task.Delay(2000);
        }

        if (!ready) 
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "ERROR: Modem đã bị rút trong lúc khởi động lại." });
            return;
        }

        await Task.Delay(1000);
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
            await SendCommandAsync(portName, "AT+QPCMV=0,0", 5000, silent: true);
            await SendCommandAsync(portName, "AT+QAUDMOD=0", 5000, silent: true); 
        } 
        await SendCommandAsync(portName, "AT+CMGD=1,4", 10000, silent: true); 
        
        string cnmi = await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 5000, silent: true);
        if (cnmi.Contains("ERROR")) 
        {
            cnmi = await SendCommandAsync(portName, "AT+CNMI=1,1,0,0,0", 5000, silent: true);
            if (cnmi.Contains("ERROR"))
            {
                await SendCommandAsync(portName, "AT+CNMI=2,2,0,0,0", 5000, silent: true);
            }
        }
        
        // === FIX QUAN TRỌNG: Sau Accept SIM / reboot, bắt buộc bật full radio ===
        await SendCommandAsync(portName, "AT+CFUN=1", 12000, silent: true);
        await Task.Delay(2000);   // Cho modem thời gian gắn mạng

        // Re-apply scan mode sau khi CFUN=1 (vì COPS=0 hay CFUN có thể reset)
        if (vendor.Contains("QUECTEL"))
        {
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,1", 8000, silent: true);
            await SendCommandAsync(portName, "AT+QCFG=\"nwscanseq\",030201,1", 8000, silent: true);
        }
        
        // Để thiết bị tự động quét mạng theo mặc định của Baseband, không ngắt tiến trình attach tự nhiên
        StartPollingNetwork(portName);
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

                string cpin = await SendCommandAsync(portName, "AT+CPIN?", 5000, silent: true);
                bool isSimPresent = !cpin.Contains("ERROR") && (cpin.Contains("READY") || cpin.Contains("SIM PIN") || cpin.Contains("SIM PUK"));
                
                // Nếu không có CPIN, kiểm tra thêm bằng CCID để chắc chắn
                if (!isSimPresent && !cpin.Contains("ERROR"))
                {
                     string pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
                     isSimPresent = !pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp);
                }

                _lastSimState.TryGetValue(portName, out bool lastState);

                if (isSimPresent && !lastState)
                {
                    _lastSimState[portName] = true;
                    // Bổ trợ cho Option 1: Nếu URC bị trượt, vòng lặp này sẽ bắt lại
                    _ = Task.Run(() => HandleSimInsertedAsync(portName));
                }
                else if (!isSimPresent && lastState)
                {
                    _lastSimState[portName] = false;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra (Quét nền)!" });
                    _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                    StartHotplugWaitLoop(portName);
                }
                
                if (!_lastSimState.ContainsKey(portName))
                {
                    _lastSimState[portName] = isSimPresent; // Lần đầu gán giá trị khởi tạo
                }
            }
        });
    }

    public void StartHotplugWaitLoop(string portName)
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
            bool isWaitingForAcceptance = false;

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(2000, token); } catch { break; }
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

                string pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
                bool hasSim = !pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp);

                if (hasSim)
                {
                    if (!isWaitingForAcceptance)
                    {
                        // Có SIM rồi → ép tắt sóng lần nữa
                        await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);

                        // Đọc IMEI hiện tại (vẫn offline)
                        string currentImei = await SendCommandAsync(portName, "AT+CGSN", 5000, silent: true);
                        if (!string.IsNullOrWhiteSpace(currentImei) && !currentImei.Contains("ERROR"))
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs 
                            { 
                                PortName = portName, 
                                Data = $"[PARSE_IMEI] {currentImei.Replace("OK", "").Trim()}" 
                            });
                        }

                        LogMessage?.Invoke(this, new GsmDataEventArgs 
                        { 
                            PortName = portName, 
                            Data = $"[PARSE_CCID] {pollResp.Replace("OK", "").Trim()}" 
                        });

                        // Quan trọng: Báo cho UI biết đây là SIM mới đang chờ chấp nhận
                        var settings = gsm.Services.SettingsService.Current;
                        if (settings != null && settings.EnableNewSimIntakeMode)
                        {
                            isWaitingForAcceptance = true;
                            LogMessage?.Invoke(this, new GsmDataEventArgs 
                            { 
                                PortName = portName, 
                                Data = "[STATUS_WAITING_ACCEPT] SIM mới đã cắm – CHỜ USER CHẤP NHẬN" 
                            });
                        }
                        else 
                        {
                            LogMessage?.Invoke(this, new GsmDataEventArgs 
                            { 
                                PortName = portName, 
                                Data = "[STATUS_HOTPLUG_SIM_DETECTED] Đã nhận diện SIM thay nóng, đang cấu hình..." 
                            });
                            break; // Thoát vòng lặp, nhường cho tiến trình xử lý auto
                        }
                    }
                }
                else
                {
                    if (isWaitingForAcceptance)
                    {
                        // Đang chờ chấp nhận nhưng SIM lại bị rút ra!
                        isWaitingForAcceptance = false;
                        LogMessage?.Invoke(this, new GsmDataEventArgs 
                        { 
                            PortName = portName, 
                            Data = "[WAITING_FOR_SIM] SIM đã bị rút ra!" 
                        });
                    }
                }
            }
        });
    }

    public async Task HandleSimInsertedAsync(string portName)
    {
        if (!_serialPorts.ContainsKey(portName)) return;

        // Đợi 2 giây để thẻ SIM khởi động bên trong modem
        await Task.Delay(2000);
        
        // Đảm bảo tắt sóng trước khi làm việc với CCID/IMEI
        await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);

        // Đọc IMEI hiện tại
        string currentImei = await SendCommandAsync(portName, "AT+CGSN", 5000, silent: true);
        if (!string.IsNullOrWhiteSpace(currentImei) && !currentImei.Contains("ERROR"))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {currentImei.Replace("OK", "").Trim()}" });
        }

        // Đọc CCID
        string pollResp = await ReadCcidWithFallbackAsync(portName, 5000, silent: true);
        bool hasSim = !pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp);

        if (hasSim)
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {pollResp.Replace("OK", "").Trim()}" });

            var settings = gsm.Services.SettingsService.Current;
            if (settings != null && settings.EnableNewSimIntakeMode)
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
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Không đọc được SIM (Lỗi phần cứng hoặc SIM hỏng)" });
        }
    }

    public async Task<bool> AcceptNewSimAndPaintImeiAsync(string portName, string targetImei)
    {
        if (!_serialPorts.ContainsKey(portName)) return false;

        LogMessage?.Invoke(this, new GsmDataEventArgs 
        { 
            PortName = portName, 
            Data = $"[ACCEPT_NEW_SIM] Đang tráng IMEI {targetImei}..." 
        });

        // 1. Đảm bảo tắt sóng
        await SendCommandAsync(portName, "AT+CFUN=4", 8000, silent: true);
        await Task.Delay(800);

        // 2. Ghi IMEI
        string writeResp = await SendCommandAsync(portName, $"AT+EGMR=1,7,\"{targetImei}\"", 30000);
        if (writeResp.Contains("ERROR"))
        {
            // Fallback
            writeResp = await SendCommandAsync(portName, $"AT+SIMEI=\"{targetImei}\"", 30000);
        }

        // 3. Verify
        string verify = await SendCommandAsync(portName, "AT+CGSN", 8000, silent: true);
        string finalImei = verify.Replace("OK", "").Trim();

        if (finalImei != targetImei && !finalImei.Contains(targetImei))
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs 
            { 
                PortName = portName, 
                Data = $"[ACCEPT_NEW_SIM] Ghi IMEI thất bại! Đọc lại: {finalImei}" 
            });
            return false;
        }

        LogMessage?.Invoke(this, new GsmDataEventArgs 
        { 
            PortName = portName, 
            Data = $"[ACCEPT_NEW_SIM] Ghi IMEI thành công → đang khôi phục sóng..." 
        });

        // 4. Tải lại sóng để áp dụng không ngắt USB
        await SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true);
        await Task.Delay(1000);
        await SendCommandAsync(portName, "AT+CFUN=1", 12000, silent: true);
        return true;
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
                    SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = cmgl });
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
            
            if (buffer.Length > 2000)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[WARNING] Buffer overflow ({buffer.Length} chars). Cleaning up..." });
                buffer.Clear();
                currentData = "";
            }

            if (currentData.Contains("+CMS ERROR: 302") || currentData.Contains("memory full"))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi đầy bộ nhớ SIM (+CMS ERROR: 302). Tự động xóa rác..." });
                _ = SendCommandAsync(portName, "AT+CMGD=1,4", 10000, true);
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
                    
                    // Đẩy việc đọc tất cả các tin nhắn vào Task chạy ngầm.
                    _ = Task.Run(async () => 
                    {
                        foreach (Match match in matches)
                        {
                            string msgIndex = match.Groups[1].Value;
                            string smsContent = "";
                            bool success = false;
                            
                            for (int attempt = 1; attempt <= 3; attempt++)
                            {
                                smsContent = await SendCommandAsync(portName, $"AT+CMGR={msgIndex}");
                                // Đảm bảo không bị OK rỗng (Modem trả OK nhưng chưa kịp xuất nội dung)
                                if (!smsContent.Contains("ERROR") && smsContent.Contains("+CMGR:"))
                                {
                                    success = true;
                                    break;
                                }
                                await Task.Delay(1000);
                            }
                            
                            if (success)
                            {
                                string body = DecodeSmsBody(smsContent);
                                string senderInfo = ParseSenderFromCmgr(smsContent);
                                
                                string? fullContent = TryAssembleMultipart(portName, senderInfo, body, msgIndex, out var indicesToDelete);
                                if (fullContent == null)
                                {
                                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[MULTIPART] Nhận phần của tin dài từ {senderInfo}, chờ phần tiếp..." });
                                    continue;
                                }
                                
                                string? otp = ExtractOtp(fullContent);
                                
                                foreach (var idx in indicesToDelete)
                                {
                                    _ = SendCommandAsync(portName, $"AT+CMGD={idx}", 3000, silent: true);
                                }

                                SmsReceived?.Invoke(this, new GsmDataEventArgs 
                                { 
                                    PortName = portName, 
                                    Data = fullContent, 
                                    MsgIndex = msgIndex,
                                    Sender = senderInfo,
                                    Otp = otp ?? ""
                                });
                            }
                            else
                            {
                                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Lỗi đọc tin nhắn ở vị trí {msgIndex} sau 3 lần thử. Đang kích hoạt quét vét ngay lập tức..." });
                                
                                // Kích hoạt quét vét ngay lập tức (sau 2s) thay vì chờ chu kỳ 3 phút
                                _ = Task.Run(async () => 
                                {
                                    await Task.Delay(2000);
                                    await SweepUnreadSmsAsync(portName);
                                });
                            }
                        }
                    });
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
                    
                    _ = Task.Run(async () => 
                    {
                        foreach (Match match in matches)
                        {
                            string msgIndex = match.Groups[1].Value;
                            string smsContent = "";
                            bool success = false;
                            
                            for (int attempt = 1; attempt <= 3; attempt++)
                            {
                                smsContent = await SendCommandAsync(portName, $"AT+CMGR={msgIndex}");
                                if (!smsContent.Contains("ERROR") && smsContent.Contains("+CMGR:"))
                                {
                                    success = true;
                                    break;
                                }
                                await Task.Delay(1000);
                            }
                            
                            if (success)
                            {
                                string body = DecodeSmsBody(smsContent);
                                string senderInfo = ParseSenderFromCmgr(smsContent);
                                
                                string? fullContent = TryAssembleMultipart(portName, senderInfo, body, msgIndex, out var indicesToDelete);
                                if (fullContent == null)
                                {
                                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[MULTIPART] Nhận phần của tin dài từ {senderInfo}, chờ phần tiếp..." });
                                    continue;
                                }
                                
                                string? otp = ExtractOtp(fullContent);
                                
                                foreach (var idx in indicesToDelete)
                                {
                                    _ = SendCommandAsync(portName, $"AT+CMGD={idx}", 3000, silent: true);
                                }

                                SmsReceived?.Invoke(this, new GsmDataEventArgs 
                                { 
                                    PortName = portName, 
                                    Data = fullContent, 
                                    MsgIndex = msgIndex,
                                    Sender = senderInfo,
                                    Otp = otp ?? ""
                                });
                            }
                            else
                            {
                                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[Sweep] Lỗi đọc tin nhắn kẹt ở vị trí {msgIndex} sau 3 lần thử." });
                            }
                        }
                    });
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
            if (currentData.Contains("+QSIMSTAT: 1,1"))
            {
                buffer.Replace("+QSIMSTAT: 1,1", "");
                currentData = buffer.ToString();
                
                // Khởi động luồng đọc CCID và IMEI, sau đó báo UI
                _ = Task.Run(() => HandleSimInsertedAsync(portName));
            }

            if (currentData.Contains("+CPIN: NOT READY") || currentData.Contains("+CPIN: NOT INSERTED") || currentData.Contains("+QSIMSTAT: 1,0"))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra!" });
                buffer.Replace("+CPIN: NOT READY", "");
                buffer.Replace("+CPIN: NOT INSERTED", "");
                buffer.Replace("+QSIMSTAT: 1,0", "");
                currentData = buffer.ToString();
                
                // Tắt sóng (AT+CFUN=4) để an toàn
                _ = SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);
                
                // Khởi động lại luồng chờ SIM (để UI tiếp tục theo dõi nếu hụt URC)
                StartHotplugWaitLoop(portName);
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
        foreach (var kvp in _serialPorts)
        {
            try
            {
                kvp.Value.Close();
                kvp.Value.Dispose();
            }
            catch { }
        }
        foreach (var kvp in _semaphores)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _serialPorts.Clear();
        _semaphores.Clear();
        _portBuffers.Clear();
        _commandTcs.Clear();
        _connectionErrors.Clear();
        _sleepingPorts.Clear();
        _portVendors.Clear();
    }

    public void Disconnect(string portName)
    {
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
            try { sem.Dispose(); } catch { }
            _semaphores.TryRemove(portName, out _);
            _portBuffers.TryRemove(portName, out _);
            _commandTcs.TryRemove(portName, out _);
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

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000, bool silent = false)
    {
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

            // Chờ giữa các đoạn để tránh nhà mạng xáo trộn thứ tự nhận
            if (i < total - 1) await Task.Delay(3000);
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

            // Trả lại UCS2 charset để đọc tin nhắn tiếng Việt và DCS=8 cho UCS2
            if (_serialPorts.TryGetValue(portName, out var sp2) && sp2.IsOpen)
            {
                await SendInnerAsync("AT+CSCS=\"UCS2\"");
                await SendInnerAsync("AT+CSMP=17,167,0,8");
            }

            semaphore.Release();
        }
    }

    public async Task SweepUnreadSmsAsync(string portName)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return;
        
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Đang quét tin nhắn tồn đọng (Sweep)..." });
        await SendCommandAsync(portName, "AT+CMGL=\"REC UNREAD\"");
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

        string? remoteFileName = null;

        try
        {
            // ---------- 1. Upload WAV nếu có ----------
            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
            {
                remoteFileName = await UploadWavAsync(portName, wavPath, ct);
                if (remoteFileName == null)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Upload WAV thất bại → gọi không audio" });
                }
            }

            // ---------- 2. Bật CLIP / báo trạng thái (tuỳ chọn) ----------
            await SendCommandAsync(portName, "AT+CLIP=1", 2000, silent: true);
            await SendCommandAsync(portName, "AT+CRC=1", 2000, silent: true);

            // ---------- 3. Gọi ----------
            // ATDxxx;  (dấu ; = voice call)
            string cleanPhone = (phoneNumber.StartsWith("+") ? "+" : "") + new string(phoneNumber.Where(char.IsDigit).ToArray());
            var dialResp = await SendCommandAsync(portName, $"ATD{cleanPhone};", 15000);
            if (dialResp.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                dialResp.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Gọi thất bại: {dialResp}" });
                return false;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đang gọi {phoneNumber}..." });

            // ---------- 4. Chờ nhấc máy (CLCC) hoặc timeout ----------
            bool answered = await WaitForAnswerAsync(portName, timeoutSeconds: 45, ct);
            if (!answered)
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Không nhấc máy / timeout → ATH" });
                await SendCommandAsync(portName, "ATH", 3000, silent: true);
                return false;
            }

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đã kết nối" });

            // ---------- 5. Phát WAV nếu đã upload ----------
            if (!string.IsNullOrEmpty(remoteFileName))
            {
                await PlayWavAsync(portName, remoteFileName, ct);
            }

            // ---------- 6. (Tuỳ chọn) Bắt đầu ghi âm ----------
            if (record)
            {
                try
                {
                    await SendCommandAsync(portName, "AT+QAUDRD=1,\"call_rec.wav\",1", 3000, silent: true);
                }
                catch { /* ignore nếu không hỗ trợ */ }
            }

            // ---------- 7. Giữ cuộc gọi theo duration ----------
            var end = DateTime.UtcNow.AddSeconds(Math.Max(5, durationSeconds));
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                // Kiểm tra còn trong cuộc gọi không
                var clcc = await SendCommandAsync(portName, "AT+CLCC", 2000, silent: true);
                if (!clcc.Contains("+CLCC:"))
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Cuộc gọi đã kết thúc sớm" });
                    break;
                }
                await Task.Delay(1000, ct);
            }

            // ---------- 8. Dập máy ----------
            await SendCommandAsync(portName, "ATH", 5000);
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Đã dập máy" });

            // ---------- 9. Dọn file trên modem (tuỳ chọn) ----------
            if (!string.IsNullOrEmpty(remoteFileName))
            {
                try
                {
                    await SendCommandAsync(portName, $"AT+QFDEL=\"{remoteFileName}\"", 3000, silent: true);
                }
                catch { }
            }

            if (record)
            {
                try { await SendCommandAsync(portName, "AT+QAUDRD=0", 2000, silent: true); } catch { }
            }

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



