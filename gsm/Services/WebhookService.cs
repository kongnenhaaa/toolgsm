using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

/// <summary>
/// Gửi HTTP POST đến URL webhook khi nhận OTP/SMS.
/// Payload JSON: { port, phone, sender, otp, content, timestamp }
/// </summary>
public static class WebhookService
{
    private static readonly HttpClient _client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Kiểm tra rule có khớp với tin nhắn này không, nếu khớp thì POST đến webhook URL.
    /// </summary>
    public static async Task TriggerAsync(
        WebhookRule rule,
        string portName,
        string receiverPhone,
        string sender,
        string otp,
        string content)
    {
        if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.WebhookUrl))
            return;

        // Kiểm tra OtpOnly
        bool hasOtp = !string.IsNullOrWhiteSpace(otp) && otp != "N/A";
        if (rule.OtpOnly && !hasOtp)
            return;

        // Kiểm tra SenderFilter
        if (!MatchesSenderFilter(rule.SenderFilter, sender))
            return;

        var payload = new
        {
            port     = portName,
            phone    = receiverPhone,
            sender   = sender,
            otp      = hasOtp ? otp : null as string,
            content  = content,
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        string json = JsonSerializer.Serialize(payload);

        // Retry tối đa 2 lần
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, rule.WebhookUrl);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // Thêm header bí mật nếu có
                if (!string.IsNullOrWhiteSpace(rule.SecretHeader))
                {
                    // Format: "Header-Name: value"
                    int colonIdx = rule.SecretHeader.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        string headerName  = rule.SecretHeader[..colonIdx].Trim();
                        string headerValue = rule.SecretHeader[(colonIdx + 1)..].Trim();
                        request.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }
                }

                var response = await _client.SendAsync(request);
                if (response.IsSuccessStatusCode) break; // Thành công → dừng retry
            }
            catch (Exception ex)
            {
                if (attempt == 1) // Lần cuối vẫn lỗi
                {
                    try
                    {
                        System.IO.File.AppendAllText(
                            AppPaths.ForRuntimeFile("webhook_errors.txt"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] Rule '{rule.Name}' → {rule.WebhookUrl}\n  Lỗi: {ex.Message}\n"
                        );
                    }
                    catch { }
                }
                await Task.Delay(1500); // Chờ 1.5s trước khi thử lại
            }
        }
    }

    private static bool MatchesSenderFilter(string filter, string sender)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true; // Không lọc = khớp tất cả

        var keywords = filter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var keyword in keywords)
        {
            if (sender.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
