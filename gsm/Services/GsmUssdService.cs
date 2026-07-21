using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using gsm.Models;

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
                if (preflight != null)
                {
                    result = preflight;
                    if (IsCurrent(session))
                    {
                        try { await _modem.SendCommandAsync(portName, "AT+CREG=2", 5000, true); }
                        catch { /* Không che lỗi preflight chính. */ }
                    }
                }
                else
                {
                    if (!IsCurrent(session)) return SessionChangedError;
                    try
                    {
                        if (!IsCurrent(session)) return SessionChangedError;
                        // Chuỗi tương thích SAuto: PDU mode + UCS2 charset + Hex encoded USSD
                        await CommandAsync(session, "AT+CMGF=0", 5000, token);
                        await CommandAsync(session, "AT+CSCS=\"UCS2\"", 5000, token);
                        result = await _modem.SendCommandAsync(
                            portName, $"AT+CUSD=1,\"{EncodeUcs2(ussdCode)}\"", ct: token);

                        // Một số SIM/firmware chỉ trả OK sau khi nhận lệnh nhưng tổng đài
                        // không mở phiên USSD. Không được coi OK trần là thành công.
                        if (!result.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)
                            && !result.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                        {
                            result = $"ERROR: Modem accepted USSD but network returned no +CUSD (attempt {attempt})";
                        }
                        await _delay.WaitAsync(TimeSpan.FromSeconds(1.0), token);
                    }
                    finally
                    {
                        if (IsCurrent(session))
                        {
                            try { await _modem.SendCommandAsync(portName, "AT+CMGF=1", 5000, true); }
                            catch { /* Không che kết quả USSD chính. */ }
                            try { await _modem.SendCommandAsync(portName, "AT+CSCS=\"GSM\"", 5000, true); }
                            catch { /* Không che kết quả USSD chính. */ }
                            try { await _modem.SendCommandAsync(portName, "AT+CREG=2", 5000, true); }
                            catch { /* Polling COPS vẫn tiếp tục hoạt động. */ }
                        }
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

        if (!_ussdSupported.TryGetValue(session.PortName, out bool supportsUssd))
        {
            string test = await CommandAsync(session, "AT+CUSD=?", 5000, token);
            supportsUssd = !IsCommandError(test);
            _ussdSupported[session.PortName] = supportsUssd;
        }
        if (!supportsUssd) return "ERROR: Modem firmware does not expose AT+CUSD";

        string pin = await CommandAsync(session, "AT+CPIN?", 5000, token);
        if (IsCommandError(pin)) return $"ERROR: SIM status check failed ({pin.Trim()})";
        // Retry CPIN: modem vừa khởi động hoặc SIM hot-plug có thể trả về bare OK
        // trước khi report +CPIN: READY (giống pattern retry CREG bên dưới)
        for (int pinProbe = 0; pinProbe < 3 && !Regex.IsMatch(pin, @"\+CPIN:\s*READY", RegexOptions.IgnoreCase); pinProbe++)
        {
            if (IsCommandError(pin)) break; // Lỗi thật, không retry
            await _delay.WaitAsync(TimeSpan.FromSeconds(2), token);
            pin = await CommandAsync(session, "AT+CPIN?", 5000, token);
        }
        if (!Regex.IsMatch(pin, @"\+CPIN:\s*READY", RegexOptions.IgnoreCase))
            return $"ERROR: SIM not ready ({pin.Trim()})";


        // Tắt URC CREG chi tiết trong cửa sổ USSD để phản hồi lệnh không bị xen ngang.
        // Không thay đổi RAT/radio; cuối thao tác sẽ khôi phục CREG=2.
        await CommandAsync(session, "AT+CREG=0", 5000, token);
        string registration = await CommandAsync(session, "AT+CREG?", 5000, token);
        bool registered = IsCsRegistered(registration);
        // CFUN vừa bật trên 32/64 cổng có thể cần vài giây mới vào CS. Chờ thụ động
        // thay vì COPS=0 hoặc đổi WCDMA/GSM làm các modem tự rớt mạng lẫn nhau.
        // Tăng lên 10 probe × 3s = 30s để xử lý trường hợp CS domain lên chậm sau đổi network mode.
        for (int probe = 0; probe < 10 && !registered; probe++)
        {
            await _delay.WaitAsync(TimeSpan.FromSeconds(3), token);
            registration = await CommandAsync(session, "AT+CREG?", 5000, token);
            registered = IsCsRegistered(registration);
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
                return $"ERROR: SIM not registered on CS network ({registration.Trim()})";
            }
        }


        // CSQ=99 thường chỉ là chưa có số đo tức thời. Simmart vẫn gửi USSD trong trường
        // hợp này; chỉ dùng CSQ để chẩn đoán, không loại bỏ một COM đang CREG=1/5.
        _ = await CommandAsync(session, "AT+CSQ", 5000, token);

        await CommandAsync(session, "AT+CUSD=2", 5000, token);
        await _delay.WaitAsync(TimeSpan.FromMilliseconds(400), token);
        return null;
    }

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
