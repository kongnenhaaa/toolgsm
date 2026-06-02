using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace gsm.Services;

public static class TelegramService
{
    // Cấu hình Bot (Bạn cần điền thông tin thật của bạn vào đây)
    // Ví dụ Token: "123456789:ABCDefghIJKL..."
    // Ví dụ Chat ID: "987654321" (Lấy từ @userinfobot)
    private static readonly string BotToken = "8926115937:AAFpUEvxfFqRpwGDWChbEQEWsn6xkZ-RTCQ";
    private static readonly string ChatId = "7035960212";
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task SendMessageAsync(string message)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            
            var payload = new System.Collections.Generic.Dictionary<string, string>
            {
                { "chat_id", ChatId },
                { "text", message },
                { "parse_mode", "HTML" }
            };

            var content = new FormUrlEncodedContent(payload);
            await _httpClient.PostAsync(url, content);
        }
        catch
        {
        }
    }
}
