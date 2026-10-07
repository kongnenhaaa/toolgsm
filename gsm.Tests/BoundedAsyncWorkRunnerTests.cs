using gsm.Services;

namespace gsm.Tests;

public sealed class BoundedAsyncWorkRunnerTests
{
    [Fact]
    public async Task ThirtyTwoItems_RunAllWithoutExceedingTenActiveWorkers()
    {
        const int itemCount = 32;
        const int maxConcurrency = 10;
        int active = 0;
        int maximumActive = 0;
        int completed = 0;

        await BoundedAsyncWorkRunner.RunAsync(
            Enumerable.Range(1, itemCount).ToArray(),
            maxConcurrency,
            async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                try
                {
                    await Task.Delay(10, cancellationToken);
                    Interlocked.Increment(ref completed);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        Assert.Equal(itemCount, Volatile.Read(ref completed));
        Assert.InRange(Volatile.Read(ref maximumActive), 2, maxConcurrency);
    }

    [Fact]
    public async Task SlotIsHeldUntilTheWholeWorkerFinishes()
    {
        const int maxConcurrency = 10;
        int started = 0;
        var firstWaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = BoundedAsyncWorkRunner.RunAsync(
            Enumerable.Range(1, 10).ToArray(),
            maxConcurrency,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref started) == maxConcurrency)
                    firstWaveStarted.TrySetResult();

                await releaseFirstWave.Task.WaitAsync(cancellationToken);
            });

        await firstWaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(maxConcurrency, Volatile.Read(ref started));

        releaseFirstWave.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(10, Volatile.Read(ref started));
    }

    [Fact]
    public async Task CancellationStillVisitsQueuedItemsForCleanup()
    {
        const int itemCount = 12;
        const int maxConcurrency = 10;
        int visited = 0;
        int waiting = 0;
        var firstWaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        Task run = BoundedAsyncWorkRunner.RunAsync(
            Enumerable.Range(1, itemCount).ToArray(),
            maxConcurrency,
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref visited);
                if (Interlocked.Increment(ref waiting) == maxConcurrency)
                    firstWaveStarted.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    // Represents the per-COM cleanup/result bookkeeping done
                    // by the VNPT workflow when Stop is pressed.
                }
                finally
                {
                    Interlocked.Decrement(ref waiting);
                }
            },
            cancellation.Token);

        await firstWaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(itemCount, Volatile.Read(ref visited));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (candidate <= observed) return;
        }
        while (Interlocked.CompareExchange(
                   ref maximum,
                   candidate,
                   observed) != observed);
    }
}
