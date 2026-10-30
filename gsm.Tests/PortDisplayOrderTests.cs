using gsm.Models;

namespace gsm.Tests;

public class PortDisplayOrderTests
{
    [Fact]
    public void Sort_FollowsCurrentPortListAndPreservesOrderWithinEachPort()
    {
        var ports = new[]
        {
            new SimPort { PortName = "COM12" },
            new SimPort { PortName = "COM3" }
        };
        var items = new[] { "COM3:first", "COM99:unknown", "com12:first", "COM3:second" };

        var sorted = PortDisplayOrder.Sort(items, ports, item => item.Split(':')[0]);

        Assert.Equal(new[] { "com12:first", "COM3:first", "COM3:second", "COM99:unknown" }, sorted);
    }
}
