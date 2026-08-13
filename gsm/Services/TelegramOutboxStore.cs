using System.IO;
using System.Text.Json;

namespace gsm.Services;

/// <summary>
/// Durable at-least-once Telegram outbox. A crash after Telegram accepted a
/// request but before the completion flush can produce a duplicate; it cannot
/// silently lose the notification.
/// </summary>
internal sealed class TelegramOutboxStore
{
    internal sealed record Job(
        string Id,
        string BotToken,
        string ChatId,
        string Text,
        bool UseHtml,
        DateTimeOffset CreatedAtUtc,
        int AttemptCount,
        DateTimeOffset NextAttemptUtc,
        string LastError);

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public Dictionary<string, Job> Jobs { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed record ReadResult(bool Exists, Document? Document);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _primaryPath;
    private readonly string _fallbackPath;
    private Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private long _revision;
    private bool _loadFailed;

    internal static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToolGSM",
        "Data");

    internal TelegramOutboxStore(string? directoryPath = null)
    {
        string directory = string.IsNullOrWhiteSpace(directoryPath)
            ? DefaultDirectoryPath
            : Path.GetFullPath(directoryPath);
        _primaryPath = Path.Combine(directory, "telegram_outbox.json");
        _fallbackPath = Path.Combine(directory, "telegram_outbox.backup.json");
        Load();
    }

    internal IReadOnlyList<Job> Enqueue(
        string botToken,
        string chatId,
        IReadOnlyList<(string Text, bool UseHtml)> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return Array.Empty<Job>();

        lock (_gate)
        {
            ThrowIfLoadFailed();
            Dictionary<string, Job> next = Copy();
            var added = new List<Job>(messages.Count);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string text, bool useHtml) in messages)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var job = new Job(
                    $"telegram-v1-{Guid.NewGuid():N}",
                    botToken.Trim(),
                    chatId.Trim(),
                    text,
                    useHtml,
                    now,
                    0,
                    now,
                    string.Empty);
                next[job.Id] = job;
                added.Add(job);
            }

            if (added.Count == 0) return Array.Empty<Job>();
            Commit(next);
            return added;
        }
    }

    internal IReadOnlyList<Job> GetPending()
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            return _jobs.Values
                .OrderBy(job => job.NextAttemptUtc)
                .ThenBy(job => job.CreatedAtUtc)
                .ToArray();
        }
    }

    internal bool Complete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (!_jobs.ContainsKey(id)) return false;
            Dictionary<string, Job> next = Copy();
            next.Remove(id);
            Commit(next);
            return true;
        }
    }

    internal bool Retry(
        string id,
        DateTimeOffset nextAttemptUtc,
        string error)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (!_jobs.TryGetValue(id, out Job? current)) return false;
            Dictionary<string, Job> next = Copy();
            string normalizedError = error ?? string.Empty;
            next[id] = current with
            {
                AttemptCount = current.AttemptCount + 1,
                NextAttemptUtc = nextAttemptUtc.ToUniversalTime(),
                LastError = normalizedError.Length <= 500
                    ? normalizedError
                    : normalizedError[..500]
            };
            Commit(next);
            return true;
        }
    }

    private void Load()
    {
        ReadResult primary = Read(_primaryPath);
        ReadResult fallback = Read(_fallbackPath);
        Document? latest = new[] { primary.Document, fallback.Document }
            .Where(document => document != null)
            .OrderByDescending(document => document!.Revision)
            .FirstOrDefault();
        if (latest == null)
        {
            _loadFailed = primary.Exists || fallback.Exists;
            return;
        }

        _revision = latest.Revision;
        _jobs = latest.Jobs;
    }

    private static ReadResult Read(string path)
    {
        if (!File.Exists(path)) return new ReadResult(false, null);
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(
                File.ReadAllBytes(path), JsonOptions);
            if (document is not { Version: 1, Revision: > 0 }
                || document.Jobs == null)
                return new ReadResult(true, null);

            var validated = new Dictionary<string, Job>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Job> pair in document.Jobs)
            {
                Job? job = pair.Value;
                if (job == null
                    || !string.Equals(pair.Key, job.Id, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(job.BotToken)
                    || string.IsNullOrWhiteSpace(job.ChatId)
                    || string.IsNullOrWhiteSpace(job.Text))
                    return new ReadResult(true, null);
                validated[pair.Key] = job with
                {
                    CreatedAtUtc = job.CreatedAtUtc == default
                        ? DateTimeOffset.UnixEpoch
                        : job.CreatedAtUtc.ToUniversalTime(),
                    NextAttemptUtc = job.NextAttemptUtc == default
                        ? DateTimeOffset.UnixEpoch
                        : job.NextAttemptUtc.ToUniversalTime()
                };
            }

            document.Jobs = validated;
            return new ReadResult(true, document);
        }
        catch (Exception ex) when (ex is JsonException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            return new ReadResult(true, null);
        }
    }

    private void Commit(Dictionary<string, Job> next)
    {
        long revision = Math.Max(_revision + 1, DateTime.UtcNow.Ticks);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Document
        {
            Revision = revision,
            Jobs = next
        }, JsonOptions);

        Exception? primaryError = null;
        Exception? fallbackError = null;
        bool primaryWritten = false;
        bool fallbackWritten = false;
        try
        {
            AtomicWrite(_primaryPath, payload);
            primaryWritten = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            primaryError = ex;
        }

        try
        {
            AtomicWrite(_fallbackPath, payload);
            fallbackWritten = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fallbackError = ex;
        }

        if (!primaryWritten && !fallbackWritten)
        {
            throw new IOException(
                "Cannot persist the Telegram outbox.",
                new AggregateException(primaryError!, fallbackError!));
        }

        _jobs = next;
        _revision = revision;
    }

    private Dictionary<string, Job> Copy() =>
        _jobs.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

    private void ThrowIfLoadFailed()
    {
        if (_loadFailed)
        {
            throw new InvalidDataException(
                "Both Telegram outbox copies are unreadable; existing notifications were preserved and no overwrite is allowed.");
        }
    }

    private static void AtomicWrite(string path, byte[] payload)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException("Telegram outbox has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A temp file is never considered committed.
            }
        }
    }
}
