using System.Collections.Concurrent;

namespace gsm.Services;

/// <summary>
/// Giữ OTP MyVNPT đến trong khe rất ngắn giữa lúc một workflow đã sở hữu COM
/// và lúc phiên chờ OTP được đăng ký. Chỉ OTP của đúng phiên SIM mới được lấy.
/// </summary>
internal sealed class MyVnptEarlyOtpBuffer
{
    private sealed record Entry(
        string Ccid,
        long Epoch,
        string Otp,
        DateTimeOffset ReceivedAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public void Store(
        string portName,
        string ccid,
        long epoch,
        string otp,
        DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(portName)
            || string.IsNullOrWhiteSpace(ccid)
            || string.IsNullOrWhiteSpace(otp))
        {
            return;
        }

        _entries[portName] = new Entry(ccid, epoch, otp, receivedAtUtc);
    }

    public bool TryTake(
        string portName,
        string expectedCcid,
        long expectedEpoch,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge,
        out string otp)
    {
        otp = string.Empty;
        if (!_entries.TryRemove(portName, out Entry? entry)) return false;

        bool sameSession = entry.Epoch == expectedEpoch
            && string.Equals(
                entry.Ccid,
                expectedCcid,
                StringComparison.OrdinalIgnoreCase);
        TimeSpan age = nowUtc - entry.ReceivedAtUtc;
        if (!sameSession || age < TimeSpan.Zero || age > maximumAge)
            return false;

        otp = entry.Otp;
        return true;
    }

    public void Remove(string portName) => _entries.TryRemove(portName, out _);
}
