using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace gsm.Services;

public interface IGsmSmsService : IDisposable
{
    ConcurrentDictionary<string, bool> InProgressPorts { get; }
    bool IsInProgress(string portName);
    Task<string> SendAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct = default,
        string? expectedCcid = null);
}

public enum SmsSubmitDisposition
{
    Confirmed,
    PayloadSubmittedUncertain,
    CancelledBeforePayload,
    FailedBeforePayload
}

public sealed class GsmSmsService : IGsmSmsService
{
    private readonly IGsmModemService _modem;
    private readonly IPortSessionRegistry _sessions;
    private readonly IGsmOperationDelay _delay;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, bool> InProgressPorts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public GsmSmsService(
        IGsmModemService modem,
        IPortSessionRegistry sessions,
        IGsmOperationDelay delay)
    {
        _modem = modem;
        _sessions = sessions;
        _delay = delay;
    }

    public bool IsInProgress(string portName) => InProgressPorts.ContainsKey(portName);

    public static SmsSubmitDisposition ClassifySubmitResult(string? response)
    {
        string value = response ?? string.Empty;
        // The Ctrl+Z marker is an irreversible boundary. It must win even if a
        // later recovery probe appended an OK to the diagnostic text; otherwise
        // the UI could report a false success or put the SMS back into retry.
        if (value.Contains(
                GsmModemService.SmsPayloadSubmittedMarker,
                StringComparison.Ordinal))
        {
            return SmsSubmitDisposition.PayloadSubmittedUncertain;
        }
        if (IsSuccess(value))
            return SmsSubmitDisposition.Confirmed;
        if (value.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || value.Contains("đã dừng", StringComparison.OrdinalIgnoreCase))
        {
            return SmsSubmitDisposition.CancelledBeforePayload;
        }
        return SmsSubmitDisposition.FailedBeforePayload;
    }

    public async Task<string> SendAsync(
        string portName,
        string phoneNumber,
        string content,
        CancellationToken ct = default,
        string? expectedCcid = null)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(content))
            return "ERROR: Missing SMS recipient or content";
        if (!_sessions.TryGet(portName, out var session))
            return "ERROR: Port has no current SIM session";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        CancellationToken token = linkedCts.Token;
        var portLock = _portLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        await portLock.WaitAsync(token);
        IDisposable? backgroundLease = null;

        try
        {
            if (!IsCurrent(session)) return SessionChangedError;
            // Pause only background polling while this SMS transaction owns the
            // modem. All foreground commands still share GsmModemService's
            // per-COM semaphore, so network polling cannot steal the channel
            // between CMGS and its final response.
            backgroundLease = _modem.SuspendPortBackgroundOperations(portName);
            InProgressPorts[portName] = true;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(session)) return SessionChangedError;
                if (!string.IsNullOrWhiteSpace(expectedCcid)
                    && (!string.Equals(
                            session.Ccid,
                            expectedCcid,
                            StringComparison.Ordinal)
                        || !await _modem.VerifyExpectedCcidAsync(
                            portName, expectedCcid, token)))
                {
                    return "ERROR: Current physical SIM does not match the pinned CCID";
                }

                // Preserve the user's original Unicode text. GsmModemService selects
                // GSM or UCS2 for each message and configures CSCS/CSMP while holding
                // the per-port command lock, so stripping Vietnamese diacritics or
                // forcing GSM here both loses content and creates a redundant mode flip.
                string result = await _modem.SendSmsAsync(portName, phoneNumber, content, 30000, token);
                if (result.Contains(
                        GsmModemService.SmsPayloadSubmittedMarker,
                        StringComparison.Ordinal))
                {
                    // Preserve the irreversible Ctrl+Z boundary even when a SIM
                    // watcher invalidates the session before the modem replies.
                    // A durable incoming response may still prove acceptance;
                    // this payload must never be retried. Keep the established
                    // COM/SIM session online as SAuto does: an SMS-layer timeout
                    // must not demote an otherwise healthy port to Connecting.
                    return result;
                }
                if (!IsCurrent(session)) return SessionChangedError;
                if (IsSuccess(result)) return "Gửi thành công";
                if (attempt >= 3 || !ShouldRetry(result)) return result;

                await _delay.WaitAsync(TimeSpan.FromSeconds(2 * attempt), token);
            }

            return "ERROR: SMS retry exhausted";
        }
        catch (OperationCanceledException)
        {
            return IsCurrent(session)
                ? "ERROR: SMS operation cancelled"
                : SessionChangedError;
        }
        finally
        {
            // GsmModemService owns channel restoration while its foreground
            // operation lease is still held. Do not send charset commands or
            // start an SMS sweep here: either action could overlap the next
            // queued call/USSD workflow on this COM.
            backgroundLease?.Dispose();
            InProgressPorts.TryRemove(portName, out _);
            portLock.Release();
        }
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).Replace("đ", "d").Replace("Đ", "D");
    }

    public void Dispose()
    {
        foreach (var semaphore in _portLocks.Values) semaphore.Dispose();
        _portLocks.Clear();
        InProgressPorts.Clear();
    }

    private bool IsCurrent(PortSessionLease session) =>
        _sessions.IsCurrent(session.PortName, session.Ccid, session.Epoch);

    private static bool IsSuccess(string response) =>
        !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        && (response.Contains("thành công", StringComparison.OrdinalIgnoreCase)
            || response.Contains("success", StringComparison.OrdinalIgnoreCase)
            || response.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase)
            || response.StartsWith("OK", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRetry(string response) =>
        response.Contains("Another command", StringComparison.OrdinalIgnoreCase)
        || response.Contains("waiting for lock", StringComparison.OrdinalIgnoreCase)
        // No payload has been written yet, so retrying this timeout cannot create
        // duplicate SMS. Payload/final-response timeouts are intentionally not retried.
        || response.Contains("Timeout waiting for > prompt", StringComparison.OrdinalIgnoreCase)
        || response.Contains("Timeout configuring SMS", StringComparison.OrdinalIgnoreCase);

    private const string SessionChangedError = "ERROR: SIM session changed during SMS operation";
}
