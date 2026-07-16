using System.Collections.Concurrent;

namespace gsm.Services;

public interface IGsmCallService : IDisposable
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks =
        new(StringComparer.OrdinalIgnoreCase);

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
        var portLock = _portLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));

        try
        {
            await portLock.WaitAsync(linkedCts.Token);
            try
            {
                if (!_sessions.IsCurrent(portName, session.Ccid, session.Epoch)) return false;
                bool result = await _modem.CallWithAudioAsync(
                    portName, phoneNumber, wavPath, durationSeconds, record, linkedCts.Token);
                return result && _sessions.IsCurrent(portName, session.Ccid, session.Epoch);
            }
            finally
            {
                portLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var portLock in _portLocks.Values) portLock.Dispose();
        _portLocks.Clear();
    }
}
