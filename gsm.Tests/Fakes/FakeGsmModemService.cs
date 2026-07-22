using System.Collections.Concurrent;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests.Fakes;

public sealed class FakeGsmModemService : IGsmModemService
{
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<(string Port, string Phone, string Message)> SmsRequests { get; } = new();

    public Func<string, string, Task<string>>? CommandHandler { get; set; }
    public Func<string, string, string, Task<string>>? SmsHandler { get; set; }
    public Func<string, string, CancellationToken, Task<bool>>? CallHandler { get; set; }
    public bool CallInProgress { get; set; }
    public QuectelModemProfile? ModemProfile { get; set; }


    public event EventHandler<GsmDataEventArgs>? SmsReceived;
    public event EventHandler<GsmDataEventArgs>? LogMessage;
    public event EventHandler<GsmDataEventArgs>? PortDisconnected;
    public event EventHandler<GsmDataEventArgs>? CallIncoming;
    public event EventHandler<GsmDataEventArgs>? CallEnded;
    public event EventHandler<GsmDataEventArgs>? DtmfReceived;
    public event EventHandler<IncomingCallSession>? IncomingCallRinging;
    public event EventHandler<IncomingCallSession>? IncomingCallEnded;

    public async Task<string> SendCommandAsync(
        string portName,
        string command,
        int timeoutMs = 5000,
        bool silent = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Commands.Enqueue($"{portName}:{command}");
        if (CommandHandler != null) return await CommandHandler(portName, command).WaitAsync(ct);
        return DefaultResponse(command);
    }

    public Task<string> SendRawAsync(string portName, string data, int timeoutMs = 5000, bool silent = false) =>
        SendCommandAsync(portName, data, timeoutMs, silent);

    public async Task<string> SendSmsAsync(
        string portName,
        string phoneNumber,
        string message,
        int timeoutMs = 15000,
        CancellationToken ct = default)
    {
        SmsRequests.Enqueue((portName, phoneNumber, message));
        if (SmsHandler != null) return await SmsHandler(portName, phoneNumber, message).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return "+CMGS: 1\r\nOK";
    }

    public Task<bool> CallWithAudioAsync(
        string portName,
        string phoneNumber,
        string? wavPath,
        int durationSeconds = 30,
        bool record = false,
        CancellationToken ct = default) =>
        CallHandler?.Invoke(portName, phoneNumber, ct) ?? Task.FromResult(true);

    public bool IsCallInProgress(string portName) => CallInProgress;
    public QuectelModemProfile? GetModemProfile(string portName) => ModemProfile;

    public Task SweepUnreadSmsAsync(string portName) => Task.CompletedTask;
    public Task<string> DownloadFileFromModemAsync(string portName, string remoteFile, string localFile) => Task.FromResult("OK");
    public Task<bool> UploadFileToModemAsync(string portName, string localFile, string remoteFile) => Task.FromResult(true);
    public void StartPollingNetwork(string portName) { }
    public List<string> GetAvailablePorts() => ["COM1", "COM2"];
    public string ConnectAll(int baudRate = 115200) => "OK";
    public void Disconnect(string portName) { }
    public void DisconnectAll() { }
    public IDisposable SuspendPortBackgroundOperations(string portName) => new NoopDisposable();
    public void StartHotplugWaitLoop(string portName) { }
    public Task HandleSimInsertedAsync(string portName) => Task.CompletedTask;
    public Task<bool> ReinitializeSettingsAsync(string portName, CancellationToken ct = default) => Task.FromResult(true);
    public Task ReloadSimAsync(string portName) => Task.CompletedTask;
    public Task<bool> ReloadAndResumeSimAsync(string portName, CancellationToken ct = default) => Task.FromResult(true);

    public void RaiseSms(string portName, string sender, string data) =>
        SmsReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Sender = sender, Data = data });
    public void RaiseLog(string portName, string data) =>
        LogMessage?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = data });
    public void RaiseDisconnect(string portName) =>
        PortDisconnected?.Invoke(this, new GsmDataEventArgs { PortName = portName });

    // Keep all interface events reachable in tests without depending on serial hardware.
    public void RaiseCallIncoming(string portName) => CallIncoming?.Invoke(this, new GsmDataEventArgs { PortName = portName });
    public void RaiseCallEnded(string portName) => CallEnded?.Invoke(this, new GsmDataEventArgs { PortName = portName });
    public void RaiseDtmf(string portName, string data) => DtmfReceived?.Invoke(this, new GsmDataEventArgs { PortName = portName, Data = data });
    public void RaiseRinging(IncomingCallSession session) => IncomingCallRinging?.Invoke(this, session);
    public void RaiseIncomingEnded(IncomingCallSession session) => IncomingCallEnded?.Invoke(this, session);

    private static string DefaultResponse(string command) => command switch
    {
        "AT+CPIN?" => "+CPIN: READY\r\nOK",
        "AT+CREG?" => "+CREG: 0,1\r\nOK",
        "AT+CEREG?" => "+CEREG: 0,1\r\nOK",
        "AT+COPS?" => "+COPS: 0,0,\"VINAPHONE\"\r\nOK",
        "AT+CSQ" => "+CSQ: 20,99\r\nOK",
        _ when command.StartsWith("AT+CUSD=1", StringComparison.Ordinal) => "+CUSD: 0,\"10000 VND\",15\r\nOK",
        _ => "OK"
    };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public sealed class ImmediateGsmOperationDelay : IGsmOperationDelay
{
    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Enqueue(delay);
        return Task.CompletedTask;
    }
}
