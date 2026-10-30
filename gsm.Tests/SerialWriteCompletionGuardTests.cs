using gsm.Services;

namespace gsm.Tests;

public sealed class SerialWriteCompletionGuardTests
{
    [Fact]
    public async Task SlowWrite_KeepsCallerWaitingUntilUnderlyingWriteFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int slowWarnings = 0;

        Task operation = SerialWriteCompletionGuard.RunAsync(
            () =>
            {
                entered.Set();
                release.Wait();
            },
            TimeSpan.FromMilliseconds(20),
            () => Interlocked.Increment(ref slowWarnings),
            CancellationToken.None);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Task.Delay(80);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, slowWarnings);
    }

    [Fact]
    public async Task CancelledWrite_KeepsCallerWaitingUntilUnderlyingWriteFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();

        Task operation = SerialWriteCompletionGuard.RunAsync(
            () =>
            {
                entered.Set();
                release.Wait();
            },
            TimeSpan.FromSeconds(1),
            () => { },
            cancellation.Token);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();
            await Task.Delay(50);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
