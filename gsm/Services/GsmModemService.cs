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
    Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 15000);
    void StartPollingNetwork(string portName);
    List<string> GetAvailablePorts();
    void ConnectAll(int baudRate = 115200);
    void Disconnect(string portName);
    void DisconnectAll();
    
    // Events
    event EventHandler<GsmDataEventArgs> SmsReceived;
    event EventHandler<GsmDataEventArgs> LogMessage;
    event EventHandler<GsmDataEventArgs> PortDisconnected;
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
    private readonly object _connectLock = new object();

    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;
    public event EventHandler<GsmDataEventArgs>? PortDisconnected;

    public List<string> GetAvailablePorts()
    {
        return new List<string>(SerialPort.GetPortNames());
    }

    public void ConnectAll(int baudRate = 115200)
    {
        lock (_connectLock)
        {
            var ports = GetAvailablePorts();
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
                    
                    sp.DataReceived += (s, e) => HandleDataReceived(p, sp);
                    sp.ErrorReceived += (s, e) => HandleErrorReceived(p, sp);
                    sp.Open();
                    
                    _serialPorts.TryAdd(p, sp);
                    _semaphores.TryAdd(p, new SemaphoreSlim(1, 1));
                    _portBuffers.TryAdd(p, new StringBuilder());
                    _connectionErrors.TryRemove(p, out _); // Reset lỗi khi kết nối thành công
                    
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Đã kết nối thành công {p} (Baud: {baudRate})" });
                    
                    // Gửi lệnh khởi tạo
                    _ = InitializeModemAsync(p);
                }
                catch (Exception ex)
                {
                    int errors = _connectionErrors.AddOrUpdate(p, 1, (key, old) => old + 1);
                    if (errors >= 3)
                    {
                        _sleepingPorts[p] = DateTime.Now.AddMinutes(5); // Cho cổng ngủ 5 phút
                        _connectionErrors.TryRemove(p, out _);
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Lỗi kết nối {p} quá 3 lần: {ex.Message}. Tạm ngưng kết nối cổng này trong 5 phút để tránh spam log." });
                    }
                    else
                    {
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Lỗi kết nối {p}: {ex.Message}" });
                    }
                    }
                }
            }
        }
    }

    private void HandleErrorReceived(string portName, SerialPort sp)
    {
        Disconnect(portName);
        PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi phần cứng (Có thể bị rút cáp)" });
    }

    private async Task InitializeModemAsync(string portName)
    {
        // Chờ 2 giây để thiết bị khởi động hoàn toàn trước khi gửi lệnh AT, tránh bị treo hoặc timeout
        await Task.Delay(2000);
        
        await SendCommandAsync(portName, "ATZ", 30000); // Reset
        await SendCommandAsync(portName, "ATE0", 30000); // Turn off echo
        await SendCommandAsync(portName, "AT+CMGF=1", 30000); // Set SMS to text mode
        await SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 30000); // Đọc được tiếng Việt
        
        // Xóa toàn bộ SMS cũ trong SIM để tránh bị đầy bộ nhớ khiến không nhận được CMTI mới
        await SendCommandAsync(portName, "AT+CMGD=1,4", 30000); 
        
        // Cấu hình đẩy SMS: 2,1 để lưu vào SIM và gửi +CMTI (phù hợp với Regex lấy msgIndex)
        await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0", 30000); 
        
        // Lấy thông tin mạng và thông tin thiết bị
        await SendCommandAsync(portName, "AT+COPS?", 30000);
        await SendCommandAsync(portName, "AT+CSQ", 30000);
        
        string imei = await SendCommandAsync(portName, "AT+CGSN", 30000);
        string ccid = await SendCommandAsync(portName, "AT+CCID", 30000);
        string cnum = await SendCommandAsync(portName, "AT+CNUM", 30000);

        // Gửi thông tin sang ViewModel qua event log với Prefix đặc biệt
        if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
        if (!ccid.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
        if (!cnum.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CNUM] {cnum.Replace("OK", "").Trim()}" });

        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "[STATUS_ACTIVE]" });

        StartPollingNetwork(portName);
    }

    public void StartPollingNetwork(string portName)
    {
        // Tạo luồng ngầm chờ thiết bị đăng ký mạng thành công để lấy nhà mạng (Tránh việc AT+COPS? chạy quá sớm lúc chưa có sóng)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 15; i++) // Thử tối đa 30 giây (15 lần x 2s)
            {
                await Task.Delay(2000);
                if (!_serialPorts.ContainsKey(portName)) break; // Cổng đã bị rút
                
                string copsStr = await SendCommandAsync(portName, "AT+COPS?", 5000, silent: true);
                if (copsStr.Contains("+COPS:") && Regex.IsMatch(copsStr, @"\+COPS:\s*\d+,\d+,""([^""]+)"""))
                {
                    // Lấy mạng thành công, nhả sự kiện ra để ViewModel bắt và tự động chạy USSD
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = copsStr.Trim() });
                    break;
                }
            }
        });
    }

    private void HandleDataReceived(string portName, SerialPort sp)
    {
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
                var match = Regex.Match(currentData, @"\+CMTI:\s*""[^""]+"",\s*(\d+)");
                if (match.Success)
                {
                    string msgIndex = match.Groups[1].Value;
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Phát hiện tin nhắn ở vị trí {msgIndex}, đang đọc..." });
                    
                    // Cắt bỏ phần thông báo này khỏi bộ đệm để không xử lý lại
                    // (Giữ nguyên các dữ liệu khác đang chờ nếu có)
                    buffer.Replace(match.Value, ""); 
                    currentData = buffer.ToString();
                    
                    // Đẩy việc đọc tin nhắn vào Task chạy ngầm.
                    // Nó sẽ tự động xếp hàng đợi Semaphore mà không làm kẹt luồng đọc hiện tại!
                    _ = Task.Run(async () => 
                    {
                        string smsContent = await SendCommandAsync(portName, $"AT+CMGR={msgIndex}");
                        SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = smsContent, MsgIndex = msgIndex });
                        // Không tự động xóa tin nhắn ở đây nữa, đẩy việc quyết định xóa lên ViewModel
                    });
                }
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
                // Kiểm tra dấu hiệu kết thúc của lệnh AT (OK, ERROR, hoặc CMS/CME ERROR, hoặc dấu nhắc >)
                bool isCompleted = Regex.IsMatch(currentData, @"\r?\nOK\r?\n?$|\r?\nERROR\r?\n?$|\+CMS ERROR:|\+CME ERROR:|>\s*$");
                if (isCompleted)
                {
                    if (tcs.Task.AsyncState is string cmd && cmd.StartsWith("AT+CUSD"))
                    {
                        // Đợi USSD từ tổng đài, không thoát sớm nếu chỉ mới nhận được OK
                        if (currentData.Contains("OK\r\n") && !currentData.Contains("+CUSD:"))
                        {
                            return; 
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
        }

        _portBuffers.TryRemove(portName, out _);
        _commandTcs.TryRemove(portName, out _);
    }

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000, bool silent = false)
    {
        // Kéo dài thời gian chờ cho các lệnh đặc biệt
        if (command.StartsWith("AT+CUSD")) timeoutMs = 15000;
        else if (command.StartsWith("AT+CMGR")) timeoutMs = 10000;

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
                return "ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)";
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

    public async Task<string> SendSmsAsync(string portName, string phoneNumber, string message, int timeoutMs = 15000)
    {
        if (!_serialPorts.TryGetValue(portName, out var sp) || !sp.IsOpen) return "ERROR: Port not open";
        if (!_semaphores.TryGetValue(portName, out var semaphore)) return "ERROR: Semaphore missing";

        bool lockAcquired = await semaphore.WaitAsync(timeoutMs);
        if (!lockAcquired) return "ERROR: Timeout waiting for lock";

        var tcs = new TaskCompletionSource<string>("AT+CMGS", TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandTcs.TryAdd(portName, tcs))
        {
            semaphore.Release();
            return "ERROR: Another command is already in progress";
        }

        try
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> AT+CMGS=\"{phoneNumber}\"" });
            if (_portBuffers.TryGetValue(portName, out var buffer)) buffer.Clear();
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();

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
            semaphore.Release();
        }
    }
}
