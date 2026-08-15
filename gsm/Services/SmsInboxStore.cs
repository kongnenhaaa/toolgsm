using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
/// Session-only SMS inbox. Records exist only in memory and disappear when
/// ToolGSM exits. This type never creates, reads or writes SMS history files.
/// </summary>
public sealed class SmsInboxStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private readonly object _gate = new();
    private readonly List<SmsInboxRecord> _records = new();
    private readonly Dictionary<string, string> _deliveryFingerprints =
        new(StringComparer.Ordinal);

    // Keep the old optional argument for source compatibility. It is
    // deliberately ignored so callers cannot enable disk persistence.
    public SmsInboxStore(string? directoryPath = null)
    {
        _ = directoryPath;
    }

    internal SmsInboxStore(
        string? directoryPath,
        bool durableWrites,
        Action? beforeFlushForTests = null)
    {
        _ = directoryPath;
        _ = durableWrites;
        _ = beforeFlushForTests;
    }

    public static SmsInboxStore CreateInMemory() => new();

    public string DirectoryPath => string.Empty;
    public IReadOnlyList<string> RecoveryWarnings => Array.Empty<string>();

    public long Count
    {
        get
        {
            lock (_gate)
                return _records.Count;
        }
    }

    /// <summary>
    /// Adds one SMS to the current process only. Returns false when the same
    /// DeliveryId is already present in this session.
    /// </summary>
    public bool Append(SmsInboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        lock (_gate)
        {
            string fingerprint = PayloadFingerprint(record);
            if (_deliveryFingerprints.TryGetValue(
                    record.DeliveryId,
                    out string? existingFingerprint))
            {
                if (!string.Equals(
                        existingFingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Conflicting SMS payload for DeliveryId '{record.DeliveryId}'.");
                }

                return false;
            }

            _records.Add(record);
            _deliveryFingerprints.Add(record.DeliveryId, fingerprint);
            return true;
        }
    }

    public IReadOnlyList<SmsInboxRecord> GetRecent(int count)
    {
        if (count <= 0) return Array.Empty<SmsInboxRecord>();

        lock (_gate)
        {
            return _records
                .OrderByDescending(record => record.SmsTimestampUtc
                    ?? record.ReceivedAtUtc)
                .Take(count)
                .ToArray();
        }
    }

    public int Delete(IEnumerable<string> deliveryIds)
    {
        ArgumentNullException.ThrowIfNull(deliveryIds);
        HashSet<string> targets = deliveryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0) return 0;

        lock (_gate)
        {
            int deleted = _records.RemoveAll(record =>
                targets.Contains(record.DeliveryId));
            if (deleted == 0) return 0;

            _deliveryFingerprints.Clear();
            foreach (SmsInboxRecord record in _records)
            {
                _deliveryFingerprints.Add(
                    record.DeliveryId,
                    PayloadFingerprint(record));
            }

            return deleted;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            int deleted = _records.Count;
            _records.Clear();
            _deliveryFingerprints.Clear();
            return deleted;
        }
    }

    public static string CreateDeliveryId(params string?[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
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

    private static string PayloadFingerprint(SmsInboxRecord record)
    {
        string normalizedContent = record.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return CreateDeliveryId("payload-v2", normalizedContent);
    }

    private static void Validate(SmsInboxRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Content);
        if (record.ReceivedAtUtc == default)
        {
            throw new ArgumentException(
                "ReceivedAtUtc is required.",
                nameof(record));
        }
    }
}
