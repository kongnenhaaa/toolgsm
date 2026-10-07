using System.Diagnostics;
using gsm.Services;

namespace gsm.Tests;

public sealed class AsyncRequestPacerTests
{
    [Fact]
    public async Task ConcurrentWaiters_StartInSpacedOrder()
    {
        TimeSpan spacing = TimeSpan.FromMilliseconds(40);
        var pacer = new AsyncRequestPacer(spacing);
        var stopwatch = Stopwatch.StartNew();

        Task<TimeSpan>[] starts = Enumerable.Range(0, 4)
            .Select(async _ =>
            {
                await pacer.WaitForTurnAsync();
                return stopwatch.Elapsed;
            })
            .ToArray();

        TimeSpan[] actualStarts = (await Task.WhenAll(starts))
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(4, actualStarts.Length);
        for (int index = 1; index < actualStarts.Length; index++)
        {
            Assert.True(
                actualStarts[index] - actualStarts[index - 1]
                    >= TimeSpan.FromMilliseconds(30),
                $"Starts {index - 1} and {index} were not paced: " +
                $"{actualStarts[index - 1].TotalMilliseconds:0.0}ms -> " +
                $"{actualStarts[index].TotalMilliseconds:0.0}ms");
        }
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotBlockFollowingRequests()
    {
        var pacer = new AsyncRequestPacer(TimeSpan.FromMilliseconds(30));
        await pacer.WaitForTurnAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pacer.WaitForTurnAsync(cancellation.Token));

        await pacer.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }
}
