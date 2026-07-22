using System.IO;
using System.Text.Json;

namespace gsm.Services;

/// <summary>
/// Durable copy of decoded multipart segments. A segment must be committed here
/// before its recyclable SIM slot may be released with CMGD.
/// </summary>
internal sealed class SmsMultipartJournal
{
    internal sealed record Part(int Sequence, string Content);

    private sealed class Entry
    {
        public string Scope { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public int Reference { get; set; }
        public int Total { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public Dictionary<int, string> Parts { get; set; } = new();

        public Entry Clone() => new()
        {
            Scope = Scope,
            Sender = Sender,
            Reference = Reference,
            Total = Total,
            LastUpdated = LastUpdated,
            Parts = Parts.ToDictionary(x => x.Key, x => x.Value)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loadFailed;

    public SmsMultipartJournal(string filePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        Load();
    }

    public IReadOnlyList<Part> RecordAndGetParts(
        string scope,
        string sender,
        SmsConcatInfo concat,
        string content,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (concat.Total is < 2 or > 255 || concat.Sequence < 1 || concat.Sequence > concat.Total)
            throw new InvalidDataException("Invalid multipart metadata.");

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string key = Key(scope, sender, concat.Reference, concat.Total);
        lock (_gate)
        {
            if (_loadFailed)
                throw new InvalidDataException(
                    $"Multipart journal is unreadable and was preserved at '{_filePath}'.");
            RemoveExpiredLocked(timestamp);
            _entries.TryGetValue(key, out Entry? existing);
            Entry? rollback = existing?.Clone();
            bool wasMissing = existing == null;

            if (existing == null)
            {
                existing = new Entry
                {
                    Scope = scope,
                    Sender = sender,
                    Reference = concat.Reference,
                    Total = concat.Total,
                    LastUpdated = timestamp
                };
                _entries[key] = existing;
            }

            if (existing.Parts.TryGetValue(concat.Sequence, out string? previous)
                && !string.Equals(previous, content, StringComparison.Ordinal))
            {
                RestoreLocked(key, rollback, wasMissing);
                throw new InvalidDataException(
                    $"Multipart conflict for {scope}/{sender}/{concat.Reference}, part {concat.Sequence}.");
            }

            existing.Parts[concat.Sequence] = content;
            existing.LastUpdated = timestamp;
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreLocked(key, rollback, wasMissing);
                throw;
            }

            return existing.Parts.OrderBy(x => x.Key)
                .Select(x => new Part(x.Key, x.Value))
                .ToArray();
        }
    }

    public IReadOnlyList<Part> GetParts(string scope, string sender, SmsConcatInfo concat)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(scope, sender, concat.Reference, concat.Total), out Entry? entry)
                || DateTimeOffset.UtcNow - entry.LastUpdated > _timeout)
                return Array.Empty<Part>();
            return entry.Parts.OrderBy(x => x.Key).Select(x => new Part(x.Key, x.Value)).ToArray();
        }
    }

    public void Complete(string scope, string sender, SmsConcatInfo concat)
    {
        string key = Key(scope, sender, concat.Reference, concat.Total);
        lock (_gate)
        {
            if (!_entries.Remove(key, out Entry? removed)) return;
            try
            {
                SaveLocked();
            }
            catch
            {
                _entries[key] = removed;
                throw;
            }
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                Entry[] entries = JsonSerializer.Deserialize<Entry[]>(
                    File.ReadAllText(_filePath), JsonOptions) ?? Array.Empty<Entry>();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (Entry entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Scope)
                        || string.IsNullOrWhiteSpace(entry.Sender)
                        || entry.Total is < 2 or > 255
                        || entry.Parts.Count == 0
                        || now - entry.LastUpdated > _timeout)
                        continue;
                    _entries[Key(entry.Scope, entry.Sender, entry.Reference, entry.Total)] = entry;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Keep the unreadable file for manual recovery. New segments will fail
                // safely on save instead of deleting their SIM records without a journal.
                _loadFailed = true;
            }
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        foreach (string key in _entries
                     .Where(x => now - x.Value.LastUpdated > _timeout)
                     .Select(x => x.Key)
                     .ToArray())
            _entries.Remove(key);
    }

    private void SaveLocked()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string tempPath = _filePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _entries.Values.ToArray(), JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(_filePath)) File.Replace(tempPath, _filePath, null);
        else File.Move(tempPath, _filePath);
    }

    private void RestoreLocked(string key, Entry? rollback, bool wasMissing)
    {
        if (wasMissing || rollback == null) _entries.Remove(key);
        else _entries[key] = rollback;
    }

    private static string Key(string scope, string sender, int reference, int total) =>
        $"{scope}\u001f{sender}\u001f{reference}\u001f{total}";
}
