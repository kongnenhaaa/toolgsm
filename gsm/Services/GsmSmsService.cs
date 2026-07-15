using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace gsm.Services;

public interface IGsmSmsService : IDisposable
{
    ConcurrentDictionary<string, bool> InProgressPorts { get; }
    bool IsInProgress(string portName);
    Task<string> SendAsync(string portName, string phoneNumber, string content, CancellationToken ct = default);
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(content))
            return "ERROR: Missing SMS recipient or content";
        if (!_sessions.TryGet(portName, out var session))
            return "ERROR: Port has no current SIM session";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        CancellationToken token = linkedCts.Token;
        var portLock = _portLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        await portLock.WaitAsync(token);

        try
        {
            if (!IsCurrent(session)) return SessionChangedError;
            InProgressPorts[portName] = true;
            string safeContent = RemoveDiacritics(content);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(session)) return SessionChangedError;

                await _modem.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);
                if (!IsCurrent(session)) return SessionChangedError;
                await _modem.SendCommandAsync(portName, "AT+CSMP=17,167,0,0", 5000, true);
                if (!IsCurrent(session)) return SessionChangedError;

                string result = await _modem.SendSmsAsync(portName, phoneNumber, safeContent, 30000);
                if (!IsCurrent(session)) return SessionChangedError;
                if (IsSuccess(result)) return "Gửi thành công";
                if (attempt >= 3 || !ShouldRetry(result)) return result;

                await _delay.WaitAsync(TimeSpan.FromSeconds(2 * attempt), token);
            }

            return "ERROR: SMS retry exhausted";
        }
        catch (OperationCanceledException)
        {
            return "ERROR: SMS operation cancelled";
        }
        finally
        {
            if (IsCurrent(session))
            {
                try
                {
                    await _modem.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true);
                    await _modem.SendCommandAsync(portName, "AT+CSMP=17,167,0,8", 5000, true);
                }
                catch
                {
                    // Không che kết quả gửi chính; lần khởi tạo/poll kế tiếp sẽ đặt lại charset.
                }
            }
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
        response.Contains("OK", StringComparison.OrdinalIgnoreCase)
        || response.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRetry(string response) =>
        response.Contains("Another command", StringComparison.OrdinalIgnoreCase)
        || response.Contains("waiting for lock", StringComparison.OrdinalIgnoreCase);

    private const string SessionChangedError = "ERROR: SIM session changed during SMS operation";
}
