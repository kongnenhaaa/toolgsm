using gsm.Services;

namespace gsm.Tests;

public sealed class BackendConcurrencyTests
{
    [Fact]
    public async Task DynamicScheduler_StartsMoreThanSixtyFourPortsTogether()
    {
        const int portCount = BackendConcurrency.BaselineConcurrentPorts * 2;
        int entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = BackendConcurrency.ForEachPortAsync(
            Enumerable.Range(1, portCount),
            async (_, _) =>
            {
                if (Interlocked.Increment(ref entered) == portCount)
                    allEntered.TrySetResult();
                await release.Task;
            });

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(portCount, Volatile.Read(ref entered));
        release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task OnePortFailure_DoesNotStopOtherPortWorkflows()
    {
        const int portCount = BackendConcurrency.BaselineConcurrentPorts * 2;
        int completed = 0;

        Task run = BackendConcurrency.ForEachPortAsync(
            Enumerable.Range(1, portCount),
            async (port, _) =>
            {
                await Task.Yield();
                if (port == 37) throw new IOException("isolated COM failure");
                Interlocked.Increment(ref completed);
            });

        IOException error = await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal("isolated COM failure", error.Message);
        Assert.Equal(portCount - 1, Volatile.Read(ref completed));
    }
}
