using gsm.Models;
using gsm.Services;
using gsm.Tests.Fakes;

namespace gsm.Tests;

public sealed class GsmOperationServicesTests
{
    private const string CcidA = "89840123456789012345";
    private const string CcidB = "89840987654321098765";

    [Fact]
    public async Task Sms_Success_PreservesVietnameseWithoutPostWorkflowCommands()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM5", CcidA);
        var modem = new FakeGsmModemService();
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync("COM5", "0912345678", "Tiếng Việt đẹp");

        Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(modem.SmsRequests);
        Assert.Equal("COM5", request.Port);
        Assert.Equal("Tiếng Việt đẹp", request.Message);
        Assert.Empty(modem.Commands);
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
    public async Task Ussd_ManualSequence_UsesOnlyCapturedSautoCommands()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM8", CcidA);
        var modem = new FakeGsmModemService();
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM8", "*101#");

        Assert.Contains("10000 VND", result);
        Assert.Equal(
        [
            "COM8:AT+CUSD=2",
            "COM8:AT+CUSD=1,\"*101#\",15"
        ], modem.Commands);
        Assert.Equal(
        [
            "COM8:AT+CUSD=2\r\n",
            "COM8:AT+CUSD=1,\"*101#\",15\r\n\r\n"
        ], modem.SautoWireWrites);
        Assert.Empty(modem.BackgroundSuspensions);
        Assert.Empty(modem.BackgroundResumptions);
    }

    [Fact]
    public async Task Ussd_101_DoesNotInjectRegistrationOrRatCommands()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM7", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CUSD=1,\"*101#\",15" =>
                    "+CUSD: 0,\"8000 VND\",15\r\nOK",
                _ => "OK"
            })
        };

        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM7", "*101#");

        Assert.Contains("8000 VND", result);
        Assert.DoesNotContain(modem.Commands, command =>
            command.Contains("CPIN", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CREG", StringComparison.OrdinalIgnoreCase)
            || command.Contains("COPS", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CFUN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ussd_BareOkThenLateCusd_IsReportedAsSuccess()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM81", CcidA);
        var modem = new FakeGsmModemService();
        modem.CommandHandler = async (port, command) =>
        {
            if (command.StartsWith("AT+CUSD=1", StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    modem.RaiseLog(port, "+CUSD: 0,\"4321 VND\",15");
                });
                return "OK";
            }

            return command switch
            {
                "AT+CPIN?" => "+CPIN: READY\r\nOK",
                "AT+CREG?" => "+CREG: 0,1\r\nOK",
                "AT+CSQ" => "+CSQ: 20,99\r\nOK",
                _ => "OK"
            };
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM81", "*101#");

        Assert.Contains("4321 VND", result);
        Assert.DoesNotContain("Modem accepted USSD", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ussd_BareOk_RetriesSameDirectSautoFlowUntilCusdArrives()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM24", CcidA);
        int ussdAttempts = 0;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CUSD=1,\"*101#\",15" =>
                    Interlocked.Increment(ref ussdAttempts) == 1
                        ? "OK"
                        : "+CUSD: 0,\"4321 VND\",15\r\nOK",
                _ => "OK"
            })
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM24", "*101#");

        Assert.Contains("4321 VND", result);
        Assert.Equal(2, modem.Commands.Count(c => c == "COM24:AT+CUSD=1,\"*101#\",15"));
        Assert.Equal(2, modem.Commands.Count(c => c == "COM24:AT+CUSD=2"));
        Assert.DoesNotContain(modem.Commands, c => c.Contains("nwscanmode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ussd_101_DoesNotProbeRegistrationBeforeCusd()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM25", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                _ when command.StartsWith("AT+CUSD=1", StringComparison.Ordinal) =>
                    "+CUSD: 0,\"12000 VND\",15\r\nOK",
                _ => "OK"
            })
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM25", "*101#");

        Assert.Contains("12000 VND", result);
        Assert.Contains("COM25:AT+CUSD=1,\"*101#\",15", modem.Commands);
        Assert.DoesNotContain("COM25:AT+CREG?", modem.Commands);
        Assert.DoesNotContain("COM25:AT+CEREG?", modem.Commands);
    }

    [Fact]
    public async Task Ussd_SecondAttempt_RepeatsOnlyDirectSautoSequence()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM26", CcidA);
        int ussdAttempts = 0;
        var modem = new FakeGsmModemService
        {
            ModemProfile = QuectelModemProfile.FromIdentity("Quectel", "EC20F", "test"),
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CUSD=1,\"*101#\",15" =>
                    Interlocked.Increment(ref ussdAttempts) == 1
                        ? "OK"
                        : "+CUSD: 0,\"9000 VND\",15\r\nOK",
                _ => "OK"
            })
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM26", "*101#");

        Assert.Contains("9000 VND", result);
        Assert.Equal(2, modem.Commands.Count(c => c == "COM26:AT+CUSD=1,\"*101#\",15"));
        Assert.Equal(2, modem.Commands.Count(c => c == "COM26:AT+CUSD=2"));
        Assert.DoesNotContain(modem.Commands, c => c.Contains("nwscanmode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ussd_111_UsesTheSameDirectSautoSequence()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM29", CcidA);
        var modem = new FakeGsmModemService();
        using var sms = new GsmSmsService(
            modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM29", "*111#");

        Assert.Contains("10000 VND", result);
        Assert.Contains("COM29:AT+CUSD=1,\"*111#\",15", modem.Commands);
    }

    [Fact]
    public async Task Ussd_DoesNotProbeCsqBeforeSending()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM27", CcidA);
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CUSD=1,\"*101#\",15" =>
                    "+CUSD: 0,\"15000 VND\",15\r\nOK",
                _ => "OK"
            })
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM27", "*101#");

        Assert.Contains("15000 VND", result);
        Assert.DoesNotContain("COM27:AT+CSQ", modem.Commands);
    }

    [Fact]
    public async Task Ussd_SimRegistryChange_DoesNotAddASeparateConstraint()
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
        using var ussd = new GsmUssdService(modem);

        Task<string> operation = ussd.SendAsync("COM9", "*101#");
        await entered.Task;
        sessions.Invalidate("COM9");
        release.SetResult();
        string result = await operation;

        Assert.Contains("SUCCESS", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ussd_CancellingOneCom_ReleasesItImmediatelyWithoutStoppingAnotherCom()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM17", CcidA);
        sessions.Begin("COM18", CcidB);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            CommandHandler = async (port, command) =>
            {
                if (!command.StartsWith("AT+CUSD=1", StringComparison.Ordinal))
                    return command switch
                    {
                        "AT+CPIN?" => "+CPIN: READY\r\nOK",
                        "AT+CREG?" => "+CREG: 0,1\r\nOK",
                        "AT+CSQ" => "+CSQ: 20,99\r\nOK",
                        _ => "OK"
                    };

                if (port == "COM17")
                {
                    firstEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }

                await releaseSecond.Task;
                return "+CUSD: 0,\"20000 VND\",15\r\nOK";
            }
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);
        using var firstCts = new CancellationTokenSource();

        Task<string> first = ussd.SendAsync("COM17", "*101#", firstCts.Token);
        Task<string> second = ussd.SendAsync("COM18", "*101#");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstCts.Cancel();

        string cancelled = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("cancelled", cancelled, StringComparison.OrdinalIgnoreCase);

        releaseSecond.TrySetResult();
        Assert.Contains("20000 VND", await second.WaitAsync(TimeSpan.FromSeconds(2)));
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
    public async Task Call_DifferentComPorts_DoNotBlockEachOther()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM12", CcidA);
        sessions.Begin("COM13", CcidB);
        int entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            CallHandler = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
                await release.Task;
                return true;
            }
        };
        using var calls = new GsmCallService(modem, sessions);

        Task<bool> first = calls.CallAsync("COM12", "0911111111", null, 30, false);
        Task<bool> second = calls.CallAsync("COM13", "0922222222", null, 30, false);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();

        Assert.All(await Task.WhenAll(first, second), Assert.True);
    }

    [Fact]
    public async Task Call_SameCom_IsSerialized()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM12", CcidA);
        int entered = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            CallHandler = async (_, _, _) =>
            {
                int call = Interlocked.Increment(ref entered);
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondEntered.TrySetResult();
                }
                return true;
            }
        };
        using var calls = new GsmCallService(modem, sessions);

        Task<bool> first = calls.CallAsync("COM12", "0911111111", null, 30, false);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> second = calls.CallAsync("COM12", "0922222222", null, 30, false);

        Task early = await Task.WhenAny(secondEntered.Task, Task.Delay(150));
        Assert.NotSame(secondEntered.Task, early);
        releaseFirst.TrySetResult();

        Assert.True(await first);
        Assert.True(await second);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Call_MoreThanSixtyFourComPorts_CanRunConcurrently()
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
            CallHandler = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref entered) == portCount) allEntered.TrySetResult();
                await release.Task;
                return true;
            }
        };
        using var calls = new GsmCallService(modem, sessions);

        Task<bool>[] operations = Enumerable.Range(1, portCount)
            .Select(i => calls.CallAsync($"COM{i}", "0912345678", null, 30, false))
            .ToArray();

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(portCount, Volatile.Read(ref entered));
        release.TrySetResult();
        Assert.All(await Task.WhenAll(operations), Assert.True);
    }

    [Fact]
    public async Task Sms_PromptTimeout_AbortsAndRetriesBeforeAnyPayloadCouldBeDuplicated()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM118", CcidA);
        int attempts = 0;
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) => Task.FromResult(
                Interlocked.Increment(ref attempts) == 1
                    ? "ERROR: Timeout waiting for > prompt"
                    : "+CMGS: 1\r\nOK")
        };
        var delay = new ImmediateGsmOperationDelay();
        using var sms = new GsmSmsService(modem, sessions, delay);

        string result = await sms.SendAsync("COM118", "0912345678", "test");

        Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, attempts);
        Assert.Equal(2, modem.SmsRequests.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delay.Delays));
    }

    [Fact]
    public async Task Sms_PayloadTimeout_IsNotRetriedBecauseNetworkMayHaveAcceptedMessage()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM119", CcidA);
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) => Task.FromResult("ERROR: Timeout sending SMS payload")
        };
        var delay = new ImmediateGsmOperationDelay();
        using var sms = new GsmSmsService(modem, sessions, delay);

        string result = await sms.SendAsync("COM119", "0912345678", "test");

        Assert.Contains("Timeout sending SMS payload", result);
        Assert.Single(modem.SmsRequests);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task Sms_ChannelRecoveryFailure_KeepsEstablishedPortOnlineWithoutResendingPayload()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM120", CcidA);
        const string uncertainResult =
            "ERROR: Timeout sending SMS payload; SMS channel recovery failed";
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) => Task.FromResult(uncertainResult)
        };
        var delay = new ImmediateGsmOperationDelay();
        using var sms = new GsmSmsService(modem, sessions, delay);

        string result = await sms.SendAsync("COM120", "0912345678", "test");

        Assert.Equal(uncertainResult, result);
        Assert.Single(modem.SmsRequests);
        Assert.Empty(delay.Delays);
        Assert.Equal(["COM120"], modem.BackgroundSuspensions.ToArray());
        Assert.Equal(["COM120"], modem.BackgroundResumptions.ToArray());
        Assert.False(sms.IsInProgress("COM120"));
    }

    [Fact]
    public async Task Sms_Cms350_IsNotRetriedOrReconnected()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM122", CcidA);
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) => Task.FromResult("+CMS ERROR: 350")
        };
        var delay = new ImmediateGsmOperationDelay();
        using var sms = new GsmSmsService(modem, sessions, delay);

        string result = await sms.SendAsync("COM122", "888", "DK EZ");

        Assert.Contains("+CMS ERROR: 350", result, StringComparison.OrdinalIgnoreCase);
        Assert.Single(modem.SmsRequests);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task Sms_SessionChangesBeforeRecovery_DoesNotReconnectStaleSession()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM123", CcidA);
        const string uncertainResult =
            "ERROR: Timeout sending SMS payload; SMS channel recovery failed";
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) =>
            {
                sessions.Begin("COM123", CcidB);
                return Task.FromResult(uncertainResult);
            }
        };
        using var sms = new GsmSmsService(
            modem,
            sessions,
            new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync("COM123", "0912345678", "test");

        Assert.Contains("SIM session changed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Single(modem.SmsRequests);
    }

    [Theory]
    [InlineData(30_000, 90_000)]
    [InlineData(90_000, 90_000)]
    [InlineData(120_000, 120_000)]
    public void Sms_PayloadTimeout_WaitsAtLeastNinetySeconds(int requested, int expected) =>
        Assert.Equal(expected, GsmModemService.GetSmsPayloadTimeoutMs(requested));

    [Theory]
    [InlineData("OK")]
    [InlineData("AT\r\r\nOK\r\n")]
    public void Sms_RecoveryProbe_AcceptsOnlyCleanAtResponses(string response) =>
        Assert.True(GsmModemService.IsCleanSmsRecoveryProbe(response));

    [Theory]
    [InlineData("+CMGS: 27\r\nOK\r\n")]
    [InlineData("ERROR")]
    [InlineData("+CMS ERROR: 500")]
    [InlineData("ERROR\r\nOK\r\n")]
    [InlineData("ERROR: Timeout configuring SMS with AT")]
    public void Sms_RecoveryProbe_RejectsLateOrFailedResponses(string response) =>
        Assert.False(GsmModemService.IsCleanSmsRecoveryProbe(response));

    [Fact]
    public void Sms_HeaderTimestamp_UsesCarrierTimezoneAndNotReadTime()
    {
        const string raw =
            "+CMGR: \"REC READ\",\"888\",\"\",\"26/07/27,20:47:56+28\"\r\n"
            + "Dung luong Data con lai\r\nOK\r\n";

        Assert.True(GsmModemService.TryParseSmsTimestamp(
            raw,
            out DateTimeOffset timestampUtc));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 13, 47, 56, TimeSpan.Zero),
            timestampUtc);
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
    public async Task Sms_StopWhileModemIsWaiting_CancelsTheActiveSend()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM16", CcidA);
        var enteredSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modem = new FakeGsmModemService
        {
            SmsHandler = async (_, _, _) =>
            {
                enteredSend.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return "+CMGS: 1\r\nOK";
            }
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());
        using var cts = new CancellationTokenSource();

        Task<string> operation = sms.SendAsync("COM16", "900", "test", cts.Token);
        await enteredSend.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        string result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("cancelled", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(sms.IsInProgress("COM16"));
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
        using var ussd = new GsmUssdService(modem);

        Task<string>[] operations = Enumerable.Range(1, portCount)
            .Select(i => ussd.SendAsync($"COM{i}", "*101#"))
            .ToArray();

        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(portCount, Volatile.Read(ref entered));
        release.TrySetResult();

        Assert.All(await Task.WhenAll(operations), result => Assert.Contains("10000 VND", result));
    }

    [Fact]
    public async Task Sms_DoesNotRunCharsetRestoreOutsideModemWorkflow()
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
    public async Task Sms_Ec20c_DoesNotRunPduRestoreOutsideModemWorkflow()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM34", CcidA);
        var modem = new FakeGsmModemService
        {
            ModemProfile = QuectelModemProfile.FromIdentity("Quectel", "EC20C", "EC20CEHCLGR06A01M1G")
        };
        using var sms = new GsmSmsService(modem, sessions, new ImmediateGsmOperationDelay());

        await sms.SendAsync("COM34", "0912345678", "hello");

        Assert.Empty(modem.Commands);
    }

    [Fact]
    public async Task Sms_ExpectedCcid_IsReadFreshBeforeEverySafeRetry()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM40", CcidA);
        int sends = 0;
        var modem = new FakeGsmModemService
        {
            SmsHandler = (_, _, _) => Task.FromResult(
                Interlocked.Increment(ref sends) == 1
                    ? "ERROR: Timeout waiting for > prompt"
                    : "+CMGS: 7\r\nOK")
        };
        using var sms = new GsmSmsService(
            modem, sessions, new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync(
            "COM40", "888", "data", expectedCcid: CcidA);

        Assert.Contains("thành công", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, modem.CcidVerifications.Count);
        Assert.All(modem.CcidVerifications,
            proof => Assert.Equal(CcidA, proof.ExpectedCcid));
    }

    [Fact]
    public async Task Sms_PhysicalCcidMismatch_FailsBeforePayload()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM41", CcidA);
        var modem = new FakeGsmModemService
        {
            CcidVerificationHandler = (_, _, _) => Task.FromResult(false)
        };
        using var sms = new GsmSmsService(
            modem, sessions, new ImmediateGsmOperationDelay());

        string result = await sms.SendAsync(
            "COM41", "888", "data", expectedCcid: CcidA);

        Assert.Contains("does not match", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(modem.SmsRequests);
    }

    [Fact]
    public async Task Ussd_DoesNotInjectPhysicalCcidProbeBeforeCusd()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM42", CcidA);
        var modem = new FakeGsmModemService
        {
            CcidVerificationHandler = (_, _, _) => Task.FromResult(false)
        };
        using var sms = new GsmSmsService(
            modem, sessions, new ImmediateGsmOperationDelay());
        using var ussd = new GsmUssdService(modem);

        string result = await ussd.SendAsync("COM42", "*101#");

        Assert.Contains("10000 VND", result);
        Assert.Empty(modem.CcidVerifications);
        Assert.Contains("COM42:AT+CUSD=1,\"*101#\",15", modem.Commands);
    }

    [Fact]
    public async Task Call_PhysicalCcidMismatch_FailsBeforeDial()
    {
        using var sessions = new PortSessionRegistry();
        sessions.Begin("COM43", CcidA);
        bool callEntered = false;
        var modem = new FakeGsmModemService
        {
            CcidVerificationHandler = (_, _, _) => Task.FromResult(false),
            CallHandler = (_, _, _) =>
            {
                callEntered = true;
                return Task.FromResult(true);
            }
        };
        using var calls = new GsmCallService(modem, sessions);

        bool result = await calls.CallAsync(
            "COM43", "900", null, 15, false, expectedCcid: CcidA);

        Assert.False(result);
        Assert.False(callEntered);
    }
}
