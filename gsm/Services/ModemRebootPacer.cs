namespace gsm.Services;

/// <summary>
/// Spaces modem reset submissions across a USB bank. A per-port command lock
/// cannot prevent several modems on the same hub from resetting together.
/// </summary>
internal sealed class ModemRebootPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumGap;
    private long? _lastSubmissionCompletedTick;

    internal ModemRebootPacer(TimeSpan minimumGap)
    {
        if (minimumGap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumGap));
        _minimumGap = minimumGap;
    }

    internal async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submit);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            if (_lastSubmissionCompletedTick is long lastTick)
            {
                long remainingMilliseconds =
                    (long)_minimumGap.TotalMilliseconds
                    - (Environment.TickCount64 - lastTick);
                if (remainingMilliseconds > 0)
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(remainingMilliseconds),
                        cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            return await submit(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (submitted)
                _lastSubmissionCompletedTick = Environment.TickCount64;
            _gate.Release();
        }
    }
}
