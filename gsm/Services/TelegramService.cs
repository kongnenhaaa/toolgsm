using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace gsm.Services;

public static class TelegramService
{
    // Cấu hình Bot (Bạn cần điền thông tin thật của bạn vào đây)
    // Ví dụ Token: "123456789:ABCDefghIJKL..."
    // Ví dụ Chat ID: "987654321" (Lấy từ @userinfobot)
    private static readonly string BotToken = "8926115937:AAFpUEvxfFqRpwGDWChbEQEWsn6xkZ-RTCQ";
    private static readonly string ChatId = "-1003586587027";
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentQueue<string> _messageQueue = new ConcurrentQueue<string>();
    private static readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
    private static readonly TimeSpan _sendDelay = TimeSpan.FromMilliseconds(1200);

    static TelegramService()
    {
        _ = Task.Run(ProcessQueueAsync);
    }

    public static async Task SendMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

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
                    var response = await _httpClient.PostAsync(url, content);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        // Thử lại không dùng HTML parse_mode nếu bị lỗi (để tránh mất tin nhắn do sai format)
                        payload.Remove("parse_mode");
                        content = new FormUrlEncodedContent(payload);
                        response = await _httpClient.PostAsync(url, content);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText("tele_error.txt", $"{DateTime.Now}: {ex.Message}\n{message}\n");
                }

                await Task.Delay(_sendDelay);
            }
        }
    }
}
