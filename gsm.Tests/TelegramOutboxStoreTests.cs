using System.Net;
using System.Text.RegularExpressions;
using gsm.Services;

namespace gsm.Tests;

public sealed class TelegramOutboxStoreTests
{
    [Fact]
    public void EnqueuedNotification_IsSessionOnlyAndCreatesNoFiles()
    {
        using var temp = new TempDirectory();
        const string text = "Tin dài phần 1\nphần 2 🔐 & <safe>";
        var store = new TelegramOutboxStore(temp.Path);

        TelegramOutboxStore.Job created = Assert.Single(store.Enqueue(
            "token",
            "chat",
            [(text, false)]));

        TelegramOutboxStore.Job queued = Assert.Single(store.GetPending());
        Assert.Equal(created.Id, queued.Id);
        Assert.Equal(text, queued.Text);
        Assert.False(queued.UseHtml);
        Assert.False(File.Exists(Path.Combine(
            temp.Path, "telegram_outbox.json")));
        Assert.False(File.Exists(Path.Combine(
            temp.Path, "telegram_outbox.backup.json")));
        Assert.Empty(new TelegramOutboxStore(temp.Path).GetPending());
    }

    [Fact]
    public void MissingDestination_IsRetainedOnlyWithinCurrentSession()
    {
        using var temp = new TempDirectory();
        var store = new TelegramOutboxStore(temp.Path);

        TelegramOutboxStore.Job created = Assert.Single(store.Enqueue(
            string.Empty,
            string.Empty,
            [("SMS phải chờ cấu hình", true)]));

        TelegramOutboxStore.Job queued = Assert.Single(store.GetPending());
        Assert.Equal(created.Id, queued.Id);
        Assert.Empty(queued.BotToken);
        Assert.Empty(queued.ChatId);
        Assert.Empty(new TelegramOutboxStore(temp.Path).GetPending());
    }

    [Fact]
    public void RetryAndCompletion_WorkWithinCurrentSession()
    {
        var store = new TelegramOutboxStore();
        TelegramOutboxStore.Job job = Assert.Single(store.Enqueue(
            "token",
            "chat",
            [("full message", true)]));
        DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddMinutes(2);

        Assert.True(store.Retry(job.Id, retryAt, "offline"));
        TelegramOutboxStore.Job retried = Assert.Single(store.GetPending());
        Assert.Equal(1, retried.AttemptCount);
        Assert.Equal("offline", retried.LastError);
        Assert.Equal(retryAt, retried.NextAttemptUtc, TimeSpan.FromMilliseconds(1));
        Assert.True(store.Complete(job.Id));
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public void LongHtmlMessage_IsSplitWithoutTruncatingUnicodeOrSmsText()
    {
        string sms = string.Concat(Enumerable.Repeat(
            "Nội dung 🔐 & ký tự <nguyên bản> — ",
            220));
        string html = $"<b>SMS mới</b>\nNội dung: {WebUtility.HtmlEncode(sms)}";

        IReadOnlyList<(string Text, bool UseHtml)> chunks =
            NotifyService.PrepareTelegramMessages(html);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.False(chunk.UseHtml);
            Assert.True(chunk.Text.Length <= 3650);
            AssertNoUnpairedSurrogates(chunk.Text);
        });
        string reconstructed = string.Concat(chunks.Select(chunk =>
            Regex.Replace(chunk.Text, @"^\[\d+/\d+\]\n", string.Empty)));
        Assert.Equal("SMS mới\nNội dung: " + sms, reconstructed);
    }

    [Fact]
    public void TelegramTargets_UseSavedConfigForWaitingJob_AndKeepAllChatIds()
    {
        (string token, IReadOnlyList<string> chatIds) =
            NotifyService.ResolveTelegramTargets(
                string.Empty,
                string.Empty,
                " token-1 , token-ignored ",
                " chat-a; chat-b,chat-a ",
                "fallback-chat");

        Assert.Equal("token-1", token);
        Assert.Equal(["chat-a", "chat-b"], chatIds);
    }

    [Fact]
    public void TelegramTargets_PreferDestinationCapturedAtEnqueue()
    {
        (string token, IReadOnlyList<string> chatIds) =
            NotifyService.ResolveTelegramTargets(
                "job-token",
                "job-chat-1;job-chat-2",
                "new-token",
                "new-chat",
                string.Empty);

        Assert.Equal("job-token", token);
        Assert.Equal(["job-chat-1", "job-chat-2"], chatIds);
    }

    private static void AssertNoUnpairedSurrogates(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsHighSurrogate(current))
            {
                Assert.True(index + 1 < value.Length);
                Assert.True(char.IsLowSurrogate(value[++index]));
            }
            else
            {
                Assert.False(char.IsLowSurrogate(current));
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"toolgsm-telegram-outbox-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
