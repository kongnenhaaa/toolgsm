using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;

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
                    sp.Open();
                    
                    _serialPorts.TryAdd(p, sp);
                    _semaphores.TryAdd(p, new SemaphoreSlim(1, 1));
                    
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

    private async Task InitializeModemAsync(string portName)
    {
        await SendCommandAsync(portName, "ATZ"); // Reset
        await SendCommandAsync(portName, "ATE0"); // Turn off echo
        await SendCommandAsync(portName, "AT+CMGF=1"); // Set SMS to text mode
        
        // Cấu hình đẩy SMS: 2,1 để lưu vào SIM và gửi +CMTI (phù hợp với Regex lấy msgIndex)
        await SendCommandAsync(portName, "AT+CNMI=2,1,0,0,0"); 
        
        // Gửi lệnh lấy nhà mạng
        await SendCommandAsync(portName, "AT+COPS?");
    }

    private async void HandleDataReceived(string portName, SerialPort sp)
    {
        try
        {
            string data = sp.ReadExisting();
            if (!string.IsNullOrWhiteSpace(data))
            {
                // Bắt sự kiện có tin nhắn mới tới
                if (data.Contains("+CMTI:"))
                {
                    // Dùng Regex tìm vị trí lưu tin nhắn. VD: +CMTI: "SM",1 -> Lấy số 1
                    var match = Regex.Match(data, @"\+CMTI:\s*""[^""]+"",(\d+)");
                    if (match.Success)
                    {
                        string msgIndex = match.Groups[1].Value;
                        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"Phát hiện tin nhắn ở vị trí {msgIndex}, đang đọc..." });
                        
                        // Gửi lệnh ĐỌC tin nhắn đó
                        string smsContent = await SendCommandAsync(portName, $"AT+CMGR={msgIndex}");
                        
                        // Gắn vào sự kiện để ném sang ViewModel
                        SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = smsContent });

                        // Tùy chọn: Xóa tin nhắn sau khi đọc để tránh đầy SIM (AT+CMGD=1)
                        await SendCommandAsync(portName, $"AT+CMGD={msgIndex},4");
                    }
                }
                else if (data.Contains("+CUSD:") || data.Contains("+CMGL:"))
                {
                    // Ignore, these are usually caught by the SendCommandAsync loop
                }
                else
                {
                    LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"[URC] {data.Trim()}" });
                }
            }
        }
        catch { }
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
        _serialPorts.Clear();
        _semaphores.Clear();
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

        try
        {
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"> {command}" });
            
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
            
            sp.Write(command + "\r\n");
            
            StringBuilder response = new StringBuilder();
            Stopwatch sw = Stopwatch.StartNew();
            
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (sp.BytesToRead > 0)
                {
                    string chunk = sp.ReadExisting();
                    response.Append(chunk);
                    
                    string fullResp = response.ToString();
                    // End markers for AT commands
                    if (fullResp.Contains("OK\r\n") || fullResp.Contains("ERROR\r\n") || fullResp.Contains("> "))
                    {
                        // Some commands like USSD take longer to respond with +CUSD after OK.
                        if (command.StartsWith("AT+CUSD") && fullResp.Contains("OK\r\n") && !fullResp.Contains("+CUSD:"))
                        {
                            // Wait for the actual USSD response
                            continue;
                        }
                        
                        break;
                    }
                }
                await Task.Delay(50);
            }
            
            string finalResp = response.ToString().Trim();
            LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = $"< {finalResp}" });
            return finalResp;
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
        finally
        {
            semaphore.Release();
        }
    }
}
