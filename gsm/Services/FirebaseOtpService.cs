using System;
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
        if (string.IsNullOrEmpty(DbUrl) || string.IsNullOrEmpty(machineId) || string.IsNullOrEmpty(portId))
            return;

        try
        {
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
            var url = Path($"machines/{machineId}/ports/{port.PortId}");
            var json = System.Text.Json.JsonSerializer.Serialize(port);
            await _http.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Ignore network errors
        }
    }
}
