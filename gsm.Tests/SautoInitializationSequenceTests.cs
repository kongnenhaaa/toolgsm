using gsm.Services;

namespace gsm.Tests;

public sealed class SautoInitializationSequenceTests
{
    [Fact]
    public void NofakeInitialization_RoutesUrcToUartThenReadsSimState()
    {
        string[] expected =
        [
            "AT+QURCCFG=\"urcport\",\"uart1\"",
            "AT+CPIN?"
        ];

        Assert.Equal(expected, GsmModemService.NofakeInitializationCommandOrder);
        Assert.DoesNotContain(expected, command =>
            command.Contains("CFUN=", StringComparison.OrdinalIgnoreCase)
            || command.Contains("EGMR", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CMGD", StringComparison.OrdinalIgnoreCase)
            || command.Contains("usbat", StringComparison.OrdinalIgnoreCase)
            || command.Contains("QPRTPARA", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("+CFUN: 0\r\nOK", true, 0)]
    [InlineData("+CFUN: 1\r\nOK", true, 1)]
    [InlineData("+CFUN: 4\r\nOK", true, 4)]
    [InlineData("+CFUN: 4\r\nERROR", false, -1)]
    [InlineData("+CME ERROR: 100", false, -1)]
    [InlineData("OK", false, -1)]
    public void HotplugRf_ParsesOnlySuccessfulCfunQuery(
        string response,
        bool expectedParsed,
        int expectedMode)
    {
        bool parsed = GsmModemService.TryGetHotplugCfunMode(
            response,
            out int mode);

        Assert.Equal(expectedParsed, parsed);
        Assert.Equal(expectedMode, mode);
    }

    [Theory]
    [InlineData("+CFUN: 0\r\nOK", true)]
    [InlineData("+CFUN: 4\r\nOK", true)]
    [InlineData("+CFUN: 1\r\nOK", false)]
    [InlineData("+CFUN: 5\r\nOK", false)]
    [InlineData("+CFUN: 4\r\nERROR", false)]
    [InlineData("ERROR", false)]
    public void HotplugRf_EnablesOnlyExplicitZeroOrFour(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldEnableHotplugRf(response));

    [Theory]
    [InlineData("AT+CFUN=1", true)]
    [InlineData(" at + cfun = 1 ", true)]
    [InlineData("AT+CFUN=0", false)]
    [InlineData("AT+CFUN=4", false)]
    [InlineData("AT+CFUN=1,1", false)]
    [InlineData("AT+CFUN=1\r\nAT+QPRTPARA=3", false)]
    public void HotplugRf_PrivilegedBypassAllowsOnlyNonRebootingEnable(
        string command,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsAuthorizedHotplugRfRecoveryCommand(command));

    [Theory]
    [InlineData("867400022047199\r\nOK", "867400022047199")]
    [InlineData("AT+CGSN\r\n867400022047199\r\nOK", "867400022047199")]
    [InlineData("867400022047199\r\nERROR", "")]
    [InlineData("OK", "")]
    public void HotplugImei_ReadsOnlySuccessfulCgsnOrGsnPayload(
        string response,
        string expected) =>
        Assert.Equal(
            expected,
            GsmModemService.GetHotplugReadOnlyImei(response));

    [Theory]
    [InlineData("+CME ERROR: 13", true)]
    [InlineData("\r\n+CME ERROR: 13\r\n", true)]
    [InlineData("+CME ERROR: 10", false)]
    [InlineData("+CPIN: READY\r\nOK", false)]
    public void HotplugSimFailure_MatchesOnlyCme13(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsHotplugSimFailureResponse(response));

    [Theory]
    [InlineData("+CME ERROR: 10", true)]
    [InlineData("\r\n+CME ERROR: 10\r\n", true)]
    [InlineData("+CME ERROR: 13", false)]
    [InlineData("+CPIN: NOT INSERTED", false)]
    [InlineData("+CPIN: READY\r\nOK", false)]
    public void HotplugCpinUnavailable_MatchesOnlyCme10(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsHotplugCpinUnavailableResponse(response));

    [Theory]
    [InlineData("+CME ERROR: 10", "+CME ERROR: 13", true)]
    [InlineData("+CME ERROR: 10", "+CME ERROR: 10", false)]
    [InlineData("+CME ERROR: 13", "+CME ERROR: 13", false)]
    [InlineData("+CME ERROR: 10", "+ICCID: 89840200011815310980\r\nOK", false)]
    [InlineData("+CPIN: READY\r\nOK", "+CME ERROR: 13", false)]
    public void HotplugContactRecovery_RequiresCpin10ThenIccid13(
        string cpinResponse,
        string iccidResponse,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsHotplugCpin10Iccid13Signature(
                cpinResponse,
                iccidResponse));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    public void HotplugSimFailure_RequiresThreeConsecutiveResponses(
        int count,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldRecoverHotplugSimFailure(count));

    [Theory]
    [InlineData("AT+CFUN=1,1", true)]
    [InlineData(" at + cfun = 1 , 1 ", true)]
    [InlineData("AT+CFUN=1", false)]
    [InlineData("AT+CFUN=0", false)]
    [InlineData("AT+CFUN=1,1\r\nAT+QPRTPARA=3", false)]
    public void HotplugSimFailure_PrivilegedBypassAllowsOnlyOneRebootCommand(
        string command,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsAuthorizedHotplugSimFailureRecoveryCommand(command));

    [Fact]
    public void HotplugRecovery_ClaimsAreScopedAndConcurrentSafe()
    {
        var service = new GsmModemService();
        const string ccid = "89840200011815310980";
        int rfClaims = 0;
        int rebootClaims = 0;

        Parallel.For(0, 32, _ =>
        {
            if (service.TryClaimHotplugRfRecovery("COM75", ccid))
                Interlocked.Increment(ref rfClaims);
            if (service.TryClaimHotplugSimFailureReboot("COM75"))
                Interlocked.Increment(ref rebootClaims);
        });

        Assert.Equal(1, rfClaims);
        Assert.Equal(1, rebootClaims);
        Assert.True(service.TryClaimHotplugRfRecovery(
            "COM75",
            "89840200011815310981"));
        Assert.True(service.TryClaimHotplugSimFailureReboot("COM76"));
        Assert.False(service.TryClaimHotplugRfRecovery("COM75", "invalid"));
    }

    [Fact]
    public void InitialUssdCommandOrder_ContainsOnlyCapturedCommands()
    {
        Assert.Equal(
            ["AT+CSCS=\"GSM\"", "AT+CUSD=2", "AT+CUSD=1,\"*101#\",15"],
            GsmModemService.SautoInitial101CommandOrder);
    }

    [Theory]
    [InlineData("+QCFG: \"ims/ut\",1,1,0\r\n\r\nOK", true)]
    [InlineData("+QCFG: \"ims/ut\", 1, 1, 0\nOK", true)]
    [InlineData("+QCFG: \"ims/ut\",1,0,0\r\nOK", false)]
    [InlineData("+QCFG: \"ims/ut\",0,0,0\r\nOK", false)]
    [InlineData("+QCFG: \"ims/ut\",1,1,1\r\nOK", false)]
    [InlineData("+QCFG: \"ims/ut\",1,1,0\r\nERROR", false)]
    [InlineData("ERROR", false)]
    public void ImsUtRecovery_RequiresExactUnavailableLteUssdState(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.RequiresImsUtCsFallback(response));

    [Theory]
    [InlineData("+QCFG: \"ims/ut\",0,0,0\r\n\r\nOK", true)]
    [InlineData("+QCFG: \"ims/ut\",1,1,0\r\n\r\nOK", false)]
    [InlineData("+QCFG: \"ims/ut\",0,0,0\r\nERROR", false)]
    [InlineData("ERROR", false)]
    public void ImsUtRecovery_RecognizesPersistedDisabledState(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsImsUtDisabledResponse(response));

    [Theory]
    [InlineData("+CREG: 2,1,\"1817\",\"9516535\",7\r\nOK", true)]
    [InlineData("+CREG: 0,5\r\nOK", true)]
    [InlineData("+CREG: 2,0\r\nOK", false)]
    [InlineData("+CREG: 2,1\r\nERROR", false)]
    [InlineData("+CEREG: 2,1\r\nOK", false)]
    public void ImsUtRecovery_WaitsForCircuitRegistration(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsCircuitSwitchedRegisteredResponse(response));

    [Theory]
    [InlineData("AT+QCFG=\"ims/ut\",0", true)]
    [InlineData("AT+CFUN=1,1", true)]
    [InlineData("AT+CFUN=1", false)]
    [InlineData("AT+QPRTPARA=3", false)]
    [InlineData("AT+QURCCFG=\"urcport\",\"uart1\"", false)]
    [InlineData("AT+QCFG=\"ims/ut\",0\r\nAT+CFUN=1,1", false)]
    public void ImsUtRecovery_PrivilegedBypassAllowsOnlyExactCommands(
        string command,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsAuthorizedImsUtRecoveryCommand(command));

    [Theory]
    [InlineData("AT+QCFG=\"ims/ut\",0", true)]
    [InlineData(" AT + QCFG = \"nwscanmode\" , 0 , 0 ", true)]
    [InlineData("at+cfun=1,1", true)]
    [InlineData("AT+QCFG=\"ims\",2", false)]
    [InlineData("AT+QCFG=\"nwscanmode\",3", false)]
    [InlineData("AT+CFUN=0", false)]
    [InlineData("AT+COPS=2", false)]
    [InlineData("AT+QPRTPARA=3", false)]
    [InlineData("AT+CFUN=1,1\r\nAT+QPRTPARA=3", false)]
    public void UssdFix_PrivilegedBypassAllowsOnlyExactRfCommands(
        string command,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsAuthorizedUssdFixCommand(command));

    [Theory]
    [InlineData("+QCFG: \"ims\",1,0\r\nOK", "ims", true, 1)]
    [InlineData("+QCFG: \"ims/ut\",0,0,0\r\nOK", "ims/ut", true, 0)]
    [InlineData("+QCFG: \"nwscanmode\",3\r\nOK", "nwscanmode", true, 3)]
    [InlineData("ERROR", "ims/ut", false, 0)]
    public void UssdFix_ParsesFirstQcfgValue(
        string response,
        string key,
        bool expectedParsed,
        int expectedValue)
    {
        bool parsed = GsmModemService.TryGetUssdFixQcfgFirstValue(
            response,
            key,
            out int value);

        Assert.Equal(expectedParsed, parsed);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("ERROR", true)]
    [InlineData("+CME ERROR: unknown", true)]
    [InlineData("+CME ERROR: operation not supported", true)]
    [InlineData("", false)]
    [InlineData("+CME ERROR: 100", false)]
    [InlineData("+QCFG: \"ims/ut\",0,0,0\r\nOK", false)]
    public void UssdFix_DetectsOnlyExplicitUnsupportedImsUt(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsUssdFixQcfgExplicitlyUnsupported(
                response,
                "ims/ut"));

    [Theory]
    [InlineData("+QSIMDET: 1,0\r\nOK", true, 1, 0)]
    [InlineData("+QSIMDET: 1,1\r\nOK", true, 1, 1)]
    [InlineData("+QSIMDET: 0,0\r\nOK", true, 0, 0)]
    [InlineData("+QSIMDET: 2,9\r\nOK", false, 0, 0)]
    [InlineData("ERROR", false, 0, 0)]
    public void UssdFix_PreservesOnlyParsedQsimdetConfiguration(
        string response,
        bool expectedParsed,
        int expectedEnabled,
        int expectedPolarity)
    {
        bool parsed = GsmModemService.TryGetUssdFixQsimdetConfig(
            response,
            out int enabled,
            out int polarity);

        Assert.Equal(expectedParsed, parsed);
        Assert.Equal(expectedEnabled, enabled);
        Assert.Equal(expectedPolarity, polarity);
    }

    [Fact]
    public void UssdFix_ParsesPostRebootReadinessAndIccid()
    {
        Assert.True(GsmModemService.IsUssdFixUart1Active(
            "+QURCCFG: \"urcport\",\"uart1\"\r\nOK"));
        Assert.False(GsmModemService.IsUssdFixUart1Active(
            "+QURCCFG: \"urcport\",\"usbat\"\r\nOK"));
        Assert.True(GsmModemService.IsUssdFixCfunOne(
            "+CFUN: 1\r\nOK"));
        Assert.False(GsmModemService.IsUssdFixCfunOne(
            "+CFUN: 4\r\nOK"));
        Assert.True(GsmModemService.IsUssdFixCpinReady(
            "+CPIN: READY\r\nOK"));
        Assert.True(GsmModemService.IsUssdFixRegisteredResponse(
            "+CREG: 2,1\r\nOK",
            "C"));
        Assert.True(GsmModemService.IsUssdFixRegisteredResponse(
            "+CGREG: 0,5\r\nOK",
            "CG"));
        Assert.False(GsmModemService.IsUssdFixRegisteredResponse(
            "+CEREG: 2,0\r\nOK",
            "CE"));
        Assert.Equal(
            "89840200011815310980",
            GsmModemService.GetUssdFixIccid(
                "+QCCID: 89840200011815310980\r\nOK"));
        Assert.Empty(GsmModemService.GetUssdFixIccid("+CME ERROR: 10"));
    }

    [Fact]
    public void UssdFix_UsesSameBoundedWaitsAsPythonTool()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), GsmModemService.UssdFixInitialAtDeadline);
        Assert.Equal(TimeSpan.FromSeconds(8), GsmModemService.UssdFixBootDelay);
        Assert.Equal(TimeSpan.FromSeconds(90), GsmModemService.UssdFixAtReadyDeadline);
        Assert.Equal(TimeSpan.FromSeconds(60), GsmModemService.UssdFixCfunReadyDeadline);
        Assert.Equal(TimeSpan.FromSeconds(30), GsmModemService.UssdFixNetworkDeadline);
        Assert.Equal(TimeSpan.FromSeconds(35), GsmModemService.SautoAutomaticUssdResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), GsmModemService.SautoAutomaticUssdCsReadyDeadline);
    }

    [Fact]
    public void ImsUtRecovery_IsClaimedOnlyOncePerPortAndSim()
    {
        var service = new GsmModemService();
        const string firstCcid = "89840200011815310980";
        const string secondCcid = "89840200011815310981";

        Assert.True(service.TryClaimImsUtRecovery("COM105", firstCcid));
        Assert.False(service.TryClaimImsUtRecovery("com105", firstCcid));
        Assert.True(service.TryClaimImsUtRecovery("COM105", secondCcid));
        Assert.True(service.TryClaimImsUtRecovery("COM94", firstCcid));
        Assert.False(service.TryClaimImsUtRecovery("COM105", "invalid"));
    }

    [Fact]
    public void NetworkDataPortCycle_MatchesDecompiledSautoCommandsAndGuards()
    {
        Assert.Equal(
        [
            "AT+CPIN? \r",
            "AT+CSQ \r",
            "AT+COPS?"
        ], GsmModemService.SautoNetworkPollingCommandOrder);
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            GsmModemService.SautoDataPortStepDelay);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            GsmModemService.SautoNetworkRecheckInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(400),
            GsmModemService.SautoDataPortLoopDelay);
        Assert.DoesNotContain(
            GsmModemService.SautoNetworkPollingCommandOrder,
            command => command.Contains(
                "CREG",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ussd_AllowsLateNetworkPayloadBeforeRetry()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            GsmModemService.SautoManualUssdResponseTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(35),
            GsmModemService.SautoAutomaticUssdResponseTimeout);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            GsmModemService.SautoAutomaticUssdRetryInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            GsmModemService.SautoUssdCleanupResponseTimeout);
    }

    [Fact]
    public void AutomaticUssd_UsesBoundedParallelWaves()
    {
        Assert.Equal(8, GsmModemService.SautoAutomaticUssdMaxConcurrency);
    }

    [Theory]
    [InlineData(true, "AT+CUSD=1,\"*101#\",15")]
    [InlineData(false, "AT+CUSD=1,\"*101#\"")]
    public void UssdRetry_ChangesOnlyDcsForm(
        bool includeDcs,
        string expected)
    {
        string command =
            GsmModemService.BuildSautoUssdRequestCommand(
                "*101#",
                includeDcs);

        Assert.Equal(expected, command);
        Assert.DoesNotContain(
            "CFUN",
            command,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutomaticUssd_IgnoresPollingRefreshButStopsWithPortLifetime()
    {
        using var polling = new CancellationTokenSource();
        using var portLifetime = new CancellationTokenSource();
        CancellationToken selected =
            GsmModemService.SelectSautoAutomaticUssdCancellationToken(
                polling.Token,
                portLifetime.Token);

        polling.Cancel();
        Assert.False(selected.IsCancellationRequested);

        portLifetime.Cancel();
        Assert.True(selected.IsCancellationRequested);
    }

    [Fact]
    public void AutomaticUssd_FallsBackToPollingTokenWithoutPortLifetime()
    {
        using var polling = new CancellationTokenSource();
        CancellationToken selected =
            GsmModemService.SelectSautoAutomaticUssdCancellationToken(
                polling.Token,
                CancellationToken.None);

        polling.Cancel();
        Assert.True(selected.IsCancellationRequested);
    }

    [Theory]
    [InlineData("+CME ERROR: 100", true)]
    [InlineData("+CMS ERROR: 500", true)]
    [InlineData("ERROR", true)]
    [InlineData("OK", false)]
    [InlineData("+CUSD: 0,\"TK=1000\",15", false)]
    public void UssdRetry_DetectsTerminalErrorWithoutRfRecovery(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoTerminalErrorResponse(response));

    [Theory]
    [InlineData("+CME ERROR: 100", true)]
    [InlineData("+CMS ERROR: 500", true)]
    [InlineData("ERROR", true)]
    [InlineData("ERROR: Timeout waiting for automatic +CUSD", true)]
    [InlineData("ERROR: USSD did not return +CUSD", true)]
    [InlineData("+CUSD: 0,\"TK chinh=100 VND\",15", false)]
    [InlineData("+CUSD: 1,\"TB :0849882209\",15", false)]
    [InlineData("+CUSD: 0,\"TK chinh=100 VND\",15\r\nERROR", false)]
    [InlineData("OK", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AutomaticUssdFix_TriggersOnlyForFailedUssdWithoutCusd(
        string? response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldRunAutomaticUssdFix(response));

    [Fact]
    public void AutomaticUssdFix_IsClaimedOnlyOncePerPortAndSimSession()
    {
        var service = new GsmModemService();
        const string firstCcid = "89840200011815310980";
        const string secondCcid = "89840200011815310981";

        Assert.True(service.TryClaimAutomaticUssdFix("COM105", firstCcid));
        Assert.False(service.TryClaimAutomaticUssdFix("com105", firstCcid));
        Assert.True(service.TryClaimAutomaticUssdFix("COM105", secondCcid));
        Assert.True(service.TryClaimAutomaticUssdFix("COM94", firstCcid));
        Assert.False(service.TryClaimAutomaticUssdFix("", firstCcid));
        Assert.False(service.TryClaimAutomaticUssdFix("COM105", "invalid"));

        // A new service instance is a new ToolGSM session.
        var nextSession = new GsmModemService();
        Assert.True(nextSession.TryClaimAutomaticUssdFix("COM105", firstCcid));
    }

    [Fact]
    public void AutomaticUssdFix_ConcurrentClaimsAllowExactlyOneReboot()
    {
        var service = new GsmModemService();
        const string ccid = "89840200011815310980";
        int accepted = 0;

        Parallel.For(0, 32, _ =>
        {
            if (service.TryClaimAutomaticUssdFix("COM105", ccid))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void AutomaticUssdFix_SharesOneRebootBudgetWithImsUtRecovery()
    {
        const string ccid = "89840200011815310980";

        var imsFirst = new GsmModemService();
        Assert.True(imsFirst.TryClaimImsUtRecovery("COM105", ccid));
        Assert.False(imsFirst.TryClaimAutomaticUssdFix("COM105", ccid));

        var fullFixFirst = new GsmModemService();
        Assert.True(fullFixFirst.TryClaimAutomaticUssdFix("COM105", ccid));
        Assert.False(fullFixFirst.TryClaimImsUtRecovery("COM105", ccid));
    }

    [Fact]
    public void NetworkDataPortCycle_RechecksUnknownCarrierOnlyAfterTwoSeconds()
    {
        DateTimeOffset lastCheck = new(
            2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.False(GsmModemService.ShouldQuerySautoNetwork(
            string.Empty,
            lastCheck.AddSeconds(2),
            lastCheck));
        Assert.True(GsmModemService.ShouldQuerySautoNetwork(
            "No Signal",
            lastCheck.AddMilliseconds(2001),
            lastCheck));
        Assert.False(GsmModemService.ShouldQuerySautoNetwork(
            "VINAPHONE",
            lastCheck.AddMinutes(10),
            lastCheck));
    }

    [Fact]
    public void SmsReceiveRestore_ReturnsToSautoGsmCharset()
    {
        Assert.Equal(
        [
            "AT+CMGF=1",
            "AT+CSCS=\"GSM\"",
            "AT+CPMS=\"SM\",\"SM\",\"SM\"",
            "AT+CNMI=1,1,0,0,0"
        ], GsmModemService.SmsReceiveRestoreCommandOrder);
        Assert.DoesNotContain(
            GsmModemService.SmsReceiveRestoreCommandOrder,
            command => command.Contains(
                "UCS2",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("+QIND: SMS DONE", true)]
    [InlineData("SMS Ready", true)]
    [InlineData("SMS DONE", true)]
    [InlineData("+QIND: PB DONE", false)]
    [InlineData("Call Ready", false)]
    public void SmsStorageReadyUrc_TriggersStoredMessageDrain(
        string line,
        bool expected) =>
        Assert.Equal(expected, GsmModemService.IsSmsStorageReadyUrc(line));

    [Fact]
    public void SmsWatchdog_UsesSafeFiveMinuteStorageCleanupSweep()
    {
        Assert.Equal(
            "AT+CMGL=\"ALL\"",
            GsmModemService.SmsReceiveWatchdogCommand);
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            GsmModemService.SmsReceiveWatchdogInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            GsmModemService.SmsReceiveWatchdogTurnGap);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            GsmModemService.SmsStorageCleanupInterval);
        Assert.DoesNotContain(
            "CMGD",
            GsmModemService.SmsReceiveWatchdogCommand,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            GsmModemService.SmsReceiveRestoreCommandOrder,
            command => command.Contains(
                "CMGD",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SmsStorageCleanup_WaitsOneFullFiveMinuteInterval()
    {
        const long start = 1_000;
        long interval = (long)GsmModemService
            .SmsStorageCleanupInterval.TotalMilliseconds;

        Assert.False(GsmModemService.IsSmsStorageCleanupDue(
            start + interval - 1,
            start));
        Assert.True(GsmModemService.IsSmsStorageCleanupDue(
            start + interval,
            start));
    }

    [Fact]
    public void SmsWatchdog_RoundRobinOrdersPortsNumerically()
    {
        string[] ports = ["COM109", "COM84", "COM9", "COM100"];

        Assert.Equal(
            ["COM9", "COM84", "COM100", "COM109"],
            ports.OrderBy(GsmModemService.GetSmsReceiveWatchdogPortOrder));
    }

    [Theory]
    [InlineData("\r\n+CME ERROR: 10\r\n", false)]
    [InlineData("\r\n+CME ERROR: 13\r\n", false)]
    [InlineData("\r\n+CPIN: NOT INSERTED\r\n", true)]
    [InlineData("\r\n+CPIN: SIM PIN\r\n\r\nOK\r\n", false)]
    [InlineData("\r\nERROR\r\n", false)]
    public void SimAbsent_UsesOnlyExplicitGsmEvidence(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoSimAbsentResponse(response));

    [Theory]
    [InlineData("+CUSD: 1,\"TB :0812345678\",15", true)]
    [InlineData("+CUSD: 0,\"TK chinh=100 VND\",15", true)]
    [InlineData("+CUSD: 2", false)]
    [InlineData("OK", false)]
    [InlineData("+CME ERROR: 100", false)]
    [InlineData("+CUSD: 1,\"TB :0812345678\",15\r\nERROR", false)]
    public void Ussd_AdvancesOnlyAfterSuccessfulCusdPayload(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoSuccessfulUssdResponse(response));

    [Theory]
    [InlineData("*101#", "+CUSD: 0,\"TK chinh=100 VND\",15", true)]
    [InlineData("*101#", "+CUSD: 1,\"TB :0812345678\",15", false)]
    [InlineData("*111#", "+CUSD: 1,\"TB :0812345678\",15", true)]
    [InlineData("*111#", "+CUSD: 0,\"TK chinh=100 VND\",15", false)]
    [InlineData("*101#", "OK", false)]
    public void ManualUssd_AcceptsOnlyTheResponseForItsRequestedStage(
        string stage,
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.HasSautoManualUssdPayloadForStage(response, stage));

    [Theory]
    [InlineData("+CUSD: 1,\"TB :0849882209,Ngay KH:25/12/2024\",15", true)]
    [InlineData("+CUSD: 0,\"So TB 0812345678. TK chinh=100 VND\",15", true)]
    [InlineData("+CUSD: 0,\"TK chinh=100 VND\",15", false)]
    [InlineData("ccid=89840200011768850016", false)]
    public void AutomaticUssd_CompletesOnlyWhenPhoneNumberWasParsed(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ContainsSautoPhoneNumber(response));

    [Theory]
    [InlineData("+CUSD: 0,\"TK chinh=100 VND\",15", true)]
    [InlineData("+CUSD: 1,\"TB :0849882209,Ngay KH:25/12/2024\",15", false)]
    [InlineData("+CUSD: 1,\"Bam 1 de tra cuu\",15", false)]
    [InlineData("OK", false)]
    public void SmsMaintenance_OpensOnlyAfterCompletedAutomatic101Rx(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoAutomatic101Completion(response));

    [Theory]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", true, true)]
    [InlineData(7, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", true, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", false, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768859999", "89840200011768850016", true, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768859999", true, false)]
    public void SmsMaintenance_GateRejectsStale101AndIdentityChanges(
        long expectedGeneration,
        long currentGeneration,
        string expectedCcid,
        string smsCcid,
        string networkCcid,
        bool automatic111Completed,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.CanOpenSmsReceiveMaintenanceGate(
                expectedGeneration,
                currentGeneration,
                expectedCcid,
                smsCcid,
                networkCcid,
                automatic111Completed));

    [Theory]
    [InlineData("+COPS: 0,0,\"VINAPHONE\",2\r\nOK", "VINAPHONE", "2")]
    [InlineData("+COPS: 0,2,45202,7\r\nOK", "45202", "7")]
    [InlineData("AT+COPS?\r\n+COPS: 0,1,\"VINA\"\r\nOK", "VINA", "")]
    public void CopsParser_AcceptsCapturedOperatorFormats(
        string response,
        string expectedOperator,
        string expectedAct)
    {
        Assert.True(GsmModemService.TryParseCopsResponse(
            response, out string operatorName, out string act));
        Assert.Equal(expectedOperator, operatorName);
        Assert.Equal(expectedAct, act);
    }
}
