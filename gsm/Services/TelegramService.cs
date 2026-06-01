using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace gsm.Services;

public static class TelegramService
{
    // Cấu hình Bot (Bạn cần điền thông tin thật của bạn vào đây)
    // Ví dụ Token: "123456789:ABCDefghIJKL..."
    // Ví dụ Chat ID: "987654321" (Lấy từ @userinfobot)
    
    private static readonly string BotToken = "YOUR_BOT_TOKEN_HERE";
    private static readonly string ChatId = "YOUR_CHAT_ID_HERE";
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task SendMessageAsync(string message)
    {
        if (BotToken == "YOUR_BOT_TOKEN_HERE" || ChatId == "YOUR_CHAT_ID_HERE")
        {
            // Bỏ qua nếu chưa được cấu hình
            return;
        }

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
            // Bỏ qua lỗi mạng khi gửi Telegram để không làm treo luồng chính
        }
    }
}
