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
        private string _databaseUrl 
        {
            get 
            {
                var url = SettingsService.Current.FirebaseUrl;
                if (string.IsNullOrEmpty(url)) url = "https://toolweb-c7702-default-rtdb.firebaseio.com/";
                if (!url.EndsWith("/")) url += "/";
                return url;
            }
        }
        private static readonly string _machineId = Environment.MachineName.Replace(".", "_").Replace("$", "").Replace("#", "").Replace("[", "").Replace("]", "");

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
            if (!SettingsService.Current.EnableWebNotification) return;
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

                // Dùng Task.Run để không block UI thread, dùng PUT để đè lại toàn bộ node ports của máy này
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _restClient.PutAsync($"{_databaseUrl}machines/{_machineId}/ports.json", content);
                    }
                    catch { /* Mất mạng tạm thời, bỏ qua */ }
                });

                // Sử dụng Server Timestamp của Firebase để tránh lệch giờ giữa PC và Web
                var statusJson = "{\"lastSync\": {\".sv\": \"timestamp\"}}";
                var statusContent = new StringContent(statusJson, Encoding.UTF8, "application/json");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _restClient.PutAsync($"{_databaseUrl}machines/{_machineId}/server_status.json", statusContent);
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
                // Kiểm tra xem lệnh này có dành cho máy hiện tại không
                if (cmdData.TryGetProperty("machineId", out var machineIdEl))
                {
                    string targetMachine = machineIdEl.GetString() ?? "";
                    // Nếu có machineId mà không phải máy này thì bỏ qua (không xóa lệnh, để máy khác đọc)
                    if (!string.IsNullOrEmpty(targetMachine) && targetMachine != _machineId)
                    {
                        return;
                    }
                }

                if (cmdData.TryGetProperty("portId", out var portIdEl) &&
                    cmdData.TryGetProperty("recipient", out var recipientEl) &&
                    cmdData.TryGetProperty("content", out var contentEl))
                {
                    string portId = portIdEl.GetString() ?? "";
                    string recipient = recipientEl.GetString() ?? "";
                    string content = contentEl.GetString() ?? "";

                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        _vm.SystemLogs.Insert(0, new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = "FIREBASE", Message = $"Nhận lệnh gửi SMS: Cổng={portId}, Gửi đến={recipient}, Nội dung={content}" });
                    });

                    // Xử lý gửi SMS ngầm, đợi kết quả rồi mới xóa khỏi Firebase
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (recipient == "USSD" && content == "BALANCE")
                            {
                                await _vm.CheckBalanceForPortAsync(portId);
                            }
                            else if (recipient == "SYSTEM" && content == "REFRESH_PORT")
                            {
                                await _vm.RefreshPortAsync(portId);
                            }
                            else if (recipient == "SYSTEM" && content == "REFRESH_ALL")
                            {
                                _vm.RefreshAllPorts();
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
            
            // Đánh dấu cổng này đang có SMS sắp gửi, USSD sẽ tự động nhường đường
            _vm.SmsInProgressPorts.TryAdd(portId, true);

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

                if (result.Contains("ERROR"))
                {
                    string errorMsg = GetHumanReadableError(result);
                    await SendErrorToWebAsync(portId, errorMsg);
                    _ = TelegramService.SendMessageAsync($"⚠️ <b>Lỗi Gửi SMS Từ {portId}</b>\n📱 Tới: {recipient}\n📝 Nội dung: {content}\n❌ Chi tiết: <code>{errorMsg}</code>");
                }
                else
                {
                    await SendSuccessToWebAsync(portId);
                }
            }
            finally
            {
                // Xóa dấu hiệu SMS đang chờ sau khi xử lý xong
                _vm.SmsInProgressPorts.TryRemove(portId, out _);

                // QUAN TRỌNG: Luôn khôi phục về UCS2 dù gửi SMS thành công hay lỗi
                // Nếu không, modem sẽ kẹt ở GSM mode, không đọc được tiếng Việt/UCS2 nữa!
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSCS=\"UCS2\"", 10000, true);
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSMP=17,167,0,8", 10000, true);
                sem.Release();
            }
        }

        private string GetHumanReadableError(string result)
        {
            if (result.Contains("+CMS ERROR: 350")) return "Sim bị chặn SMS / DV không hỗ trợ (350)";
            if (result.Contains("+CMS ERROR: 500")) return "Lỗi thiết bị (500)";
            if (result.Contains("+CMS ERROR: 302")) return "Không được phép hoạt động (302)";
            if (result.Contains("+CMS ERROR: 331")) return "Mạng không khả dụng (331)";
            if (result.Contains("+CMS ERROR: 332")) return "Hết thời gian chờ mạng (332)";
            if (result.Contains("+CMS ERROR: 512")) return "Nhà mạng từ chối (Có thể hết tiền) (512)";
            if (result.Contains("+CMS ERROR: 2162")) return "Từ chối gửi SMS tới đầu số này (2162)";
            if (result.Contains("+CME ERROR: 10")) return "Không nhận diện được SIM (10)";
            if (result.Contains("+CME ERROR: 11")) return "Yêu cầu mã PIN (11)";
            if (result.Contains("+CME ERROR: 13")) return "Lỗi thẻ SIM (13)";
            if (result.Contains("+CME ERROR: 14")) return "SIM bị khóa cần PUK (14)";
            if (result.Contains("+CME ERROR: 32")) return "Mạng chỉ cho phép gọi khẩn cấp (32)";
            if (result.Contains("+CME ERROR: 58")) return "Mạng giới hạn truy cập (58)";
            if (result.Contains("+CME ERROR: 100")) return "Lỗi thiết bị không xác định (100)";
            if (result.Contains("Timeout sending SMS payload")) return "Sim không gửi tin nhắn đi được hoặc không nhận được tin nhắn phản hồi";
            if (result.Contains("Timeout")) return "Lỗi thiết bị không phản hồi (Timeout)";
            
            return result.Replace("ERROR: ", "").Replace("+CMS ERROR:", "Lỗi SMS:").Replace("+CME ERROR:", "Lỗi Modem:");
        }

        public static async Task SendSuccessToWebAsync(string portId)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri("https://toolweb-c7702-default-rtdb.firebaseio.com/");
                var json = JsonSerializer.Serialize(new
                {
                    smsSent = true,
                    errorMsg = (string?)null
                });
                var contentData = new StringContent(json, Encoding.UTF8, "application/json");

                // Cập nhật lên Firebase ở node web_states của máy tính hiện tại
                await client.PutAsync($"/web_states/machines/{_machineId}/ports/{portId}.json", contentData);
            }
            catch { }
        }

        public static async Task SendErrorToWebAsync(string portId, string errorMsg)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri("https://toolweb-c7702-default-rtdb.firebaseio.com/");
                var json = JsonSerializer.Serialize(errorMsg);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                // Cập nhật thuộc tính errorMsg vào nhánh web_states của cổng bị lỗi (máy hiện tại)
                await client.PutAsync($"https://toolweb-c7702-default-rtdb.firebaseio.com/web_states/machines/{_machineId}/ports/{portId}/errorMsg.json", content);
            }
            catch { }
        }
    }
}
