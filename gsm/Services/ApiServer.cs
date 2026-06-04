using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using gsm.ViewModels;

namespace gsm.Services
{
    public class ApiServer
    {
        private readonly MainViewModel _vm;

        public ApiServer(MainViewModel vm) => _vm = vm;

        public void Start()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://localhost:5000");
            
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            var app = builder.Build();
            app.UseCors();

            // GET /api/ports — trả danh sách SIM
            app.MapGet("/api/ports", () =>
            {
                return _vm.Ports.Select(p => new {
                    id = p.PortName,
                    phone = p.PhoneNumber,
                    status = p.Status == "Đang hoạt động" ? "online" : "offline",
                    otp = p.Otp,
                    network = p.NetworkProvider,
                    balance = p.Balance,
                    signal = p.SignalStrength
                });
            });

            // POST /api/sms — gửi SMS từ cổng chỉ định
            app.MapPost("/api/sms", async (SmsRequest req) =>
            {
                string result = await _vm.ModemService.SendCommandAsync(
                    req.PortId,
                    $"AT+CMGS=\"{req.Recipient}\"\r{req.Content}\x1A",
                    timeoutMs: 15000
                );
                return result.Contains("ERROR")
                    ? Results.BadRequest(result)
                    : Results.Ok(new { success = true });
            });

            app.RunAsync(); // không block UI thread
        }
    }

    public record SmsRequest(string PortId, string Recipient, string Content);
}
