using gsm.Models;

namespace gsm.Tests;

public class SimPortSignalScanTests
{
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
