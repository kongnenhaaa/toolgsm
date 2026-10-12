using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class MyVnptOtpWaitPolicyTests
{
    [Fact]
    public void BatchConfiguration_StartsFullBankAndAllowsTwoAutomaticResends()
    {
        Assert.Equal(64, MainViewModel.MaxConcurrentMyVnptWorkflows);
        Assert.Equal(3, MainViewModel.MaxMyVnptOtpSendAttempts);
        Assert.Equal(6, MainViewModel.MaxMyVnptFreshOtpRecoveryRounds);
        Assert.Equal(
            TimeSpan.FromSeconds(65),
            MainViewModel.MyVnptOtpWaitTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            MainViewModel.MyVnptQueuedOtpGraceTimeout);
    }

    [Fact]
    public async Task OtpArrivesOnFirstAttempt_DoesNotResend()
    {
        Task claimed = Task.CompletedTask;
        int resendCount = 0;

        bool received = await MyVnptOtpWaitPolicy.WaitForClaimAsync(
            claimed,
            TimeSpan.FromMilliseconds(20),
            maxSendAttempts: 2,
            (_, _) =>
            {
                Interlocked.Increment(ref resendCount);
                return Task.CompletedTask;
            });

        Assert.True(received);
        Assert.Equal(0, Volatile.Read(ref resendCount));
    }

    [Fact]
    public async Task FirstAttemptTimesOut_ResendsOnceAndAcceptsOtp()
    {
        var claimed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int resendCount = 0;

        bool received = await MyVnptOtpWaitPolicy.WaitForClaimAsync(
            claimed.Task,
            TimeSpan.FromMilliseconds(20),
            maxSendAttempts: 2,
            (_, _) =>
            {
                Interlocked.Increment(ref resendCount);
                claimed.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.True(received);
        Assert.Equal(1, Volatile.Read(ref resendCount));
    }

    [Fact]
    public async Task BothAttemptsTimeOut_ReturnsFalseAfterOneResend()
    {
        var claimed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int resendCount = 0;

        bool received = await MyVnptOtpWaitPolicy.WaitForClaimAsync(
            claimed.Task,
            TimeSpan.FromMilliseconds(20),
            maxSendAttempts: 2,
            (_, _) =>
            {
                Interlocked.Increment(ref resendCount);
                return Task.CompletedTask;
            });

        Assert.False(received);
        Assert.Equal(1, Volatile.Read(ref resendCount));
    }
}
