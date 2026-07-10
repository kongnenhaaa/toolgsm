using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Services;

/// <summary>
/// Embedded HTTP REST API server — lắng nghe cổng 8080 (mặc định).
/// Endpoints:
///   GET  /api/ports            — Danh sách tất cả SIM
///   GET  /api/otp/{portName}   — OTP mới nhất của cổng
///   GET  /api/otp/latest       — OTP mới nhất toàn hệ thống
///   POST /api/send-sms         — Gửi SMS (body JSON)
///   GET  /api/history          — 50 OTP gần nhất từ CSV
/// </summary>
public class ApiServerService
{
    private readonly MainViewModel _vm;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();

    public ApiServerService(MainViewModel vm) => _vm = vm;

    public void Start(int port = 8080)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/api/");
            _listener.Start();

            Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            // Ghi log nhưng không crash app nếu port bị chiếm
            System.IO.File.AppendAllText(AppPaths.ForRuntimeFile("system_log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [WARN] [API] Không thể khởi động API server: {ex.Message}\n");
        }
    }

    public void Stop()
    {
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            if (_listener?.IsListening == true)
            {
                _listener.Stop();
            }
        }
        catch (ObjectDisposedException) { }
        catch (HttpListenerException) { }
        catch (InvalidOperationException) { }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (_listener?.IsListening == true))
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch { /* Tiếp tục lắng nghe */ }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        try
        {
            // CORS headers cho phép gọi từ browser
            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS") { resp.StatusCode = 204; resp.Close(); return; }

            string path = req.Url?.AbsolutePath.ToLower().TrimEnd('/') ?? "";

            if (req.HttpMethod == "GET" && path == "/api/ports")
                await WriteJson(resp, GetPortsData());

            else if (req.HttpMethod == "GET" && path == "/api/otp/latest")
                await WriteJson(resp, GetLatestOtp());

            else if (req.HttpMethod == "GET" && path.StartsWith("/api/otp/"))
            {
                string portName = path["/api/otp/".Length..].ToUpper();
                await WriteJson(resp, GetOtpByPort(portName));
            }

            else if (req.HttpMethod == "GET" && path == "/api/history")
                await WriteJson(resp, OtpHistoryService.GetRecent(50));

            else if (req.HttpMethod == "POST" && path == "/api/send-sms")
                await HandleSendSms(req, resp);

            else if (req.HttpMethod == "GET" && path.StartsWith("/api/proxy/reset/"))
            {
                string portName = path["/api/proxy/reset/".Length..].ToUpper();
                bool result = await _vm.ModemService.ResetNetworkAsync(portName);
                if (result)
                {
                    await WriteJson(resp, new { success = true, message = $"Đã gửi lệnh ngắt/bật mạng cho {portName}" });
                }
                else
                {
                    resp.StatusCode = 404;
                    await WriteJson(resp, new { success = false, error = $"Cổng {portName} không tồn tại hoặc lỗi lệnh" });
                }
            }

            else if (req.HttpMethod == "GET" && path == "/api/proxies")
            {
                await WriteJson(resp, _vm.ProxyManager.GetProxies());
            }

            else
            {
                resp.StatusCode = 404;
                await WriteJson(resp, new { error = "Endpoint không tồn tại" });
            }
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            await WriteJson(resp, new { error = ex.Message });
        }
        finally
        {
            resp.Close();
        }
    }

    private object GetPortsData()
    {
        return _vm.Ports.Select(p => new
        {
            port     = p.PortName,
            phone    = p.PhoneNumber,
            network  = p.NetworkProvider,
            status   = p.Status,
            signal   = p.SignalStrength,
            balance  = p.Balance,
            expiry   = p.ExpiryDate,
            otp      = p.Otp,
            updatedAt = p.UpdatedAt
        }).ToList();
    }

    private object GetLatestOtp()
    {
        var latest = _vm.Ports
            .Where(p => !string.IsNullOrEmpty(p.Otp) && p.Otp != "N/A")
            .OrderByDescending(p => p.LastReceivedTime)
            .FirstOrDefault();

        if (latest == null) return new { otp = (string?)null, port = (string?)null, phone = (string?)null };
        return new { otp = latest.Otp, port = latest.PortName, phone = latest.PhoneNumber, time = latest.LastReceivedTime };
    }

    private object GetOtpByPort(string portName)
    {
        var port = _vm.Ports.FirstOrDefault(p =>
            p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));

        if (port == null) return new { error = $"Không tìm thấy cổng {portName}" };
        return new { otp = port.Otp, port = port.PortName, phone = port.PhoneNumber, time = port.LastReceivedTime };
    }

    private async Task HandleSendSms(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync();

        var dto = JsonSerializer.Deserialize<SendSmsDto>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Port) ||
            string.IsNullOrWhiteSpace(dto.To) || string.IsNullOrWhiteSpace(dto.Message))
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { error = "Thiếu trường: port, to, hoặc message" });
            return;
        }

        // Kiểm tra cổng có tồn tại không
        var portExists = _vm.Ports.Any(p =>
            p.PortName.Equals(dto.Port, StringComparison.OrdinalIgnoreCase));

        if (!portExists)
        {
            resp.StatusCode = 404;
            await WriteJson(resp, new { error = $"Cổng {dto.Port} không tồn tại" });
            return;
        }

        // Gửi SMS thông qua ModemService
        _ = Task.Run(async () =>
        {
            try
            {
                await _vm.QueueSmsAsync(dto.Port, dto.To, dto.Message);
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(AppPaths.ForRuntimeFile("system_log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] [API] Gửi SMS lỗi: {ex.Message}\n");
            }
        });

        await WriteJson(resp, new { success = true, message = $"Đã lên lịch gửi SMS đến {dto.To} qua {dto.Port}" });
    }

    private static async Task WriteJson(HttpListenerResponse resp, object data)
    {
        resp.ContentType = "application/json; charset=utf-8";
        string json = JsonSerializer.Serialize(data,
            new JsonSerializerOptions { WriteIndented = true });
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
    }

    private class SendSmsDto
    {
        public string Port    { get; set; } = string.Empty;
        public string To      { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
