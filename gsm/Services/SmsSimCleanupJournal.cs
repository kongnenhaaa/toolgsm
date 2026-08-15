using System.IO;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Session-only intent tracker used while releasing multipart SMS slots from a
/// SIM. State is never read from or written to a local file.
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

    private readonly object _gate = new();
    private readonly Dictionary<string, Intent> _intents =
        new(StringComparer.Ordinal);

    // Paths are deliberately ignored for source compatibility. Persistence is
    // disabled so sms_sim_cleanup_journal*.json can never be created.
    public SmsSimCleanupJournal(string primaryPath, string fallbackPath)
    {
        _ = primaryPath;
        _ = fallbackPath;
    }

    private SmsSimCleanupJournal()
    {
    }

    internal static SmsSimCleanupJournal CreateInMemory() => new();

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
            if (_intents.TryGetValue(intentId, out Intent? existing))
            {
                if (!string.Equals(existing.Scope, scope, StringComparison.Ordinal)
                    || !string.Equals(existing.SimIndex, simIndex, StringComparison.Ordinal)
                    || !string.Equals(existing.MessageId, messageId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Cleanup identity is already owned by different SIM data.");
                }

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
            _intents[intentId] = intent;
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
            if (!_intents.TryGetValue(intentId, out Intent? existing)
                || !string.Equals(
                    existing.MessageId,
                    expectedMessageId,
                    StringComparison.Ordinal))
                return false;
            return _intents.Remove(intentId);
        }
    }

    public IReadOnlyList<Intent> GetForScope(string scope)
    {
        lock (_gate)
        {
            return _intents.Values
                .Where(intent => string.Equals(
                    intent.Scope, scope, StringComparison.Ordinal))
                .OrderBy(intent => intent.CreatedAtUtc)
                .ToArray();
        }
    }

    private static void Validate(
        string scope,
        string portName,
        string simIndex,
        string messageId,
        string partIdentity)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || !Regex.IsMatch(
                portName ?? string.Empty,
                @"^COM\d+$",
                RegexOptions.IgnoreCase)
            || !Regex.IsMatch(simIndex ?? string.Empty, @"^\d+$")
            || string.IsNullOrWhiteSpace(messageId)
            || string.IsNullOrWhiteSpace(partIdentity))
        {
            throw new InvalidDataException("Invalid SMS SIM-cleanup intent.");
        }
    }
}
