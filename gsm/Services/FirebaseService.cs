using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Services
{
    public class FirebaseService
    {
        private readonly MainViewModel _vm;
        private readonly HttpClient _sseClient;
        private readonly HttpClient _restClient;
        private readonly string _databaseUrl = "https://toolweb-c7702-default-rtdb.firebaseio.com/";

        public FirebaseService(MainViewModel vm)
        {
            _vm = vm;
            _sseClient = new HttpClient();
            _sseClient.Timeout = Timeout.InfiniteTimeSpan; // Ngăn không bị ngắt kết nối SSE tự động
            
            _restClient = new HttpClient();
        }

        public void Start()
        {
            // Bắt đầu lắng nghe lệnh gửi SMS từ web
            _ = ListenForCommandsAsync();

            // Đồng bộ định kỳ mỗi 2 giây
            _ = PeriodicSyncAsync();
        }

        private async Task PeriodicSyncAsync()
        {
            while (true)
            {
                SyncPorts();
                await Task.Delay(2000);
            }
        }

        private void SyncPorts()
        {
            try
            {
                // Dữ liệu cần thiết cho Web
                var portsData = _vm.Ports.ToDictionary(p => p.PortName, p => new {
                    id = p.PortName,
                    phone = p.PhoneNumber,
                    status = p.Status == "Đang hoạt động" ? "online" : "offline",
                    otp = string.IsNullOrEmpty(p.Otp) || p.Otp == "N/A" ? null : p.Otp,
                    network = p.NetworkProvider,
                    balance = p.Balance,
                    signal = p.SignalStrength
                });

                var json = JsonSerializer.Serialize(portsData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Dùng Task.Run để không block UI thread, dùng PUT để đè lại toàn bộ node ports
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _restClient.PutAsync($"{_databaseUrl}ports.json", content);
                    }
                    catch { /* Mất mạng tạm thời, bỏ qua */ }
                });

                var statusJson = JsonSerializer.Serialize(new { lastSync = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                var statusContent = new StringContent(statusJson, Encoding.UTF8, "application/json");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _restClient.PutAsync($"{_databaseUrl}server_status.json", statusContent);
                    }
                    catch { /* Mất mạng tạm thời, bỏ qua */ }
                });
            }
            catch { }
        }

        private async Task ListenForCommandsAsync()
        {
            while (true)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{_databaseUrl}commands.json");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var response = await _sseClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    while (true)
                    {
                        var readTask = reader.ReadLineAsync();
                        // Firebase gửi keep-alive mỗi ~30s. Nếu 45s không có tín hiệu, ngắt để nối lại.
                        var completedTask = await Task.WhenAny(readTask, Task.Delay(45000));
                        
                        if (completedTask != readTask)
                        {
                            throw new Exception("SSE Timeout (No keep-alive received)");
                        }
                        
                        string? line = await readTask;
                        if (line == null) break;

                        if (string.IsNullOrWhiteSpace(line)) continue;

                        if (line.StartsWith("data: "))
                        {
                            var dataJson = line.Substring(6);
                            if (dataJson != "null")
                            {
                                ProcessCommandData(dataJson);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Lỗi mạng hoặc Firebase bị gián đoạn, thử lại gần như ngay lập tức (chờ 1s để tránh vắt kiệt CPU nếu mất mạng hoàn toàn)
                    await Task.Delay(1000); 
                }
            }
        }

        private void ProcessCommandData(string dataJson)
        {
            try
            {
                // dataJson của Server-Sent Events Firebase: {"path":"/","data":{...}}
                using var doc = JsonDocument.Parse(dataJson);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("path", out var pathElement) && root.TryGetProperty("data", out var dataElement))
                {
                    string path = pathElement.GetString() ?? "";
                    
                    if (dataElement.ValueKind == JsonValueKind.Null) return; // Sự kiện xóa

                    if (path == "/")
                    {
                        // Toàn bộ commands hiện tại lúc mới kết nối
                        foreach (var prop in dataElement.EnumerateObject())
                        {
                            ExecuteAndRemoveCommand(prop.Name, prop.Value);
                        }
                    }
                    else
                    {
                        // Có command mới thêm vào, path có dạng "/-Nxxxx"
                        string cmdId = path.Trim('/');
                        ExecuteAndRemoveCommand(cmdId, dataElement);
                    }
                }
            }
            catch { }
        }

        private void ExecuteAndRemoveCommand(string cmdId, JsonElement cmdData)
        {
            try
            {
                if (cmdData.TryGetProperty("portId", out var portIdEl) &&
                    cmdData.TryGetProperty("recipient", out var recipientEl) &&
                    cmdData.TryGetProperty("content", out var contentEl))
                {
                    string portId = portIdEl.GetString() ?? "";
                    string recipient = recipientEl.GetString() ?? "";
                    string content = contentEl.GetString() ?? "";

                    // Xử lý gửi SMS ngầm, đợi kết quả rồi mới xóa khỏi Firebase
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (recipient == "USSD" && content == "BALANCE")
                            {
                                await _vm.CheckBalanceForPortAsync(portId);
                            }
                            else
                            {
                                await ExecuteSmsAsync(portId, recipient, content);
                            }
                        }
                        catch { }
                        finally
                        {
                            // Chỉ xóa khi đã xử lý xong (hoặc lỗi), tránh bị dính lệnh vĩnh viễn trên Firebase
                            await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
                        }
                    });
                }
            }
            catch { }
        }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSemaphores = new();

        private async Task ExecuteSmsAsync(string portId, string recipient, string content)
        {
            var sem = _smsSemaphores.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Đổi charset sang GSM để gửi text ASCII (tránh lỗi ZALO không phải Hex UCS2)
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSCS=\"GSM\"", 10000, true);
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSMP=17,167,0,0", 10000, true); // Sửa lỗi 305 Invalid text mode parameter

                // Cho phép chờ lâu hơn (45s) nếu modem đang bận chạy lệnh USSD hoặc kiểm tra TKC
                string result = await _vm.ModemService.SendSmsAsync(
                    portId,
                    recipient,
                    content,
                    timeoutMs: 45000
                );

                // Trả lại UCS2 để đọc tiếng Việt
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSCS=\"UCS2\"", 10000, true);
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSMP=17,167,0,8", 10000, true);
            }
            finally
            {
                sem.Release();
            }


        }
    }
}
