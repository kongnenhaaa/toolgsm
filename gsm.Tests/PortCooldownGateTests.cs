using System.Diagnostics;
using gsm.Services;

namespace gsm.Tests;

public sealed class PortCooldownGateTests
{
    [Fact]
    public async Task WaitAsync_WaitsThenAllowsOperationInsteadOfReturningAnError()
    {
        var gate = new PortCooldownGate();
        gate.Start("COM118", TimeSpan.FromMilliseconds(40));
        var stopwatch = Stopwatch.StartNew();

        await gate.WaitAsync("COM118");

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(20));
        Assert.False(gate.TryGetRemaining("COM118", out _));
        Assert.Equal(0, gate.ActiveCount);
    }

    [Fact]
    public async Task WaitAsync_ObservesCancellationSoChangedSimCannotReceiveQueuedSms()
    {
        var gate = new PortCooldownGate();
        gate.Start("COM81", TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync("COM81", ct: cts.Token));
    }

    [Fact]
    public async Task WaitAsync_ObservesCooldownExtendedByAnotherFailure()
    {
        var gate = new PortCooldownGate();
        gate.Start("COM90", TimeSpan.FromMilliseconds(35));
        Task wait = gate.WaitAsync("COM90");
        await Task.Delay(15);
        gate.Start("COM90", TimeSpan.FromMilliseconds(55));

        await wait;

        Assert.False(gate.TryGetRemaining("COM90", out _));
    }
}
