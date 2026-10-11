namespace gsm.Services;

/// <summary>
/// Waits for a per-COM MyVNPT OTP and retries otp_send when an SMS is lost.
/// The underlying claim task is never cancelled by an attempt timeout, so an
/// OTP that arrives at the boundary can still complete the workflow.
/// </summary>
internal static class MyVnptOtpWaitPolicy
{
    public static async Task<bool> WaitForClaimAsync(
        Task otpClaimedTask,
        TimeSpan attemptTimeout,
        int maxSendAttempts,
        Func<int, CancellationToken, Task> resendAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(otpClaimedTask);
        ArgumentNullException.ThrowIfNull(resendAsync);
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        if (maxSendAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSendAttempts));

        for (int sendAttempt = 1; sendAttempt <= maxSendAttempts; sendAttempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(attemptTimeout);

            try
            {
                await otpClaimedTask.WaitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (otpClaimedTask.IsCompleted)
                {
                    await otpClaimedTask.ConfigureAwait(false);
                    return true;
                }

                if (sendAttempt == maxSendAttempts)
                    return false;

                await resendAsync(sendAttempt + 1, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return false;
    }
}
