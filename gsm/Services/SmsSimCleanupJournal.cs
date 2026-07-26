using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Durable intent written before CMGD for a stored multipart segment. It closes
/// the crash window where the modem deletes a slot but the multipart journal
/// has not yet persisted CleanedPartIdentities.
/// </summary>
internal sealed class SmsSimCleanupJournal
{
    internal sealed record Intent(
        string IntentId,
        string Scope,
        string PortName,
        string SimIndex,
        string MessageId,
        string PartIdentity,
        DateTimeOffset CreatedAtUtc);

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public Dictionary<string, Intent> Intents { get; set; } =
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
    private Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
    private long _revision;
    private bool _loadFailed;

    public SmsSimCleanupJournal(string primaryPath, string fallbackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackPath);
        _primaryPath = Path.GetFullPath(primaryPath);
        _fallbackPath = Path.GetFullPath(fallbackPath);
        Load();
    }

    public Intent Prepare(
        string scope,
        string portName,
        string simIndex,
        string messageId,
        string partIdentity)
    {
        Validate(scope, portName, simIndex, messageId, partIdentity);
        string intentId = partIdentity;
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (_intents.TryGetValue(intentId, out Intent? existing))
            {
                if (!string.Equals(existing.Scope, scope, StringComparison.Ordinal)
                    || !string.Equals(existing.SimIndex, simIndex, StringComparison.Ordinal)
                    || !string.Equals(existing.MessageId, messageId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Cleanup identity is already owned by different SIM data.");
                }

                // The SIM may be moved to another modem after a crash. Scope is
                // the physical CCID owner, while PortName is only diagnostic, so
                // the same exact part must remain resumable on its new COM.
                return existing;
            }

            var intent = new Intent(
                intentId,
                scope,
                portName.Trim().ToUpperInvariant(),
                simIndex,
                messageId,
                partIdentity,
                DateTimeOffset.UtcNow);
            Dictionary<string, Intent> next = Copy();
            next[intentId] = intent;
            Commit(next);
            return intent;
        }
    }

    public bool Complete(string intentId, string expectedMessageId)
    {
        if (string.IsNullOrWhiteSpace(intentId)
            || string.IsNullOrWhiteSpace(expectedMessageId))
            return false;
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (!_intents.TryGetValue(intentId, out Intent? existing)
                || !string.Equals(
                    existing.MessageId,
                    expectedMessageId,
                    StringComparison.Ordinal))
                return false;
            Dictionary<string, Intent> next = Copy();
            next.Remove(intentId);
            Commit(next);
            return true;
        }
    }

    public IReadOnlyList<Intent> GetForScope(string scope)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            return _intents.Values
                .Where(intent => string.Equals(
                    intent.Scope, scope, StringComparison.Ordinal))
                .OrderBy(intent => intent.CreatedAtUtc)
                .ToArray();
        }
    }

    private void Load()
    {
        ReadResult primaryRead = Read(_primaryPath);
        ReadResult fallbackRead = Read(_fallbackPath);
        if (primaryRead.Exists && primaryRead.Document == null
            || fallbackRead.Exists && fallbackRead.Document == null)
        {
            // One surviving copy may be older than the unreadable one. Choosing
            // it would silently forget a Prepare that already authorized CMGD.
            _loadFailed = true;
            return;
        }

        Document? primary = primaryRead.Document;
        Document? fallback = fallbackRead.Document;
        if (primary != null
            && fallback != null
            && primary.Revision == fallback.Revision
            && !DocumentsEquivalent(primary, fallback))
        {
            _loadFailed = true;
            return;
        }

        Document? latest = new[] { primary, fallback }
            .Where(document => document != null)
            .OrderByDescending(document => document!.Revision)
            .FirstOrDefault();
        if (latest == null)
        {
            return;
        }
        _revision = latest.Revision;
        _intents = latest.Intents;
    }

    private static bool DocumentsEquivalent(Document left, Document right)
    {
        if (left.Intents.Count != right.Intents.Count) return false;
        foreach (KeyValuePair<string, Intent> pair in left.Intents)
        {
            if (!right.Intents.TryGetValue(pair.Key, out Intent? other)
                || !Equals(pair.Value, other))
                return false;
        }
        return true;
    }

    private static ReadResult Read(string path)
    {
        bool exists = File.Exists(path);
        if (!exists) return new ReadResult(false, null);
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(
                File.ReadAllBytes(path), JsonOptions);
            if (document is not { Version: 1, Revision: > 0 }
                || document.Intents == null)
                return new ReadResult(true, null);
            var validated = new Dictionary<string, Intent>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Intent> pair in document.Intents)
            {
                Intent? intent = pair.Value;
                if (intent == null
                    || !string.Equals(pair.Key, intent.IntentId, StringComparison.Ordinal))
                    return new ReadResult(true, null);
                Validate(
                    intent.Scope,
                    intent.PortName,
                    intent.SimIndex,
                    intent.MessageId,
                    intent.PartIdentity);
                validated[pair.Key] = intent with
                {
                    PortName = intent.PortName.Trim().ToUpperInvariant(),
                    CreatedAtUtc = intent.CreatedAtUtc == default
                        ? DateTimeOffset.UnixEpoch
                        : intent.CreatedAtUtc.ToUniversalTime()
                };
            }
            document.Intents = validated;
            return new ReadResult(true, document);
        }
        catch (Exception ex) when (ex is JsonException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            return new ReadResult(true, null);
        }
    }

    private void Commit(Dictionary<string, Intent> next)
    {
        long revision = Math.Max(_revision + 1, DateTime.UtcNow.Ticks);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Document
        {
            Revision = revision,
            Intents = next
        }, JsonOptions);

        Exception? primaryError = null;
        try
        {
            AtomicWrite(_primaryPath, payload);
            TryDelete(_fallbackPath);
            _intents = next;
            _revision = revision;
            return;
        }
        catch (Exception ex)
        {
            primaryError = ex;
        }

        try
        {
            AtomicWrite(_fallbackPath, payload);
            _intents = next;
            _revision = revision;
        }
        catch (Exception fallbackError)
        {
            throw new IOException(
                "Cannot persist SMS SIM-cleanup intent.",
                new AggregateException(primaryError!, fallbackError));
        }
    }

    private Dictionary<string, Intent> Copy() =>
        _intents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private void ThrowIfLoadFailed()
    {
        if (_loadFailed)
            throw new InvalidDataException(
                "SMS SIM-cleanup journal is unreadable; SIM deletion is blocked.");
    }

    private static void Validate(
        string scope,
        string portName,
        string simIndex,
        string messageId,
        string partIdentity)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || !Regex.IsMatch(portName ?? string.Empty, @"^COM\d+$", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(simIndex ?? string.Empty, @"^\d+$")
            || string.IsNullOrWhiteSpace(messageId)
            || string.IsNullOrWhiteSpace(partIdentity))
            throw new InvalidDataException("Invalid SMS SIM-cleanup intent.");
    }

    private static void AtomicWrite(string path, byte[] payload)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException("Cleanup journal has no parent directory.");
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
