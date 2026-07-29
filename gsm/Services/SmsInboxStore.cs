using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace gsm.Services;

public sealed record SmsInboxRecord
{
    public required string DeliveryId { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public DateTimeOffset? SmsTimestampUtc { get; init; }
    public required string PortName { get; init; }
    public string ReceiverPhone { get; init; } = string.Empty;
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public string Otp { get; init; } = string.Empty;
    public string NetworkProvider { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string CallCount { get; init; } = "0";
    public string ForwardContent { get; init; } = string.Empty;
}

/// <summary>
/// Append-only durable inbox for complete decoded SMS messages. Each JSON object
/// occupies one physical line, so SMS content may safely contain Unicode, CR/LF,
/// quotes and commas without changing the record framing.
/// </summary>
public sealed class SmsInboxStore
{
    private const int RecentDeliveryIdCapacity = 100_000;
    private const string FilePrefix = "sms-inbox-";
    private const string FileExtension = ".jsonl";
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _directoryPath;
    private readonly bool _durableWrites;
    private readonly Action? _beforeFlushForTests;
    private readonly Dictionary<string, string> _recentDeliveryFingerprints =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _recentDeliveryOrder = new();
    private readonly HashSet<string> _tornTailFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedCorruptLines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recoveryAppendPaths =
        new(StringComparer.Ordinal);
    private readonly List<string> _recoveryWarnings = new();
    private long _count;

    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToolGSM",
        "Data",
        "SmsInbox");

    public SmsInboxStore(string? directoryPath = null)
        : this(directoryPath, durableWrites: true, beforeFlushForTests: null)
    {
    }

    internal SmsInboxStore(
        string? directoryPath,
        bool durableWrites,
        Action? beforeFlushForTests = null)
    {
        string selectedPath = string.IsNullOrWhiteSpace(directoryPath)
            ? DefaultDirectoryPath
            : directoryPath;
        _directoryPath = Path.GetFullPath(selectedPath);
        _durableWrites = durableWrites;
        _beforeFlushForTests = beforeFlushForTests;
        LoadDeliveryIds();
    }

    public string DirectoryPath => _directoryPath;
    public IReadOnlyList<string> RecoveryWarnings
    {
        get
        {
            lock (_gate)
                return _recoveryWarnings.ToArray();
        }
    }

    public long Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>
    /// Writes and flushes a new record before returning. Returns false when the
    /// same DeliveryId was already committed, including by a previous process.
    /// Storage and serialization failures are deliberately propagated.
    /// </summary>
    public bool Append(SmsInboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        lock (_gate)
        {
            string fingerprint = PayloadFingerprint(record);
            if (_recentDeliveryFingerprints.TryGetValue(
                    record.DeliveryId,
                    out string? existingFingerprint))
            {
                if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Conflicting SMS payload for DeliveryId '{record.DeliveryId}'.");
                }

                return false;
            }

            Directory.CreateDirectory(_directoryPath);
            string filePath = FilePathFor(record.ReceivedAtUtc);
            string json = JsonSerializer.Serialize(record, JsonOptions);
            byte[] line = Utf8NoBom.GetBytes(json + "\n");

            var options = new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = _durableWrites ? FileOptions.WriteThrough : FileOptions.None
            };
            try
            {
                using var stream = new FileStream(filePath, options);
                stream.Write(line);
                _beforeFlushForTests?.Invoke();
                if (_durableWrites)
                    stream.Flush(flushToDisk: true);
                else
                    stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MarkFailedAppend(filePath, ex.Message);
                throw;
            }

            RememberDelivery(record, fingerprint);
            return true;
        }
    }

    /// <summary>
    /// Returns at most <paramref name="count"/> newest committed records without
    /// loading the complete history into the returned collection.
    /// </summary>
    public IReadOnlyList<SmsInboxRecord> GetRecent(int count)
    {
        if (count <= 0) return Array.Empty<SmsInboxRecord>();

        lock (_gate)
        {
            if (!Directory.Exists(_directoryPath))
                return Array.Empty<SmsInboxRecord>();

            var result = new List<SmsInboxRecord>(Math.Min(count, 5000));
            var seenDeliveryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string filePath in EnumerateInboxFiles(descending: true))
            {
                foreach (SmsInboxRecord record in ReadRecordsSafely(filePath).Reverse())
                {
                    // A power loss during Flush(true) can leave a complete but
                    // uncertain line; retry then commits the same DeliveryId in
                    // a recovery file. Keep both physical copies for recovery,
                    // but expose exactly one inbox row. Also hide a conflicting
                    // corrupt copy when the canonical fingerprint is known.
                    if (_recentDeliveryFingerprints.TryGetValue(
                            record.DeliveryId,
                            out string? canonicalFingerprint)
                        && !string.Equals(
                            canonicalFingerprint,
                            PayloadFingerprint(record),
                            StringComparison.Ordinal))
                        continue;
                    if (!seenDeliveryIds.Add(record.DeliveryId))
                        continue;

                    result.Add(record);
                    if (result.Count == count)
                        return result;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Permanently removes the selected durable inbox records. The UI must not
    /// remove a row only from memory because LoadSmsInbox would restore it on
    /// the next render/restart.
    /// </summary>
    public int Delete(IEnumerable<string> deliveryIds)
    {
        ArgumentNullException.ThrowIfNull(deliveryIds);
        HashSet<string> targets = deliveryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0) return 0;

        lock (_gate)
        {
            if (!Directory.Exists(_directoryPath)) return 0;

            int deleted = 0;
            foreach (string filePath in EnumerateInboxFiles(descending: false))
            {
                List<SmsInboxRecord> records = ReadRecordsSafely(filePath).ToList();
                List<SmsInboxRecord> remaining = records
                    .Where(record => !targets.Contains(record.DeliveryId))
                    .ToList();
                int removedFromFile = records.Count - remaining.Count;
                if (removedFromFile == 0) continue;

                if (remaining.Count == 0)
                {
                    File.Delete(filePath);
                }
                else
                {
                    RewriteFile(filePath, remaining);
                }

                deleted += removedFromFile;
            }

            if (deleted > 0)
                RebuildIndexesLocked();
            return deleted;
        }
    }

    /// <summary>
    /// Permanently clears the application SMS history. This does not send
    /// AT+CMGD and therefore does not delete unread SMS still stored in SIMs.
    /// </summary>
    public int Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directoryPath))
            {
                ResetIndexesLocked();
                return 0;
            }

            string[] files = EnumerateInboxFiles(descending: false).ToArray();
            int deleted = 0;
            foreach (string filePath in files)
            {
                deleted += ReadRecordsSafely(filePath).Count();
                File.Delete(filePath);
            }

            ResetIndexesLocked();
            return deleted;
        }
    }

    /// <summary>
    /// Deterministic fallback for older event producers that do not yet provide
    /// a transport-level DeliveryId.
    /// </summary>
    public static string CreateDeliveryId(params string?[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthPrefix = stackalloc byte[4];
        foreach (string value in fields.Select(field => field ?? string.Empty))
        {
            byte[] bytes = Utf8NoBom.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);
            hash.AppendData(lengthPrefix);
            hash.AppendData(bytes);
        }

        return $"sms-v1-{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private void LoadDeliveryIds()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directoryPath)) return;

            foreach (string filePath in EnumerateInboxFiles(descending: false))
            {
                foreach (SmsInboxRecord record in ReadRecordsSafely(filePath))
                {
                    string fingerprint = PayloadFingerprint(record);
                    if (_recentDeliveryFingerprints.TryGetValue(
                            record.DeliveryId,
                            out string? existingFingerprint))
                    {
                        if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                        {
                            MarkCorruptLine(
                                filePath,
                                0,
                                $"conflicting payload for DeliveryId '{record.DeliveryId}'; preserved both records and kept the first deduplication identity");
                        }

                        continue;
                    }

                    RememberDelivery(record, fingerprint);
                }
            }

            ResolveRecoveryAppendPaths();
        }
    }

    private void RebuildIndexesLocked()
    {
        ResetIndexesLocked();
        LoadDeliveryIds();
    }

    private void ResetIndexesLocked()
    {
        _recentDeliveryFingerprints.Clear();
        _recentDeliveryOrder.Clear();
        _tornTailFiles.Clear();
        _reportedCorruptLines.Clear();
        _recoveryAppendPaths.Clear();
        _recoveryWarnings.Clear();
        _count = 0;
    }

    private void RewriteFile(
        string filePath,
        IReadOnlyList<SmsInboxRecord> records)
    {
        string temporaryPath = filePath
            + $".delete-{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = _durableWrites ? FileOptions.WriteThrough : FileOptions.None
            };
            using (var stream = new FileStream(temporaryPath, options))
            {
                foreach (SmsInboxRecord record in records)
                {
                    byte[] line = Utf8NoBom.GetBytes(
                        JsonSerializer.Serialize(record, JsonOptions) + "\n");
                    stream.Write(line);
                }

                if (_durableWrites)
                    stream.Flush(flushToDisk: true);
                else
                    stream.Flush();
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original delete result; a leftover temp file is
                // outside the inbox filename pattern and is harmless.
            }
        }
    }

    private IReadOnlyList<string> EnumerateInboxFiles(bool descending)
    {
        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(
                _directoryPath,
                $"{FilePrefix}*{FileExtension}",
                SearchOption.TopDirectoryOnly);
            return (descending
                    ? files.OrderByDescending(FileSortKey, StringComparer.Ordinal)
                    : files.OrderBy(FileSortKey, StringComparer.Ordinal))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException)
        {
            _recoveryWarnings.Add(
                $"Could not enumerate SMS inbox directory '{_directoryPath}': {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private IEnumerable<SmsInboxRecord> ReadRecords(string filePath)
    {
        bool hasTerminatingNewline = EndsWithLineFeed(filePath);
        using var reader = new StreamReader(filePath, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        int lineNumber = 0;
        string? line = reader.ReadLine();
        while (line != null)
        {
            lineNumber++;
            string? nextLine = reader.ReadLine();
            bool isFinalLine = nextLine == null;
            if (string.IsNullOrWhiteSpace(line))
            {
                line = nextLine;
                continue;
            }

            SmsInboxRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<SmsInboxRecord>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                if (isFinalLine)
                {
                    MarkTornTail(filePath, lineNumber, ex.Message);
                    yield break;
                }

                MarkCorruptLine(filePath, lineNumber, $"invalid JSON: {ex.Message}");
                line = nextLine;
                continue;
            }

            if (record == null)
            {
                if (isFinalLine)
                {
                    MarkTornTail(filePath, lineNumber, "deserialized record is null");
                    yield break;
                }

                MarkCorruptLine(filePath, lineNumber, "deserialized record is null");
                line = nextLine;
                continue;
            }

            try
            {
                Validate(record);
            }
            catch (ArgumentException ex)
            {
                if (isFinalLine)
                {
                    MarkTornTail(filePath, lineNumber, ex.Message);
                    yield break;
                }

                MarkCorruptLine(filePath, lineNumber, $"invalid record: {ex.Message}");
                line = nextLine;
                continue;
            }

            if (isFinalLine && !hasTerminatingNewline)
            {
                // The JSON object is complete and safe to display, but appending
                // directly would concatenate the next object onto the same line.
                MarkTornTail(
                    filePath,
                    lineNumber,
                    "valid final JSON record has no terminating newline; future writes use a recovery file");
            }

            yield return record;
            line = nextLine;
        }
    }

    private IEnumerable<SmsInboxRecord> ReadRecordsSafely(string filePath)
    {
        using IEnumerator<SmsInboxRecord> records =
            ReadRecords(filePath).GetEnumerator();
        while (true)
        {
            bool hasRecord = false;
            Exception? readError = null;
            try
            {
                hasRecord = records.MoveNext();
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or DecoderFallbackException)
            {
                readError = ex;
            }

            if (readError != null)
            {
                MarkUnreadableFile(filePath, readError.Message);
                yield break;
            }
            if (!hasRecord) yield break;
            yield return records.Current;
        }
    }

    private static bool EndsWithLineFeed(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private string FilePathFor(DateTimeOffset receivedAtUtc)
    {
        string day = receivedAtUtc.UtcDateTime.ToString("yyyyMMdd");
        return _recoveryAppendPaths.TryGetValue(day, out string? recoveryPath)
            ? recoveryPath
            : Path.Combine(_directoryPath, $"{FilePrefix}{day}{FileExtension}");
    }

    private void RememberDelivery(SmsInboxRecord record, string fingerprint)
    {
        _recentDeliveryFingerprints.Add(record.DeliveryId, fingerprint);
        _recentDeliveryOrder.Enqueue(record.DeliveryId);
        _count++;

        while (_recentDeliveryOrder.Count > RecentDeliveryIdCapacity)
        {
            string expired = _recentDeliveryOrder.Dequeue();
            _recentDeliveryFingerprints.Remove(expired);
        }
    }

    private static string PayloadFingerprint(SmsInboxRecord record)
    {
        // DeliveryId is transport-stable, while COM, sender representation,
        // extracted OTP and UI metadata can legitimately change when the same
        // still-stored SIM record is replayed after a restart or SIM move. Only
        // the normalized carrier payload may turn an existing DeliveryId into a
        // real conflict; otherwise the replay must be acknowledged idempotently.
        string normalizedContent = record.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .Normalize(NormalizationForm.FormC);
        return CreateDeliveryId("payload-v2", normalizedContent);
    }

    private void MarkTornTail(string filePath, int lineNumber, string detail)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!_tornTailFiles.Add(fullPath)) return;
        _recoveryWarnings.Add(
            $"Preserved torn final SMS inbox line at '{fullPath}', line {lineNumber}: {detail}");
    }

    private void MarkFailedAppend(string filePath, string detail)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (_tornTailFiles.Add(fullPath))
        {
            _recoveryWarnings.Add(
                $"SMS inbox append did not complete for '{fullPath}'; subsequent writes use a recovery file: {detail}");
        }

        AssignRecoveryAppendPath(FileDay(fullPath));
    }

    private void MarkCorruptLine(string filePath, int lineNumber, string detail)
    {
        string fullPath = Path.GetFullPath(filePath);
        string warningKey = $"{fullPath}\0{lineNumber}";
        if (!_reportedCorruptLines.Add(warningKey)) return;
        _recoveryWarnings.Add(
            $"Preserved and skipped corrupt SMS inbox line at '{fullPath}', line {lineNumber}: {detail}");
    }

    private void MarkUnreadableFile(string filePath, string detail)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!_tornTailFiles.Add(fullPath)) return;
        _recoveryWarnings.Add(
            $"Preserved unreadable SMS inbox file '{fullPath}'; future writes use a recovery file: {detail}");
        AssignRecoveryAppendPath(FileDay(fullPath));
    }

    private void ResolveRecoveryAppendPaths()
    {
        foreach (string day in _tornTailFiles.Select(FileDay).Distinct(StringComparer.Ordinal))
            AssignRecoveryAppendPath(day);
    }

    private void AssignRecoveryAppendPath(string day)
    {
        string? reusable = null;
        try
        {
            reusable = Directory.EnumerateFiles(
                    _directoryPath,
                    $"{FilePrefix}{day}*{FileExtension}",
                    SearchOption.TopDirectoryOnly)
                .Where(path => !_tornTailFiles.Contains(Path.GetFullPath(path)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException)
        {
            _recoveryWarnings.Add(
                $"Could not inspect SMS recovery files for day '{day}': {ex.Message}");
        }

        _recoveryAppendPaths[day] = reusable ?? Path.Combine(
            _directoryPath,
            $"{FilePrefix}{day}-recovery-{Guid.NewGuid():N}{FileExtension}");
    }

    private static string FileDay(string filePath)
    {
        string name = Path.GetFileName(filePath);
        int start = FilePrefix.Length;
        return name.Length >= start + 8
            ? name.Substring(start, 8)
            : "unknown";
    }

    private static string FileSortKey(string filePath)
    {
        string day = FileDay(filePath);
        long ticks = File.GetLastWriteTimeUtc(filePath).Ticks;
        return $"{day}-{ticks:D19}-{Path.GetFileName(filePath)}";
    }

    private static void Validate(SmsInboxRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Content);
        if (record.ReceivedAtUtc == default)
            throw new ArgumentException("ReceivedAtUtc is required.", nameof(record));
    }
}
