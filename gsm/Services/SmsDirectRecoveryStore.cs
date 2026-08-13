using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Crash-safe owner for a direct +CMT frame that could not yet be decoded.
/// Direct delivery has no SIM slot to read again, so the serial buffer may be
/// released only after at least one flushed recovery copy exists.
/// </summary>
internal sealed class SmsDirectRecoveryStore
{
    internal sealed record Pending(
        string Id,
        string PortName,
        string Scope,
        string Raw,
        string Reason,
        int DecodeAttempts,
        DateTimeOffset ReceivedAtUtc);

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public Dictionary<string, Pending> Entries { get; set; } =
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
    private Dictionary<string, Pending> _entries = new(StringComparer.Ordinal);
    private long _revision;
    private bool _loadFailed;

    internal SmsDirectRecoveryStore(string primaryPath, string fallbackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackPath);
        _primaryPath = Path.GetFullPath(primaryPath);
        _fallbackPath = Path.GetFullPath(fallbackPath);
        Load();
    }

    internal Pending Store(
        string portName,
        string scope,
        string raw,
        string reason,
        int decodeAttempts)
    {
        Validate(portName, scope, raw);
        lock (_gate)
        {
            ThrowIfLoadFailed();
            var pending = new Pending(
                $"direct-raw-v1-{Guid.NewGuid():N}",
                portName.Trim().ToUpperInvariant(),
                scope,
                raw,
                reason ?? string.Empty,
                Math.Max(1, decodeAttempts),
                DateTimeOffset.UtcNow);
            Dictionary<string, Pending> next = Copy();
            next[pending.Id] = pending;
            Commit(next);
            return pending;
        }
    }

    internal IReadOnlyList<Pending> GetForPort(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        lock (_gate)
        {
            ThrowIfLoadFailed();
            return _entries.Values
                .Where(entry => string.Equals(
                    entry.PortName,
                    portName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.ReceivedAtUtc)
                .ToArray();
        }
    }

    internal IReadOnlyList<Pending> GetRecoverable(
        string portName,
        string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            ThrowIfLoadFailed();
            return _entries.Values
                .Where(entry => string.Equals(
                            entry.PortName,
                            portName,
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            entry.Scope,
                            scope,
                            StringComparison.Ordinal))
                .OrderBy(entry => entry.ReceivedAtUtc)
                .ToArray();
        }
    }

    internal bool Complete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (!_entries.ContainsKey(id)) return false;
            Dictionary<string, Pending> next = Copy();
            next.Remove(id);
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
        _entries = latest.Entries;
    }

    private static ReadResult Read(string path)
    {
        if (!File.Exists(path)) return new ReadResult(false, null);
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(
                File.ReadAllBytes(path), JsonOptions);
            if (document is not { Version: 1, Revision: > 0 }
                || document.Entries == null)
                return new ReadResult(true, null);

            var validated = new Dictionary<string, Pending>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Pending> pair in document.Entries)
            {
                Pending? entry = pair.Value;
                if (entry == null
                    || !string.Equals(pair.Key, entry.Id, StringComparison.Ordinal))
                    return new ReadResult(true, null);
                Validate(entry.PortName, entry.Scope, entry.Raw);
                validated[pair.Key] = entry with
                {
                    PortName = entry.PortName.Trim().ToUpperInvariant(),
                    ReceivedAtUtc = entry.ReceivedAtUtc == default
                        ? DateTimeOffset.UnixEpoch
                        : entry.ReceivedAtUtc.ToUniversalTime()
                };
            }

            document.Entries = validated;
            return new ReadResult(true, document);
        }
        catch (Exception ex) when (ex is JsonException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            return new ReadResult(true, null);
        }
    }

    private void Commit(Dictionary<string, Pending> next)
    {
        long revision = Math.Max(_revision + 1, DateTime.UtcNow.Ticks);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Document
        {
            Revision = revision,
            Entries = next
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
                "Cannot persist a direct SMS recovery frame.",
                new AggregateException(primaryError!, fallbackError!));
        }

        _entries = next;
        _revision = revision;
    }

    private Dictionary<string, Pending> Copy() =>
        _entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

    private void ThrowIfLoadFailed()
    {
        if (_loadFailed)
        {
            throw new InvalidDataException(
                "Both direct SMS recovery copies are unreadable; the serial frame must be retained.");
        }
    }

    private static void Validate(string portName, string scope, string raw)
    {
        if (!Regex.IsMatch(
                portName ?? string.Empty,
                @"^COM\d+$",
                RegexOptions.IgnoreCase)
            || string.IsNullOrWhiteSpace(scope)
            || string.IsNullOrWhiteSpace(raw)
            || !raw.Contains("+CMT:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid direct SMS recovery frame.");
        }
    }

    private static void AtomicWrite(string path, byte[] payload)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException("Direct SMS recovery file has no parent directory.");
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
                // A stale temp file is never treated as a committed recovery copy.
            }
        }
    }
}
