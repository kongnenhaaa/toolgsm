using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace gsm.Services;

public interface INotifyService
{
    Task SendTelegramAsync(string botToken, string chatId, string text);
    Task PushWebhookAsync(string url, object payload);
}

public class NotifyService : INotifyService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task SendTelegramAsync(string botToken, string chatId, string text)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var url = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
            var body = new
            {
                chat_id = chatId.Trim(),
                text = text,
                disable_web_page_preview = true,
                parse_mode = "HTML"
            };
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Telegram fail: {resp.StatusCode} {err}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Telegram exception: {ex.Message}");
        }
    }

    public async Task PushWebhookAsync(string url, object payload)
    {
        if (string.IsNullOrWhiteSpace(url) || payload == null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(url.Trim(), content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Webhook fail: {resp.StatusCode} {err}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Webhook exception: {ex.Message}");
        }
    }
}
