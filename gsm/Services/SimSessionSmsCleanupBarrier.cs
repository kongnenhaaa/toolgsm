using System.Collections.Concurrent;

namespace gsm.Services;

/// <summary>
/// Owns exactly one initial SMS-cleanup task for each COM + SIM session.
/// Multiple callers await the same task, so an early MyVNPT batch cannot race
/// the delayed post-USSD cleanup or start a second destructive cleanup.
/// </summary>
internal sealed class SimSessionSmsCleanupBarrier
{
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<(bool Success, string Message)>>> _cleanupTasks =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<(bool Success, string Message)> EnsureAsync(
        string sessionKey,
        Func<Task<(bool Success, string Message)>> cleanupFactory,
        CancellationToken waitCancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(cleanupFactory);

        Lazy<Task<(bool Success, string Message)>> cleanup =
            _cleanupTasks.GetOrAdd(
                sessionKey,
                _ => new Lazy<Task<(bool Success, string Message)>>(
                    () => RunCleanupFactoryAsync(cleanupFactory),
                    LazyThreadSafetyMode.ExecutionAndPublication));

        // Cancelling one waiter must not cancel the shared cleanup. The SIM
        // session token owned by the cleanup itself controls its lifetime.
        return cleanup.Value.WaitAsync(waitCancellationToken);
    }

    public void RemovePort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return;

        string prefix = portName + "#";
        foreach (string key in _cleanupTasks.Keys.Where(key =>
                     key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _cleanupTasks.TryRemove(key, out _);
        }
    }

    private static async Task<(bool Success, string Message)> RunCleanupFactoryAsync(
        Func<Task<(bool Success, string Message)>> cleanupFactory) =>
        await cleanupFactory().ConfigureAwait(false);
}
