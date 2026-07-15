using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace gsm.Services;

public readonly record struct PortSessionLease(
    string PortName,
    string Ccid,
    long Epoch,
    CancellationToken Token);

public interface IPortSessionRegistry : IDisposable
{
    PortSessionLease Begin(string portName, string ccid, CancellationToken lifetimeToken = default);
    bool TryGet(string portName, out PortSessionLease session);
    bool IsCurrent(string portName, string ccid, long epoch);
    bool IsEpochCurrent(string portName, long epoch);
    void Invalidate(string portName);
    void InvalidateAll();
}

/// <summary>
/// Nguồn sự thật duy nhất cho phiên SIM trên từng COM. Mỗi lần cắm/rút/thay SIM
/// sẽ tăng epoch và hủy token của tất cả thao tác đang chạy trên phiên cũ.
/// </summary>
public sealed class PortSessionRegistry : IPortSessionRegistry
{
    private sealed record Entry(string Ccid, long Epoch, CancellationTokenSource Cts);

    private readonly ConcurrentDictionary<string, Entry> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _epochs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _portGates =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PortSessionLease Begin(string portName, string ccid, CancellationToken lifetimeToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        string normalizedCcid = NormalizeCcid(ccid);
        if (string.IsNullOrWhiteSpace(normalizedCcid))
            throw new ArgumentException("CCID is required to start a SIM session.", nameof(ccid));

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        object portGate = _portGates.GetOrAdd(portName, static _ => new object());
        lock (portGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            CancelAndDispose(portName);

            long epoch = _epochs.AddOrUpdate(portName, 1, (_, old) => checked(old + 1));
            var cts = lifetimeToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken)
                : new CancellationTokenSource();
            var entry = new Entry(normalizedCcid, epoch, cts);
            _sessions[portName] = entry;
            return new PortSessionLease(portName, normalizedCcid, epoch, cts.Token);
        }
    }

    public bool TryGet(string portName, out PortSessionLease session)
    {
        session = default;
        if (!_sessions.TryGetValue(portName, out var entry) || entry.Cts.IsCancellationRequested)
            return false;

        if (!IsCurrent(portName, entry.Ccid, entry.Epoch))
            return false;

        session = new PortSessionLease(portName, entry.Ccid, entry.Epoch, entry.Cts.Token);
        return true;
    }

    public bool IsCurrent(string portName, string ccid, long epoch)
    {
        return _sessions.TryGetValue(portName, out var entry)
            && !entry.Cts.IsCancellationRequested
            && entry.Epoch == epoch
            && string.Equals(entry.Ccid, NormalizeCcid(ccid), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsEpochCurrent(string portName, long epoch) =>
        _epochs.TryGetValue(portName, out long currentEpoch) && currentEpoch == epoch;

    public void Invalidate(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return;
        object portGate = _portGates.GetOrAdd(portName, static _ => new object());
        lock (portGate)
        {
            _epochs.AddOrUpdate(portName, 1, (_, old) => checked(old + 1));
            CancelAndDispose(portName);
        }
    }

    public void InvalidateAll()
    {
        string[] portNames = _sessions.Keys.ToArray();
        Parallel.ForEach(portNames, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, portNames.Length)
        }, Invalidate);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        string[] portNames = _sessions.Keys.ToArray();
        Parallel.ForEach(portNames, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, portNames.Length)
        }, portName =>
        {
            object portGate = _portGates.GetOrAdd(portName, static _ => new object());
            lock (portGate)
                CancelAndDispose(portName);
        });
        _portGates.Clear();
    }

    internal static string NormalizeCcid(string? ccid)
    {
        if (string.IsNullOrWhiteSpace(ccid)) return string.Empty;
        var match = Regex.Match(ccid, @"\b([A-Za-z0-9]{18,22})\b");
        if (match.Success) return match.Groups[1].Value;

        string clean = ccid.Replace("+CCID:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("+QCCID:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("OK", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ERROR", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\r", "")
            .Replace("\n", "")
            .Trim();
        return Regex.Replace(clean, @"\s+", "");
    }

    private void CancelAndDispose(string portName)
    {
        if (!_sessions.TryRemove(portName, out var old)) return;
        try { old.Cts.Cancel(); } catch { }
        old.Cts.Dispose();
    }
}
