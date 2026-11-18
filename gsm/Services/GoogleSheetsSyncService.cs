using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using gsm.Models;

namespace gsm.Services;

public sealed record GoogleSheetSyncColumn(string Key, string Header);

public sealed record GoogleSheetSyncResult(
    bool Success,
    int RowCount,
    string Message);

public sealed class GoogleSheetsSyncService : IDisposable
{
    private static readonly Regex SpreadsheetIdPattern = new(
        @"/spreadsheets/d/(?<id>[A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GoogleSheetsSyncService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(45) }, true)
    {
    }

    internal GoogleSheetsSyncService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<GoogleSheetSyncResult> TestConnectionAsync(
        string webAppUrl,
        string sharedSecret,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateWebAppUrl(webAppUrl, out Uri? endpoint, out string endpointError))
            return new(false, 0, endpointError);

        if (string.IsNullOrWhiteSpace(sharedSecret))
            return new(false, 0, "Chưa nhập mã bảo mật Google Sheets.");

        // spreadsheetId deliberately remains invalid. New Code.gs versions
        // answer action=ping immediately; older deployed versions pass the
        // secret check and then report the invalid ID, which still proves that
        // the URL and shared secret are connected without modifying a Sheet.
        string json = JsonSerializer.Serialize(new
        {
            secret = sharedSecret.Trim(),
            action = "ping",
            spreadsheetId = "test",
            machineId = machineId?.Trim() ?? string.Empty
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsync(
                endpoint,
                content,
                cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403)
                {
                    return new(
                        false,
                        0,
                        $"Apps Script từ chối quyền (HTTP {(int)response.StatusCode}). "
                        + "Hãy deploy Web app với quyền 'Bất kỳ ai' rồi dán lại URL /exec.");
                }
                return new(
                    false,
                    0,
                    $"Apps Script trả HTTP {(int)response.StatusCode}: "
                    + GetSafeResponseMessage(responseBody));
            }

            if (!TryReadGatewayResponse(
                    responseBody,
                    0,
                    out GoogleSheetSyncResult gatewayResult))
            {
                return new(
                    false,
                    0,
                    "Không nhận được phản hồi ToolGSM từ Apps Script. Hãy kiểm tra "
                    + "quyền Web app là 'Bất kỳ ai' và dùng đúng URL /exec.");
            }

            if (gatewayResult.Success)
            {
                return new(
                    true,
                    0,
                    "Kết nối Google Sheets thành công: URL và mã bảo mật hợp lệ.");
            }

            if (gatewayResult.Message.Contains(
                    "Spreadsheet ID không hợp lệ",
                    StringComparison.OrdinalIgnoreCase)
                || gatewayResult.Message.Contains(
                    "Không có cột dữ liệu",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    true,
                    0,
                    "Kết nối Google Sheets thành công: URL và mã bảo mật hợp lệ.");
            }

            return gatewayResult;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, 0, "Apps Script không phản hồi trong 45 giây.");
        }
        catch (Exception ex)
        {
            return new(false, 0, "Không thể kết nối Apps Script: " + ex.Message);
        }
    }

    public async Task<GoogleSheetSyncResult> SyncAsync(
        string webAppUrl,
        string sharedSecret,
        GoogleSheetTargetSettings target,
        IReadOnlyList<GoogleSheetSyncColumn> columns,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string machineId,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateWebAppUrl(webAppUrl, out Uri? endpoint, out string endpointError))
            return new(false, 0, endpointError);

        if (string.IsNullOrWhiteSpace(sharedSecret))
            return new(false, 0, "Chưa nhập mã bảo mật Google Sheets trong Cài đặt.");

        if (!TryExtractSpreadsheetId(
                target.SpreadsheetUrl,
                out string spreadsheetId))
        {
            return new(
                false,
                0,
                $"Link '{target.Name}' không phải URL Google Sheet hợp lệ.");
        }

        if (columns.Count == 0)
            return new(false, 0, "Không có cột dữ liệu để đồng bộ.");

        if (rows.Any(row => row.Count != columns.Count))
            return new(false, 0, "Số ô trong dữ liệu không khớp số cột.");

        string json = BuildRequestJson(
            sharedSecret,
            target,
            spreadsheetId,
            columns,
            rows,
            machineId);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsync(
                endpoint,
                content,
                cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403)
                {
                    return new(
                        false,
                        0,
                        $"Apps Script từ chối quyền (HTTP {(int)response.StatusCode}). "
                        + "Hãy deploy Web app với quyền 'Bất kỳ ai' rồi dán lại URL /exec.");
                }
                return new(
                    false,
                    0,
                    $"Google Sheets trả HTTP {(int)response.StatusCode}: "
                    + GetSafeResponseMessage(responseBody));
            }

            if (TryReadGatewayResponse(
                    responseBody,
                    rows.Count,
                    out GoogleSheetSyncResult gatewayResult))
            {
                return gatewayResult;
            }

            return new(
                false,
                0,
                "Apps Script trả phản hồi không hợp lệ. Hãy kiểm tra quyền triển khai "
                + "Web app là 'Anyone' và dùng đúng URL /exec.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, 0, "Google Sheets không phản hồi trong 45 giây.");
        }
        catch (Exception ex)
        {
            return new(false, 0, "Không thể đồng bộ Google Sheets: " + ex.Message);
        }
    }

    internal static bool TryExtractSpreadsheetId(string? spreadsheetUrl, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(spreadsheetUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !(uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".docs.google.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Match match = SpreadsheetIdPattern.Match(uri.AbsolutePath);
        if (!match.Success) return false;

        id = match.Groups["id"].Value;
        return id.Length >= 20;
    }

    internal static bool TryValidateWebAppUrl(
        string? webAppUrl,
        out Uri? endpoint,
        out string error)
    {
        endpoint = null;
        error = string.Empty;
        if (!Uri.TryCreate(webAppUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("script.google.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.TrimEnd('/').StartsWith(
                "/macros/s/",
                StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.TrimEnd('/').EndsWith(
                "/exec",
                StringComparison.OrdinalIgnoreCase))
        {
            error = "Apps Script Web App URL không hợp lệ (phải là link /macros/s/.../exec).";
            return false;
        }

        endpoint = uri;
        return true;
    }

    internal static string BuildRequestJson(
        string sharedSecret,
        GoogleSheetTargetSettings target,
        string spreadsheetId,
        IReadOnlyList<GoogleSheetSyncColumn> columns,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string machineId)
    {
        var payload = new
        {
            secret = sharedSecret,
            spreadsheetId,
            spreadsheetUrl = target.SpreadsheetUrl.Trim(),
            worksheetName = string.IsNullOrWhiteSpace(target.WorksheetName)
                ? "ToolGSM"
                : target.WorksheetName.Trim(),
            destinationName = target.Name.Trim(),
            machineId = machineId?.Trim() ?? string.Empty,
            syncedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            mode = "replace",
            columns = columns.Select(column => new
            {
                key = column.Key,
                header = column.Header
            }),
            rows
        };

        return JsonSerializer.Serialize(payload);
    }

    private static bool TryReadGatewayResponse(
        string responseBody,
        int requestedRowCount,
        out GoogleSheetSyncResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(responseBody)) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out JsonElement okElement)
                || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            bool ok = okElement.GetBoolean();
            int rowCount = root.TryGetProperty("rows", out JsonElement rowsElement)
                           && rowsElement.TryGetInt32(out int parsedRows)
                ? parsedRows
                : ok ? requestedRowCount : 0;
            string message = root.TryGetProperty("message", out JsonElement messageElement)
                             && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? string.Empty
                : ok
                    ? $"Đã đồng bộ {rowCount} dòng lên Google Sheets."
                    : "Apps Script từ chối yêu cầu đồng bộ.";
            result = new(ok, rowCount, message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetSafeResponseMessage(string responseBody)
    {
        string message = (responseBody ?? string.Empty).Trim();
        if (message.Length == 0) return "Không có nội dung phản hồi.";
        return message.Length <= 300 ? message : message[..300] + "…";
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
