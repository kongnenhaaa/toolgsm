namespace gsm.Services;

public interface IGsmUssdService : IDisposable
{
    Task<string> SendAsync(
        string portName,
        string ussdCode,
        CancellationToken ct = default);
}

/// <summary>
/// Luồng USSD thủ công tương ứng trực tiếp với GSMController.USSDCheck của
/// SAuto: hủy phiên, đợi terminal OK, gửi nguyên mã với DCS 15 rồi đợi
/// payload +CUSD thành công. Mỗi lệnh chỉ giữ UART đến phản hồi terminal rồi
/// nhả cổng; vòng CPIN/CSQ nền tiếp tục chạy trong lúc chờ +CUSD như SAuto.
/// </summary>
public sealed class GsmUssdService : IGsmUssdService
{
    private readonly IGsmModemService _modem;

    public GsmUssdService(IGsmModemService modem)
    {
        _modem = modem;
    }

    public async Task<string> SendAsync(
        string portName,
        string ussdCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ussdCode))
            return "ERROR: Invalid USSD request";

        try
        {
            string[] stages = ussdCode.Split('|');
            if (stages.Length == 0) return "ERROR: Invalid USSD request";

            string? response = await _modem.RunSautoManualUssdAsync(
                portName,
                stages,
                ct);

            return string.IsNullOrWhiteSpace(response)
                ? "ERROR: USSD response timeout"
                : UssdResponseDecoder.Normalize(response);
        }
        catch (OperationCanceledException)
        {
            return "ERROR: USSD operation cancelled";
        }
    }

    public void Dispose() { }
}
