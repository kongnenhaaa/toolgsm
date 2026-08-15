using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Durable copy of decoded multipart segments. A segment must be committed here
/// before its recyclable SIM slot may be released with CMGD.
/// </summary>
internal sealed class SmsMultipartJournal
{
    private static readonly int[] ReplaceRetryDelaysMs = [25, 50, 100, 200, 400, 800];
    internal static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(30);

    internal sealed record Part(int Sequence, string Content);
    internal sealed record CompletedSnapshot(
        string MessageId,
        string Scope,
        string PortName,
        string Sender,
        SmsConcatInfo Concatenation,
        string Content,
        bool DeliveryAcknowledged,
        bool RequiresSimCleanup,
        bool SimCleanupConfirmed);

    internal sealed record StalledSnapshot(
        string MessageId,
        string Scope,
        string PortName,
        string Sender,
        SmsConcatInfo Concatenation,
        string Content,
        int PresentParts,
        DateTimeOffset LastUpdated,
        bool PartialDeliveryAcknowledged);

    private sealed class Entry
    {
        public string Scope { get; set; } = string.Empty;
        public string GenerationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string LastPortName { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public HashSet<string> AcceptedSenders { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int Reference { get; set; }
        public int Total { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public bool DeliveryAcknowledged { get; set; }
        public bool PartialDeliveryAcknowledged { get; set; }
        public Dictionary<int, string> Parts { get; set; } = new();
        public Dictionary<int, string> PartIdentities { get; set; } = new();
        public HashSet<string> CleanedPartIdentities { get; set; } =
            new(StringComparer.Ordinal);

        public Entry Clone() => new()
        {
            Scope = Scope,
            GenerationId = GenerationId,
            MessageId = MessageId,
            LastPortName = LastPortName,
            Sender = Sender,
            AcceptedSenders = new HashSet<string>(
                AcceptedSenders, StringComparer.OrdinalIgnoreCase),
            Reference = Reference,
            Total = Total,
            LastUpdated = LastUpdated,
            DeliveryAcknowledged = DeliveryAcknowledged,
            PartialDeliveryAcknowledged = PartialDeliveryAcknowledged,
            Parts = Parts.ToDictionary(x => x.Key, x => x.Value),
            PartIdentities = PartIdentities.ToDictionary(x => x.Key, x => x.Value),
            CleanedPartIdentities = new HashSet<string>(
                CleanedPartIdentities,
                StringComparer.Ordinal)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly bool _inMemory;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loadFailed;

    public SmsMultipartJournal(
        string filePath,
        TimeSpan? timeout = null,
        IEnumerable<string>? legacyPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        // `timeout` remains in the signature for binary/source compatibility
        // with older callers. Once a part has been durably committed, its SIM
        // slot may already have been released; expiring that part by time would
        // therefore be irreversible data loss.
        _ = timeout;
        // Legacy journal migration was removed. Keep the optional argument only
        // for source compatibility; no sidecar manifest is created or read.
        _ = legacyPaths;
        Load();
    }

    private SmsMultipartJournal()
    {
        _filePath = string.Empty;
        _inMemory = true;
    }

    internal static SmsMultipartJournal CreateInMemory() => new();

    public IReadOnlyList<Part> RecordAndGetParts(
        string scope,
        string sender,
        SmsConcatInfo concat,
        string content,
        DateTimeOffset? now = null,
        string? portName = null,
        string? partIdentity = null,
        string? messageIdHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (concat.Total is < 1 or > 255 || concat.Sequence < 1 || concat.Sequence > concat.Total)
            throw new InvalidDataException("Invalid multipart metadata.");

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            EnsureLoadedLocked();
            Dictionary<string, Entry> rollback = CloneEntriesLocked();

            KeyValuePair<string, Entry>? anchor = FindAppendTargetLocked(
                scope,
                sender,
                concat,
                content,
                partIdentity,
                timestamp);
            List<KeyValuePair<string, Entry>> entriesToMerge = new();
            if (anchor.HasValue)
            {
                entriesToMerge.Add(anchor.Value);
                entriesToMerge.AddRange(FindCompatibleAliasesLocked(
                    anchor.Value,
                    scope,
                    sender,
                    concat,
                    content));
            }
            else
            {
                // A sender may switch from 888 to 565656 after the first segment.
                // Adopt only one recent, compatible alias entry. Multiple matches
                // are ambiguous and therefore remain isolated.
                // With a durable source fingerprint a conflicting sequence is
                // a new carrier generation reusing the same concat reference,
                // not corruption of the old incomplete message. Callers that
                // cannot provide an identity retain the conservative legacy
                // conflict behavior below.
                if (string.IsNullOrWhiteSpace(partIdentity))
                {
                    KeyValuePair<string, Entry>[] conflictingDirect = _entries
                        .Where(pair => SameMultipart(
                                           pair.Value,
                                           scope,
                                           concat.Reference,
                                           concat.Total)
                                       && SenderWasAccepted(pair.Value, sender)
                                       && WithinCorrelationWindow(
                                           pair.Value.LastUpdated,
                                           timestamp))
                        .OrderByDescending(pair => pair.Value.LastUpdated)
                        .ToArray();
                    if (conflictingDirect.Length == 1)
                        entriesToMerge.Add(conflictingDirect[0]);
                }
            }

            Entry existing;
            string key;
            if (entriesToMerge.Count == 0)
            {
                string generationId = Guid.NewGuid().ToString("N");
                existing = new Entry
                {
                    Scope = scope,
                    GenerationId = generationId,
                    MessageId = string.IsNullOrWhiteSpace(messageIdHint)
                        ? BuildGeneratedMessageId(generationId)
                        : messageIdHint,
                    LastPortName = portName ?? InferLegacyPort(scope),
                    Sender = sender,
                    AcceptedSenders = new HashSet<string>(
                        [sender], StringComparer.OrdinalIgnoreCase),
                    Reference = concat.Reference,
                    Total = concat.Total,
                    LastUpdated = timestamp
                };
                key = Key(existing);
                _entries[key] = existing;
            }
            else
            {
                KeyValuePair<string, Entry> target = entriesToMerge[0];
                key = target.Key;
                existing = target.Value;
                foreach (KeyValuePair<string, Entry> source in entriesToMerge.Skip(1))
                    MergeEntryLocked(key, existing, source);
            }

            if (!string.IsNullOrWhiteSpace(messageIdHint)
                && !string.Equals(
                    existing.MessageId,
                    messageIdHint,
                    StringComparison.Ordinal))
            {
                RestoreAllLocked(rollback);
                throw new InvalidDataException(
                    $"Delivery identity conflict for {scope}/{sender}/{concat.Reference}.");
            }

            if (!IsPartCompatible(existing, concat.Sequence, content))
            {
                RestoreAllLocked(rollback);
                throw new InvalidDataException(
                    $"Multipart conflict for {scope}/{sender}/{concat.Reference}, part {concat.Sequence}.");
            }

            existing.AcceptedSenders.Add(existing.Sender);
            existing.AcceptedSenders.Add(sender);
            if (!string.IsNullOrWhiteSpace(portName))
                existing.LastPortName = portName;
            if (string.IsNullOrWhiteSpace(existing.MessageId))
                existing.MessageId = IsLegacyGeneration(existing.GenerationId)
                    ? BuildLegacyMessageId(
                        existing.Scope,
                        existing.Sender,
                        existing.Reference,
                        existing.Total)
                    : BuildGeneratedMessageId(existing.GenerationId);
            existing.Parts[concat.Sequence] = content;
            if (!string.IsNullOrWhiteSpace(partIdentity))
                existing.PartIdentities[concat.Sequence] = partIdentity;
            existing.LastUpdated = timestamp;
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
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
            EnsureLoadedLocked();
            List<KeyValuePair<string, Entry>> group = ResolveExistingGroupLocked(
                scope, sender, concat.Reference, concat.Total);
            if (group.Count == 0)
                return Array.Empty<Part>();

            return CombineParts(group.Select(pair => pair.Value))
                .OrderBy(x => x.Key)
                .Select(x => new Part(x.Key, x.Value))
                .ToArray();
        }
    }

    public string GetMessageId(
        string scope,
        string sender,
        SmsConcatInfo concat)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            List<KeyValuePair<string, Entry>> group = ResolveExistingGroupLocked(
                scope, sender, concat.Reference, concat.Total);
            Entry? anchor = group
                .OrderByDescending(pair => pair.Value.LastUpdated)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (anchor == null)
                return string.Empty;
            return string.IsNullOrWhiteSpace(anchor.MessageId)
                ? MessageIdFor(anchor)
                : anchor.MessageId;
        }
    }

    /// <summary>
    /// Định danh của mọi mảnh thuộc một tin. Dùng để đánh dấu "đã phát" cho
    /// từng mảnh: đọc lại đúng slot đó sau khi tin đã ra không được tạo thêm
    /// một nhóm ghép dở mới.
    /// </summary>
    internal IReadOnlyList<string> GetPartIdentities(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return Array.Empty<string>();
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _entries.Values
                .Where(entry => string.Equals(
                    entry.MessageId, messageId, StringComparison.Ordinal))
                .SelectMany(entry => entry.PartIdentities.Values)
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string GetMessageIdForPartIdentity(
        string scope,
        string partIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(partIdentity);
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? entry = _entries.Values
                .Where(candidate => string.Equals(
                    candidate.Scope,
                    scope,
                    StringComparison.Ordinal))
                .Where(candidate => candidate.PartIdentities.Values.Any(value =>
                    string.Equals(value, partIdentity, StringComparison.Ordinal)))
                .OrderByDescending(candidate => candidate.LastUpdated)
                .FirstOrDefault();
            if (entry == null) return string.Empty;
            return string.IsNullOrWhiteSpace(entry.MessageId)
                ? MessageIdFor(entry)
                : entry.MessageId;
        }
    }

    public IReadOnlyList<CompletedSnapshot> GetCompletedSnapshots(
        string? scope = null,
        bool includeAcknowledged = false)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var completed = new List<CompletedSnapshot>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Entry> candidate in _entries
                         .OrderBy(pair => pair.Value.LastUpdated))
            {
                if (visited.Contains(candidate.Key)
                    || scope != null
                    && !string.Equals(candidate.Value.Scope, scope, StringComparison.Ordinal))
                    continue;

                List<KeyValuePair<string, Entry>> group =
                    ExactGroupLocked(candidate.Value);
                if (group.Count == 0) group.Add(candidate);
                foreach (KeyValuePair<string, Entry> pair in group)
                    visited.Add(pair.Key);

                Dictionary<int, string> parts = CombineParts(
                    group.Select(pair => pair.Value));
                if (parts.Count != candidate.Value.Total
                    || Enumerable.Range(1, candidate.Value.Total)
                        .Any(sequence => !parts.ContainsKey(sequence)))
                    continue;

                bool acknowledged = group.All(pair =>
                    pair.Value.DeliveryAcknowledged);
                if (acknowledged && !includeAcknowledged) continue;

                Entry anchor = group
                    .OrderBy(pair => pair.Value.LastUpdated)
                    .First().Value;
                Dictionary<int, string> partIdentities = CombinePartIdentities(
                    group.Select(pair => pair.Value));
                bool requiresSimCleanup = group.Any(pair =>
                        IsLegacyGeneration(pair.Value.GenerationId))
                    || partIdentities.Count < parts.Count
                    || partIdentities.Values.Any(identity =>
                        identity.StartsWith(
                            "sms-stored-",
                            StringComparison.Ordinal));
                string[] storedPartIdentities = partIdentities.Values
                    .Where(identity => identity.StartsWith(
                        "sms-stored-",
                        StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                bool simCleanupConfirmed = partIdentities.Count == parts.Count
                    && storedPartIdentities.Length > 0
                    && storedPartIdentities.All(identity => group.Any(pair =>
                        pair.Value.CleanedPartIdentities.Contains(identity)));
                string messageId = string.IsNullOrWhiteSpace(anchor.MessageId)
                    ? MessageIdFor(anchor)
                    : anchor.MessageId;
                completed.Add(new CompletedSnapshot(
                    messageId,
                    anchor.Scope,
                    group.Select(pair => pair.Value.LastPortName)
                        .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
                        ?? InferLegacyPort(anchor.Scope),
                    anchor.Sender,
                    new SmsConcatInfo(anchor.Reference, anchor.Total, anchor.Total),
                    string.Concat(Enumerable.Range(1, anchor.Total)
                        .Select(sequence => parts[sequence])),
                    acknowledged,
                    requiresSimCleanup,
                    simCleanupConfirmed));
            }

            return completed;
        }
    }

