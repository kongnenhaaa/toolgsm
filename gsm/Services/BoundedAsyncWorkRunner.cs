namespace gsm.Services;

/// <summary>
/// Runs every queued item while keeping the complete lifetime of each worker
/// inside a bounded concurrency slot. Every worker is invoked even when the
/// batch token has been cancelled, allowing it to record a cancelled result
/// and release item-specific resources; the cancelled token is still passed
/// to the worker so it can finish that cleanup immediately.
/// </summary>
internal static class BoundedAsyncWorkRunner
{
    public static async Task RunAsync<T>(
        IReadOnlyCollection<T> items,
        int maxConcurrency,
        Func<T, CancellationToken, Task> worker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(worker);
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        if (items.Count == 0) return;

        using var gate = new SemaphoreSlim(
            Math.Min(maxConcurrency, items.Count),
            Math.Min(maxConcurrency, items.Count));

        Task[] tasks = items.Select(RunQueuedItemAsync).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task RunQueuedItemAsync(T item)
        {
            // Do not cancel the queue wait itself. Otherwise items that have
            // not acquired a slot never get a chance to publish "Đã hủy" and
            // remain stuck in the UI as "Chờ lượt...".
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await worker(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
