namespace gsm.Services;

/// <summary>
/// A cancelled or slow serial write still owns the COM command lock until the
/// underlying write finishes. Releasing it sooner permits overlapping writes
/// against the same USB driver handle.
/// </summary>
internal static class SerialWriteCompletionGuard
{
    internal static async Task RunAsync(
        Action write,
        TimeSpan slowThreshold,
        Action onSlow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(onSlow);
        cancellationToken.ThrowIfCancellationRequested();

        Task writeTask = Task.Run(write, cancellationToken);
        try
        {
            Task first = await Task.WhenAny(
                writeTask,
                Task.Delay(slowThreshold, cancellationToken)).ConfigureAwait(false);
            if (!ReferenceEquals(first, writeTask)
                && !cancellationToken.IsCancellationRequested)
            {
                onSlow();
            }
        }
        finally
        {
            // SerialPort.WriteTimeout bounds normal driver behavior. If a
            // driver is stuck, keep this COM locked instead of issuing another
            // write onto the same handle.
            await writeTask.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
