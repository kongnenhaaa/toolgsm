using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace gsm.Services;

public static class TelegramService
{
    // Cấu hình Bot (Bạn cần điền thông tin thật của bạn vào đây)
    // Các giá trị này được lấy động từ SettingsService
    // private static readonly string BotToken = "8926115937:AAFpUEvxfFqRpwGDWChbEQEWsn6xkZ-RTCQ";
    // private static readonly string ChatId = "-1003586587027";
    private static readonly HttpClient _httpClient;
    private static readonly ConcurrentQueue<string> _messageQueue = new ConcurrentQueue<string>();
    private static readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
    private static readonly TimeSpan _sendDelay = TimeSpan.FromMilliseconds(1200);

    static TelegramService()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _ = Task.Run(ProcessQueueAsync);
    }

    public static async Task SendMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!SettingsService.Current.EnableTelegramNotification) return;

        _messageQueue.Enqueue(message);
        _queueSignal.Release();
        await Task.CompletedTask;
    }

    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            await _queueSignal.WaitAsync();

            if (_messageQueue.TryDequeue(out var message))
            {
                var tokensRaw = SettingsService.Current.TelegramBotToken;
                var token = string.IsNullOrWhiteSpace(tokensRaw) ? null : tokensRaw.Split(',')[0].Trim();
                var idsRaw = !string.IsNullOrWhiteSpace(SettingsService.Current.TelegramChatIds) 
                    ? SettingsService.Current.TelegramChatIds 
                    : SettingsService.Current.TelegramChatId;
                var chatIds = idsRaw?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                if (string.IsNullOrWhiteSpace(token) || chatIds == null || chatIds.Length == 0)
                {
                    // Chờ nếu chưa config
                    await Task.Delay(2000);
                    continue;
                }

                foreach (var id in chatIds)
                {
                    var chatIdStr = id.Trim();
                    if (string.IsNullOrEmpty(chatIdStr)) continue;

                    int retryCount = 0;
                    bool success = false;
                    
                    while (retryCount < 3 && !success)
                    {
                        try
                        {
                            string url = $"https://api.telegram.org/bot{token.Trim()}/sendMessage";
                            
                            var payload = new System.Collections.Generic.Dictionary<string, string>
                            {
                                { "chat_id", chatIdStr },
                                { "text", message },
                                { "parse_mode", "HTML" }
                            };

                            var content = new FormUrlEncodedContent(payload);
                            var response = await _httpClient.PostAsync(url, content);
                            
                            if (!response.IsSuccessStatusCode)
                            {
                                // Thử lại không dùng HTML parse_mode
                                payload.Remove("parse_mode");
                                content = new FormUrlEncodedContent(payload);
                                response = await _httpClient.PostAsync(url, content);
                            }
                            
                            response.EnsureSuccessStatusCode();
                            success = true;
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            if (retryCount >= 3)
                            {
                                System.IO.File.AppendAllText("tele_error.txt", $"{DateTime.Now}: {ex.Message} (After 3 retries)\n{message}\n");
                            }
                            else
                            {
                                await Task.Delay(2000); // Chờ 2s rồi thử lại
                            }
                        }
                    }
                }

                await Task.Delay(_sendDelay);
            }
        }
    }
}
