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
        private long _lastStaleCleanupAt;
        private readonly ConcurrentDictionary<string, byte> _scheduledCommands =
            new(StringComparer.OrdinalIgnoreCase);
        private const long StaleRunningCommandMs = 10 * 60 * 1000;
        private static readonly TimeSpan PendingOtpWaitTimeout = TimeSpan.FromMinutes(5);
        private string _databaseUrl 
        {
            get 
            {
                return GetDatabaseUrl();
            }
        }
        public const string DatabaseUrl = "https://toolweb-c7702-default-rtdb.firebaseio.com/";
        public static string MachineId => SanitizeFirebaseKey(
            string.IsNullOrWhiteSpace(SettingsService.Current.MachineId)
                ? Environment.MachineName
                : SettingsService.Current.MachineId);
        private static string _machineId => MachineId;

        private static string SanitizeFirebaseKey(string value) => value.Trim()
            .Replace(".", "_").Replace("$", "").Replace("#", "")
            .Replace("[", "").Replace("]", "").Replace("/", "_");

        private static string GetDatabaseUrl()
        {
            return DatabaseUrl;
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
                        // Giữ nguyên commandId/reservation để request web đang chờ không
                        // bị mất liên kết khi ToolGSM khởi động lại.
                        _vm.AddLog("[FIREBASE] Khởi động cầu nối Web; giữ nguyên các request đang chờ.", "INFO");
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

                    var resultPersisted = await WriteCommandResultAsync(cmdId, portId, recipient, content, type, "failed", null, error);
                    await UpdateCommandStatusAsync(cmdId, "failed", error);
                    await UpdateWebCommandStateAsync(portId, cmdId, "failed", error);
                    if (resultPersisted)
                        await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
                    _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "failed", null, error, "Firebase");
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
                // SSE nhận lệnh tức thời; polling bảo đảm không mất lệnh nếu stream
                // bị ngắt đúng lúc web vừa ghi command.
                await PollQueuedCommandsAsync(ct);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - Interlocked.Read(ref _lastStaleCleanupAt) >= 60_000
                    && Interlocked.Exchange(ref _lastStaleCleanupAt, now) <= now - 60_000)
                {
                    await CleanupStaleOwnedCommandsAsync();
                }
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
                    portId = p.PortName,
                    deviceName = p.DeviceName,
                    machineId = _machineId,
                    phone = p.PhoneNumber,
                    status = p.Status == SimStatus.Active ? "online" : "offline",
                    otp = GetWebOtpValue(p.Otp),
                    network = p.NetworkProvider,
                    balance = p.Balance,
                    signal = p.SignalStrength,
                    // Keep the exact carrier message in the periodic snapshot. SyncPortsAsync
                    // replaces the full ports node, so omitting these fields would erase lastSms.
                    smsContent = p.LastMessageContent,
                    smsSender = p.Sender,
                    smsReceivedAt = p.LastReceivedTime,
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
                var statusJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["machineId"] = _machineId,
                    ["deviceName"] = Environment.MachineName,
                    ["lastSync"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
                });
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
                    response.EnsureSuccessStatusCode();
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
                catch (Exception ex)
                {
                    _vm.AddLog($"[FIREBASE_LISTEN_ERROR] {ex.Message}; sẽ kết nối lại.", "WARN");
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
                        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length == 0) return;
                        string cmdId = segments[0];
                        if (segments.Length == 1 && dataElement.ValueKind == JsonValueKind.Object)
                            ExecuteAndRemoveCommand(cmdId, dataElement);
                        else
                            _ = Task.Run(() => FetchAndProcessCommandAsync(cmdId, _cts.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.AddLog($"[FIREBASE_EVENT_ERROR] Không đọc được sự kiện command: {ex.Message}", "WARN");
            }
        }

        private async Task FetchAndProcessCommandAsync(string cmdId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cmdId) || ct.IsCancellationRequested) return;
            try
            {
                using var response = await _restClient.GetAsync($"{_databaseUrl}commands/{cmdId}.json", ct);
                if (!response.IsSuccessStatusCode) return;
                var json = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    ExecuteAndRemoveCommand(cmdId, doc.RootElement.Clone());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _vm.AddLog($"[FIREBASE_COMMAND_FETCH_ERROR] {cmdId}: {ex.Message}", "WARN");
            }
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
                    if (!string.IsNullOrWhiteSpace(targetMachine)
                        && !string.Equals(targetMachine.Trim(), _machineId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false; // Lệnh dành cho máy khác
                    }
                }

                if (!await ReservationAllowsCommandAsync(node, cmdId)) return false;

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

                // Reservation and command live on different RTDB paths. Recheck
                // after the ETag claim so a concurrent web cancel/fence wins
                // before any physical SMS/USSD/CLEAR operation can start.
                if (!await ReservationAllowsCommandAsync(node, cmdId))
                {
                    await UpdateCommandStatusAsync(
                        cmdId, "canceled", "Reservation changed before execution");
                    await _restClient.DeleteAsync(commandUrl);
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

        private async Task<bool> ReservationAllowsCommandAsync(JsonObject command, string cmdId)
        {
            var requestedPort = command.TryGetPropertyValue("portId", out var portNode)
                ? portNode?.GetValue<string>()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(requestedPort)
                || string.Equals(requestedPort, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var requestedMachine = command.TryGetPropertyValue("machineId", out var machineNode)
                ? machineNode?.GetValue<string>()?.Trim() : _machineId;
            var stateJson = await _restClient.GetStringAsync(
                $"{_databaseUrl}web_states/machines/{requestedMachine ?? _machineId}/ports/{requestedPort}.json");
            if (string.IsNullOrWhiteSpace(stateJson) || stateJson == "null")
            {
                return false;
            }

            var stateNode = JsonNode.Parse(stateJson) as JsonObject;
            var reservationId = stateNode?["reservationId"]?.GetValue<string>();
            var expiresAt = stateNode?["reservationExpiresAt"]?.GetValue<long?>() ?? 0;
            var reservationIsActive = expiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Every command targeting a physical COM must hold the exact active
            // reservation. requestSource is caller-controlled and cannot safely
            // decide whether the concurrency protocol applies.
            return reservationIsActive
                && string.Equals(reservationId, cmdId, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> WriteCommandResultAsync(string cmdId, string portId, string recipient, string content, string type, string status, string? result = null, string? error = null, string? smsContent = null)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(cmdId)) return false;
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
                    smsContent,
                    error,
                    handledBy = _machineId,
                    updatedAt = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };

                var json = JsonSerializer.Serialize(payload);
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    using var contentData = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _restClient.PutAsync($"{_databaseUrl}command_results/{cmdId}.json", contentData);
                    if (response.IsSuccessStatusCode) return true;
                    if (attempt < 5) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
                }
                _vm.AddLog($"[FIREBASE_RESULT_ERROR] Không lưu được kết quả lệnh {cmdId} sau 5 lần thử.", "ERROR");
            }
            catch (Exception ex)
            {
                _vm.AddLog($"[FIREBASE_RESULT_ERROR] {cmdId}: {ex.Message}", "WARN");
            }
            return false;
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

        private async Task UpdateWebCommandStateAsync(string portId, string cmdId, string status, string? error = null, string? smsContent = null)
        {
            if (!SettingsService.Current.EnableWebNotification || string.IsNullOrWhiteSpace(portId) || portId == "ALL") return;
            try
            {
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

                if (!string.IsNullOrWhiteSpace(smsContent))
                {
                    payload["smsContent"] = smsContent;
                    payload["smsContentAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" };
                }

                await PatchWebCommandStateIfCurrentAsync(portId, cmdId, payload);
            }
            catch { }
        }

        private static bool IsWebCommandCurrent(JsonObject state, string cmdId)
        {
            if (state["commandId"]?.GetValue<string>() == cmdId
                || state["reservationId"]?.GetValue<string>() == cmdId)
            {
                return true;
            }

            if (state["commandIds"] is JsonArray commandIds)
            {
                foreach (var idNode in commandIds)
                {
                    if (idNode?.GetValue<string>() == cmdId) return true;
                }
            }

            return false;
        }

        private async Task<bool> PatchWebCommandStateIfCurrentAsync(
            string portId,
            string cmdId,
            IReadOnlyDictionary<string, object?> payload)
        {
            var stateUrl = $"{_databaseUrl}web_states/machines/{_machineId}/ports/{portId}.json";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, stateUrl);
                getRequest.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
                using var getResponse = await _restClient.SendAsync(getRequest);
                if (!getResponse.IsSuccessStatusCode)
                {
                    await Task.Delay(200);
                    continue;
                }

                var etag = getResponse.Headers.ETag?.Tag;
                var stateJson = await getResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(etag)
                    || string.IsNullOrWhiteSpace(stateJson)
                    || stateJson == "null"
                    || JsonNode.Parse(stateJson) is not JsonObject state
                    || !IsWebCommandCurrent(state, cmdId))
                {
                    return false;
                }

                foreach (var item in payload)
                {
                    state[item.Key] = item.Value == null
                        ? null
                        : JsonSerializer.SerializeToNode(item.Value);
                }

                using var putRequest = new HttpRequestMessage(HttpMethod.Put, stateUrl);
                putRequest.Headers.TryAddWithoutValidation("if-match", etag);
                putRequest.Content = new StringContent(
                    state.ToJsonString(), Encoding.UTF8, "application/json");
                using var putResponse = await _restClient.SendAsync(putRequest);
                if (putResponse.IsSuccessStatusCode) return true;
                if (putResponse.StatusCode != HttpStatusCode.PreconditionFailed) return false;
                await Task.Delay(100 * (attempt + 1));
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
                    if (!string.IsNullOrEmpty(targetMachine)
                        && !string.Equals(targetMachine.Trim(), _machineId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                JsonElement portIdEl = default;
                JsonElement recipientEl = default;
                JsonElement contentEl = default;
                bool hasRequiredFields = cmdData.TryGetProperty("portId", out portIdEl) &&
                    cmdData.TryGetProperty("recipient", out recipientEl) &&
                    cmdData.TryGetProperty("content", out contentEl) &&
                    portIdEl.ValueKind == JsonValueKind.String &&
                    recipientEl.ValueKind == JsonValueKind.String &&
                    contentEl.ValueKind == JsonValueKind.String;
                if (!hasRequiredFields)
                {
                    _ = Task.Run(() => RejectMalformedCommandAsync(cmdId));
                    return;
                }
                if (hasRequiredFields)
                {
                    if (cmdData.TryGetProperty("status", out var statusEl))
                    {
                        var currentStatus = statusEl.GetString();
                        if (!string.IsNullOrWhiteSpace(currentStatus) && currentStatus != "queued")
                        {
                            return;
                        }
                    }

                    string requestedPortId = (portIdEl.GetString() ?? "").Trim();
                    var port = _vm.Ports.FirstOrDefault(p => string.Equals(p.PortName, requestedPortId, StringComparison.OrdinalIgnoreCase));
                    string portId = port?.PortName ?? requestedPortId;
                    string recipient = (recipientEl.GetString() ?? "").Trim();
                    string content = contentEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(portId) || string.IsNullOrWhiteSpace(recipient)) return;
                    string type = cmdData.TryGetProperty("type", out var typeEl)
                        ? typeEl.GetString() ?? (recipient == "USSD" ? "balance" : "sms")
                        : (recipient == "USSD" ? "balance" : recipient == "SYSTEM" ? "system" : "sms");
                    if (!_scheduledCommands.TryAdd(cmdId, 0)) return;
                    _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "queued", source: "Firebase");

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
                            port = _vm.Ports.FirstOrDefault(p => string.Equals(p.PortName, portId, StringComparison.OrdinalIgnoreCase));

                            await UpdateWebCommandStateAsync(portId, cmdId, "running");
                            _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, "running", source: "Firebase");

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
                                _pendingOtpCommands.TryRemove(portId, out _);
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
                                var pendingOtp = new PendingWebOtpCommand(
                                    cmdId, portId, recipient, content, DateTime.UtcNow);
                                _pendingOtpCommands[portId] = pendingOtp;
                                finalResult = await ExecuteSmsAsync(portId, recipient, content);
                                SmsSubmitDisposition disposition =
                                    GsmSmsService.ClassifySubmitResult(finalResult);
                                if (disposition == SmsSubmitDisposition.PayloadSubmittedUncertain)
                                {
                                    finalStatus = "maybe_sent";
                                    RefreshPendingOtpWaitStart(portId, ref pendingOtp);
                                    finalError = GetHumanReadableError(finalResult);
                                }
                                else if (disposition == SmsSubmitDisposition.Confirmed)
                                {
                                    finalStatus = "sent";
                                    RefreshPendingOtpWaitStart(portId, ref pendingOtp);
                                }
                                else
                                {
                                    finalStatus = "failed";
                                    finalError = GetHumanReadableError(finalResult);
                                    _pendingOtpCommands.TryRemove(
                                        new KeyValuePair<string, PendingWebOtpCommand>(portId, pendingOtp));
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
                                var resultSemaphore = _commandResultSemaphores.GetOrAdd(
                                    cmdId, _ => new SemaphoreSlim(1, 1));
                                await resultSemaphore.WaitAsync();
                                try
                                {
                                    // An OTP can arrive before ExecuteSmsAsync finishes. Never let
                                    // the ordinary "sent" result overwrite the final OTP result.
                                    bool resultPersisted = _otpCompletedCommands.ContainsKey(cmdId);
                                    if (!resultPersisted)
                                    {
                                        _vm.UpsertCommandQueue(cmdId, portId, type, recipient, content, finalStatus, finalResult, finalError, "Firebase");
                                        resultPersisted = await WriteCommandResultAsync(cmdId, portId, recipient, content, type, finalStatus, finalResult, finalError);
                                        await UpdateCommandStatusAsync(cmdId, finalStatus, finalError);
                                        await UpdateWebCommandStateAsync(portId, cmdId, finalStatus, finalError);
                                    }
                                // Chỉ xóa khi đã xử lý xong (hoặc lỗi), tránh bị dính lệnh vĩnh viễn trên Firebase
                                    if (resultPersisted)
                                    {
                                        using var deleteResponse = await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
                                        if (!deleteResponse.IsSuccessStatusCode)
                                            _vm.AddLog($"[FIREBASE] Chưa xóa được command {cmdId}: HTTP {(int)deleteResponse.StatusCode}", "WARN");
                                    }
                                }
                                finally
                                {
                                    resultSemaphore.Release();
                                }
                            }
                            _scheduledCommands.TryRemove(cmdId, out _);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _scheduledCommands.TryRemove(cmdId, out _);
                _vm.AddLog($"[FIREBASE_COMMAND_ERROR] Lệnh {cmdId}: {ex.Message}", "ERROR");
            }
        }

        private async Task RejectMalformedCommandAsync(string cmdId)
        {
            if (!_scheduledCommands.TryAdd(cmdId, 0)) return;
            try
            {
                var persisted = await WriteCommandResultAsync(
                    cmdId, "", "", "", "sms", "failed", null,
                    "Malformed command: required portId/recipient/content fields are missing.");
                await UpdateCommandStatusAsync(cmdId, "failed", "Malformed command");
                if (persisted)
                    await _restClient.DeleteAsync($"{_databaseUrl}commands/{cmdId}.json");
            }
            finally
            {
                _scheduledCommands.TryRemove(cmdId, out _);
            }
        }

        private sealed record PendingWebOtpCommand(
            string CommandId,
            string PortId,
            string Recipient,
            string Content,
            DateTime CreatedAtUtc);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _smsSemaphores = new();
        private readonly ConcurrentDictionary<string, PendingWebOtpCommand> _pendingOtpCommands =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _commandResultSemaphores =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _otpCompletedCommands =
            new(StringComparer.OrdinalIgnoreCase);

        private void RefreshPendingOtpWaitStart(string portId, ref PendingWebOtpCommand pending)
        {
            var refreshed = pending with { CreatedAtUtc = DateTime.UtcNow };
            // OTP có thể về ngay khi ExecuteSmsAsync chưa trả kết quả. Chỉ cập nhật nếu
            // pending cũ vẫn còn; tuyệt đối không tạo lại request đã nhận OTP xong.
            if (_pendingOtpCommands.TryUpdate(portId, refreshed, pending))
                pending = refreshed;
        }

        public bool HasPendingOtpCommand(string portId) =>
            !string.IsNullOrWhiteSpace(portId) && _pendingOtpCommands.ContainsKey(portId);

        public async Task MarkPendingCommandFailedAsync(
            string portId,
            string error,
            string? carrierResponse = null)
        {
            if (string.IsNullOrWhiteSpace(portId)
                || !_pendingOtpCommands.TryGetValue(portId, out var pending))
            {
                await SendErrorToWebAsync(portId, error);
                return;
            }

            var resultSemaphore = _commandResultSemaphores.GetOrAdd(
                pending.CommandId, _ => new SemaphoreSlim(1, 1));
            await resultSemaphore.WaitAsync();
            try
            {
                if (!_pendingOtpCommands.TryGetValue(portId, out var current)
                    || !string.Equals(current.CommandId, pending.CommandId, StringComparison.OrdinalIgnoreCase))
                    return;

                // Carrier responses arrive after +CMGS and are the real business result.
                // Prevent the ordinary "sent" completion from overwriting this failure.
                _otpCompletedCommands[pending.CommandId] = 0;
                _vm.UpsertCommandQueue(
                    pending.CommandId, portId, "sms", pending.Recipient, pending.Content,
                    "failed", carrierResponse, error, "Firebase");

                await WriteCommandResultAsync(
                    pending.CommandId, portId, pending.Recipient, pending.Content,
                    "sms", "failed", carrierResponse, error, carrierResponse);
                await UpdateCommandStatusAsync(pending.CommandId, "failed", error);
                await UpdateWebCommandStateAsync(portId, pending.CommandId, "failed", error, carrierResponse);

                _pendingOtpCommands.TryRemove(
                    new KeyValuePair<string, PendingWebOtpCommand>(portId, pending));
            }
            finally
            {
                resultSemaphore.Release();
            }
        }

        private async Task<string> ExecuteSmsAsync(string portId, string recipient, string content)
        {
            var sem = _smsSemaphores.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            
            // Đánh dấu cổng này đang có SMS sắp gửi, USSD sẽ tự động nhường đường
            _vm.SmsInProgressPorts.TryAdd(portId, true);

            try
            {
                string result = "";
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    // Dùng đúng pipeline gửi thủ công: kiểm tra session SIM hiện tại,
                    // chờ cooldown của cổng và để GsmSmsService khóa/configure modem.
                    // Trước đây web tự gửi AT+CSMP rồi gọi thẳng _smsService, có thể
                    // chạy giữa lúc cổng đang chuyển trạng thái và báo sent giả.
                    result = await _vm.QueueSmsAsync(portId, recipient, content);

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
                    string errorMsg = GetHumanReadableError(result);
                    var fbCfg = SettingsService.Current;
                    if (fbCfg != null && fbCfg.TelegramOnError &&
                        !string.IsNullOrWhiteSpace(fbCfg.TelegramBotToken) &&
                        !string.IsNullOrWhiteSpace(fbCfg.TelegramChatId))
                    {
                        string errText = $"⚠️ <b>Lỗi Gửi SMS Từ {portId}</b>\n📱 Tới: {recipient}\n📝 Nội dung: {content}\n❌ Chi tiết: <code>{errorMsg}</code>";
                        _ = TelegramService.SendMessageAsync(errText); // TelegramService tự lấy token từ Settings
                    }
                }

                return result;
            }
            finally
            {
                // Xóa dấu hiệu SMS đang chờ sau khi xử lý xong
                _vm.SmsInProgressPorts.TryRemove(portId, out _);

                // QUAN TRỌNG: Luôn khôi phục về UCS2 dù gửi SMS thành công hay lỗi
                // Nếu không, modem sẽ kẹt ở GSM mode, không đọc được tiếng Việt/UCS2 nữa!
                sem.Release();
            }
        }

        public async Task PublishOtpForPendingCommandAsync(
            string portId, string otp, string smsContent, string sender)
        {
            if (string.IsNullOrWhiteSpace(portId)
                || string.IsNullOrWhiteSpace(otp)
                || otp == "N/A") return;

            if (!_pendingOtpCommands.TryGetValue(portId, out var pending)) return;
            if (DateTime.UtcNow - pending.CreatedAtUtc > PendingOtpWaitTimeout)
            {
                _pendingOtpCommands.TryRemove(
                    new KeyValuePair<string, PendingWebOtpCommand>(portId, pending));
                return;
            }

            try
            {
                var resultSemaphore = _commandResultSemaphores.GetOrAdd(
                    pending.CommandId, _ => new SemaphoreSlim(1, 1));
                await resultSemaphore.WaitAsync();
                try
                {
                var resultPayload = new Dictionary<string, object?>
                {
                    ["id"] = pending.CommandId,
                    ["machineId"] = _machineId,
                    ["portId"] = portId,
                    ["recipient"] = pending.Recipient,
                    ["content"] = pending.Content,
                    ["type"] = "sms",
                    ["status"] = "otp_received",
                    ["result"] = "OTP received",
                    ["error"] = null,
                    ["otp"] = otp,
                    ["otpSender"] = sender,
                    ["otpContent"] = smsContent,
                    ["handledBy"] = _machineId,
                    ["updatedAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };
                string resultJson = JsonSerializer.Serialize(resultPayload);
                bool resultPersisted = false;
                int attempt = 0;

                // The SMS has already reached this machine, so a temporary
                // Firebase outage must not lose the correlated OTP. Keep the
                // command semaphore while retrying so the ordinary "sent"
                // result cannot overwrite otp_received.
                while (DateTime.UtcNow - pending.CreatedAtUtc <= PendingOtpWaitTimeout)
                {
                    if (!_pendingOtpCommands.TryGetValue(portId, out var current)
                        || !string.Equals(current.CommandId, pending.CommandId, StringComparison.OrdinalIgnoreCase))
                        return;

                    attempt++;
                    try
                    {
                        using var resultContent = new StringContent(
                            resultJson, Encoding.UTF8, "application/json");
                        using var resultResponse = await _restClient.PutAsync(
                            $"{_databaseUrl}command_results/{pending.CommandId}.json", resultContent);
                        if (resultResponse.IsSuccessStatusCode)
                        {
                            resultPersisted = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 1 || attempt % 10 == 0)
                            _vm.AddLog($"[{portId}] [FIREBASE_OTP_RETRY] {ex.Message}", "WARN");
                    }

                    await Task.Delay(Math.Min(5000, 250 * attempt));
                }

                if (!resultPersisted)
                {
                    _vm.AddLog($"[{portId}] [FIREBASE_OTP_RESULT_ERROR] Không lưu được OTP cho {pending.CommandId} trong thời gian chờ.", "ERROR");
                    return;
                }

                _otpCompletedCommands[pending.CommandId] = 0;

                var statePayload = new Dictionary<string, object?>
                {
                    ["commandId"] = pending.CommandId,
                    ["commandStatus"] = "otp_received",
                    ["smsSent"] = false,
                    ["otp"] = otp,
                    ["smsContent"] = smsContent,
                    ["smsSender"] = sender,
                    ["errorMsg"] = null,
                    ["otpReceivedAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" },
                    ["updatedAt"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
                };
                await PatchWebCommandStateIfCurrentAsync(
                    portId, pending.CommandId, statePayload);

                _vm.UpsertCommandQueue(
                    pending.CommandId, portId, "sms", pending.Recipient, pending.Content,
                    "otp_received", "OTP received", source: "Firebase");
                _pendingOtpCommands.TryRemove(
                    new KeyValuePair<string, PendingWebOtpCommand>(portId, pending));
                }
                finally
                {
                    resultSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _vm.AddLog($"[{portId}] [FIREBASE_OTP_RESULT_ERROR] {ex.Message}", "WARN");
            }
        }

        private async Task PollQueuedCommandsAsync(CancellationToken ct)
        {
            try
            {
                using var response = await _restClient.GetAsync($"{_databaseUrl}commands.json", ct);
                if (!response.IsSuccessStatusCode)
                {
                    _vm.AddLog($"[FIREBASE_POLL] Không đọc được commands: HTTP {(int)response.StatusCode}", "WARN");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                foreach (var command in doc.RootElement.EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();
                    ExecuteAndRemoveCommand(command.Name, command.Value);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _vm.AddLog($"[FIREBASE_POLL_ERROR] {ex.Message}", "WARN");
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

                var currentJson = await client.GetStringAsync($"/web_states/machines/{_machineId}/ports/{portId}.json");
                if (string.IsNullOrWhiteSpace(currentJson) || currentJson == "null") return;

                // Reconnect chỉ được dọn lỗi tạm thời; không xóa OTP/nội dung SMS
                // đang được web giữ lại.
                using var currentDoc = JsonDocument.Parse(currentJson);
                var root = currentDoc.RootElement;
                var keepOtp = root.TryGetProperty("otp", out var otpValue)
                    && otpValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(otpValue.GetString());
                var keepSms = root.TryGetProperty("smsContent", out var smsValue)
                    && smsValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(smsValue.GetString());
                if (keepOtp || keepSms)
                {
                    using var clearError = new StringContent(
                        JsonSerializer.Serialize(new { errorMsg = (string?)null }),
                        Encoding.UTF8,
                        "application/json");
                    await client.PatchAsync($"/web_states/machines/{_machineId}/ports/{portId}.json", clearError);
                    return;
                }

                await client.DeleteAsync($"/web_states/machines/{_machineId}/ports/{portId}.json");
            }
            catch { }
        }
    }
}
