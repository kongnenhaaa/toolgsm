using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

public interface IFirebaseOtpService
{
    Task WritePortOtpAsync(string machineId, string portId, string? otp, string? content, string? phone);
    Task WritePortSnapshotAsync(string machineId, ApiPortDto port);
}

public class FirebaseOtpService : IFirebaseOtpService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    
    private string DbUrl => FirebaseService.DatabaseUrl.TrimEnd('/');

    string Path(string relative)
    {
        var url = $"{DbUrl}/{relative.TrimStart('/')}.json";
        return url;
    }

    public async Task WritePortOtpAsync(string machineId, string portId, string? otp, string? content, string? phone)
    {
        if (string.IsNullOrEmpty(DbUrl) || string.IsNullOrEmpty(portId))
            return;

        try
        {
            machineId = await FirebaseService.EnsureUniqueMachineIdAsync();
            // Path toolweb đang listen: machines/{machineId}/ports/{portId}/otp
            var otpUrl = Path($"machines/{machineId}/ports/{portId}/otp");
            var body = System.Text.Json.JsonSerializer.Serialize(otp ?? "");
            await _http.PutAsync(otpUrl, new StringContent(body, Encoding.UTF8, "application/json"));

            // (khuyến nghị) ghi thêm meta
            var meta = new
            {
                otp = otp ?? "",
                content = content ?? "",
                phone = phone ?? "",
                updatedAt = DateTime.UtcNow.ToString("o")
            };
            var metaUrl = Path($"machines/{machineId}/ports/{portId}/lastSms");
            var metaJson = System.Text.Json.JsonSerializer.Serialize(meta);
            await _http.PutAsync(metaUrl, new StringContent(metaJson, Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Ignore network errors
        }
    }

    public async Task WritePortSnapshotAsync(string machineId, ApiPortDto port)
    {
        if (string.IsNullOrEmpty(DbUrl)) return;
        try
        {
            machineId = await FirebaseService.EnsureUniqueMachineIdAsync();
            var url = Path($"machines/{machineId}/ports/{port.PortId}");
            // SyncPortsAsync là writer chính và dùng schema camelCase. Chỉ PATCH
            // các giá trị thực có ở snapshot tức thời để không xóa/đổi casing
            // status, balance, smsContent... trong lúc sync 2 giây đang chạy.
            var payload = new Dictionary<string, object?>
            {
                ["id"] = port.PortId,
                ["portId"] = port.PortId
            };
            if (!string.IsNullOrWhiteSpace(port.PortName)) payload["portName"] = port.PortName;
            if (!string.IsNullOrWhiteSpace(port.Status)) payload["status"] = port.Status;
            if (port.Phone != null) payload["phone"] = port.Phone;
            if (port.Operator != null) payload["network"] = port.Operator;
            if (port.Balance != null) payload["balance"] = port.Balance;
            if (port.Imei != null) payload["imei"] = port.Imei;
            if (port.Ccid != null) payload["ccid"] = port.Ccid;
            if (port.Otp != null) payload["otp"] = port.Otp;
            if (port.LastContent != null) payload["smsContent"] = port.LastContent;
            if (port.UpdatedAt != null) payload["updatedAt"] = port.UpdatedAt;

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PatchAsync(url, content);
        }
        catch
        {
            // Ignore network errors
        }
    }
}
