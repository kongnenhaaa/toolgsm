using System.Diagnostics;

namespace gsm.Services;

/// <summary>
/// Spaces the start time of requests without serializing the requests after
/// they have started.
/// </summary>
internal sealed class AsyncRequestPacer(TimeSpan minimumSpacing)
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly long _minimumSpacingTicks = ToStopwatchTicks(
        minimumSpacing > TimeSpan.Zero
            ? minimumSpacing
            : throw new ArgumentOutOfRangeException(nameof(minimumSpacing)));
    private long _nextStartTimestamp;

    public async Task<TimeSpan> WaitForTurnAsync(
        CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long now = Stopwatch.GetTimestamp();
            long waitTicks = Math.Max(0, _nextStartTimestamp - now);
            TimeSpan waited = FromStopwatchTicks(waitTicks);
            if (waited > TimeSpan.Zero)
            {
                await Task.Delay(waited, cancellationToken).ConfigureAwait(false);
            }

            _nextStartTimestamp = Stopwatch.GetTimestamp()
                + _minimumSpacingTicks;
            return waited;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static long ToStopwatchTicks(TimeSpan value) =>
        (long)Math.Ceiling(value.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan FromStopwatchTicks(long value) =>
        TimeSpan.FromSeconds(value / (double)Stopwatch.Frequency);
}
