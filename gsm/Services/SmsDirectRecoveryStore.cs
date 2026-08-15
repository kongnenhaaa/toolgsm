using System.IO;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Session-only owner for direct +CMT frames awaiting decode. Raw SMS frames
/// are never read from or written to local recovery files.
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

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _entries =
        new(StringComparer.Ordinal);

    // Paths are deliberately ignored for source compatibility. Persistence is
    // disabled so sms_direct_recovery*.json can never be created.
    internal SmsDirectRecoveryStore(string primaryPath, string fallbackPath)
    {
        _ = primaryPath;
        _ = fallbackPath;
    }

    private SmsDirectRecoveryStore()
    {
    }

    internal static SmsDirectRecoveryStore CreateInMemory() => new();

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
            var pending = new Pending(
                $"direct-raw-v1-{Guid.NewGuid():N}",
                portName.Trim().ToUpperInvariant(),
                scope,
                raw,
                reason ?? string.Empty,
                Math.Max(1, decodeAttempts),
                DateTimeOffset.UtcNow);
            _entries[pending.Id] = pending;
            return pending;
        }
    }

    internal IReadOnlyList<Pending> GetForPort(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        lock (_gate)
        {
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
            return _entries.Remove(id);
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
}
