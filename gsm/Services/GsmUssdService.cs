using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using gsm.Models;

namespace gsm.Services;

public interface IGsmUssdService : IDisposable
{
    Task<string> SendAsync(
        string portName,
        string ussdCode,
        int maxAttempts = 3,
        CancellationToken ct = default,
        string? expectedCcid = null);
}

public sealed class GsmUssdService : IGsmUssdService
{
    private static readonly TimeSpan PerPortInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FirstAttemptResponseWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FinalAttemptResponseWindow = TimeSpan.FromSeconds(20);

    private readonly IGsmModemService _modem;
    private readonly IPortSessionRegistry _sessions;
    private readonly IGsmSmsService _sms;
    private readonly IGsmOperationDelay _delay;
    private readonly ConcurrentDictionary<string, DateTime> _lastByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _ussdSupported = new(StringComparer.OrdinalIgnoreCase);

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
        CancellationToken ct = default,
        string? expectedCcid = null)
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

                IDisposable? backgroundLease = _modem.SuspendPortBackgroundOperations(portName);
                PortPreparation preparation;
                try
                {
                    preparation = await PreparePortAsync(session, token);
                }
                catch
                {
                    backgroundLease?.Dispose();
                    throw;
                }
                bool restoreAutomaticNetwork = preparation.RestoreAutomaticNetwork;
                string? preflight = preparation.Error;
                if (preflight != null)
                {
                    result = preflight;
                    if (restoreAutomaticNetwork && IsCurrent(session))
                    {
                        try { await _modem.SendCommandAsync(portName, "AT+COPS=0", 15000, true); }
                        catch { /* Do not hide the primary USSD result. */ }
                    }
                    if (IsCurrent(session))
                    {
                        try { await _modem.SendCommandAsync(portName, "AT+CREG=2", 5000, true); }
                        catch { /* Không che lỗi preflight chính. */ }
                    }
                    backgroundLease?.Dispose();
                    backgroundLease = null;
                }
                else
                {
                    if (!IsCurrent(session))
                    {
                        backgroundLease?.Dispose();
                        return SessionChangedError;
                    }
                    try
                    {
                        if (!IsCurrent(session)) return SessionChangedError;
                        // The preflight lease remains active through CUSD.
                        // Chuỗi tương thích SAuto: PDU mode + UCS2 charset + Hex encoded USSD
                        await CommandAsync(session, "AT+CMGF=0", 5000, token);
                        await CommandAsync(session, "AT+CSCS=\"UCS2\"", 5000, token);
                        result = await SendAndAwaitUssdResponseAsync(
                            session,
                            $"AT+CUSD=1,\"{EncodeUcs2(ussdCode)}\"",
                            attempt,
                            maxAttempts,
                            token);

                        // Một số SIM/firmware chỉ trả OK sau khi nhận lệnh nhưng tổng đài
                        // không mở phiên USSD. Không được coi OK trần là thành công.
                        await _delay.WaitAsync(TimeSpan.FromSeconds(1.0), token);
                    }
                    finally
                    {
                        if (IsCurrent(session))
                        {
                            if (restoreAutomaticNetwork)
                            {
                                try { await _modem.SendCommandAsync(portName, "AT+COPS=0", 15000, true); }
                                catch { /* Do not hide the primary USSD result. */ }
                            }
                            try { await _modem.SendCommandAsync(portName, "AT+CMGF=1", 5000, true); }
                            catch { /* Không che kết quả USSD chính. */ }
                            try { await _modem.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true); }
                            catch { /* Không che kết quả USSD chính. */ }
                            try { await _modem.SendCommandAsync(portName, "AT+CREG=2", 5000, true); }
                            catch { /* Polling COPS vẫn tiếp tục hoạt động. */ }
                        }
                        backgroundLease?.Dispose();
                    }
                }

                if (!IsFailure(result)) return UssdResponseDecoder.Normalize(result);
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
        _ussdSupported.Clear();
    }

    private async Task<string> SendAndAwaitUssdResponseAsync(
        PortSessionLease session,
        string command,
        int attempt,
        int maxAttempts,
        CancellationToken token)
    {
        var responseTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnModemLog(object? sender, GsmDataEventArgs e)
        {
            if (!e.PortName.Equals(session.PortName, StringComparison.OrdinalIgnoreCase)
                || !e.Data.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase))
                return;

            responseTcs.TrySetResult(e.Data);
        }

        _modem.LogMessage += OnModemLog;
        try
        {
            string acknowledgement = await _modem.SendCommandAsync(
                session.PortName, command, 10000, silent: true, ct: token);

            if (acknowledgement.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)
                || acknowledgement.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return acknowledgement;

            TimeSpan responseWindow = attempt >= maxAttempts
                ? FinalAttemptResponseWindow
                : FirstAttemptResponseWindow;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(responseWindow);
            try
            {
                return await responseTcs.Task.WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return $"ERROR: USSD response timeout after ACK (no +CUSD within {responseWindow.TotalSeconds:0}s; attempt {attempt})";
            }
        }
        finally
        {
            _modem.LogMessage -= OnModemLog;
        }
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

    private async Task<PortPreparation> PreparePortAsync(PortSessionLease session, CancellationToken token)
    {
        string at = await CommandAsync(session, "AT", 3000, token);
        if (IsCommandError(at)) return new($"ERROR: Modem not ready ({at.Trim()})", false);

        if (!_ussdSupported.TryGetValue(session.PortName, out bool supportsUssd))
        {
            string test = await CommandAsync(session, "AT+CUSD=?", 5000, token);
            supportsUssd = !IsCommandError(test);
            _ussdSupported[session.PortName] = supportsUssd;
        }
        if (!supportsUssd) return new("ERROR: Modem firmware does not expose AT+CUSD", false);

        string pin = await CommandAsync(session, "AT+CPIN?", 5000, token);
        if (IsCommandError(pin)) return new($"ERROR: SIM status check failed ({pin.Trim()})", false);
        // Retry CPIN: modem vừa khởi động hoặc SIM hot-plug có thể trả về bare OK
        // trước khi report +CPIN: READY (giống pattern retry CREG bên dưới)
        for (int pinProbe = 0; pinProbe < 3 && !Regex.IsMatch(pin, @"\+CPIN:\s*READY", RegexOptions.IgnoreCase); pinProbe++)
        {
            if (IsCommandError(pin)) break; // Lỗi thật, không retry
            await _delay.WaitAsync(TimeSpan.FromSeconds(2), token);
            pin = await CommandAsync(session, "AT+CPIN?", 5000, token);
        }
        if (!Regex.IsMatch(pin, @"\+CPIN:\s*READY", RegexOptions.IgnoreCase))
            return new($"ERROR: SIM not ready ({pin.Trim()})", false);


        // Tắt URC CREG chi tiết trong cửa sổ USSD để phản hồi lệnh không bị xen ngang.
        // Chỉ chuyển RAT tạm thời khi LTE không có đăng ký CS; cuối thao tác
        // sẽ khôi phục COPS tự động và CREG=2.
        await CommandAsync(session, "AT+CREG=0", 5000, token);
        string registration = await CommandAsync(session, "AT+CREG?", 5000, token);
        bool registered = IsCsRegistered(registration);
        bool restoreAutomaticNetwork = false;
        // Cho modem vài giây để CS lên tự nhiên trước khi dùng fallback RAT riêng COM.
        // Fallback chỉ chạy sau 5 probe, tránh ép 3G khi CREG vừa chuyển sang 1/5.
        for (int probe = 0; probe < 10 && !registered; probe++)
        {
            await _delay.WaitAsync(TimeSpan.FromSeconds(3), token);
            registration = await CommandAsync(session, "AT+CREG?", 5000, token);
            registered = IsCsRegistered(registration);

            if (!registered && probe == 4 && TryGetCregStatus(registration, out int probeStatus)
                && probeStatus is 0 or 2)
            {
                (bool recovered, bool changedNetwork) =
                    await TryRecoverCsRegistrationAsync(session, token);
                restoreAutomaticNetwork |= changedNetwork;
                if (recovered)
                {
                    registered = true;
                    break;
                }
            }
        }
        if (!registered)
        {
            // Phân biệt trạng thái để dễ debug:
            // stat=2 = đang tìm (searching) — có thể do 3G chưa phủ hoặc chưa kịp đăng ký
            // stat=3 = bị từ chối (denied) — lỗi thật, không nên gửi USSD
            var statMatch = Regex.Match(registration, @"\+CREG:\s*\d+\s*,\s*(\d+)");
            int stat = statMatch.Success && int.TryParse(statMatch.Groups[1].Value, out int s) ? s : -1;
            if (stat == 2)
            {
                // Vẫn đang tìm mạng CS — thử gửi USSD vì EC20 đôi khi xử lý được
                // dù CREG chưa cập nhật kịp (hành vi thực tế quan sát thấy trên modem này).
                // Nếu gửi vẫn fail, lỗi sẽ hiện rõ ở tầng AT+CUSD.
            }
            else
            {
                return new(
                    $"ERROR: SIM not registered on CS network ({registration.Trim()})",
                    restoreAutomaticNetwork);
            }
        }


        // CSQ=99 thường chỉ là chưa có số đo tức thời. Simmart vẫn gửi USSD trong trường
        // hợp này; chỉ dùng CSQ để chẩn đoán, không loại bỏ một COM đang CREG=1/5.
        _ = await CommandAsync(session, "AT+CSQ", 5000, token);

        await CommandAsync(session, "AT+CUSD=2", 5000, token);
        await _delay.WaitAsync(TimeSpan.FromMilliseconds(400), token);
        return new(null, restoreAutomaticNetwork);
    }

    private async Task<(bool Registered, bool ChangedNetwork)> TryRecoverCsRegistrationAsync(
        PortSessionLease session,
        CancellationToken token)
    {
        string cops = await CommandAsync(session, "AT+COPS?", 5000, token);
        if (!IsLteNetwork(cops))
            return (false, false);

        bool changedNetwork = false;
        foreach (string operatorCode in GsmModemService.GetOperatorCodesForCcid(session.Ccid))
        {
            changedNetwork = true;
            string forced3G = await CommandAsync(
                session,
                $"AT+COPS=1,2,\"{operatorCode}\",2",
                20000,
                token);
            if (IsCommandError(forced3G)) continue;

            for (int probe = 0; probe < 8; probe++)
            {
                await _delay.WaitAsync(TimeSpan.FromSeconds(2), token);
                string registration = await CommandAsync(session, "AT+CREG?", 5000, token);
                if (IsCsRegistered(registration))
                    return (true, true);
            }
        }

        // Preferred 3G can be unavailable at the current cell. Return the
        // modem to automatic selection instead of leaving a port pinned there.
        changedNetwork = true;
        await CommandAsync(session, "AT+COPS=0", 15000, token);
        for (int probe = 0; probe < 6; probe++)
        {
            await _delay.WaitAsync(TimeSpan.FromSeconds(2), token);
            string registration = await CommandAsync(session, "AT+CREG?", 5000, token);
            if (IsCsRegistered(registration))
                return (true, true);
        }

        return (false, changedNetwork);
    }

    private static bool IsLteNetwork(string response) =>
        Regex.IsMatch(
            response ?? string.Empty,
            @"\+COPS:[^\r\n]*,\s*7(?:\s|$)",
            RegexOptions.IgnoreCase);

    private static bool TryGetCregStatus(string response, out int status)
    {
        status = -1;
        var match = Regex.Match(response ?? string.Empty, @"\+CREG:\s*\d+\s*,\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out status);
    }

    private readonly record struct PortPreparation(
        string? Error,
        bool RestoreAutomaticNetwork);

    private async Task<string> CommandAsync(PortSessionLease session, string command, int timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(session)) throw new OperationCanceledException(token);
        string result = await _modem.SendCommandAsync(session.PortName, command, timeout, true, token);
        if (!IsCurrent(session)) throw new OperationCanceledException(token);
        return result;
    }

    private bool IsCurrent(PortSessionLease session) =>
        _sessions.IsCurrent(session.PortName, session.Ccid, session.Epoch);

    private static bool IsCommandError(string response) =>
        string.IsNullOrWhiteSpace(response) || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private static bool IsCsRegistered(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        var reg = Regex.Match(response, @"\+CREG:\s*\d+\s*,\s*(\d+)");
        return reg.Success && reg.Groups[1].Value is "1" or "5";
    }

    private static string EncodeUcs2(string value) =>
        Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value));

    private static bool IsFailure(string result) =>
        result.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || result.Contains("Thao tac khong hop le", StringComparison.OrdinalIgnoreCase)
        || result.Contains("he thong ban", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 2", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 4", StringComparison.OrdinalIgnoreCase)
        || result.Contains("+CUSD: 5", StringComparison.OrdinalIgnoreCase);

    private const string SessionChangedError = "ERROR: SIM session changed during USSD operation";
}