    public void MarkDeliveryAcknowledged(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? anchor = _entries.Values.FirstOrDefault(entry =>
                string.Equals(
                    entry.MessageId,
                    messageId,
                    StringComparison.Ordinal));
            if (anchor == null) return;
            Entry[] matches = ExactGroupLocked(anchor)
                .Select(pair => pair.Value)
                .ToArray();
            if (matches.Length == 0 || matches.All(entry =>
                    entry.DeliveryAcknowledged))
                return;

            Dictionary<string, Entry> rollback = CloneEntriesLocked();
            foreach (Entry entry in matches)
                entry.DeliveryAcknowledged = true;
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
                throw;
            }
        }
    }

    public bool IsDeliveryAcknowledged(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? anchor = _entries.Values.FirstOrDefault(entry =>
                string.Equals(
                    entry.MessageId,
                    messageId,
                    StringComparison.Ordinal));
            if (anchor == null) return false;
            List<KeyValuePair<string, Entry>> group =
                ExactGroupLocked(anchor);
            return group.Count > 0
                && group.All(pair => pair.Value.DeliveryAcknowledged);
        }
    }

    public void MarkPartCleaned(string messageId, string partIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partIdentity);
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? anchor = _entries.Values.FirstOrDefault(entry =>
                string.Equals(entry.MessageId, messageId, StringComparison.Ordinal));
            if (anchor == null)
                throw new InvalidDataException(
                    $"Multipart cleanup target '{messageId}' does not exist.");
            Entry[] group = ExactGroupLocked(anchor)
                .Select(pair => pair.Value)
                .ToArray();
            if (group.Length == 0
                || group.Any(entry => entry.CleanedPartIdentities.Contains(
                    partIdentity)))
                return;

            bool identityBelongsToGroup = group.Any(entry =>
                entry.PartIdentities.Values.Any(identity => string.Equals(
                    identity,
                    partIdentity,
                    StringComparison.Ordinal)));
            if (!identityBelongsToGroup)
                throw new InvalidDataException(
                    $"Part identity '{partIdentity}' does not belong to '{messageId}'.");

            Dictionary<string, Entry> rollback = CloneEntriesLocked();
            anchor.CleanedPartIdentities.Add(partIdentity);
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
                throw;
            }
        }
    }

    public bool IsSimCleanupConfirmed(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? anchor = _entries.Values.FirstOrDefault(entry =>
                string.Equals(entry.MessageId, messageId, StringComparison.Ordinal));
            if (anchor == null) return false;
            Entry[] group = ExactGroupLocked(anchor)
                .Select(pair => pair.Value)
                .ToArray();
            if (group.Length == 0) return false;
            Dictionary<int, string> parts = CombineParts(group);
            Dictionary<int, string> partIdentities = CombinePartIdentities(group);
            string[] storedPartIdentities = partIdentities.Values
                .Where(identity => identity.StartsWith(
                    "sms-stored-",
                    StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return parts.Count > 0
                && partIdentities.Count == parts.Count
                && storedPartIdentities.Length > 0
                && storedPartIdentities.All(identity => group.Any(entry =>
                    entry.CleanedPartIdentities.Contains(identity)));
        }
    }

    public void Complete(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            EnsureLoadedLocked();
            Entry? anchor = _entries.Values.FirstOrDefault(entry =>
                string.Equals(
                    entry.MessageId,
                    messageId,
                    StringComparison.Ordinal));
            if (anchor == null) return;
            List<KeyValuePair<string, Entry>> group =
                ExactGroupLocked(anchor);
            if (group.Count == 0) return;
            Dictionary<string, Entry> rollback = CloneEntriesLocked();
            foreach (KeyValuePair<string, Entry> pair in group)
                _entries.Remove(pair.Key);
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
                throw;
            }
        }
    }

    public void RebindLegacyPortScope(string portName, string newScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newScope);
        lock (_gate)
        {
            EnsureLoadedLocked();
            KeyValuePair<string, Entry>[] moving = _entries
                .Where(pair => string.Equals(
                                   pair.Value.Scope,
                                   portName,
                                   StringComparison.OrdinalIgnoreCase)
                               && string.Equals(
                                   pair.Value.LastPortName,
                                   portName,
                                   StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (moving.Length == 0) return;

            Dictionary<string, Entry> rollback = CloneEntriesLocked();
            DateTimeOffset reboundAt = DateTimeOffset.UtcNow;
            foreach (KeyValuePair<string, Entry> pair in moving)
            {
                _entries.Remove(pair.Key);
                pair.Value.Scope = newScope;
                pair.Value.LastPortName = portName;
                pair.Value.LastUpdated = reboundAt;
                // Keep MessageId stable. It may already be present in the
                // session inbox, and it is the at-least-once deduplication key.
                _entries[UniqueKeyLocked(pair.Value)] = pair.Value;
            }
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
                throw;
            }
        }
    }

    public void Complete(string scope, string sender, SmsConcatInfo concat)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            List<KeyValuePair<string, Entry>> group = ResolveExistingGroupLocked(
                scope, sender, concat.Reference, concat.Total);
            if (group.Count == 0) return;
            Dictionary<string, Entry> rollback = CloneEntriesLocked();
            foreach (KeyValuePair<string, Entry> pair in group)
                _entries.Remove(pair.Key);
            try
            {
                SaveLocked();
            }
            catch
            {
                RestoreAllLocked(rollback);
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
                Entry[] entries = ReadValidatedEntries(_filePath);
                var staged = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (Entry entry in entries)
                {
                    if (!staged.TryAdd(Key(entry), entry))
                        throw new InvalidDataException(
                            "Multipart journal contains a duplicate generation.");
                }

                // Publish only after the entire file has parsed and validated.
                // A corrupt tail must never expose a valid prefix to replay or
                // cleanup callers.
                _entries.Clear();
                foreach (KeyValuePair<string, Entry> pair in staged)
                    _entries[pair.Key] = pair.Value;
            }
            catch (Exception ex) when (IsJournalReadWriteException(ex))
            {
                // Keep the unreadable file for manual recovery. New segments will fail
                // safely on save instead of deleting their SIM records without a journal.
                _entries.Clear();
                _loadFailed = true;
            }
        }
    }

    private static Entry[] ReadValidatedEntries(string path)
    {
        Entry?[]? deserialized = JsonSerializer.Deserialize<Entry?[]>(
            File.ReadAllText(path), JsonOptions);
        if (deserialized == null)
            throw new InvalidDataException(
                "Multipart journal root must be an array, not null.");

        var validated = new Entry[deserialized.Length];
        for (int index = 0; index < deserialized.Length; index++)
        {
            Entry entry = deserialized[index]
                ?? throw new InvalidDataException(
                    "Multipart journal contains a null entry.");
            if (entry.Parts == null)
                throw new InvalidDataException(
                    "Multipart journal contains null parts.");

            NormalizeLoadedEntry(entry);
            ValidateLoadedEntry(entry);
            validated[index] = entry;
        }
        return validated;
    }

    private static void ValidateLoadedEntry(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Scope)
            || string.IsNullOrWhiteSpace(entry.Sender)
            // Direct +CMT messages use a stable positive 31-bit hash as their
            // synthetic reference; carrier multipart references are narrower.
            || entry.Reference < 0
            || entry.Total is < 1 or > 255
            || entry.Parts.Count == 0
            || entry.Parts.Any(part =>
                part.Key < 1
                || part.Key > entry.Total
                || string.IsNullOrWhiteSpace(part.Value))
            || entry.PartIdentities.Any(identity =>
                identity.Key < 1
                || identity.Key > entry.Total
                || !entry.Parts.ContainsKey(identity.Key)
                || string.IsNullOrWhiteSpace(identity.Value))
            || entry.CleanedPartIdentities.Any(string.IsNullOrWhiteSpace)
            || entry.AcceptedSenders.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(entry.GenerationId)
            || string.IsNullOrWhiteSpace(entry.MessageId))
        {
            throw new InvalidDataException(
                "Multipart journal contains an invalid entry.");
        }
    }

    private void SaveLocked()
    {
        if (_inMemory) return;
        SaveJsonAtomicallyLocked(_filePath, _entries.Values.ToArray());
    }

    private static bool IsJournalReadWriteException(Exception ex) =>
        ex is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private void EnsureLoadedLocked()
    {
        if (_loadFailed)
            throw new InvalidDataException(
                $"Multipart journal is unreadable and was preserved at '{_filePath}'.");
    }

    private void SaveJsonAtomicallyLocked(string destinationPath, object value)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string tempPath = destinationPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, value.GetType(), JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        CommitTempFileWithRetry(tempPath, destinationPath);
    }

    private static void CommitTempFileWithRetry(
        string tempPath,
        string destinationPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Re-evaluate the destination on every attempt. ReplaceFile can leave
                // both names intact after a transient sharing/delete conflict, while
                // another failure mode can leave only the replacement name behind.
                if (File.Exists(destinationPath))
                    File.Replace(tempPath, destinationPath, null);
                else
                    File.Move(tempPath, destinationPath);
                return;
            }
            catch (IOException) when (attempt < ReplaceRetryDelaysMs.Length)
            {
                // Antivirus, indexers and backup software can briefly open the journal
                // without FILE_SHARE_DELETE. The decoded segment is already durable in
                // the flushed temp file, so bounded retry is safe and prevents the SIM
                // slot from being retained/re-read because of a millisecond-scale lock.
                Thread.Sleep(ReplaceRetryDelaysMs[attempt]);
            }
        }
    }

    private Dictionary<string, Entry> CloneEntriesLocked() =>
        _entries.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    private void RestoreAllLocked(Dictionary<string, Entry> rollback)
    {
        _entries.Clear();
        foreach (KeyValuePair<string, Entry> pair in rollback)
            _entries[pair.Key] = pair.Value;
    }

    private KeyValuePair<string, Entry>? FindAppendTargetLocked(
        string scope,
        string sender,
        SmsConcatInfo concat,
        string content,
        string? partIdentity,
        DateTimeOffset timestamp)
    {
        KeyValuePair<string, Entry>[] direct = _entries
            .Where(pair => SameMultipart(
                               pair.Value,
                               scope,
                               concat.Reference,
                               concat.Total)
                           && SenderWasAccepted(pair.Value, sender)
                           && (PartIdentityMatches(
                                   pair.Value,
                                   concat.Sequence,
                                   partIdentity)
                               || WithinCorrelationWindow(
                                   pair.Value.LastUpdated,
                                   timestamp))
                           && CanAppendPart(
                               pair.Value,
                               concat.Sequence,
                               content,
                               partIdentity))
            .OrderByDescending(pair => PartIdentityMatches(
                pair.Value, concat.Sequence, partIdentity))
            .ThenByDescending(pair => pair.Value.LastUpdated)
            .ToArray();
        if (direct.Length > 0)
            return direct[0];

        KeyValuePair<string, Entry>[] aliases = _entries
            .Where(pair => SameMultipart(
                               pair.Value,
                               scope,
                               concat.Reference,
                               concat.Total)
                           && SmsMultipartSenderAliases.AreEquivalent(
                               pair.Value.Sender,
                               sender)
                           && (PartIdentityMatches(
                                   pair.Value,
                                   concat.Sequence,
                                   partIdentity)
                               || WithinAliasWindow(
                                   pair.Value.LastUpdated,
                                   timestamp))
                           && CanAppendPart(
                               pair.Value,
                               concat.Sequence,
                               content,
                               partIdentity))
            .OrderByDescending(pair => PartIdentityMatches(
                pair.Value, concat.Sequence, partIdentity))
            .ThenByDescending(pair => pair.Value.LastUpdated)
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private KeyValuePair<string, Entry>? FindTrustedAnchorLocked(
        string scope,
        string sender,
        int reference,
        int total)
    {
        KeyValuePair<string, Entry>[] matches = _entries
            .Where(pair => SameMultipart(pair.Value, scope, reference, total))
            .ToArray();
        KeyValuePair<string, Entry>[] direct = matches
            .Where(pair => string.Equals(
                pair.Value.Sender, sender, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value.LastUpdated)
            .ToArray();
        if (direct.Length > 0) return direct[0];

        KeyValuePair<string, Entry>[] accepted = matches
            .Where(pair => pair.Value.AcceptedSenders.Any(value =>
                string.Equals(value, sender, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(pair => pair.Value.LastUpdated)
            .ToArray();
        return accepted.Length > 0 ? accepted[0] : null;
    }

    private static bool SenderWasAccepted(Entry entry, string sender) =>
        string.Equals(entry.Sender, sender, StringComparison.OrdinalIgnoreCase)
        || entry.AcceptedSenders.Any(value => string.Equals(
            value, sender, StringComparison.OrdinalIgnoreCase));

    private static bool PartIdentityMatches(
        Entry entry,
        int sequence,
        string? partIdentity) =>
        !string.IsNullOrWhiteSpace(partIdentity)
        && entry.PartIdentities.TryGetValue(sequence, out string? existing)
        && string.Equals(existing, partIdentity, StringComparison.Ordinal);

    private static bool CanAppendPart(
        Entry entry,
        int sequence,
        string content,
        string? partIdentity)
    {
        if (!IsPartCompatible(entry, sequence, content))
            return false;
        if (string.IsNullOrWhiteSpace(partIdentity)
            || !entry.PartIdentities.TryGetValue(sequence, out string? existing)
            || string.IsNullOrWhiteSpace(existing))
            return true;
        return string.Equals(existing, partIdentity, StringComparison.Ordinal);
    }

    private IEnumerable<KeyValuePair<string, Entry>> FindCompatibleAliasesLocked(
        KeyValuePair<string, Entry> anchor,
        string scope,
        string sender,
        SmsConcatInfo concat,
        string content) =>
        _entries.Where(pair =>
            !string.Equals(pair.Key, anchor.Key, StringComparison.Ordinal)
            && SameMultipart(pair.Value, scope, concat.Reference, concat.Total)
            && SmsMultipartSenderAliases.AreEquivalent(pair.Value.Sender, sender)
            && WithinAliasWindow(pair.Value.LastUpdated, anchor.Value.LastUpdated)
            && CanGroupEntries(anchor.Value, pair.Value)
            && EntriesAreCompatible(anchor.Value, pair.Value)
            && IsPartCompatible(pair.Value, concat.Sequence, content));

    private List<KeyValuePair<string, Entry>> ResolveExistingGroupLocked(
        string scope,
        string sender,
        int reference,
        int total)
    {
        KeyValuePair<string, Entry>? anchor = FindTrustedAnchorLocked(
            scope, sender, reference, total);
        if (!anchor.HasValue)
        {
            KeyValuePair<string, Entry>[] aliases = _entries
                .Where(pair => SameMultipart(pair.Value, scope, reference, total)
                               && SmsMultipartSenderAliases.AreEquivalent(
                                   pair.Value.Sender, sender))
                .ToArray();
            if (aliases.Length != 1) return new();
            anchor = aliases[0];
        }

        var group = new List<KeyValuePair<string, Entry>> { anchor.Value };
        group.AddRange(_entries.Where(pair =>
            !string.Equals(pair.Key, anchor.Value.Key, StringComparison.Ordinal)
            && SameMultipart(pair.Value, scope, reference, total)
            && SmsMultipartSenderAliases.AreEquivalent(pair.Value.Sender, sender)
            && WithinAliasWindow(pair.Value.LastUpdated, anchor.Value.Value.LastUpdated)
            && CanGroupEntries(anchor.Value.Value, pair.Value)
            && EntriesAreCompatible(anchor.Value.Value, pair.Value)));
        return group;
    }

    /// <summary>
    /// Resolves only the persisted carrier generation represented by
    /// <paramref name="anchor"/>.  A concatenation reference is only 8/16 bits
    /// and is routinely reused; acknowledgement/cleanup must therefore never
    /// re-resolve by scope/sender/reference alone and accidentally mutate a
    /// newer message generation.
    /// </summary>
    private List<KeyValuePair<string, Entry>> ExactGroupLocked(Entry anchor)
    {
        if (!IsLegacyGeneration(anchor.GenerationId))
        {
            return _entries
                .Where(pair =>
                    string.Equals(
                        pair.Value.GenerationId,
                        anchor.GenerationId,
                        StringComparison.Ordinal)
                    || string.Equals(
                        pair.Value.MessageId,
                        anchor.MessageId,
                        StringComparison.Ordinal))
                .ToList();
        }

        // Legacy journals predate GenerationId.  Their split 888/565656
        // entries are one group only when the old narrow alias/time/content
        // safeguards all agree.  New entries always use a random generation.
        return _entries
            .Where(pair =>
                IsLegacyGeneration(pair.Value.GenerationId)
                && SameMultipart(
                    pair.Value,
                    anchor.Scope,
                    anchor.Reference,
                    anchor.Total)
                && SmsMultipartSenderAliases.AreEquivalent(
                    pair.Value.Sender,
                    anchor.Sender)
                && WithinAliasWindow(
                    pair.Value.LastUpdated,
                    anchor.LastUpdated)
                && EntriesAreCompatible(pair.Value, anchor))
            .ToList();
    }

    private void MergeEntryLocked(
        string targetKey,
        Entry target,
        KeyValuePair<string, Entry> source)
    {
        foreach (KeyValuePair<int, string> part in source.Value.Parts)
            target.Parts[part.Key] = part.Value;
        foreach (KeyValuePair<int, string> identity in source.Value.PartIdentities)
            target.PartIdentities.TryAdd(identity.Key, identity.Value);
        foreach (string identity in source.Value.CleanedPartIdentities)
            target.CleanedPartIdentities.Add(identity);
        target.AcceptedSenders.Add(target.Sender);
        target.AcceptedSenders.Add(source.Value.Sender);
        foreach (string acceptedSender in source.Value.AcceptedSenders)
            target.AcceptedSenders.Add(acceptedSender);
        if (string.IsNullOrWhiteSpace(target.MessageId))
            target.MessageId = source.Value.MessageId;
        target.DeliveryAcknowledged |= source.Value.DeliveryAcknowledged;
        if (source.Value.LastUpdated > target.LastUpdated)
        {
            target.LastUpdated = source.Value.LastUpdated;
            if (!string.IsNullOrWhiteSpace(source.Value.LastPortName))
                target.LastPortName = source.Value.LastPortName;
        }
        if (!string.Equals(source.Key, targetKey, StringComparison.Ordinal))
            _entries.Remove(source.Key);
    }

    /// <summary>
    /// Ghép lại những tin đã bị chẻ thành nhiều nhóm ghép dở vì người gửi được
    /// firmware trả ở hai dạng khác nhau trong cùng một tin. Chỉ hợp nhất khi
    /// kết quả tạo ra một tin ĐỦ mảnh và không mảnh nào xung đột nội dung –
    /// nhóm còn thiếu mảnh vẫn được giữ nguyên để không bao giờ trộn hai tin
    /// khác nhau dùng trùng concat reference. Trả về số tin đã cứu được.
    /// </summary>
    internal int SalvageSplitSenderGroups()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_loadFailed) return 0;

            int salvaged = 0;
            bool changed = false;
            foreach (IGrouping<(string Scope, int Reference, int Total), KeyValuePair<string, Entry>> group
                     in _entries
                         .GroupBy(pair => (
                             pair.Value.Scope,
                             pair.Value.Reference,
                             pair.Value.Total))
                         .ToArray())
            {
                KeyValuePair<string, Entry>[] candidates = group.ToArray();
                if (candidates.Length < 2) continue;

                KeyValuePair<string, Entry> anchor = candidates
                    .OrderBy(pair => pair.Value.LastUpdated)
                    .First();
                KeyValuePair<string, Entry>[] mergeable = candidates
                    .Where(pair => !string.Equals(
                            pair.Key, anchor.Key, StringComparison.Ordinal)
                        && SmsMultipartSenderAliases.AreEquivalent(
                            pair.Value.Sender, anchor.Value.Sender)
                        && EntriesAreCompatible(anchor.Value, pair.Value))
                    .ToArray();
                if (mergeable.Length == 0) continue;

                Dictionary<int, string> combined = CombineParts(
                    mergeable.Select(pair => pair.Value).Prepend(anchor.Value));
                bool complete = anchor.Value.Total > 0
                    && Enumerable.Range(1, anchor.Value.Total)
                        .All(combined.ContainsKey);
                if (!complete) continue;

                foreach (KeyValuePair<string, Entry> source in mergeable)
                    MergeEntryLocked(anchor.Key, anchor.Value, source);
                salvaged++;
                changed = true;
            }

            if (changed) SaveLocked();
            return salvaged;
        }
    }

    /// <summary>
    /// Các nhóm còn thiếu mảnh quá lâu. Chúng không tự hiện được nên phải nhìn
    /// thấy được thay vì im lặng nằm trong journal.
    /// </summary>
    internal IReadOnlyList<string> DescribeStalledGroups(
        TimeSpan olderThan,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_loadFailed) return Array.Empty<string>();

            return _entries.Values
                .Where(entry => !entry.DeliveryAcknowledged
                    && entry.Total > 0
                    && entry.Parts.Count < entry.Total
                    && now - entry.LastUpdated >= olderThan)
                .OrderBy(entry => entry.LastUpdated)
                .Select(entry =>
                    $"port={entry.LastPortName}; sender={entry.Sender}; ref={entry.Reference}; "
                    + $"parts={entry.Parts.Count}/{entry.Total}; missing="
                    + string.Join(
                        ",",
                        Enumerable.Range(1, entry.Total)
                            .Where(sequence => !entry.Parts.ContainsKey(sequence)))
                    + $"; last={entry.LastUpdated:HH:mm:ss}")
                .ToArray();
        }
    }

    /// <summary>
    /// Returns incomplete multipart messages that have been waiting longer than
    /// the caller's safety window. The SIM parts may already have been deleted
    /// after they were durably journaled, so they must not be silently hidden
    /// forever just because a carrier segment never arrives.
    /// </summary>
    internal IReadOnlyList<StalledSnapshot> GetStalledSnapshots(
        TimeSpan olderThan,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_loadFailed) return Array.Empty<StalledSnapshot>();

            var snapshots = new List<StalledSnapshot>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Entry> candidate in _entries
                         .OrderBy(pair => pair.Value.LastUpdated))
            {
                if (visited.Contains(candidate.Key)) continue;

                List<KeyValuePair<string, Entry>> group =
                    ExactGroupLocked(candidate.Value);
                if (group.Count == 0) group.Add(candidate);
                foreach (KeyValuePair<string, Entry> pair in group)
                    visited.Add(pair.Key);

                Entry anchor = group
                    .OrderBy(pair => pair.Value.LastUpdated)
                    .First().Value;
                Dictionary<int, string> parts = CombineParts(
                    group.Select(pair => pair.Value));
                if (anchor.DeliveryAcknowledged
                    || anchor.PartialDeliveryAcknowledged
                    || anchor.Total <= 0
                    || parts.Count == 0
                    || parts.Count >= anchor.Total
                    || now - anchor.LastUpdated < olderThan)
                {
                    continue;
                }

                snapshots.Add(new StalledSnapshot(
                    string.IsNullOrWhiteSpace(anchor.MessageId)
                        ? MessageIdFor(anchor)
                        : anchor.MessageId,
                    anchor.Scope,
                    anchor.LastPortName,
                    anchor.Sender,
                    new SmsConcatInfo(anchor.Reference, anchor.Total, 1),
                    string.Concat(parts.OrderBy(pair => pair.Key)
                        .Select(pair => pair.Value)),
                    parts.Count,
                    anchor.LastUpdated,
                    anchor.PartialDeliveryAcknowledged));
            }

            return snapshots;
        }
    }

    /// <summary>
    /// Marks the one-time partial fallback as delivered. This is deliberately
    /// separate from DeliveryAcknowledged: a late missing segment may still
    /// complete the original message and must remain eligible for full replay.
    /// </summary>
    internal void MarkPartialDeliveryAcknowledged(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            EnsureLoadedLocked();
            KeyValuePair<string, Entry>[] matches = _entries
                .Where(pair => string.Equals(
                    pair.Value.MessageId,
                    messageId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0) return;

            bool changed = false;
            foreach (KeyValuePair<string, Entry> pair in matches)
            {
                if (!pair.Value.PartialDeliveryAcknowledged)
                {
                    pair.Value.PartialDeliveryAcknowledged = true;
                    changed = true;
                }
            }

            if (changed) SaveLocked();
        }
    }

    private static bool SameMultipart(
        Entry entry,
        string scope,
        int reference,
        int total) =>
        string.Equals(entry.Scope, scope, StringComparison.Ordinal)
        && entry.Reference == reference
        && entry.Total == total;

    private static bool IsPartCompatible(Entry entry, int sequence, string content) =>
        !entry.Parts.TryGetValue(sequence, out string? existing)
        || string.Equals(existing, content, StringComparison.Ordinal);

    private static bool EntriesAreCompatible(Entry left, Entry right) =>
        left.Parts.All(part => !right.Parts.TryGetValue(part.Key, out string? other)
                               || string.Equals(part.Value, other, StringComparison.Ordinal));

    private static bool CanGroupEntries(Entry left, Entry right) =>
        string.Equals(
            left.GenerationId,
            right.GenerationId,
            StringComparison.Ordinal)
        || IsLegacyGeneration(left.GenerationId)
        && IsLegacyGeneration(right.GenerationId);

    private static Dictionary<int, string> CombineParts(IEnumerable<Entry> entries)
    {
        var parts = new Dictionary<int, string>();
        foreach (Entry entry in entries)
            foreach (KeyValuePair<int, string> part in entry.Parts)
                parts.TryAdd(part.Key, part.Value);
        return parts;
    }

    private static Dictionary<int, string> CombinePartIdentities(
        IEnumerable<Entry> entries)
    {
        var identities = new Dictionary<int, string>();
        foreach (Entry entry in entries)
            foreach (KeyValuePair<int, string> part in entry.PartIdentities)
                identities.TryAdd(part.Key, part.Value);
        return identities;
    }

    private static bool WithinAliasWindow(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= SmsMultipartSenderAliases.HandoffWindow;

    private static bool WithinCorrelationWindow(
        DateTimeOffset left,
        DateTimeOffset right) =>
        (left - right).Duration() <= CorrelationWindow;

    private static void NormalizeLoadedEntry(Entry entry)
    {
        entry.AcceptedSenders = new HashSet<string>(
            entry.AcceptedSenders ?? [], StringComparer.OrdinalIgnoreCase);
        entry.AcceptedSenders.Add(entry.Sender);
        entry.Parts ??= new Dictionary<int, string>();
        entry.PartIdentities ??= new Dictionary<int, string>();
        entry.CleanedPartIdentities = new HashSet<string>(
            entry.CleanedPartIdentities ?? [],
            StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(entry.GenerationId))
            entry.GenerationId = BuildLegacyGenerationId(
                entry.Scope,
                entry.Sender,
                entry.Reference,
                entry.Total);
        if (string.IsNullOrWhiteSpace(entry.MessageId))
            entry.MessageId = MessageIdFor(entry);
        if (string.IsNullOrWhiteSpace(entry.LastPortName))
            entry.LastPortName = InferLegacyPort(entry.Scope);
    }

    private static string BuildLegacyGenerationId(
        string scope,
        string sender,
        int reference,
        int total)
    {
        string identity =
            $"legacy\u001f{scope}\u001f{sender}\u001f{reference}\u001f{total}";
        return $"legacy-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static string BuildLegacyMessageId(
        string scope,
        string sender,
        int reference,
        int total)
    {
        string identity =
            $"multipart\u001f{scope}\u001f{sender}\u001f{reference}\u001f{total}";
        return $"sms-mp-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static string BuildGeneratedMessageId(string generationId) =>
        $"sms-mp-{generationId}";

    private static bool IsLegacyGeneration(string generationId) =>
        generationId.StartsWith("legacy-", StringComparison.Ordinal);

    private static string MessageIdFor(Entry entry) =>
        IsLegacyGeneration(entry.GenerationId)
            ? BuildLegacyMessageId(
                entry.Scope,
                entry.Sender,
                entry.Reference,
                entry.Total)
            : BuildGeneratedMessageId(entry.GenerationId);

    private static string InferLegacyPort(string scope)
    {
        string candidate = scope.Split('\u001f', 2)[0].Trim();
        return Regex.IsMatch(candidate, @"^COM\d+$", RegexOptions.IgnoreCase)
            ? candidate
            : string.Empty;
    }

    private static string Key(Entry entry) =>
        $"{entry.Scope}\u001f{entry.Sender}\u001f{entry.Reference}\u001f{entry.Total}\u001f{entry.GenerationId}";

    private string UniqueKeyLocked(Entry entry)
    {
        string key = Key(entry);
        if (!_entries.ContainsKey(key)) return key;
        bool wasLegacy = IsLegacyGeneration(entry.GenerationId);
        entry.GenerationId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(entry.MessageId)
            || wasLegacy)
            entry.MessageId = BuildGeneratedMessageId(entry.GenerationId);
        return Key(entry);
    }
}
