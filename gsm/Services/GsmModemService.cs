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
    void ConnectAll(int baudRate = 115200);
    void Disconnect(string portName);
    void DisconnectAll();
    void StartHotplugWaitLoop(string portName);
    Task ReinitializeSettingsAsync(string portName);
    Task<bool> ResetNetworkAsync(string portName);
    
    // Events
    event EventHandler<GsmDataEventArgs> SmsReceived;
    event EventHandler<GsmDataEventArgs> LogMessage;
    event EventHandler<GsmDataEventArgs> PortDisconnected;
    event EventHandler<GsmDataEventArgs> CallIncoming;
    event EventHandler<GsmDataEventArgs> CallEnded;
    event EventHandler<GsmDataEventArgs> DtmfReceived;
}

public class GsmDataEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string MsgIndex { get; set; } = string.Empty;
}

public class GsmModemService : IGsmModemService
{
    private readonly ConcurrentDictionary<string, SerialPort> _serialPorts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _portBuffers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _commandTcs = new();
    private readonly ConcurrentDictionary<string, int> _connectionErrors = new();
    private readonly ConcurrentDictionary<string, DateTime> _sleepingPorts = new();
    private readonly ConcurrentDictionary<string, SerialDataReceivedEventHandler> _dataReceivedHandlers = new();
    private readonly ConcurrentDictionary<string, bool> _isDownloading = new();
    private readonly object _connectLock = new object();

    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;
    public event EventHandler<GsmDataEventArgs>? PortDisconnected;
    public event EventHandler<GsmDataEventArgs>? CallIncoming;
    public event EventHandler<GsmDataEventArgs>? CallEnded;
    public event EventHandler<GsmDataEventArgs>? DtmfReceived;

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

    public void ConnectAll(int baudRate = 115200)
    {
        List<string> newlyOpenedPorts = new List<string>();

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
    }

    private void HandleErrorReceived(string portName, SerialPort sp)
    {
        Disconnect(portName);
        PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi phần cứng (Có thể bị rút cáp)" });
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
            if (cfunStatus.Contains("+CFUN: 4") || cfunStatus.Contains("+CFUN: 0"))
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
        await SendCommandAsync(portName, "AT+QTONEDET=1", 30000); // Bật bộ phát hiện âm tần DTMF
        
        // Bật xuất âm thanh cuộc gọi ra cổng USB (UAC) cho Quectel EC20
        await SendCommandAsync(portName, "AT+QPCMV=0,0", 30000); 
        await SendCommandAsync(portName, "AT+QAUDMOD=0", 30000); 
        
        string imei = await SendCommandAsync(portName, "AT+CGSN", 30000);
        
        // Thử đọc CCID nhiều lần (SIM có thể cần vài giây để khởi tạo)
        string ccid = "ERROR";
        for (int i = 0; i < 15; i++)
        {
            string resp = await SendCommandAsync(portName, "AT+CCID", 30000);
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
            await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 30000); 
            
            string cnum = await SendCommandAsync(portName, "AT+CNUM", 30000);

            // Gửi thông tin sang ViewModel qua event log với Prefix đặc biệt
            if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
            if (!cnum.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CNUM] {cnum.Replace("OK", "").Trim()}" });
        }
        else
        {
            if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
            
            // NEW SIM INTAKE MODE: PRE-COAT FAKE IMEI
            var settings = gsm.Services.SettingsService.Current;
            if (settings != null && settings.EnableNewSimIntakeMode)
            {
                string cleanImei = imei.Replace("OK", "").Trim();
                bool isAlreadyFake = gsm.Services.ImeiManagementService.IsFakeImei(cleanImei);

                if (!isAlreadyFake && !string.IsNullOrEmpty(cleanImei))
                {
                    string targetImei = gsm.Services.ImeiManagementService.GenerateRandomImei();
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NEW_SIM_MODE] Đang tráng sẵn Fake IMEI: {targetImei}" });
                    await SendCommandAsync(portName, $"AT+EGMR=1,7,\"{targetImei}\"", 30000);
                    await SendCommandAsync(portName, "AT+CFUN=1,1", 30000); // Reboot modem
                    return; // Return and wait for it to reconnect
                }
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
        await SendCommandAsync(portName, "AT+QTONEDET=1", 5000, silent: true); // Bật bộ phát hiện âm tần DTMF
        await SendCommandAsync(portName, "AT+QPCMV=0,0", 5000, silent: true);
        await SendCommandAsync(portName, "AT+QAUDMOD=0", 5000, silent: true); 
        await SendCommandAsync(portName, "AT+CMGD=1,4", 10000, silent: true); 
        await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 5000, silent: true);
        
