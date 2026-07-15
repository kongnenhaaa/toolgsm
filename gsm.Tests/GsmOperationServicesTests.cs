using gsm.Services;
using gsm.Tests.Fakes;

namespace gsm.Tests;

public sealed class GsmOperationServicesTests
{
    private const string CcidA = "89840123456789012345";
    private const string CcidB = "89840987654321098765";

    [Fact]
    public async Task Sms_Success_UsesCurrentComAndRestoresUcs2()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM5", CcidA);
        var modem = new FakeGsmModemService();
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync("COM5", "0912345678", "Tiếng Việt đẹp");

        Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(modem.SmsRequests);
        Assert.Equal("COM5", request.Port);
        Assert.Equal("Tieng Viet dep", request.Message);
        Assert.Contains("COM5:AT+CSCS=\"UCS2\"", modem.Commands);
        Assert.False(sms.IsInProgress("COM5"));
    }

    [Fact]
    public async Task Sms_SimReplacedWhileSending_CannotCompleteForNewSession()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM6", CcidA);
        var modem = new FakeGsmModemService();
        var enteredSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.SmsHandler = async (_, _, _) =>
        {
            enteredSend.SetResult();
            await releaseSend.Task;
            return "+CMGS: 1\r\nOK";
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        Task<string> operation = sms.SendAsync("COM6", "0912345678", "hello");
        await enteredSend.Task;
        sessions.Begin("COM6", CcidB);
        releaseSend.SetResult();
        string result = await operation;

        Assert.Contains("session changed", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COM6:AT+CSCS=\"UCS2\"", modem.Commands);
    }

    [Fact]
    public async Task Ussd_PreflightAndSend_RunAgainstFakeModem()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM8", CcidA);
        var modem = new FakeGsmModemService();
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem, sessions, sms, new ImmediateGsmOperationDelay());

        string result = await ussd.SendAsync("COM8", "*101#", 1);

        Assert.Contains("10000 VND", result);
        Assert.Contains("COM8:AT+CPIN?", modem.Commands);
        Assert.Contains("COM8:AT+CREG?", modem.Commands);
        Assert.Contains("COM8:AT+CUSD=1,\"*101#\",15", modem.Commands);
    }

    [Fact]
    public async Task Ussd_BareOk_IsRetriedWithAlternateDcsUntilCusdArrives()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM24", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CPIN?" => "+CPIN: READY\r\nOK",
                "AT+CREG?" => "+CREG: 0,1\r\nOK",
                "AT+CSQ" => "+CSQ: 20,99\r\nOK",
                "AT+CUSD=1,\"*101#\",15" => "OK",
                "AT+CUSD=1,\"*101#\",0" => "+CUSD: 0,\"4321 VND\",15\r\nOK",
                _ => "OK"
            })
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem, sessions, sms, new ImmediateGsmOperationDelay());

        string result = await ussd.SendAsync("COM24", "*101#", 3);

        Assert.Contains("4321 VND", result);
        Assert.Contains("COM24:AT+CUSD=1,\"*101#\",15", modem.Commands);
        Assert.Contains("COM24:AT+CUSD=2", modem.Commands);
        Assert.Contains("COM24:AT+CUSD=1,\"*101#\",0", modem.Commands);
    }

    [Fact]
    public async Task Ussd_SimRemovedDuringCommand_ReturnsCancelledNotSuccess()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM9", CcidA);
        var modem = new FakeGsmModemService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.CommandHandler = async (_, command) =>
        {
            if (!command.StartsWith("AT+CUSD=1", StringComparison.Ordinal))
                return command switch
                {
                    "AT+CPIN?" => "+CPIN: READY\r\nOK",
                    "AT+CREG?" => "+CREG: 0,1\r\nOK",
                    "AT+CSQ" => "+CSQ: 20,99\r\nOK",
                    _ => "OK"
                };
            entered.SetResult();
            await release.Task;
            return "+CUSD: 0,\"SUCCESS\",15\r\nOK";
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem, sessions, sms, new ImmediateGsmOperationDelay());

        Task<string> operation = ussd.SendAsync("COM9", "*101#", 1);
        await entered.Task;
        sessions.Invalidate("COM9");
        release.SetResult();
        string result = await operation;

        Assert.Contains("cancelled", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUCCESS", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Call_SimRemoved_CancelsFakeCall()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM10", CcidA);
        var enteredCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            CallHandler = async (_, _, ct) =>
            {
                enteredCall.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            }
        };
        var calls = new GsmCallService(modem, sessions);

        Task<bool> operation = calls.CallAsync("COM10", "0912345678", null, 30, false);
        await enteredCall.Task;
        sessions.Invalidate("COM10");

        Assert.False(await operation);
    }

    [Fact]
    public async Task Sms_CallerCancelsWhileWaitingForSameComLock_DoesNotSendSecondMessage()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM11", CcidA);
        var modem = new FakeGsmModemService();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.SmsHandler = async (_, _, _) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
            return "+CMGS: 1\r\nOK";
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        Task<string> first = sms.SendAsync("COM11", "0911111111", "first");
        await firstEntered.Task;
        using var cts = new CancellationTokenSource();
        Task<string> second = sms.SendAsync("COM11", "0922222222", "second", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        releaseFirst.SetResult();
        Assert.Contains("thành công", await first, StringComparison.OrdinalIgnoreCase);
        Assert.Single(modem.SmsRequests);
    }

    [Fact]
    public async Task Sms_DifferentComPorts_DoNotBlockEachOther()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM12", CcidA);
        sessions.Begin("COM13", CcidB);
        var modem = new FakeGsmModemService();
        int entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.SmsHandler = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
            await release.Task;
            return "+CMGS: 1\r\nOK";
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        Task<string> first = sms.SendAsync("COM12", "0911111111", "one");
        Task<string> second = sms.SendAsync("COM13", "0922222222", "two");
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();

        Assert.All(await Task.WhenAll(first, second), result =>
            Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sms_MoreThanSixtyFourComPorts_CanRunConcurrently()
    {
        using var sessions = new PortSessionRegistry();
        const int portCount = BackendConcurrency.BaselineConcurrentPorts * 2;
        for (int i = 1; i <= portCount; i++)
            sessions.Begin($"COM{i}", $"8984{i:D16}");

        var modem = new FakeGsmModemService();
        int entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.SmsHandler = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref entered) == portCount)
                allEntered.TrySetResult();
            await release.Task;
            return "+CMGS: 1\r\nOK";
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        Task<string>[] operations = Enumerable.Range(1, portCount)
            .Select(i => sms.SendAsync($"COM{i}", "0912345678", $"message-{i}"))
            .ToArray();

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(portCount, Volatile.Read(ref entered));
        release.TrySetResult();

        Assert.All(await Task.WhenAll(operations), result =>
            Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ussd_MoreThanSixtyFourComPorts_CanReachNetworkConcurrently()
    {
        using var sessions = new PortSessionRegistry();
        const int portCount = BackendConcurrency.BaselineConcurrentPorts * 2;
        for (int i = 1; i <= portCount; i++)
            sessions.Begin($"COM{i}", $"8984{i:D16}");

        int entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            CommandHandler = async (_, command) =>
            {
                if (!command.StartsWith("AT+CUSD=1", StringComparison.Ordinal))
                    return command switch
                    {
                        "AT+CPIN?" => "+CPIN: READY\r\nOK",
                        "AT+CREG?" => "+CREG: 0,1\r\nOK",
                        "AT+CSQ" => "+CSQ: 20,99\r\nOK",
                        _ => "OK"
                    };

                if (Interlocked.Increment(ref entered) == portCount)
                    allEntered.TrySetResult();
                await release.Task;
                return "+CUSD: 0,\"10000 VND\",15\r\nOK";
            }
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem, sessions, sms, new ImmediateGsmOperationDelay());

        Task<string>[] operations = Enumerable.Range(1, portCount)
            .Select(i => ussd.SendAsync($"COM{i}", "*101#", 1))
            .ToArray();

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(portCount, Volatile.Read(ref entered));
        release.TrySetResult();

        Assert.All(await Task.WhenAll(operations), result => Assert.Contains("10000 VND", result));
    }

    [Fact]
    public async Task Sms_CharsetRestoreFailure_DoesNotHideSuccessfulSend()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM14", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => command == "AT+CSCS=\"UCS2\""
                ? Task.FromException<string>(new IOException("restore failed"))
                : Task.FromResult("OK")
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync("COM14", "0912345678", "hello");

        Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ussd_CharsetRestoreFailure_DoesNotHideSuccessfulResult()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM15", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => command switch
            {
                "AT+CSCS=\"UCS2\"" => Task.FromException<string>(new IOException("restore failed")),
                "AT+CPIN?" => Task.FromResult("+CPIN: READY\r\nOK"),
                "AT+CREG?" => Task.FromResult("+CREG: 0,1\r\nOK"),
                "AT+CSQ" => Task.FromResult("+CSQ: 20,99\r\nOK"),
                _ when command.StartsWith("AT+CUSD=1", StringComparison.Ordinal) =>
                    Task.FromResult("+CUSD: 0,\"25000 VND\",15\r\nOK"),
                _ => Task.FromResult("OK")
            }
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem, sessions, sms, new ImmediateGsmOperationDelay());

        string result = await ussd.SendAsync("COM15", "*101#", 1);

        Assert.Contains("25000 VND", result);
    }
}
