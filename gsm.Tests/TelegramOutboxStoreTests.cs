using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using gsm.Services;

namespace gsm.Tests;

public sealed class TelegramOutboxStoreTests
{
    [Fact]
    public void EnqueuedNotification_SurvivesRestartWithoutContentLoss()
    {
        using var temp = new TempDirectory();
        const string text = "Tin dài phần 1\nphần 2 🔐 & <safe>";
        var firstRun = new TelegramOutboxStore(temp.Path);
        TelegramOutboxStore.Job created = Assert.Single(firstRun.Enqueue(
            "token",
            "chat",
            [(text, false)]));

        var afterRestart = new TelegramOutboxStore(temp.Path);
        TelegramOutboxStore.Job recovered =
            Assert.Single(afterRestart.GetPending());

        Assert.Equal(created.Id, recovered.Id);
        Assert.Equal(text, recovered.Text);
        Assert.False(recovered.UseHtml);
    }

    [Fact]
    public void RetryAndCompletion_AreDurableAcrossRestart()
    {
        using var temp = new TempDirectory();
        var store = new TelegramOutboxStore(temp.Path);
        TelegramOutboxStore.Job job = Assert.Single(store.Enqueue(
            "token",
            "chat",
            [("full message", true)]));
        DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddMinutes(2);

        Assert.True(store.Retry(job.Id, retryAt, "offline"));
        var afterRetry = new TelegramOutboxStore(temp.Path);
        TelegramOutboxStore.Job retried =
            Assert.Single(afterRetry.GetPending());
        Assert.Equal(1, retried.AttemptCount);
        Assert.Equal("offline", retried.LastError);
        Assert.Equal(retryAt, retried.NextAttemptUtc, TimeSpan.FromMilliseconds(1));

        Assert.True(afterRetry.Complete(job.Id));
        Assert.Empty(new TelegramOutboxStore(temp.Path).GetPending());
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
    public void ValidBackup_RecoversWhenPrimaryOutboxIsCorrupt()
    {
        using var temp = new TempDirectory();
        var store = new TelegramOutboxStore(temp.Path);
        store.Enqueue("token", "chat", [("message", true)]);
        File.WriteAllText(
            Path.Combine(temp.Path, "telegram_outbox.json"),
            "{corrupt");

        Assert.Single(new TelegramOutboxStore(temp.Path).GetPending());
    }

    [Fact]
    public void BothCorruptOutboxCopies_FailClosedWithoutOverwritingJobs()
    {
        using var temp = new TempDirectory();
        var store = new TelegramOutboxStore(temp.Path);
        store.Enqueue("token", "chat", [("first", true)]);
        string primary = Path.Combine(temp.Path, "telegram_outbox.json");
        string fallback = Path.Combine(
            temp.Path, "telegram_outbox.backup.json");
        File.WriteAllText(primary, "{primary-corrupt");
        File.WriteAllText(fallback, "{fallback-corrupt");
        var blocked = new TelegramOutboxStore(temp.Path);

        Assert.Throws<InvalidDataException>(() => blocked.Enqueue(
            "token",
            "chat",
            [("second", true)]));
        Assert.Equal("{primary-corrupt", File.ReadAllText(primary));
        Assert.Equal("{fallback-corrupt", File.ReadAllText(fallback));
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
                // Best-effort test cleanup.
            }
        }
    }
}
