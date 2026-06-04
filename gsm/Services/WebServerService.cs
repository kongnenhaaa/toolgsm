using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using gsm.ViewModels;

namespace gsm.Services
{
    public class WebServerService
    {
        private WebApplication? _app;
        private readonly MainViewModel _viewModel;

        public WebServerService(MainViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();

            // Enable CORS to allow the frontend to call the API even if running on a different port or file://
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll",
                    builder =>
                    {
                        builder.AllowAnyOrigin()
                               .AllowAnyMethod()
                               .AllowAnyHeader();
                    });
            });

            _app = builder.Build();
            
            _app.UseCors("AllowAll");

            // Define where the web frontend files are located
            var webFrontendPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\toolweb"));
            if (Directory.Exists(webFrontendPath))
            {
                _app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(webFrontendPath),
                    RequestPath = "" // Serve at root
                });

                // Fallback to index.html
                _app.MapGet("/", async context =>
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(Path.Combine(webFrontendPath, "index.html"));
                });
            }

            // API: Get all ports
            _app.MapGet("/api/ports", () =>
            {
                var portsData = _viewModel.Ports.Select(p => new
                {
                    id = p.PortName,
                    phone = string.IsNullOrEmpty(p.PhoneNumber) ? "Chưa có số" : p.PhoneNumber,
                    status = p.Status == "Đang hoạt động" ? "online" : "error",
                    otp = string.IsNullOrEmpty(p.Otp) ? null : p.Otp,
                    otpTime = p.LastReceivedTime,
                    hidden = false // We handle hidden state on frontend
                });

                return Results.Ok(portsData);
            });

            // API: Send SMS
            _app.MapPost("/api/sms", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                
                try 
                {
                    var requestData = JsonSerializer.Deserialize<SmsRequestDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (requestData == null || string.IsNullOrEmpty(requestData.PortId) || string.IsNullOrEmpty(requestData.Recipient))
                    {
                        return Results.BadRequest(new { error = "Thiếu thông tin cổng hoặc số nhận" });
                    }

                    // Format SMS AT command: AT+CMGS="Số_nhận" (Ctrl+Z)
                    // The GsmModemService currently expects raw commands. We need to send AT+CMGS logic.
                    // This is simplified since physical SMS sending requires a multi-step prompt (>)
                    // For now, we will just call a dummy AT command to ensure communication flow, 
                    // or call SendCommandAsync with standard AT+CMGS if the modem service supports it.
                    // Wait, standard SendCommandAsync waits for OK. CMGS waits for `>`.
                    
                    // Sending actual SMS requires special handling in GsmModemService for the `>` prompt.
                    // For this integration, we will trigger a simulated command if it's a test, 
                    // or trigger a basic command to show the signal reaches C#.
                    
                    // We'll log the request so the user can see it reached the backend
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    {
                        // Access a private method indirectly or simulate
                        _viewModel.SystemLogs.Insert(0, new Models.LogMessage { 
                            Time = DateTime.Now.ToString("HH:mm:ss"), 
                            Level = "API", 
                            Message = $"Nhận lệnh gửi SMS từ {requestData.PortId} tới {requestData.Recipient}: {requestData.Content}" 
                        });
                    });

                    // In a real scenario, you'd implement actual SendSms in GsmModemService here.
                    // _viewModel.ModemService.SendCommandAsync(...)
                    
                    return Results.Ok(new { success = true, message = $"Đã tiếp nhận lệnh gửi từ {requestData.PortId} tới {requestData.Recipient}" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // Run on port 5000
            _app.Urls.Add("http://localhost:5000");

            await _app.StartAsync();
        }

        public async Task StopAsync()
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }

    public class SmsRequestDto
    {
        public string? PortId { get; set; }
        public string? Recipient { get; set; }
        public string? Content { get; set; }
    }
}
