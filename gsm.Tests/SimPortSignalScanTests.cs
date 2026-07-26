using gsm.Models;

namespace gsm.Tests;

public class SimPortSignalScanTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(15, 15)]
    [InlineData(600, 300)]
    public void SignalScanInterval_IsClampedToSafeRange(int configured, int expected)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            gsm.Services.GsmBackgroundSupervisor.GetSignalScanInterval(configured));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SignalProbe_DefersSmsAndCalls(
        bool smsInProgress,
        bool callInProgress,
        bool expected)
    {
        Assert.Equal(
            expected,
            gsm.Services.GsmBackgroundSupervisor.ShouldSkipSignalProbe(
                smsInProgress,
                callInProgress));
    }

    [Fact]
    public void LastSignalScanAt_FormatsTimeAndNotifiesDisplay()
    {
        var port = new SimPort();
        var changed = new List<string?>();
        port.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        port.LastSignalScanAt = new DateTime(2026, 7, 22, 14, 5, 9);

        Assert.Equal("14:05:09", port.LastSignalScanDisplay);
        Assert.Contains(nameof(SimPort.LastSignalScanAt), changed);
        Assert.Contains(nameof(SimPort.LastSignalScanDisplay), changed);
    }

    [Fact]
    public void LastSignalScanAt_WhenCleared_HasEmptyDisplay()
    {
        var port = new SimPort
        {
            LastSignalScanAt = new DateTime(2026, 7, 22, 14, 5, 9)
        };

        port.LastSignalScanAt = null;

        Assert.Empty(port.LastSignalScanDisplay);
    }
}
