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
        bool reconnectPort = false;
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
                    // this payload must never be retried.
                    reconnectPort = RequiresPortReconnect(result);
                    return result;
                }
                if (!IsCurrent(session)) return SessionChangedError;
                if (IsSuccess(result)) return "Gửi thành công";
                if (RequiresPortReconnect(result))
                {
                    // The payload may already have reached the carrier. Never resend it.
                    // Reopen only this COM after the modem's in-place channel recovery
                    // has failed, so later operations start from a clean serial session.
                    reconnectPort = true;
                    return result;
                }
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
            try
            {
                // A failed SMS channel cannot be restored reliably with another charset
                // command. Skip that work and perform one targeted COM reconnect instead.
                if (!reconnectPort && IsCurrent(session))
                {
                    try
                    {
                        if (_modem.GetModemProfile(portName)?.IsQuectel == true)
                            await _modem.SendCommandAsync(portName, "AT+CMGF=0", 5000, true);
                        else
                        {
                            await _modem.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);
                            await _modem.SendCommandAsync(portName, "AT+CSMP=17,167,0,8", 5000, true);
                        }
                    }
                    catch
                    {
                        // Không che kết quả gửi chính; lần khởi tạo/poll kế tiếp sẽ đặt lại charset.
                    }
                }

                if (reconnectPort
                    && !ct.IsCancellationRequested
                    && IsCurrent(session))
                {
                    try
                    {
                        // Use the caller lifetime rather than session.Token: reconnect
                        // initialization intentionally replaces the old SIM session.
                        await _modem.ReconnectPortAsync(portName, 115200, ct);
                    }
                    catch
                    {
                        // Preserve the original uncertain send result. A reconnect
                        // failure must never turn into an automatic payload retry.
                    }
                }
            }
            finally
            {
                backgroundLease?.Dispose();
                InProgressPorts.TryRemove(portName, out _);
                portLock.Release();

                // CMGS can leave a +CMTI notification queued while the modem is
                // finishing the send. Read stored SMS immediately after the
                // transaction instead of waiting for the periodic sweep.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(750).ConfigureAwait(false);
                        await _modem.SweepUnreadSmsAsync(portName)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // The modem service owns the durable retry path; a
                        // best-effort post-send sweep must never change the SMS
                        // send result or fault the caller's task.
                    }
                });
            }
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
        response.Contains("OK", StringComparison.OrdinalIgnoreCase)
        || response.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRetry(string response) =>
        response.Contains("Another command", StringComparison.OrdinalIgnoreCase)
        || response.Contains("waiting for lock", StringComparison.OrdinalIgnoreCase)
        // No payload has been written yet, so retrying this timeout cannot create
        // duplicate SMS. Payload/final-response timeouts are intentionally not retried.
        || response.Contains("Timeout waiting for > prompt", StringComparison.OrdinalIgnoreCase)
        || response.Contains("Timeout configuring SMS", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresPortReconnect(string response) =>
        response.Contains("SMS channel recovery failed", StringComparison.OrdinalIgnoreCase);

    private const string SessionChangedError = "ERROR: SIM session changed during SMS operation";
}
