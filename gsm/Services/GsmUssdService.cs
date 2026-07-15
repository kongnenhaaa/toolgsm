using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace gsm.Services;

public interface IGsmUssdService : IDisposable
{
    Task<string> SendAsync(string portName, string ussdCode, int maxAttempts = 3, CancellationToken ct = default);
}

public sealed class GsmUssdService : IGsmUssdService
{
    private static readonly TimeSpan PerPortInterval = TimeSpan.FromSeconds(3);

    private readonly IGsmModemService _modem;
    private readonly IPortSessionRegistry _sessions;
    private readonly IGsmSmsService _sms;
    private readonly IGsmOperationDelay _delay;
    private readonly ConcurrentDictionary<string, DateTime> _lastByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks = new(StringComparer.OrdinalIgnoreCase);

    public GsmUssdService(
        IGsmModemService modem,
        IPortSessionRegistry sessions,
        IGsmSmsService sms,
        IGsmOperationDelay delay)
    {
        _modem = modem;
        _sessions = sessions;
        _sms = sms;
        _delay = delay;
    }

    public async Task<string> SendAsync(
        string portName,
        string ussdCode,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ussdCode)) return "ERROR: Invalid USSD request";
        if (!_sessions.TryGet(portName, out var session)) return SessionChangedError;
        var portLock = _portLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        CancellationToken token = linkedCts.Token;
        await portLock.WaitAsync(token);

        try
        {
            string result = string.Empty;
            for (int attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
            {
                if (!IsCurrent(session)) return SessionChangedError;
                await ThrottleAsync(portName, token);

                string? preflight = await PreparePortAsync(session, token);
                if (preflight != null) result = preflight;
                else
                {
                    if (!IsCurrent(session)) return SessionChangedError;
                    await _modem.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true);
                    bool forcedGsm = false;
                    try
                    {
                        if (!IsCurrent(session)) return SessionChangedError;
                        // Một số thuê bao EC20 đăng ký LTE/CS đầy đủ nhưng tổng đài không trả
                        // USSD qua fallback. Lần cuối tạm ép GSM/2G, rồi luôn khôi phục Auto.
                        if (attempt >= 3 && !_modem.IsCallInProgress(portName))
                        {
                            string force = await _modem.SendCommandAsync(
                                portName, "AT+QCFG=\"nwscanmode\",1,0", 10000, true);
                            forcedGsm = !force.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                            if (forcedGsm)
                            {
                                for (int probe = 0; probe < 8; probe++)
                                {
                                    await _delay.WaitAsync(TimeSpan.FromSeconds(probe == 0 ? 5 : 3), token);
                                    string reg = await CommandAsync(session, "AT+CREG?", 5000, token);
                                    if (IsNetworkRegistered(reg)) break;
                                }
                            }
                        }

                        string cusdCommand = attempt switch
                        {
                            1 => $"AT+CUSD=1,\"{ussdCode}\",15",
                            2 => $"AT+CUSD=1,\"{ussdCode}\",0",
                            _ => $"AT+CUSD=1,\"{ussdCode}\""
                        };
                        result = await _modem.SendCommandAsync(portName, cusdCommand);
                        // Một số SIM/firmware chỉ trả OK sau khi nhận lệnh nhưng tổng đài
                        // không mở phiên USSD. Không được coi OK trần là thành công.
                        if (!result.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)
                            && !result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                        {
                            result = $"ERROR: Modem accepted USSD but network returned no +CUSD (attempt {attempt})";
                        }
                        await _delay.WaitAsync(TimeSpan.FromSeconds(3.5), token);
                    }
                    finally
                    {
                        if (IsCurrent(session))
                        {
                            try { await _modem.SendCommandAsync(portName, "AT+CSCS=\"UCS2\"", 5000, true); }
                            catch { /* Không che kết quả USSD chính. */ }
                            if (forcedGsm)
                            {
                                try { await _modem.SendCommandAsync(portName, "AT+QCFG=\"nwscanmode\",0,0", 10000, true); }
                                catch { /* Watchdog vẫn có thể khôi phục Auto sau đó. */ }
                            }
                        }
                    }
                }

                if (!IsFailure(result)) return result;
                if (attempt >= maxAttempts || _sms.IsInProgress(portName)) return result;
                // Đóng phiên im lặng trước khi thử DCS tiếp theo.
                await _modem.SendCommandAsync(portName, "AT+CUSD=2", 5000, true);
                await _delay.WaitAsync(TimeSpan.FromSeconds(Math.Min(3 + (attempt - 1) * 2, 30)), token);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return "ERROR: USSD operation cancelled";
        }
        finally
        {
            portLock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var semaphore in _portLocks.Values) semaphore.Dispose();
        _portLocks.Clear();
    }

    private async Task ThrottleAsync(string portName, CancellationToken token)
    {
        DateTime now = DateTime.UtcNow;
        if (_lastByPort.TryGetValue(portName, out DateTime lastPort))
        {
            TimeSpan wait = PerPortInterval - (now - lastPort);
            if (wait > TimeSpan.Zero)
            {
                await _delay.WaitAsync(wait, token);
                now = DateTime.UtcNow;
            }
        }

        _lastByPort[portName] = now;
    }

    private async Task<string?> PreparePortAsync(PortSessionLease session, CancellationToken token)
    {
        string at = await CommandAsync(session, "AT", 3000, token);
        if (IsCommandError(at)) return $"ERROR: Modem not ready ({at.Trim()})";

        string pin = await CommandAsync(session, "AT+CPIN?", 5000, token);
        if (IsCommandError(pin)) return $"ERROR: SIM status check failed ({pin.Trim()})";
        if (pin.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase)
            || pin.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase)
            || pin.Contains("PH-NET PIN", StringComparison.OrdinalIgnoreCase))
            return $"ERROR: SIM not ready ({pin.Trim()})";

        string registration = await CommandAsync(session, "AT+CREG?", 5000, token);
        if (IsCommandError(registration))
            return $"ERROR: Network registration check failed ({registration.Trim()})";
        if (!IsNetworkRegistered(registration))
        {
            string lte = await CommandAsync(session, "AT+CEREG?", 5000, token);
            string cops = IsNetworkRegistered(lte)
                ? lte
                : await CommandAsync(session, "AT+COPS?", 5000, token);
            if (!IsNetworkRegistered(cops))
                return $"ERROR: SIM not registered on network ({registration.Trim()})";
        }

        string signal = await CommandAsync(session, "AT+CSQ", 5000, token);
        if (IsCommandError(signal)) return $"ERROR: Signal quality check failed ({signal.Trim()})";
        if (!HasUsableSignal(signal)) return $"ERROR: Signal too weak for USSD ({signal.Trim()})";

        await CommandAsync(session, "AT+CUSD=2", 5000, token);
        await _delay.WaitAsync(TimeSpan.FromMilliseconds(400), token);
        return null;
    }

    private async Task<string> CommandAsync(PortSessionLease session, string command, int timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(session)) throw new OperationCanceledException(token);
        string result = await _modem.SendCommandAsync(session.PortName, command, timeout, true);
        if (!IsCurrent(session)) throw new OperationCanceledException(token);
        return result;
    }

    private bool IsCurrent(PortSessionLease session) =>
        _sessions.IsCurrent(session.PortName, session.Ccid, session.Epoch);

    private static bool IsCommandError(string response) =>
        string.IsNullOrWhiteSpace(response) || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkRegistered(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        var reg = Regex.Match(response, @"\+(?:C|CG|CE)REG:\s*\d+\s*,\s*(\d+)");
        if (reg.Success && reg.Groups[1].Value is "1" or "5") return true;
        var cops = Regex.Match(response, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""([^""]+)""");
        return cops.Success && !string.IsNullOrWhiteSpace(cops.Groups[1].Value);
    }

    private static bool HasUsableSignal(string response)
    {
        var match = Regex.Match(response, @"\+CSQ:\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int csq) && csq is >= 6 and < 99;
    }

    private static bool IsFailure(string result) =>
        result.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || result.Contains("Thao tac khong hop le", StringComparison.OrdinalIgnoreCase)
        || result.Contains("he thong ban", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 2", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 4", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 5", StringComparison.OrdinalIgnoreCase);

    private const string SessionChangedError = "ERROR: SIM session changed during USSD operation";
}
