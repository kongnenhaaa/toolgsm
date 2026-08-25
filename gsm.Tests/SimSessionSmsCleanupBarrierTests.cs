using gsm.Services;

namespace gsm.Tests;

public sealed class SimSessionSmsCleanupBarrierTests
{
    [Fact]
    public async Task ConcurrentCallers_RunOneCleanupAndAwaitTheSameResult()
    {
        var barrier = new SimSessionSmsCleanupBarrier();
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanupToFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        async Task<(bool Success, string Message)> Cleanup()
        {
            Interlocked.Increment(ref runs);
            cleanupStarted.TrySetResult();
            await allowCleanupToFinish.Task;
            return (true, "clean");
        }

        Task<(bool Success, string Message)> first =
            barrier.EnsureAsync("COM1#ccid#1", Cleanup);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<(bool Success, string Message)> second =
            barrier.EnsureAsync("COM1#ccid#1", Cleanup);

        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        allowCleanupToFinish.TrySetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task BatchCannotStartUntilEverySelectedPortCleanupFinishes()
    {
        var barrier = new SimSessionSmsCleanupBarrier();
        var finishCom1 = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCom2 = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool myVnptStarted = false;

        Task batch = RunBatchAsync();

        finishCom1.TrySetResult();
        await Task.Yield();
        Assert.False(myVnptStarted);

        finishCom2.TrySetResult();
        await batch.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(myVnptStarted);

        async Task RunBatchAsync()
        {
            await Task.WhenAll(
                barrier.EnsureAsync(
                    "COM1#ccid-1#1",
                    async () =>
                    {
                        await finishCom1.Task;
                        return (true, "clean");
                    }),
                barrier.EnsureAsync(
                    "COM2#ccid-2#1",
                    async () =>
                    {
                        await finishCom2.Task;
                        return (true, "clean");
                    }));
            myVnptStarted = true;
        }
    }

    [Fact]
    public async Task BatchContinuesAfterAllCleanupAttemptsFinish_WhenOneCleanupFails()
    {
        var barrier = new SimSessionSmsCleanupBarrier();
        var finishFailedCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSuccessfulCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool myVnptStarted = false;

        Task batch = RunBatchAsync();

        finishFailedCleanup.TrySetResult();
        await Task.Yield();
        Assert.False(myVnptStarted);

        finishSuccessfulCleanup.TrySetResult();
        await batch.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(myVnptStarted);

        async Task RunBatchAsync()
        {
            (bool Success, string Message)[] results = await Task.WhenAll(
                barrier.EnsureAsync(
                    "COM1#ccid-1#1",
                    async () =>
                    {
                        await finishFailedCleanup.Task;
                        return (false, "cannot verify");
                    }),
                barrier.EnsureAsync(
                    "COM2#ccid-2#1",
                    async () =>
                    {
                        await finishSuccessfulCleanup.Task;
                        return (true, "clean");
                    }));

            Assert.Contains(results, result => !result.Success);
            myVnptStarted = true;
        }
    }

    [Fact]
    public async Task RemovingPort_AllowsTheNewSimSessionToRunItsOwnCleanup()
    {
        var barrier = new SimSessionSmsCleanupBarrier();
        int runs = 0;

        await barrier.EnsureAsync(
            "COM3#old-ccid#1",
            () => Task.FromResult((true, $"run-{Interlocked.Increment(ref runs)}")));

        barrier.RemovePort("com3");

        await barrier.EnsureAsync(
            "COM3#new-ccid#2",
            () => Task.FromResult((true, $"run-{Interlocked.Increment(ref runs)}")));

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task FailedCleanup_RemainsCompletedAndIsNotRetriedByDelayedCaller()
    {
        var barrier = new SimSessionSmsCleanupBarrier();
        int runs = 0;

        (bool Success, string Message) first = await barrier.EnsureAsync(
            "COM4#ccid#1",
            () => Task.FromResult((
                false,
                $"failed-{Interlocked.Increment(ref runs)}")));
        (bool Success, string Message) delayedCaller = await barrier.EnsureAsync(
            "COM4#ccid#1",
            () => Task.FromResult((
                true,
                $"unexpected-{Interlocked.Increment(ref runs)}")));

        Assert.False(first.Success);
        Assert.False(delayedCaller.Success);
        Assert.Equal(first.Message, delayedCaller.Message);
        Assert.Equal(1, runs);
    }
}
