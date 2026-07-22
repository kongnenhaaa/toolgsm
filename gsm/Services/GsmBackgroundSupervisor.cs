using gsm.Models;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed class GsmBackgroundSupervisorContext
{
    public required Func<List<SimPort>> GetPorts { get; init; }
    public required Func<SimPort, bool> IsActive { get; init; }
    public required Func<int> GetSignalScanIntervalSeconds { get; init; }
    public required Func<string, bool> IsSmsInProgress { get; init; }
    public required Action<SimPort, int, int> SetSignalReading { get; init; }
    public required Action<SimPort> MarkSmsSweep { get; init; }
    public required Action<string, string> Log { get; init; }
}

public interface IGsmBackgroundSupervisor : IDisposable
{
    void Start(GsmBackgroundSupervisorContext context, CancellationToken lifetimeToken);
    void Stop();
}

/// <summary>
/// Chứa các vòng nền định kỳ. Supervisor không sở hữu UI; mọi thay đổi
/// collection/state được trả về ViewModel qua callback rõ ràng.
/// </summary>
public sealed class GsmBackgroundSupervisor : IGsmBackgroundSupervisor
{
    private readonly IGsmModemService _modem;
    private readonly IPortSessionRegistry _sessions;
    private readonly IGsmOperationDelay _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public GsmBackgroundSupervisor(
        IGsmModemService modem,
        IPortSessionRegistry sessions,
        IGsmOperationDelay delay)
    {
        _modem = modem;
        _sessions = sessions;
        _delay = delay;
    }

    public void Start(GsmBackgroundSupervisorContext context, CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            StopCore();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationToken token = _cts.Token;
            _ = RunSignalLoopAsync(context, token);
            _ = RunSmsSweepLoopAsync(context, token);
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    public void Dispose() => Stop();

    private async Task RunSignalLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(GetSignalScanInterval(
            context.GetSignalScanIntervalSeconds()), token))
        {
            try
            {
                await BackendConcurrency.ForEachPortAsync(
                    context.GetPorts().Where(context.IsActive), async (port, ct) =>
                {
                    if (_modem.IsCallInProgress(port.PortName)) return;
                    try
                    {
                        if (!_sessions.TryGet(port.PortName, out var session)) return;
                        string response = await _modem.SendCommandAsync(port.PortName, "AT+CSQ", 5000, true);
                        if (!_sessions.IsCurrent(port.PortName, session.Ccid, session.Epoch)) return;
                        var match = Regex.Match(response, @"\+CSQ:\s*(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int csq))
                            context.SetSignalReading(port, csq, csq >= 99 ? 0 : (int)Math.Round(
                                csq / 31d * 100d, MidpointRounding.AwayFromZero));
                    }
                    catch (Exception ex) { context.Log($"[{port.PortName}] Signal supervisor: {ex.Message}", "WARN"); }
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    internal static TimeSpan GetSignalScanInterval(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));

    private async Task RunSmsSweepLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(TimeSpan.FromMinutes(3), token))
        {
            try
            {
                await BackendConcurrency.ForEachPortAsync(context.GetPorts(), async (port, ct) =>
                {
                    if (port.Status != SimStatus.Active
                        || context.IsSmsInProgress(port.PortName)
                        || _modem.IsCallInProgress(port.PortName)) return;
                    try
                    {
                        if (!_sessions.TryGet(port.PortName, out var session)) return;
                        await _modem.SweepUnreadSmsAsync(port.PortName);
                        if (!_sessions.IsCurrent(port.PortName, session.Ccid, session.Epoch)) return;
                        context.MarkSmsSweep(port);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex) { context.Log($"[{port.PortName}] SMS sweep: {ex.Message}", "WARN"); }
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> WaitNextAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            await _delay.WaitAsync(interval, token);
            return !token.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void StopCore()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
    }
}
