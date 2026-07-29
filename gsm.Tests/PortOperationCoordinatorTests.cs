using gsm.Services;

namespace gsm.Tests;

public sealed class PortOperationCoordinatorTests
{
    [Fact]
    public async Task SamePort_WaitsUntilWholeForegroundWorkflowEnds()
    {
        var coordinator = new PortOperationCoordinator();
        using IDisposable first = await coordinator.AcquireAsync(
            "COM89",
            CancellationToken.None);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task second = Task.Run(async () =>
        {
            using IDisposable lease = await coordinator.AcquireAsync(
                "com89",
                CancellationToken.None);
            secondEntered.TrySetResult();
        });

        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);

        first.Dispose();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await second;
    }

    [Fact]
    public async Task DifferentPorts_RemainParallel()
    {
        var coordinator = new PortOperationCoordinator();
        using IDisposable first = await coordinator.AcquireAsync(
            "COM98",
            CancellationToken.None);

        Task<IDisposable> second = coordinator.AcquireAsync(
            "COM99",
            CancellationToken.None);

        using IDisposable secondLease =
            await second.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
