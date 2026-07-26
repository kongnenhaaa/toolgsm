using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace gsm.Services;

internal enum PendingImeiOperationKind
{
    LegacyNoSim = 0,
    CreateNew = 1,
    Restore = 2
}

internal enum PendingImeiOperationPhase
{
    Prepared = 0,
    SlotVerified = 1,
    AwaitingSim = 2,
    Blocked = 3
}

internal sealed record PendingImeiJournalEntry
{
    public string OperationId { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public string TargetImei { get; init; } = string.Empty;
    public string ExpectedCcid { get; init; } = string.Empty;
    public PendingImeiOperationKind Kind { get; init; } =
        PendingImeiOperationKind.LegacyNoSim;
    public PendingImeiOperationPhase Phase { get; init; } =
        PendingImeiOperationPhase.Prepared;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UnixEpoch;
}

/// <summary>
/// Durable write-ahead journal for an IMEI target that has not yet been bound
/// to its final CCID mapping. Version 2 keeps an operation identity so a late
/// task cannot remove or rebind a newer operation on the same COM.
/// </summary>
internal sealed class PendingNoSimImeiJournal
{
    private const int LegacyVersion = 1;
    private const int CurrentVersion = 2;

    private sealed class SnapshotDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public long Revision { get; set; }
        public Dictionary<string, PendingImeiJournalEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LoadedSnapshot(
        int Version,
        long Revision,
        Dictionary<string, PendingImeiJournalEntry> Entries)
    {
        public bool RequiresMigration => Version == LegacyVersion;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly string _primaryPath;
    private readonly string _fallbackPath;
    private Dictionary<string, PendingImeiJournalEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private bool _loadFailed;

    public PendingNoSimImeiJournal(string primaryPath, string fallbackPath)
    {
        if (string.IsNullOrWhiteSpace(primaryPath))
            throw new ArgumentException("Primary journal path is required.", nameof(primaryPath));
        if (string.IsNullOrWhiteSpace(fallbackPath))
            throw new ArgumentException("Fallback journal path is required.", nameof(fallbackPath));

        _primaryPath = Path.GetFullPath(primaryPath);
        _fallbackPath = Path.GetFullPath(fallbackPath);
        LoadLatestSnapshot();
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Legacy value-only read used by the current ViewModel. New recovery code
    /// should use TryGetEntry so it can retain the operation and CCID guards.
    /// </summary>
    public bool TryGetValue(string portName, out string imei)
    {
        if (TryGetEntry(portName, out PendingImeiJournalEntry entry))
        {
            imei = entry.TargetImei;
            return true;
        }

        imei = string.Empty;
        return false;
    }

    public bool TryGetEntry(
        string portName,
        out PendingImeiJournalEntry entry)
    {
        string normalizedPort = NormalizePortName(portName);
        lock (_sync)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(normalizedPort, out PendingImeiJournalEntry? found))
            {
                entry = found with { };
                return true;
            }
        }

        entry = new PendingImeiJournalEntry();
        return false;
    }

