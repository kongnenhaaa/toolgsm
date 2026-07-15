namespace gsm.Services;

public interface IGsmCallService
{
    Task<bool> CallAsync(
        string portName,
        string phoneNumber,
        string? wavPath,
        int durationSeconds,
        bool record,
        CancellationToken ct = default);
}

public sealed class GsmCallService : IGsmCallService
{
    private readonly IGsmModemService _modem;
    private readonly IPortSessionRegistry _sessions;

    public GsmCallService(IGsmModemService modem, IPortSessionRegistry sessions)
    {
        _modem = modem;
        _sessions = sessions;
    }

    public async Task<bool> CallAsync(
        string portName,
        string phoneNumber,
        string? wavPath,
        int durationSeconds,
        bool record,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGet(portName, out var session)) return false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);

        try
        {
            bool result = await _modem.CallWithAudioAsync(
                portName, phoneNumber, wavPath, durationSeconds, record, linkedCts.Token);
            return result && _sessions.IsCurrent(portName, session.Ccid, session.Epoch);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
