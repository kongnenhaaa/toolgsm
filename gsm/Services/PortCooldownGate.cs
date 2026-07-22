using System.Collections.Concurrent;

namespace gsm.Services;

public sealed class PortCooldownGate
{
    private readonly ConcurrentDictionary<string, DateTime> _untilUtc =
        new(StringComparer.OrdinalIgnoreCase);

    public int ActiveCount
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            return _untilUtc.Count(item => item.Value > now);
        }
    }

    public void Start(string portName, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(portName) || duration <= TimeSpan.Zero) return;
        DateTime candidate = DateTime.UtcNow.Add(duration);
        _untilUtc.AddOrUpdate(
            portName,
            candidate,
            (_, current) => current > candidate ? current : candidate);
    }

    public bool TryGetRemaining(string portName, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_untilUtc.TryGetValue(portName, out DateTime untilUtc)) return false;

        remaining = untilUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero) return true;

        _untilUtc.TryRemove(new KeyValuePair<string, DateTime>(portName, untilUtc));
        remaining = TimeSpan.Zero;
        return false;
    }

    public async Task WaitAsync(
        string portName,
        Action<TimeSpan>? waiting = null,
        CancellationToken ct = default)
    {
        while (TryGetRemaining(portName, out TimeSpan remaining))
        {
            waiting?.Invoke(remaining);
            await Task.Delay(remaining, ct);
        }
    }
}
