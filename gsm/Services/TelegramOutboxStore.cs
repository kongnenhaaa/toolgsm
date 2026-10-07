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
        string LastError,
        string DeduplicationKey,
        string DeliveredChatIds);

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedDeduplicationKeys =
        new(StringComparer.Ordinal);

    // The directory is deliberately ignored for source compatibility.
    internal TelegramOutboxStore(string? directoryPath = null)
    {
        _ = directoryPath;
    }

    internal IReadOnlyList<Job> Enqueue(
        string botToken,
        string chatId,
        IReadOnlyList<(string Text, bool UseHtml)> messages,
        string? deduplicationKey = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return Array.Empty<Job>();

        lock (_gate)
        {
            var added = new List<Job>(messages.Count);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string deduplicationRoot = (deduplicationKey ?? string.Empty).Trim();
            for (int index = 0; index < messages.Count; index++)
            {
                (string text, bool useHtml) = messages[index];
                if (string.IsNullOrWhiteSpace(text)) continue;

                string messageDeduplicationKey = deduplicationRoot.Length == 0
                    ? string.Empty
                    : messages.Count == 1
                        ? deduplicationRoot
                        : $"{deduplicationRoot}#part-{index + 1}";
                if (messageDeduplicationKey.Length > 0
                    && !_claimedDeduplicationKeys.Add(messageDeduplicationKey))
                {
                    continue;
                }

                var job = new Job(
                    $"telegram-v1-{Guid.NewGuid():N}",
                    (botToken ?? string.Empty).Trim(),
                    (chatId ?? string.Empty).Trim(),
                    text,
                    useHtml,
                    now,
                    0,
                    now,
                    string.Empty,
                    messageDeduplicationKey,
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

    internal bool MarkChatDelivered(string id, string chatId)
    {
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(chatId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? current)) return false;

            HashSet<string> delivered = ParseDeliveredChatIds(
                current.DeliveredChatIds);
            if (!delivered.Add(chatId.Trim())) return true;

            _jobs[id] = current with
            {
                DeliveredChatIds = string.Join('\n', delivered)
            };
            return true;
        }
    }

    internal static HashSet<string> ParseDeliveredChatIds(string? value) =>
        (value ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(chatId => chatId.Trim())
            .Where(chatId => chatId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}
