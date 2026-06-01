using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000);
    List<string> GetAvailablePorts();
    void ConnectAll(int baudRate = 115200);
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
}

public class GsmModemService : IGsmModemService
{
    private readonly ConcurrentDictionary<string, SerialPort> _serialPorts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _portBuffers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _commandTcs = new();

    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;
    public event EventHandler<GsmDataEventArgs>? PortDisconnected;

    public List<string> GetAvailablePorts()
    {
        return new List<string>(SerialPort.GetPortNames());
    }

    public void ConnectAll(int baudRate = 115200)
    {
        var ports = GetAvailablePorts();
        foreach (var p in ports)
        {
            if (!_serialPorts.ContainsKey(p))
            {
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
                    
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Đã kết nối thành công {p} (Baud: {baudRate})" });
                    
                    // Gửi lệnh khởi tạo
                    _ = InitializeModemAsync(p);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = p, Data = $"Lỗi kết nối {p}: {ex.Message}" });
                }
            }
        }
    }

    private void HandleErrorReceived(string portName, SerialPort sp)
    {
        PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = "Lỗi phần cứng (Có thể bị rút cáp)" });
    }

    private async Task InitializeModemAsync(string portName)
    {
        await SendCommandAsync(portName, "ATZ"); // Reset
        await SendCommandAsync(portName, "ATE0"); // Turn off echo
        await SendCommandAsync(portName, "AT+CMGF=1"); // Set SMS to text mode
        await SendCommandAsync(portName, "AT+CSCS=\"UCS2\""); // Đọc được tiếng Việt
        
        // Cấu hình đẩy SMS: 2,1 để lưu vào SIM và gửi +CMTI (phù hợp với Regex lấy msgIndex)
        await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0"); 
        
        // Lấy thông tin mạng và thông tin thiết bị
        await SendCommandAsync(portName, "AT+COPS?");
        await SendCommandAsync(portName, "AT+CSQ");
        
        string imei = await SendCommandAsync(portName, "AT+CGSN");
        string ccid = await SendCommandAsync(portName, "AT+CCID");
        string cnum = await SendCommandAsync(portName, "AT+CNUM");

        // Gửi thông tin sang ViewModel qua event log với Prefix đặc biệt
        if (!imei.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_IMEI] {imei.Replace("OK", "").Trim()}" });
        if (!ccid.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CCID] {ccid.Replace("OK", "").Trim()}" });
        if (!cnum.Contains("ERROR")) LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[PARSE_CNUM] {cnum.Replace("OK", "").Trim()}" });
    }

    private void HandleDataReceived(string portName, SerialPort sp)
    {
        try
        {
            string chunk = sp.ReadExisting();
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
                        SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = smsContent });
                        await SendCommandAsync(portName, $"AT+CMGD={msgIndex},4"); // Đọc xong xóa luôn
                    });
                }
            }

            // ---------------------------------------------------------
            // 2. XỬ LÝ LỆNH TỪ PHẦN MỀM ĐANG GỬI XUỐNG (TCS)
            // ---------------------------------------------------------
            if (_commandTcs.TryGetValue(portName, out var tcs))
            {
                // Kiểm tra dấu hiệu kết thúc của lệnh AT
                if (currentData.Contains("OK\r\n") || currentData.Contains("ERROR\r\n") || currentData.EndsWith("> "))
                {
                    if (tcs.Task.AsyncState is string cmd && cmd.StartsWith("AT+CUSD"))
                    {
                        // Đợi USSD từ tổng đài, không thoát sớm
                        if (currentData.Contains("OK\r\n") && !currentData.Contains("+CUSD:"))
                        {
                            return; 
                        }
                    }

                    tcs.TrySetResult(currentData);
                    buffer.Clear(); // An toàn để xóa vì đã nhặt SMS ra ở bước 1
                }
            }
            // ---------------------------------------------------------
            // 3. DỌN DẸP RÁC BỘ ĐỆM AN TOÀN
            // ---------------------------------------------------------
            else
            {
                // Chỉ xóa bộ đệm khi thiết bị nhả rác có chữ OK/ERROR chuẩn
                if (currentData.Contains("OK\r\n") || currentData.Contains("ERROR\r\n"))
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

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 5000)
    {
        // Kéo dài thời gian chờ cho các lệnh USSD do mạng di động phản hồi chậm
        if (command.StartsWith("AT+CUSD")) timeoutMs = 15000;

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

        var tcs = new TaskCompletionSource<string>(command);
        _commandTcs[portName] = tcs;

        try
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {command}" });
            
            if (_portBuffers.TryGetValue(portName, out var buffer)) buffer.Clear();
            
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
            
            sp.Write(command + "\r\n");
            
            // Đứng chờ HandleDataReceived bơm dữ liệu vào TCS, hoặc bị quá giờ (Timeout)
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
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
            _commandTcs.TryRemove(portName, out _);
            semaphore.Release();
        }
    }
}
