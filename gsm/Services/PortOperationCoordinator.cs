using System.Collections.Concurrent;

namespace gsm.Services;

/// <summary>
/// Serializes complete foreground workflows per physical COM port.  The modem's
/// command semaphore protects one AT command; this coordinator protects the
/// whole SMS/call/USSD transaction so a later workflow cannot enter between the
/// cleanup, command and asynchronous result phases of the previous workflow.
/// Different COM ports remain fully parallel.
/// </summary>
internal sealed class PortOperationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(
        string portName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        SemaphoreSlim gate = _gates.GetOrAdd(
            portName,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            SemaphoreSlim? current = Interlocked.Exchange(ref _gate, null);
            current?.Release();
        }
    }
}
