using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

public interface INotifyService
{
    event Action<string>? TelegramStatus;
    Task SendTelegramAsync(string botToken, string chatId, string text);
    Task PushWebhookAsync(string url, object payload);
}

public class NotifyService : INotifyService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly TelegramOutboxStore _telegramOutbox;
    private int _telegramWorkerRunning;

    public event Action<string>? TelegramStatus;

    public NotifyService()
        : this(new TelegramOutboxStore())
    {
    }

    internal NotifyService(TelegramOutboxStore telegramOutbox)
    {
        _telegramOutbox = telegramOutbox;
        EnsureTelegramWorker();
    }

    public Task SendTelegramAsync(string botToken, string chatId, string text)
    {
        if (string.IsNullOrWhiteSpace(botToken)
            || string.IsNullOrWhiteSpace(chatId)
            || string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        // This call is intentionally synchronous up to the durable commit.
        // Fire-and-forget callers cannot exit between scheduling and the first
        // outbox flush because no await occurs before both snapshots are tried.
        IReadOnlyList<TelegramOutboxStore.Job> jobs = _telegramOutbox.Enqueue(
            botToken,
            chatId,
            PrepareTelegramMessages(text));
        PublishTelegramStatus(
            $"[TELEGRAM_QUEUED] jobs={jobs.Count}; đã ghi bền vững trước khi gửi.");
        EnsureTelegramWorker();
        return Task.CompletedTask;
    }

    internal static IReadOnlyList<(string Text, bool UseHtml)>
        PrepareTelegramMessages(string text)
    {
        if (text.Length <= 4000)
            return [(text, true)];

        // SMS content is HTML-encoded by the caller. Remove only ToolGSM's own
        // formatting tags and decode entities before splitting, so unusually
        // long Unicode content is delivered in full without broken tags.
        string plain = System.Net.WebUtility.HtmlDecode(Regex.Replace(
            text,
            @"</?(?:b|strong|i|em|code|pre|u|s)>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        List<string> chunks = SplitTextElements(plain, 3600);
        if (chunks.Count == 1)
            return [(chunks[0], false)];
        return chunks
            .Select((chunk, index) =>
                ($"[{index + 1}/{chunks.Count}]\n{chunk}", false))
            .ToArray();
    }

    private static List<string> SplitTextElements(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder(Math.Min(text.Length, maxChars));
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (current.Length > 0
                && current.Length + element.Length > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(element);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private void EnsureTelegramWorker()
    {
        if (Interlocked.CompareExchange(
                ref _telegramWorkerRunning, 1, 0) != 0)
            return;
        _ = Task.Run(RunTelegramWorkerAsync);
    }

    private async Task RunTelegramWorkerAsync()
    {
        try
        {
            while (true)
            {
                IReadOnlyList<TelegramOutboxStore.Job> jobs =
                    _telegramOutbox.GetPending();
                if (jobs.Count == 0) return;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                TelegramOutboxStore.Job[] due = jobs
                    .Where(job => job.NextAttemptUtc <= now)
                    .ToArray();
                if (due.Length == 0)
                {
                    TimeSpan wait = jobs[0].NextAttemptUtc - now;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    await Task.Delay(wait > TimeSpan.FromSeconds(30)
                            ? TimeSpan.FromSeconds(30)
                            : wait)
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (TelegramOutboxStore.Job job in due)
                {
                    (bool delivered, string error) =
                        await TrySendTelegramJobAsync(job).ConfigureAwait(false);
                    if (delivered)
                    {
                        _telegramOutbox.Complete(job.Id);
                        PublishTelegramStatus(
                            $"[TELEGRAM_DELIVERED] job={job.Id}; Telegram đã xác nhận HTTP thành công.");
                        continue;
                    }

                    int nextAttempt = job.AttemptCount + 1;
                    TimeSpan retryDelay = nextAttempt switch
                    {
                        1 => TimeSpan.FromSeconds(2),
                        2 => TimeSpan.FromSeconds(5),
                        3 => TimeSpan.FromSeconds(15),
                        4 => TimeSpan.FromSeconds(30),
                        5 => TimeSpan.FromMinutes(1),
                        6 => TimeSpan.FromMinutes(2),
                        _ => TimeSpan.FromMinutes(5)
                    };
                    _telegramOutbox.Retry(
                        job.Id,
                        DateTimeOffset.UtcNow + retryDelay,
                        error);
                    PublishTelegramStatus(
                        $"[TELEGRAM_RETRY] job={job.Id}; attempt={nextAttempt}; retryIn={retryDelay.TotalSeconds:0}s; reason={error}");
                }
            }
        }
        catch (Exception ex)
        {
            // The outbox is still durable. The next enqueue or app start will
            // restart this worker without forgetting any pending job.
            System.Diagnostics.Debug.WriteLine(
                $"Telegram outbox worker paused: {ex.Message}");
            PublishTelegramStatus(
                $"[TELEGRAM_OUTBOX_PAUSED] {ex.GetType().Name}: {ex.Message}; dữ liệu vẫn còn trên đĩa.");
        }
        finally
        {
            Interlocked.Exchange(ref _telegramWorkerRunning, 0);
            try
            {
                if (_telegramOutbox.GetPending().Count > 0)
                    EnsureTelegramWorker();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Telegram outbox recovery blocked: {ex.Message}");
            }
        }
    }

    private static async Task<(bool Delivered, string Error)>
        TrySendTelegramJobAsync(TelegramOutboxStore.Job job)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{job.BotToken}/sendMessage";
            var body = new Dictionary<string, object>
            {
                ["chat_id"] = job.ChatId,
                ["text"] = job.Text,
                ["disable_web_page_preview"] = true
            };
            if (job.UseHtml) body["parse_mode"] = "HTML";

            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(
                json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response =
                await Http.PostAsync(url, content).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (true, string.Empty);

            string error = await response.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false);
            return (false, $"HTTP {(int)response.StatusCode}: {error}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PublishTelegramStatus(string status)
    {
        try
        {
            TelegramStatus?.Invoke(status);
        }
        catch (Exception ex)
        {
            // Observability must never alter the durable notification state.
            System.Diagnostics.Debug.WriteLine(
                $"Telegram status listener failed: {ex.Message}");
        }
    }

    public async Task PushWebhookAsync(string url, object payload)
    {
        if (string.IsNullOrWhiteSpace(url) || payload == null)
            return;

        try
        {
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
            using var content = new StringContent(
                json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response =
                await Http.PostAsync(url.Trim(), content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(
                    $"Webhook fail: {response.StatusCode} {error}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Webhook exception: {ex.Message}");
        }
    }
}
