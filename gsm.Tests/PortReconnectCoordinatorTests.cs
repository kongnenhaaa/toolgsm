using gsm.Services;

namespace gsm.Tests;

public sealed class PortReconnectCoordinatorTests
{
    [Fact]
    public async Task RunAsync_CoalescesConcurrentRequestsForSamePort()
    {
        var coordinator = new PortReconnectCoordinator();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;

        Task<bool> first = coordinator.RunAsync("COM7", async () =>
        {
            Interlocked.Increment(ref invocationCount);
            entered.TrySetResult();
            await release.Task;
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Task<bool> second = coordinator.RunAsync("com7", () =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(false);
        });

        Assert.Equal(1, Volatile.Read(ref invocationCount));
        Assert.Equal(1, coordinator.ActiveCount);

        release.TrySetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, Volatile.Read(ref invocationCount));
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public async Task RunAsync_ReleasesFailedOperationSoPortCanRetry()
    {
        var coordinator = new PortReconnectCoordinator();
        int invocationCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync("COM9", () =>
            {
                Interlocked.Increment(ref invocationCount);
                return Task.FromException<bool>(
                    new InvalidOperationException("open failed"));
            }));

        bool retried = await coordinator.RunAsync("COM9", () =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(true);
        });

        Assert.True(retried);
        Assert.Equal(2, invocationCount);
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public async Task RunAsync_DoesNotSerializeDifferentPorts()
    {
        var coordinator = new PortReconnectCoordinator();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int enteredCount = 0;

        Task<bool> first = coordinator.RunAsync("COM1", async () =>
        {
            Interlocked.Increment(ref enteredCount);
            await release.Task;
            return true;
        });
        Task<bool> second = coordinator.RunAsync("COM2", async () =>
        {
            Interlocked.Increment(ref enteredCount);
            await release.Task;
            return true;
        });

        Assert.Equal(2, Volatile.Read(ref enteredCount));
        Assert.Equal(2, coordinator.ActiveCount);

        release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second), Assert.True);
        Assert.Equal(0, coordinator.ActiveCount);
    }
}
