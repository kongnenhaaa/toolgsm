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
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = new string[0],
                    ContentRootPath = System.AppContext.BaseDirectory
                });
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
                    otp = string.IsNullOrEmpty(p.Otp) || p.Otp == "N/A" ? null : p.Otp,
                    network = p.NetworkProvider,
                    balance = p.Balance,
                    signal = p.SignalStrength
                });
            });

            // POST /api/sms — gửi SMS từ cổng chỉ định
            app.MapPost("/api/sms", async (SmsRequest req) =>
            {
                // Đổi charset sang GSM để gửi text ASCII (tránh lỗi ZALO không phải Hex UCS2)
                await _vm.ModemService.SendCommandAsync(req.PortId, "AT+CSCS=\"GSM\"", 5000, true);

                string result = await _vm.ModemService.SendSmsAsync(
                    req.PortId,
                    req.Recipient,
                    req.Content,
                    timeoutMs: 15000
                );

                // Trả lại UCS2 để đọc tiếng Việt
                await _vm.ModemService.SendCommandAsync(req.PortId, "AT+CSCS=\"UCS2\"", 5000, true);

                return result.Contains("ERROR")
                    ? Results.BadRequest(result)
                    : Results.Ok(new { success = true });
            });

            app.RunAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show("Lỗi khởi tạo API Server (Kestrel): " + t.Exception?.ToString(), "Lỗi", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    });
                }
            }); // không block UI thread
            }
            catch (System.Exception ex)
            {
                System.Windows.MessageBox.Show("Lỗi khởi tạo API Server: " + ex.ToString(), "Lỗi", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    public record SmsRequest(string PortId, string Recipient, string Content);
}