        StartPollingNetwork(portName);
    }

    /// <summary>
    /// Kích hoạt vòng lặp chờ SIM (Hot-plug). Đưa modem vào chế độ máy bay và liên tục kiểm tra CCID.
    /// Dùng khi khởi động không có SIM, hoặc khi người dùng yêu cầu chuẩn bị đổi SIM.
    /// </summary>
    public void StartHotplugWaitLoop(string portName)
    {
        _ = Task.Run(async () =>
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] Đang chờ cắm SIM (Hot-plug)..." });
            
            // Ép tắt sóng (Airplane mode) ngay khi bắt đầu vòng lặp chờ SIM
            await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
            
            int cfunCheckCounter = 0;
            while (true)
            {
                await Task.Delay(2000); // Tăng khoảng cách kiểm tra lên 2 giây để mạch rảnh xử lý
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                
                // Thỉnh thoảng kiểm tra lại CFUN (mỗi 5 chu kỳ = 10 giây) để tránh tự động bật sóng
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
                
                string pollResp = await SendCommandAsync(portName, "AT+CCID", 10000, silent: true);
                if (!pollResp.Contains("ERROR") && !string.IsNullOrWhiteSpace(pollResp))
                {
                    // Đè thêm 1 lệnh CFUN=4 nữa để triệt tiêu việc tự động đăng ký mạng trong lúc xử lý IMEI
                    await SendCommandAsync(portName, "AT+CFUN=4", 3000, silent: true);

                    // Cập nhật IMEI hiện tại trước khi báo CCID
                    string currentImei = await SendCommandAsync(portName, "AT+CGSN", 5000, silent: true);
                    if (!string.IsNullOrWhiteSpace(currentImei) && !currentImei.Contains("ERROR"))
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {currentImei.Replace("OK", "").Trim()}" });
                    }
                    
                    string newCcid = pollResp;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {newCcid.Replace("OK", "").Trim()}" });
                    
                    break; // Có SIM rồi thì thoát vòng lặp
                }
            }
        });
    }

    public void StartPollingNetwork(string portName)
    {
        // Tạo luồng ngầm chờ thiết bị đăng ký mạng thành công để lấy nhà mạng (Tránh việc AT+COPS? chạy quá sớm lúc chưa có sóng)
        // Lặp vô hạn cho đến khi có mạng hoặc cổng bị rút
        _ = Task.Run(async () =>
        {
            int attempts = 0;
            int recoveryCount = 0;
            while (true)
            {
                await Task.Delay(2000);
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                
                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true);
                if (copsStr.Contains("+COPS:") && Regex.IsMatch(copsStr, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)"""))
                {
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    break;
                }

                attempts++;

                // Khôi phục sóng nếu kẹt quá lâu (Khoảng 60 giây = 30 lần)
                if (attempts > 30)
                {
                    attempts = 0;
                    recoveryCount++;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[NETWORK_RECOVERY] Không tìm thấy sóng, đang thử khôi phục mạng lần {recoveryCount}..." });
                    
                    // Toggle chế độ máy bay để reset cọc sóng
                    await SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                    await Task.Delay(1000);
                    await SendCommandAsync(portName, "AT+CFUN=1", 10000, silent: true);
                    
                    // Ép tự động quét lại trạm sóng mạng
                    await SendCommandAsync(portName, "AT+COPS=0", 10000, silent: true);
                }
            }
        });
    }

    public async Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile)
    {
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
                                SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = smsContent, MsgIndex = msgIndex });
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
                                SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = smsContent, MsgIndex = msgIndex });
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
                    CallIncoming?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = callerNumber });
                    
                    // Cắt bỏ khỏi buffer
                    buffer.Replace(clipMatch.Value, "");
                    buffer.Replace("RING", ""); 
                    currentData = buffer.ToString();
                }
            }

            if (currentData.Contains("NO CARRIER"))
            {
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "NO CARRIER" });
                buffer.Replace("NO CARRIER", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("BUSY"))
            {
                CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "BUSY" });
                buffer.Replace("BUSY", "");
                currentData = buffer.ToString();
            }
            else if (currentData.Contains("NO ANSWER"))
            {
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
            // 1.4. BẮT RÚT SIM (HOT-UNPLUG)
            // ---------------------------------------------------------
            if (currentData.Contains("+CPIN: NOT READY") || currentData.Contains("+CPIN: NOT INSERTED"))
            {
                LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[WAITING_FOR_SIM] SIM đã bị rút ra!" });
                buffer.Replace("+CPIN: NOT READY", "");
                buffer.Replace("+CPIN: NOT INSERTED", "");
                currentData = buffer.ToString();
                
                // Khởi động lại luồng chờ SIM
                StartHotplugWaitLoop(portName);
            }

            // ---------------------------------------------------------
            // 1.5. BẮT KẾT QUẢ USSD (+CUSD)
            // ---------------------------------------------------------
            if (currentData.Contains("+CUSD:") && !currentData.StartsWith("AT+CUSD"))
            {
                // USSD của nhà mạng thường kết thúc bằng ",15 hoặc ",72 hoặc không có text gì (+CUSD: 2)
                bool isUssdComplete = Regex.IsMatch(currentData, @"\+CUSD:\s*\d+\r?\n?$") || 
                                      Regex.IsMatch(currentData, @"\+CUSD:\s*\d+,\""[\s\S]*?\""(,\d+)?\r?\n?$");

                if (isUssdComplete)
                {
                    if (_commandTcs.TryGetValue(portName, out var t) && t.Task.AsyncState is string c && c.StartsWith("AT+CUSD"))
                    {
                        // Đang chờ lệnh AT+CUSD, nhả kết quả cho SendCommandAsync để nó tự log
                        t.TrySetResult(currentData.Trim());
                    }
                    else
                    {
                        // Nhận được USSD tự do (không có lệnh nào đang đợi), nên ta chủ động log để MainViewModel bắt
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = currentData.Trim() });
                    }
                    
                    buffer.Clear();
                    return;
                }
                // Nếu chưa complete thì không return, để vòng lặp tiếp tục nối chuỗi
            }

            // ---------------------------------------------------------
            // 2. XỬ LÝ LỆNH TỪ PHẦN MỀM ĐANG GỬI XUỐNG (TCS)
            // ---------------------------------------------------------
            if (_commandTcs.TryGetValue(portName, out var tcs))
            {
                // Kiểm tra dấu hiệu kết thúc của lệnh AT (OK, ERROR, hoặc CMS/CME ERROR, hoặc dấu nhắc >, hoặc CONNECT)
                bool isCompleted = Regex.IsMatch(currentData, @"\r?\nOK\r?\n?$|\r?\nERROR\r?\n?$|\+CMS ERROR:|\+CME ERROR:|>\s*$|\r?\nCONNECT\r?\n?$");
                if (isCompleted)
                {
                    if (tcs.Task.AsyncState is string cmd && cmd.StartsWith("AT+CUSD"))
                    {
                        // Đợi USSD từ tổng đài. VNSKY có lỗi gửi "+CME ERROR: 100" trước "+CUSD:"
                        if (currentData.Contains("+CME ERROR: 100"))
                        {
                            return; // Bỏ qua CME ERROR 100 để tiếp tục chờ CUSD thực sự từ VNSKY
                        }
                    }

                    tcs.TrySetResult(currentData);
                    buffer.Clear(); // An toàn để xóa
                }
            }
            // ---------------------------------------------------------
            // 3. DỌN DẸP RÁC BỘ ĐỆM AN TOÀN
            // ---------------------------------------------------------
            else
            {
                // Chỉ xóa bộ đệm khi thiết bị nhả rác có chữ OK/ERROR chuẩn
                bool isCompleted = Regex.IsMatch(currentData, @"\r?\nOK\r?\n?$|\r?\nERROR\r?\n?$|\+CMS ERROR:|\+CME ERROR:");
                if (isCompleted)
                {
                    buffer.Clear();
                }
                // Nếu bị nhiễu sóng, dữ liệu rác dồn quá nhiều thì xóa để chống tràn RAM
                else if (currentData.Length > 2000) 
                {
                    buffer.Clear();
                }
            }
        }
        catch (IOException)
        {
            PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Bị rút cáp USB đột ngột!" });
        }
        catch (UnauthorizedAccessException)
        {
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
        }
    }

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000, bool silent = false)
    {
        // Kéo dài thời gian chờ cho các lệnh đặc biệt
        if (command.StartsWith("AT+CUSD")) timeoutMs = 45000;
        else if (command.StartsWith("AT+CMGR")) timeoutMs = 20000;

        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen)
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
            if (!silent) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {command}" });
            
            if (_portBuffers.TryGetValue(portName, out var buffer)) buffer.Clear();
            
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
            
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
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen)
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
            
            if (_portBuffers.TryGetValue(portName, out var buffer)) buffer.Clear();
            
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
            
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

    public async Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 30000)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return "ERROR: Port not open";
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
                if (_portBuffers.TryGetValue(portName, out var b)) b.Clear();
                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();
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
            await SendInnerAsync("AT+CSMP=17,167,0,0");
            await SendInnerAsync("AT+CSCS=\"GSM\"");

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
            if (_portBuffers.TryGetValue(portName, out var buf2)) buf2.Clear();

            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {message}" });
            sp.Write(message + "\x1A");

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
}
