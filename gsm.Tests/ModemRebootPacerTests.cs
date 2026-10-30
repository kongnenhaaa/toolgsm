using gsm.Services;

namespace gsm.Tests;

public sealed class ModemRebootPacerTests
{
    [Fact]
    public async Task ConcurrentReboots_AreSerializedAndSpaced()
    {
        var pacer = new ModemRebootPacer(TimeSpan.FromMilliseconds(60));
        var starts = new List<long>();
        int active = 0;
        int peak = 0;

        Task<int>[] submissions = Enumerable.Range(0, 3).Select(index =>
            pacer.RunAsync(async ct =>
            {
                int current = Interlocked.Increment(ref active);
                peak = Math.Max(peak, current);
                starts.Add(Environment.TickCount64);
                await Task.Delay(15, ct);
                Interlocked.Decrement(ref active);
                return index;
            }, CancellationToken.None)).ToArray();

        await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, peak);
        Assert.Equal(3, starts.Count);
        Assert.All(starts.Zip(starts.Skip(1)), pair =>
            Assert.True(pair.Second - pair.First >= 60));
    }

    [Fact]
    public async Task CancelledWait_DoesNotSubmitOrBlockNextReboot()
    {
        var pacer = new ModemRebootPacer(TimeSpan.FromMilliseconds(80));
        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        bool submitted = false;
        Task<int> waiting = pacer.RunAsync(_ =>
        {
            submitted = true;
            return Task.FromResult(2);
        }, cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(submitted);
        Assert.Equal(3, await pacer.RunAsync(
            _ => Task.FromResult(3), CancellationToken.None));
    }
}
