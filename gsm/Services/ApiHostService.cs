using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Services;

public class ApiHostService
{
    private WebApplication? _app;
    private readonly MainViewModel _vm;
    private readonly IGsmModemService _modem;
    private readonly IFirebaseOtpService _firebase;
    private readonly INotifyService _notify;

    public ApiHostService(MainViewModel vm, IGsmModemService modem,
        IFirebaseOtpService firebase, INotifyService notify)
    {
        _vm = vm;
        _modem = modem;
        _firebase = firebase;
        _notify = notify;
    }

    public async Task StartAsync()
    {
        var cfg = SettingsService.Current;
        if (!cfg.EnableApiServer) return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{cfg.ApiServerPort}");
        // CORS cho toolweb
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        _app = builder.Build();
        _app.UseCors();

        // ---------- GET /api/ports ----------
        _app.MapGet("/api/ports", () =>
        {
            var ports = (_vm.Ports ?? Enumerable.Empty<SimPort>()).Select(p => new ApiPortDto
            {
                PortId = p.PortName,
                PortName = p.PortName,
                Status = p.Status,
                Phone = p.PhoneNumber,
                Operator = p.NetworkProvider,
                Balance = p.Balance,
                Imei = p.Imei,
                Ccid = p.Serial,
                Otp = p.Otp,
                LastContent = p.LastMessageContent,
                UpdatedAt = p.UpdatedAt
            }).ToList();

            return Results.Json(new ApiPortsResponse
            {
                MachineId = SettingsService.Current.MachineId,
                Ports = ports,
                Time = DateTime.UtcNow
            });
        });

        // ---------- POST /api/sms ----------
        _app.MapPost("/api/sms", async (ApiSmsRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PortId) ||
                string.IsNullOrWhiteSpace(req.Recipient) ||
                string.IsNullOrWhiteSpace(req.Content))
            {
                return Results.Json(new ApiSmsResponse
                {
                    Ok = false,
                    Error = "portId, recipient, content bắt buộc"
                }, statusCode: 400);
            }

            var commandId = req.CommandId ?? Guid.NewGuid().ToString("N")[..12];
            var port = ResolvePort(req.PortId);
            if (port == null)
            {
                return Results.Json(new ApiSmsResponse
                {
                    Ok = false,
                    CommandId = commandId,
                    Error = $"Port không tồn tại: {req.PortId}"
                }, statusCode: 404);
            }

            var simPort = _vm.Ports?.FirstOrDefault(p => p.PortName == port);
            if (simPort == null || simPort.Status != "Active")
            {
                return Results.Json(new ApiSmsResponse
                {
                    Ok = false,
                    CommandId = commandId,
                    Error = $"Port không khả dụng (không Active): {req.PortId}"
                }, statusCode: 404);
            }

            try
            {
                // Gửi SMS thật
                await _modem.SendSmsAsync(simPort.PortName, req.Recipient.Trim(), req.Content);

                PendingCommands[commandId] = new PendingCmd
                {
                    CommandId = commandId,
                    Port = port,
                    Recipient = req.Recipient,
                    CreatedAt = DateTime.UtcNow
                };

                return Results.Json(new ApiSmsResponse
                {
                    Ok = true,
                    CommandId = commandId,
                    Message = "Đã ra lệnh gửi SMS"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiSmsResponse
                {
                    Ok = false,
                    CommandId = commandId,
                    Error = ex.Message
                }, statusCode: 500);
            }
        });

        // Health
        _app.MapGet("/api/health", () => Results.Ok(new
        {
            ok = true,
            machineId = SettingsService.Current.MachineId,
            concurrentPortLimit = "dynamic",
            baselineConcurrentPorts = BackendConcurrency.BaselineConcurrentPorts,
            time = DateTime.UtcNow
        }));

        try
        {
            await _app.StartAsync();
            System.Diagnostics.Debug.WriteLine($"API listening http://0.0.0.0:{cfg.ApiServerPort}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Không thể khởi động API Server (Cổng {cfg.ApiServerPort} có thể đang bị chiếm): {ex.Message}");
            // Tuỳ chọn: Ghi log vào file hoặc UI
        }
    }

    string? ResolvePort(string portId)
    {
        var p = _vm.Ports?.FirstOrDefault(x =>
            x.PortName.Equals(portId, StringComparison.OrdinalIgnoreCase) ||
            x.PortName.EndsWith(portId, StringComparison.OrdinalIgnoreCase));
        return p?.PortName;
    }

    public static ConcurrentDictionary<string, PendingCmd> PendingCommands { get; } = new();

    public class PendingCmd
    {
        public string CommandId { get; set; } = "";
        public string Port { get; set; } = "";
        public string Recipient { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
