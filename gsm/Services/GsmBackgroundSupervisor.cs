using gsm.Models;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed class GsmBackgroundSupervisorContext
{
    public required Func<List<SimPort>> GetPorts { get; init; }
    public required Func<SimPort, bool> IsActive { get; init; }
    public required Func<bool> IsWatchdogEnabled { get; init; }
    public required Func<string, bool> IsSmsInProgress { get; init; }
    public required Func<SimPort, string, Task> SendBalanceUssdAsync { get; init; }
    public required Action<SimPort, int> SetSignalStrength { get; init; }
    public required Action<SimPort> MarkSmsSweep { get; init; }
    public required Action<SimPort> MarkConnectionTimeout { get; init; }
    public required Action<string> InvalidateSession { get; init; }
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
            _ = RunBalanceLoopAsync(context, token);
            _ = RunSignalLoopAsync(context, token);
            _ = RunSmsSweepLoopAsync(context, token);
            _ = RunWatchdogLoopAsync(context, token);
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    public void Dispose() => Stop();

    private async Task RunBalanceLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(TimeSpan.FromMinutes(30), token))
        {
            var ports = context.GetPorts().Where(context.IsActive).ToList();
            if (ports.Count == 0) continue;
            context.Log("[HỆ THỐNG] Tự động kiểm tra số dư định kỳ (30 phút/lần)...", "INFO");
            foreach (var port in ports)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    await context.SendBalanceUssdAsync(port, "Làm mới số dư tự động");
                    await _delay.WaitAsync(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { context.Log($"[{port.PortName}] Balance supervisor: {ex.Message}", "ERROR"); }
            }
        }
    }

    private async Task RunSignalLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(TimeSpan.FromSeconds(15), token))
        {
            foreach (var port in context.GetPorts().Where(context.IsActive))
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    if (!_sessions.TryGet(port.PortName, out var session)) continue;
                    string response = await _modem.SendCommandAsync(port.PortName, "AT+CSQ", 5000, true);
                    if (!_sessions.IsCurrent(port.PortName, session.Ccid, session.Epoch)) continue;
                    var match = Regex.Match(response, @"\+CSQ:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int csq))
                        context.SetSignalStrength(port, csq >= 99 ? 0 : (int)(csq / 31d * 100));
                }
                catch (Exception ex) { context.Log($"[{port.PortName}] Signal supervisor: {ex.Message}", "WARN"); }
            }
        }
    }

    private async Task RunSmsSweepLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(TimeSpan.FromMinutes(3), token))
        {
            foreach (var port in context.GetPorts())
            {
                if (token.IsCancellationRequested) return;
                if (port.Status != SimStatus.Active || context.IsSmsInProgress(port.PortName)) continue;
                try
                {
                    if (!_sessions.TryGet(port.PortName, out var session)) continue;
                    await _modem.SweepUnreadSmsAsync(port.PortName);
                    if (!_sessions.IsCurrent(port.PortName, session.Ccid, session.Epoch)) continue;
                    context.MarkSmsSweep(port);
                    await _delay.WaitAsync(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { context.Log($"[{port.PortName}] SMS sweep: {ex.Message}", "WARN"); }
            }
        }
    }

    private async Task RunWatchdogLoopAsync(GsmBackgroundSupervisorContext context, CancellationToken token)
    {
        while (await WaitNextAsync(TimeSpan.FromMinutes(1), token))
        {
            foreach (var port in context.GetPorts())
            {
                if (token.IsCancellationRequested) return;
                if (port.Status == SimStatus.Connecting
                    && DateTime.Now - port.StatusChangedAt > TimeSpan.FromMinutes(2))
                {
                    context.MarkConnectionTimeout(port);
                    continue;
                }

                if (!context.IsWatchdogEnabled()) continue;
                if (port.Status != SimStatus.NoResponse && port.Status != "Offline" && port.Status != "Error") continue;

                try
                {
                    context.Log($"[WATCHDOG] Cổng {port.PortName} lỗi. Khởi động lại nhận diện SIM ở chế độ radio-off...", "WARN");
                    context.InvalidateSession(port.PortName);
                    await _modem.SendCommandAsync(port.PortName, "AT+CFUN=4", 8000, true);
                    _modem.StartHotplugWaitLoop(port.PortName);
                }
                catch (Exception ex)
                {
                    context.Log($"[{port.PortName}] Watchdog recovery: {ex.Message}", "ERROR");
                }
            }
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
