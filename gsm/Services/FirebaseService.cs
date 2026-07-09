using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Services
{
    public class FirebaseService : IDisposable
    {
        private readonly MainViewModel _vm;
        private readonly HttpClient _sseClient;
        private readonly HttpClient _restClient;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;
        private Task? _syncTask;
        private const long StaleRunningCommandMs = 10 * 60 * 1000;
        private string _databaseUrl 
        {
            get 
            {
                return GetDatabaseUrl();
            }
        }
        private static readonly string _machineId = Environment.MachineName.Replace(".", "_").Replace("$", "").Replace("#", "").Replace("[", "").Replace("]", "");

        private static string GetDatabaseUrl()
        {
            var url = SettingsService.Current.FirebaseUrl;
            if (string.IsNullOrEmpty(url)) url = "https://toolweb-c7702-default-rtdb.firebaseio.com/";
            if (!url.EndsWith("/")) url += "/";
            return url;
        }

        private static bool IsSpecificSmsError(string? errorMsg)
        {
            if (string.IsNullOrWhiteSpace(errorMsg)) return false;
            return errorMsg.Contains("Chọn sai đầu số")
                || errorMsg.Contains("SĐT đang không yêu cầu mã")
                || errorMsg.Contains("Hết tiền");
        }

        private static bool IsStaticWebStateInFlight(Dictionary<string, JsonElement>? state)
        {
            if (state == null) return false;

            if (state.TryGetValue("commandIds", out var commandIds)
                && commandIds.ValueKind == JsonValueKind.Array
                && commandIds.GetArrayLength() > 0)
            {
                return true;
            }

            if (!state.TryGetValue("commandStatus", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var status = statusElement.GetString();
            return status == "queued" || status == "running" || status == "sent";
        }

        private static async Task<bool> CanPatchStaticWebStateAsync(HttpClient client, string portId)
        {
            try
            {
                var json = await client.GetStringAsync($"/web_states/machines/{_machineId}/ports/{portId}.json");
                if (string.IsNullOrWhiteSpace(json) || json == "null") return false;

                var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                return IsStaticWebStateInFlight(state);
            }
            catch
            {
                return false;
            }
        }

        private static string? GetWebOtpValue(string? otp)
        {
            if (string.IsNullOrWhiteSpace(otp) || otp == "N/A") return null;
            return otp.All(char.IsDigit) ? otp : null;
        }

        public FirebaseService(MainViewModel vm)
        {
            _vm = vm;
            _sseClient = new HttpClient();
            _sseClient.Timeout = Timeout.InfiniteTimeSpan; // Ngăn không bị ngắt kết nối SSE tự động
            
            _restClient = new HttpClient();
        }

        public void Start()
        {
            if (_listenTask != null || _syncTask != null) return;

            // Xóa sạch trạng thái web_states của máy này khi bật toolgsm lên để hiển thị đầy đủ hết
            _ = Task.Run(async () =>
            {
                try
                {
                    if (SettingsService.Current.EnableWebNotification)
                    {
                        await _restClient.DeleteAsync($"{_databaseUrl}web_states/machines/{_machineId}.json");
                    }
                }
                catch { }
            });

            _ = Task.Run(CleanupStaleOwnedCommandsAsync);

            // Bắt đầu lắng nghe lệnh gửi SMS từ web
            _listenTask = ListenForCommandsAsync(_cts.Token);

            // Đồng bộ định kỳ mỗi 2 giây
            _syncTask = PeriodicSyncAsync(_cts.Token);
        }

        public void Stop()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }

        private static long GetUnixMilliseconds(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt64(out var value) => value,
                JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
                _ => 0
            };
        }

        private async Task CleanupStaleOwnedCommandsAsync()
        {
            if (!SettingsService.Current.EnableWebNotification) return;

            try
            {
                var json = await _restClient.GetStringAsync($"{_databaseUrl}commands.json");
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var command in doc.RootElement.EnumerateObject())
                {
                    var cmdId = command.Name;
                    var root = command.Value;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                    if (!string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)) continue;

                    var handledBy = root.TryGetProperty("handledBy", out var handledByEl) ? handledByEl.GetString() : null;
                    if (!string.Equals(handledBy, _machineId, StringComparison.OrdinalIgnoreCase)) continue;

                    var updatedAt = root.TryGetProperty("updatedAt", out var updatedAtEl) ? GetUnixMilliseconds(updatedAtEl) : 0;
                    if (updatedAt <= 0 || now - updatedAt < StaleRunningCommandMs) continue;

                    var portId = root.TryGetProperty("portId", out var portEl) ? portEl.GetString() ?? "" : "";
                    var recipient = root.TryGetProperty("recipient", out var recipientEl) ? recipientEl.GetString() ?? "" : "";
                    var content = root.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
                    var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "sms" : "sms";
                    var error = "Command running quá 10 phút do toolgsm tắt ngang, đã tự timeout";

                    await WriteCommandResultAsync(cmdId, portId, recipient, content, type, "failed", null, error);
                    await UpdateCommandStatusAsync(cmdId, "failed", error);
                    await UpdateWebCommandStateAsync(portId, cmdId, "failed", error);
                    await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
                    _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "failed", null, error);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _sseClient.Dispose();
            _restClient.Dispose();
            _cts.Dispose();
        }

        private async Task PeriodicSyncAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await SyncPortsAsync(ct);
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task SyncPortsAsync(CancellationToken ct)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                // Dữ liệu cần thiết cho Web
                var portsData = _vm.Ports.ToDictionary(p => p.PortName, p => new {
                    id = p.PortName,
                    phone = p.PhoneNumber,
                    status = p.Status == SimStatus.Active ? "online" : "offline",
                    otp = GetWebOtpValue(p.Otp),
                    network = p.NetworkProvider,
                    balance = p.Balance,
                    signal = p.SignalStrength,
                    timeoutCount = p.TimeoutCount,
                    smsErrorCount = p.SmsErrorCount,
                    reconnectCount = p.ReconnectCount,
                    lastSmsSentAt = p.LastSmsSentAt,
                    lastError = p.LastError
                });

                var json = JsonSerializer.Serialize(portsData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await _restClient.PutAsync($"{_databaseUrl}machines/{_machineId}/ports.json", content, ct);

                // Sử dụng Server Timestamp của Firebase để tránh lệch giờ giữa PC và Web
                var statusJson = "{\"lastSync\": {\".sv\": \"timestamp\"}}";
                var statusContent = new StringContent(statusJson, Encoding.UTF8, "application/json");
                await _restClient.PutAsync($"{_databaseUrl}machines/{_machineId}/server_status.json", statusContent, ct);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task ListenForCommandsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{_databaseUrl}commands.json");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var response = await _sseClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream);

                    while (!ct.IsCancellationRequested)
                    {
                        var readTask = reader.ReadLineAsync(ct).AsTask();
                        // Firebase gửi keep-alive mỗi ~30s. Nếu 45s không có tín hiệu, ngắt để nối lại.
                        var completedTask = await Task.WhenAny(readTask, Task.Delay(45000, ct));
                        
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Lỗi mạng hoặc Firebase bị gián đoạn, thử lại gần như ngay lập tức (chờ 1s để tránh vắt kiệt CPU nếu mất mạng hoàn toàn)
                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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

        private async Task UpdateCommandStatusAsync(string cmdId, string status, string? error = null)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(cmdId)) return;
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["handledBy"] = _machineId,
                    ["updatedAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };
                if (!string.IsNullOrWhiteSpace(error)) payload["error"] = error;

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _restClient.PatchAsync($"{_databaseUrl}commands/{cmdId}.json", content);
            }
            catch { }
        }

        private async Task<bool> TryClaimCommandAsync(string cmdId)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(cmdId)) return false;
            try
            {
                var commandUrl = $"{_databaseUrl}commands/{cmdId}.json";
                
                // 1. Kiểm tra trạng thái hiện tại (GET kèm ETag)
                var request = new HttpRequestMessage(HttpMethod.Get, commandUrl);
                request.Headers.Add("X-Firebase-ETag", "true");
                using var getResponse = await _restClient.SendAsync(request);
                
                var json = await getResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    return false; // Lệnh không tồn tại hoặc đã bị xóa
                }

                var etag = getResponse.Headers.ETag?.Tag;

                var node = JsonNode.Parse(json) as JsonObject;
                if (node == null) return false;

                var status = node.TryGetPropertyValue("status", out var statusNode) ? statusNode?.GetValue<string>() : "queued";
                if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Lệnh đã được chạy hoặc hoàn thành bởi luồng khác
                }

                if (node.TryGetPropertyValue("machineId", out var machineNode))
                {
                    var targetMachine = machineNode?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(targetMachine) && targetMachine != _machineId)
                    {
                        return false; // Lệnh dành cho máy khác
                    }
                }

                // 2. Cập nhật object hiện tại (Chuẩn bị PUT)
                node["status"] = "running";
                node["handledBy"] = _machineId;
                
                // Lưu ý: không dùng ServerValue timestamp được trong PUT gốc như PATCH unless mixed types.
                // Nhưng vì lấy node từ Firebase về nên ta cứ giữ nguyên, chỉ sửa trạng thái.
                var putJson = node.ToJsonString();
                using var putContent = new StringContent(putJson, Encoding.UTF8, "application/json");

                var putRequest = new HttpRequestMessage(HttpMethod.Put, commandUrl)
                {
                    Content = putContent
                };
                if (!string.IsNullOrEmpty(etag))
                {
                    putRequest.Headers.TryAddWithoutValidation("if-match", etag);
                }
                
                using var putResponse = await _restClient.SendAsync(putRequest);
                
                // 3. Nếu 412 Precondition Failed -> Firebase từ chối do có người đã sửa đổi (ETag không khớp)
                if (putResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                {
                    _vm.AddLog($"[FIREBASE] Đã nhường lệnh {cmdId} cho worker khác (Conflict ETag).", "INFO");
                    return false;
                }
                else if (!putResponse.IsSuccessStatusCode)
                {
                    _vm.AddLog($"[FIREBASE] Lỗi claim lệnh {cmdId}: PUT status code = {putResponse.StatusCode}", "ERROR");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _vm.AddLog($"[FIREBASE_CLAIM_ERROR] Lỗi nhận lệnh {cmdId}: {ex.Message}", "ERROR");
                return false;
            }
        }

        private async Task WriteCommandResultAsync(string cmdId, string portId, string recipient, string content, string type, string status, string? result = null, string? error = null)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(cmdId)) return;
            try
            {
                if (status == "failed" && !IsSpecificSmsError(error))
                {
                    var specificError = await TryGetSpecificWebErrorAsync(portId);
                    if (!string.IsNullOrWhiteSpace(specificError))
                    {
                        error = specificError;
                    }
                }

                var payload = new
                {
                    id = cmdId,
                    machineId = _machineId,
                    portId,
                    recipient,
                    content,
                    type,
                    status,
                    result,
                    error,
                    handledBy = _machineId,
                    updatedAt = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };

                var json = JsonSerializer.Serialize(payload);
                using var contentData = new StringContent(json, Encoding.UTF8, "application/json");
                await _restClient.PutAsync($"{_databaseUrl}command_results/{cmdId}.json", contentData);
            }
            catch { }
        }

        private async Task<string?> TryGetSpecificWebErrorAsync(string portId)
        {
            if (string.IsNullOrWhiteSpace(portId) || portId == "ALL") return null;
            try
            {
                var currentErrorJson = await _restClient.GetStringAsync($"{_databaseUrl}web_states/machines/{_machineId}/ports/{portId}/errorMsg.json");
                var currentError = JsonSerializer.Deserialize<string>(currentErrorJson);
                return IsSpecificSmsError(currentError) ? currentError : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task UpdateWebCommandStateAsync(string portId, string cmdId, string status, string? error = null)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(portId) || portId == "ALL") return;
            try
            {
                if (!await IsWebCommandCurrentAsync(portId, cmdId))
                {
                    return;
                }

                if (status == "failed" && !IsSpecificSmsError(error))
                {
                    error = await TryGetSpecificWebErrorAsync(portId) ?? error;
                }

                var payload = new Dictionary<string, object?>
                {
                    ["commandId"] = cmdId,
                    ["commandStatus"] = status,
                    ["updatedAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };

                if (status == "failed")
                {
                    payload["smsSent"] = false;
                    payload["errorMsg"] = error ?? "Lệnh thất bại";
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    payload["errorMsg"] = error;
                }
                else if (status is "sent" or "done" or "success")
                {
                    payload["errorMsg"] = null;
                }

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _restClient.PatchAsync($"{_databaseUrl}web_states/machines/{_machineId}/ports/{portId}.json", content);
            }
            catch { }
        }

        private async Task<bool> IsWebCommandCurrentAsync(string portId, string cmdId)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var json = await _restClient.GetStringAsync($"{_databaseUrl}web_states/machines/{_machineId}/ports/{portId}.json");
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("commandId", out var commandIdEl) &&
                            commandIdEl.GetString() == cmdId)
                        {
                            return true;
                        }

                        if (root.TryGetProperty("commandIds", out var commandIdsEl) &&
                            commandIdsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var idEl in commandIdsEl.EnumerateArray())
                            {
                                if (idEl.GetString() == cmdId) return true;
                            }
                        }

                        return false;
                    }

                    await Task.Delay(200);
                }
                catch
                {
                    if (attempt == 4) return false;
                    await Task.Delay(200);
                }
            }

            return false;
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
                    if (cmdData.TryGetProperty("status", out var statusEl))
                    {
                        var currentStatus = statusEl.GetString();
                        if (!string.IsNullOrWhiteSpace(currentStatus) && currentStatus != "queued")
                        {
                            return;
                        }
                    }

                    string portId = portIdEl.GetString() ?? "";
                    string recipient = recipientEl.GetString() ?? "";
                    string content = contentEl.GetString() ?? "";
                    string type = cmdData.TryGetProperty("type", out var typeEl)
                        ? typeEl.GetString() ?? (recipient == "USSD" ? "balance" : "sms")
                        : (recipient == "USSD" ? "balance" : recipient == "SYSTEM" ? "system" : "sms");
                    _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "queued");

                    Application.Current.Dispatcher.Invoke(() => 
                    {
                        _vm.SystemLogs.Insert(0, new LogMessage { Time = DateTime.Now.ToString("HH:mm:ss"), Level = "FIREBASE", Message = $"Nhận lệnh gửi SMS: Cổng={portId}, Gửi đến={recipient}, Nội dung={content}" });
                    });

                    // Xử lý gửi SMS ngầm, đợi kết quả rồi mới xóa khỏi Firebase
                    _ = Task.Run(async () =>
                    {
                        bool isClaimed = false;
                        string finalStatus = "done";
                        string? finalResult = null;
                        string? finalError = null;

                        try
                        {
                            if (!await TryClaimCommandAsync(cmdId))
                            {
                                return;
                            }
                            isClaimed = true;

                            // Reset OTP cũ khi bắt đầu nhận lệnh mới để tránh kịch bản đọc nhầm OTP cũ
                            var port = _vm.Ports.FirstOrDefault(p => p.PortName == portId);
                            if (port != null)
                            {
                                Application.Current.Dispatcher.Invoke(() => {
                                    port.Otp = "";
                                });
                            }

                            await UpdateWebCommandStateAsync(portId, cmdId, "running");
                            _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "running");

                            if (recipient == "USSD" && content == "BALANCE")
                            {
                                string ussdResult = await _vm.CheckBalanceForPortAsync(portId);
                                finalResult = ussdResult;
                                if (ussdResult.Contains("ERROR"))
                                {
                                    string err = GetHumanReadableError(ussdResult);
                                    finalStatus = "failed";
                                    finalError = err;
                                }
                                else 
                                {
                                    finalStatus = "done";
                                }
                            }
                            else if (recipient == "SYSTEM" && content == "CLEAR_OTP")
                            {
                                if (port != null)
                                {
                                    Application.Current.Dispatcher.Invoke(() => {
                                        port.Otp = "";
                                        port.LastError = "";
                                    });
                                }
                                finalResult = "CLEAR_OTP completed";
                                finalStatus = "done";
                            }
                            else if (recipient == "SYSTEM" && content == "REFRESH_PORT")
                            {
                                await _vm.RefreshPortAsync(portId);
                                finalResult = "REFRESH_PORT completed";
                                finalStatus = "done";
                            }
                            else if (recipient == "SYSTEM" && content == "REFRESH_ALL")
                            {
                                _vm.RefreshAllPorts();
                                finalResult = "REFRESH_ALL completed";
                                finalStatus = "done";
                            }
                            else
                            {
                                finalResult = await ExecuteSmsAsync(portId, recipient, content);
                                if (finalResult.Contains("ERROR") || finalResult.Contains("Timeout"))
                                {
                                    if (finalResult.Contains("Timeout"))
                                    {
                                        finalStatus = "maybe_sent";
                                    }
                                    else
                                    {
                                        finalStatus = "failed";
                                    }
                                    finalError = GetHumanReadableError(finalResult);
                                }
                                else
                                {
                                    finalStatus = "sent";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            finalStatus = "failed";
                            finalError = ex.Message;
                        }
                        finally
                        {
                            if (isClaimed)
                            {
                                _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, finalStatus, finalResult, finalError);
                                await WriteCommandResultAsync(cmdId, portId, recipient, content, type, finalStatus, finalResult, finalError);
                                await UpdateCommandStatusAsync(cmdId, finalStatus, finalError);
                                await UpdateWebCommandStateAsync(portId, cmdId, finalStatus, finalError);
                                // Chỉ xóa khi đã xử lý xong (hoặc lỗi), tránh bị dính lệnh vĩnh viễn trên Firebase
                                await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
                            }
                        }
                    });
                }
            }
            catch { }
        }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSemaphores = new();
        private readonly ConcurrentDictionary<string, DateTime> _recentSmsPayloads = new();

        private void CleanOldSmsPayloads()
        {
            var now = DateTime.Now;
            var keysToRemove = _recentSmsPayloads.Where(kv => (now - kv.Value).TotalMinutes > 3).Select(kv => kv.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _recentSmsPayloads.TryRemove(key, out _);
            }
        }

        private async Task<string> ExecuteSmsAsync(string portId, string recipient, string content)
        {
            CleanOldSmsPayloads();
            string payloadKey = $"{portId}_{recipient}_{content}";
            if (_recentSmsPayloads.TryGetValue(payloadKey, out var lastSentTime))
            {
                if ((DateTime.Now - lastSentTime).TotalMinutes <= 3)
                {
                    return "ERROR: Khóa chống gửi trùng (Idempotency). Tin nhắn giống hệt đã được gửi cách đây ít phút.";
                }
            }
            // Cập nhật thời gian gửi trước, sẽ gỡ bỏ nếu gặp lỗi không thực sự gửi đi
            _recentSmsPayloads[payloadKey] = DateTime.Now;

            var sem = _smsSemaphores.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            
            // Đánh dấu cổng này đang có SMS sắp gửi, USSD sẽ tự động nhường đường
            _vm.SmsInProgressPorts.TryAdd(portId, true);

            try
            {
                // Đổi charset sang GSM để gửi text ASCII (tránh lỗi ZALO không phải Hex UCS2)
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSCS=\"GSM\"", 10000, true);
                await _vm.ModemService.SendCommandAsync(portId, "AT+CSMP=17,167,0,0", 10000, true); // Sửa lỗi 305 Invalid text mode parameter

                string safeContent = _vm.RemoveDiacritics(content);

                string result = "";
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    result = await _vm.ModemService.SendSmsAsync(
                        portId,
                        recipient,
                        safeContent,
                        timeoutMs: 45000
                    );

                    // KHÔNG RETRY nếu lỗi Timeout để tránh gửi trùng SMS (anti-duplicate SMS)
                    // Chỉ retry khi chắc chắn lỗi là do cổng bận (Lock / Another command)
                    if (!result.Contains("ERROR") || (!result.Contains("Another command") && !result.Contains("waiting for lock")))
                    {
                        break;
                    }

                    if (attempt < 3)
                    {
                        await Task.Delay(2000); // Đợi 2s trước khi retry
                    }
                }

                if (result.Contains("ERROR"))
                {
                    // Nếu lỗi chỉ ra rằng SMS chắc chắn chưa được Modem gửi ra ngoài không trung
                    if (result.Contains("Port not open") || 
                        result.Contains("Timeout waiting for > prompt") || 
                        result.Contains("Another command") || 
                        result.Contains("waiting for lock"))
                    {
                        _recentSmsPayloads.TryRemove(payloadKey, out _);
                    }

                    string errorMsg = GetHumanReadableError(result);
                    _ = TelegramService.SendMessageAsync($"⚠️ <b>Lỗi Gửi SMS Từ {portId}</b>\n📱 Tới: {recipient}\n📝 Nội dung: {content}\n❌ Chi tiết: <code>{errorMsg}</code>");
                }

                return result;
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
            if (result.Contains("Timeout sending SMS payload")) return "Timeout (Không nhận được phản hồi) - Có thể đã gửi thành công. Không retry để tránh gửi trùng.";
            if (result.Contains("Timeout waiting for > prompt")) return "Timeout (Không gửi đi được) - Không thể nạp nội dung SMS.";
            if (result.Contains("Timeout")) return "Timeout (Không rõ trạng thái) - Có thể đã gửi. Không retry để tránh gửi trùng.";
            
            return result.Replace("ERROR: ", "").Replace("+CMS ERROR:", "Lỗi SMS:").Replace("+CME ERROR:", "Lỗi Modem:");
        }

        public static async Task SendSuccessToWebAsync(string portId)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri(GetDatabaseUrl());
                if (!await CanPatchStaticWebStateAsync(client, portId)) return;

                var json = JsonSerializer.Serialize(new
                {
                    smsSent = true,
                    commandStatus = "sent",
                    errorMsg = (string?)null,
                    updatedAt = new Dictionary<string, string> { [".sv"] = "timestamp" }
                });
                var contentData = new StringContent(json, Encoding.UTF8, "application/json");

                // Cập nhật lên Firebase ở node web_states của máy tính hiện tại
                await client.PatchAsync($"/web_states/machines/{_machineId}/ports/{portId}.json", contentData);
            }
            catch { }
        }

        public static async Task SendErrorToWebAsync(string portId, string errorMsg)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri(GetDatabaseUrl());
                if (!await CanPatchStaticWebStateAsync(client, portId)) return;

                var json = JsonSerializer.Serialize(new
                {
                    smsSent = false,
                    errorMsg,
                    updatedAt = new Dictionary<string, string> { [".sv"] = "timestamp" }
                });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                // Cập nhật thuộc tính errorMsg vào nhánh web_states của cổng bị lỗi (máy hiện tại)
                await client.PatchAsync($"/web_states/machines/{_machineId}/ports/{portId}.json", content);
            }
            catch { }
        }

        public static async Task ClearWebStateAsync(string portId)
        {
            if (!SettingsService.Current.EnableWebNotification) return;
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri(GetDatabaseUrl());
                if (await CanPatchStaticWebStateAsync(client, portId)) return;

                // Xóa toàn bộ trạng thái web_states của cổng (bao gồm cả hiddenOtp) để cổng mới mở lên hiển thị đầy đủ
                await client.DeleteAsync($"/web_states/machines/{_machineId}/ports/{portId}.json");
            }
            catch { }
        }
    }
}
