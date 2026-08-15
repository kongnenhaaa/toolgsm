namespace gsm.Services;

/// <summary>
/// Session-only Telegram retry queue. Pending notifications remain available
/// while ToolGSM is running but are never written to local outbox files.
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

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs =
        new(StringComparer.Ordinal);

    // The directory is deliberately ignored for source compatibility.
    internal TelegramOutboxStore(string? directoryPath = null)
    {
        _ = directoryPath;
    }

    internal IReadOnlyList<Job> Enqueue(
        string botToken,
        string chatId,
        IReadOnlyList<(string Text, bool UseHtml)> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return Array.Empty<Job>();

        lock (_gate)
        {
            var added = new List<Job>(messages.Count);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string text, bool useHtml) in messages)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var job = new Job(
                    $"telegram-v1-{Guid.NewGuid():N}",
                    (botToken ?? string.Empty).Trim(),
                    (chatId ?? string.Empty).Trim(),
                    text,
                    useHtml,
                    now,
                    0,
                    now,
                    string.Empty);
                _jobs[job.Id] = job;
                added.Add(job);
            }

            return added;
        }
    }

    internal IReadOnlyList<Job> GetPending()
    {
        lock (_gate)
        {
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
            return _jobs.Remove(id);
    }

    internal bool Retry(
        string id,
        DateTimeOffset nextAttemptUtc,
        string error)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? current)) return false;
            string normalizedError = error ?? string.Empty;
            _jobs[id] = current with
            {
                AttemptCount = current.AttemptCount + 1,
                NextAttemptUtc = nextAttemptUtc.ToUniversalTime(),
                LastError = normalizedError.Length <= 500
                    ? normalizedError
                    : normalizedError[..500]
            };
            return true;
        }
    }
}