    public IReadOnlyList<string> GetImeiSnapshot(string? excludedPortName = null)
    {
        string excluded = NormalizePortName(excludedPortName);
        lock (_sync)
        {
            EnsureLoaded();
            return _entries
                .Where(pair => string.IsNullOrEmpty(excluded)
                    || !string.Equals(
                        pair.Key, excluded, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value.TargetImei)
                .ToArray();
        }
    }

    public IReadOnlyList<PendingImeiJournalEntry> GetEntriesSnapshot(
        string? excludedPortName = null)
    {
        string excluded = NormalizePortName(excludedPortName);
        lock (_sync)
        {
            EnsureLoaded();
            return _entries
                .Where(pair => string.IsNullOrEmpty(excluded)
                    || !string.Equals(
                        pair.Key, excluded, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value with { })
                .ToArray();
        }
    }

    /// <summary>
    /// Persists the target before the caller mutates modem state. Repeating the
    /// exact operation is idempotent. A different operation id explicitly
    /// supersedes the old operation for this COM.
    /// </summary>
    public PendingImeiJournalEntry Prepare(
        string portName,
        string operationId,
        string targetImei,
        string? expectedCcid,
        PendingImeiOperationKind kind)
    {
        string normalizedPort = RequirePortName(portName);
        string normalizedOperation = RequireOperationId(operationId);
        string normalizedTarget = RequireImei(targetImei);
        string normalizedCcid = RequireOptionalCcid(expectedCcid);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        lock (_sync)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                && string.Equals(
                    existing.OperationId,
                    normalizedOperation,
                    StringComparison.Ordinal))
            {
                if (!ImeiManagementService.AreEquivalentImei(
                        existing.TargetImei, normalizedTarget))
                {
                    throw new InvalidOperationException(
                        "The operation id is already associated with a different IMEI target.");
                }

                if (!string.IsNullOrEmpty(existing.ExpectedCcid)
                    && !string.IsNullOrEmpty(normalizedCcid)
                    && !string.Equals(
                        existing.ExpectedCcid,
                        normalizedCcid,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The operation id is already bound to a different CCID.");
                }

                if (existing.Kind != kind
                    && existing.Kind != PendingImeiOperationKind.LegacyNoSim)
                {
                    throw new InvalidOperationException(
                        "The operation id is already associated with a different operation kind.");
                }

                bool needsUpgrade = existing.Kind != kind
                    || string.IsNullOrEmpty(existing.ExpectedCcid)
                        && !string.IsNullOrEmpty(normalizedCcid);
                if (!needsUpgrade) return existing with { };

                PendingImeiJournalEntry upgraded = existing with
                {
                    Kind = kind,
                    ExpectedCcid = string.IsNullOrEmpty(existing.ExpectedCcid)
                        ? normalizedCcid
                        : existing.ExpectedCcid,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                Dictionary<string, PendingImeiJournalEntry> upgradedEntries =
                    CopyEntries();
                upgradedEntries[normalizedPort] = upgraded;
                Commit(upgradedEntries);
                return upgraded with { };
            }

            var prepared = new PendingImeiJournalEntry
            {
                OperationId = normalizedOperation,
                PortName = normalizedPort,
                TargetImei = normalizedTarget,
                ExpectedCcid = normalizedCcid,
                Kind = kind,
                Phase = PendingImeiOperationPhase.Prepared,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next[normalizedPort] = prepared;
            Commit(next);
            return prepared with { };
        }
    }

    /// <summary>
    /// Binds a no-SIM operation once. Repeating the same binding is idempotent;
    /// trying to bind another physical SIM fails without mutating the journal.
    /// </summary>
    public bool TryBindExpectedCcid(
        string portName,
        string operationId,
        string expectedCcid)
    {
        string normalizedPort = NormalizePortName(portName);
        string normalizedOperation = NormalizeOperationId(operationId);
        if (!TryNormalizeCcid(expectedCcid, out string normalizedCcid)
            || string.IsNullOrEmpty(normalizedPort)
            || string.IsNullOrEmpty(normalizedOperation))
        {
            return false;
        }

        lock (_sync)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                || !string.Equals(
                    existing.OperationId,
                    normalizedOperation,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(existing.ExpectedCcid))
            {
                return string.Equals(
                    existing.ExpectedCcid,
                    normalizedCcid,
                    StringComparison.Ordinal);
            }

            PendingImeiJournalEntry bound = existing with
            {
                ExpectedCcid = normalizedCcid,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next[normalizedPort] = bound;
            Commit(next);
            return true;
        }
    }

    public bool TryMarkPhase(
        string portName,
        string operationId,
        string expectedImei,
        PendingImeiOperationPhase phase)
    {
        if (!Enum.IsDefined(phase)) return false;
        string normalizedPort = NormalizePortName(portName);
        string normalizedOperation = NormalizeOperationId(operationId);
        string normalizedTarget = ImeiManagementService.ToCanonicalImei(expectedImei);

        lock (_sync)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                || !string.Equals(
                    existing.OperationId,
                    normalizedOperation,
                    StringComparison.Ordinal)
                || !ImeiManagementService.AreEquivalentImei(
                    existing.TargetImei, normalizedTarget))
            {
                return false;
            }

            if (existing.Phase == phase) return true;
            if ((existing.Phase == PendingImeiOperationPhase.Blocked
                    && phase != PendingImeiOperationPhase.Blocked)
                || (phase != PendingImeiOperationPhase.Blocked
                    && (int)phase < (int)existing.Phase))
            {
                return false;
            }

            PendingImeiJournalEntry updated = existing with
            {
                Phase = phase,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next[normalizedPort] = updated;
            Commit(next);
            return true;
        }
    }

    /// <summary>
    /// Removes only the exact operation and target. An optional CCID adds the
    /// final physical-identity gate before the tombstone is committed.
    /// </summary>
    public bool Remove(
        string portName,
        string operationId,
        string expectedImei,
        string? expectedCcid = null)
    {
        string normalizedPort = NormalizePortName(portName);
        string normalizedOperation = NormalizeOperationId(operationId);
        string normalizedTarget = ImeiManagementService.ToCanonicalImei(expectedImei);
        string normalizedCcid = string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedCcid)
            && !TryNormalizeCcid(expectedCcid, out normalizedCcid))
        {
            return false;
        }

        lock (_sync)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                || !string.Equals(
                    existing.OperationId,
                    normalizedOperation,
                    StringComparison.Ordinal)
                || !ImeiManagementService.AreEquivalentImei(
                    existing.TargetImei, normalizedTarget)
                || !string.IsNullOrEmpty(normalizedCcid)
                    && !string.Equals(
                        existing.ExpectedCcid,
                        normalizedCcid,
                        StringComparison.Ordinal))
            {
                return false;
            }

            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next.Remove(normalizedPort);
            Commit(next);
            return true;
        }
    }

    /// <summary>
    /// Version-1 compatibility for the current no-SIM caller. A same-target Set
    /// reuses the durable operation; a changed target creates a new operation.
    /// </summary>
    public void Set(string portName, string imei)
    {
        string normalizedPort = RequirePortName(portName);
        string normalizedTarget = RequireImei(imei);

        lock (_sync)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                && existing.Kind == PendingImeiOperationKind.LegacyNoSim
                && ImeiManagementService.AreEquivalentImei(
                    existing.TargetImei, normalizedTarget))
            {
                return;
            }

            if (existing != null
                && existing.Kind != PendingImeiOperationKind.LegacyNoSim)
            {
                throw new InvalidOperationException(
                    "The legacy API cannot replace a version-2 IMEI operation.");
            }

            var prepared = new PendingImeiJournalEntry
            {
                OperationId = "legacy-" + Guid.NewGuid().ToString("N"),
                PortName = normalizedPort,
                TargetImei = normalizedTarget,
                Kind = PendingImeiOperationKind.LegacyNoSim,
                Phase = PendingImeiOperationPhase.Prepared,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next[normalizedPort] = prepared;
            Commit(next);
        }
    }

    /// <summary>
    /// Version-1 compatibility. New version-2 callers must use the overload
    /// containing OperationId so a late task cannot erase a newer operation.
    /// </summary>
    public bool Remove(string portName, string expectedImei)
    {
        string normalizedPort = NormalizePortName(portName);
        string normalizedTarget = ImeiManagementService.ToCanonicalImei(expectedImei);
        lock (_sync)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(
                    normalizedPort, out PendingImeiJournalEntry? existing)
                || existing.Kind != PendingImeiOperationKind.LegacyNoSim
                || !ImeiManagementService.AreEquivalentImei(
                    existing.TargetImei, normalizedTarget))
            {
                return false;
            }

            Dictionary<string, PendingImeiJournalEntry> next = CopyEntries();
            next.Remove(normalizedPort);
            Commit(next);
            return true;
        }
    }

    private Dictionary<string, PendingImeiJournalEntry> CopyEntries() =>
        _entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { },
            StringComparer.OrdinalIgnoreCase);

    private void Commit(Dictionary<string, PendingImeiJournalEntry> next)
    {
        long nextRevision = Math.Max(_revision + 1, DateTime.UtcNow.Ticks);
        var document = new SnapshotDocument
        {
            Revision = nextRevision,
            Entries = next
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            document,
            JsonOptions);

        Exception? primaryError = null;
        try
        {
            AtomicWrite(_primaryPath, payload);
            TryDelete(_fallbackPath);
            _entries = next;
            _revision = nextRevision;
            return;
        }
        catch (Exception ex)
        {
            primaryError = ex;
        }

        try
        {
            AtomicWrite(_fallbackPath, payload);
            _entries = next;
            _revision = nextRevision;
        }
        catch (Exception fallbackError)
        {
            throw new IOException(
                "Không thể lưu IMEI đang chờ vào journal chính hoặc dự phòng.",
                new AggregateException(primaryError!, fallbackError));
        }
    }

    private void LoadLatestSnapshot()
    {
        bool primaryExists = File.Exists(_primaryPath);
        bool fallbackExists = File.Exists(_fallbackPath);
        LoadedSnapshot? primary = ReadSnapshot(_primaryPath);
        LoadedSnapshot? fallback = ReadSnapshot(_fallbackPath);
        LoadedSnapshot? latest = new[] { primary, fallback }
            .Where(snapshot => snapshot != null)
            .OrderByDescending(snapshot => snapshot!.Revision)
            .ThenByDescending(snapshot => snapshot!.Version)
            .FirstOrDefault();
        if (latest == null)
        {
            _loadFailed = primaryExists || fallbackExists;
            return;
        }

        _revision = latest.Revision;
        _entries = latest.Entries;

        if (!latest.RequiresMigration) return;
        try
        {
            Commit(CopyEntries());
        }
        catch
        {
            // Keep the migrated entries in memory even when both files are
            // temporarily unwritable. The next successful mutation writes v2.
        }
    }

    private static LoadedSnapshot? ReadSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] payload = File.ReadAllBytes(path);
            using JsonDocument json = JsonDocument.Parse(payload);
            JsonElement root = json.RootElement;
            if (!TryGetProperty(root, "Version", out JsonElement versionElement)
                || !versionElement.TryGetInt32(out int version)
                || !TryGetProperty(root, "Revision", out JsonElement revisionElement)
                || !revisionElement.TryGetInt64(out long revision)
                || revision <= 0)
            {
                return null;
            }

            return version switch
            {
                CurrentVersion => ReadVersion2(payload, revision),
                LegacyVersion => ReadVersion1(root, revision),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static LoadedSnapshot? ReadVersion2(byte[] payload, long revision)
    {
        SnapshotDocument? document = JsonSerializer.Deserialize<SnapshotDocument>(
            payload,
            JsonOptions);
        if (document is not { Version: CurrentVersion }
            || document.Revision != revision)
        {
            return null;
        }

        if (document.Entries == null) return null;

        var entries = new Dictionary<string, PendingImeiJournalEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, PendingImeiJournalEntry> pair in
                 document.Entries)
        {
            string port = NormalizePortName(pair.Key);
            PendingImeiJournalEntry? candidate = pair.Value;
            if (string.IsNullOrEmpty(port)
                || candidate == null
                || string.IsNullOrEmpty(NormalizeOperationId(candidate.OperationId))
                || entries.ContainsKey(port))
            {
                return null;
            }

            string target = ImeiManagementService.ToCanonicalImei(
                candidate.TargetImei);
            if (!ImeiManagementService.IsValidImei(target)
                || !Enum.IsDefined(candidate.Kind)
                || !Enum.IsDefined(candidate.Phase))
            {
                return null;
            }

            string ccid = string.Empty;
            if (!string.IsNullOrWhiteSpace(candidate.ExpectedCcid)
                && !TryNormalizeCcid(candidate.ExpectedCcid, out ccid))
            {
                return null;
            }

            entries[port] = candidate with
            {
                OperationId = NormalizeOperationId(candidate.OperationId),
                PortName = port,
                TargetImei = target,
                ExpectedCcid = ccid,
                UpdatedAtUtc = candidate.UpdatedAtUtc == default
                    ? DateTime.UnixEpoch
                    : candidate.UpdatedAtUtc.ToUniversalTime()
            };
        }

        return new LoadedSnapshot(CurrentVersion, revision, entries);
    }

    private static LoadedSnapshot? ReadVersion1(JsonElement root, long revision)
    {
        if (!TryGetProperty(root, "Entries", out JsonElement entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entries = new Dictionary<string, PendingImeiJournalEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in entriesElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) return null;
            string port = NormalizePortName(property.Name);
            string target = ImeiManagementService.ToCanonicalImei(
                property.Value.GetString());
            if (string.IsNullOrEmpty(port)
                || !ImeiManagementService.IsValidImei(target)
                || entries.ContainsKey(port))
            {
                return null;
            }

            entries[port] = new PendingImeiJournalEntry
            {
                OperationId = BuildLegacyOperationId(port, target),
                PortName = port,
                TargetImei = target,
                Kind = PendingImeiOperationKind.LegacyNoSim,
                Phase = PendingImeiOperationPhase.Prepared,
                UpdatedAtUtc = DateTime.UnixEpoch
            };
        }

        return new LoadedSnapshot(LegacyVersion, revision, entries);
    }

    private void EnsureLoaded()
    {
        if (_loadFailed)
            throw new InvalidDataException(
                "IMEI pending journal is unreadable; IMEI mutation is blocked.");
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value)) return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(
                property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string BuildLegacyOperationId(string portName, string imei)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(portName + "|" + imei));
        return "legacy-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static void AtomicWrite(string path, byte[] payload)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException("Journal path has no parent directory.");
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
                bufferSize: 4096,
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
        catch
        {
            // A stale lower-revision file is harmless; LoadLatestSnapshot picks
            // the highest committed revision.
        }
    }

    private static string RequirePortName(string? portName)
    {
        string normalized = NormalizePortName(portName);
        return !string.IsNullOrEmpty(normalized)
            ? normalized
            : throw new ArgumentException("Port name is required.", nameof(portName));
    }

    private static string RequireOperationId(string? operationId)
    {
        string normalized = NormalizeOperationId(operationId);
        return !string.IsNullOrEmpty(normalized)
            ? normalized
            : throw new ArgumentException(
                "Operation id is required.", nameof(operationId));
    }

    private static string RequireImei(string? imei)
    {
        string normalized = ImeiManagementService.ToCanonicalImei(imei);
        return ImeiManagementService.IsValidImei(normalized)
            ? normalized
            : throw new ArgumentException("A valid IMEI is required.", nameof(imei));
    }

    private static string RequireOptionalCcid(string? ccid)
    {
        if (string.IsNullOrWhiteSpace(ccid)) return string.Empty;
        return TryNormalizeCcid(ccid, out string normalized)
            ? normalized
            : throw new ArgumentException("A valid CCID is required.", nameof(ccid));
    }

    private static bool TryNormalizeCcid(string? ccid, out string normalized)
    {
        Match match = Regex.Match(
            (ccid ?? string.Empty).Trim(),
            @"^89\d{16,20}$",
            RegexOptions.CultureInvariant);
        normalized = match.Success ? match.Value : string.Empty;
        return match.Success;
    }

    private static string NormalizeOperationId(string? operationId) =>
        string.IsNullOrWhiteSpace(operationId)
            ? string.Empty
            : operationId.Trim();

    private static string NormalizePortName(string? portName) =>
        string.IsNullOrWhiteSpace(portName)
            ? string.Empty
            : portName.Trim().ToUpperInvariant();
}
