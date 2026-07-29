using gsm.Services;

namespace gsm.Tests;

public sealed class SautoInitializationSequenceTests
{
    [Fact]
    public void InitializationCommandOrder_MatchesCapturedSautoSequence()
    {
        string[] expected =
        [
            "\u001b",
            "ATI",
            "AT+CPMS=\"ME\",\"SM\",\"MT\"",
            "AT+CFUN=4",
            "AT+CNMI=1,1,0,0,0",
            "AT+CFUN?",
            "AT+EGMR=0,7;",
            "AT+CNMI?",
            "AT+CSCS=\"GSM\"",
            "AT+QURCCFG=\"urcport\",\"uart1\"",
            "AT+CMGF=1",
            "AT+CPMS=\"SM\",\"SM\",\"SM\"",
            "AT+CMGD=1,4",
            "AT+CPMS=\"ME\",\"ME\",\"ME\"",
            "AT+CMGD=1,4",
            "AT+CPMS=\"SM\",\"SM\",\"SM\"",
            "AT+CPMS?",
            "AT+CNMI=1,1,0,0,0",
            "AT+QCFG=\"nwscanmode\",0,1",
            "AT+QURCCFG=\"urcport\",\"uart1\"",
            "AT+CPIN?"
        ];

        Assert.Equal(expected, GsmModemService.SautoInitializationCommandOrder);
        Assert.Equal(2, expected.Count(command =>
            command.Equals("AT+CMGD=1,4", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("AT+CUSD=1", expected);
        Assert.DoesNotContain(expected, command =>
            command.Contains("QSIMSTAT", StringComparison.OrdinalIgnoreCase)
            || command == "AT+CFUN=0");
    }

    [Fact]
    public void InitialUssdCommandOrder_ContainsOnlyCapturedCommands()
    {
        Assert.Equal(
            ["AT+CUSD=2", "AT+CUSD=1,\"*111#\",15"],
            GsmModemService.SautoInitial111CommandOrder);
        Assert.Equal(
            ["AT+CUSD=2", "AT+CUSD=1,\"*101#\",15"],
            GsmModemService.SautoInitial101CommandOrder);
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
    public void AirplaneMode_UsesCapturedSautoRxGuards()
    {
        Assert.Equal(5, GsmModemService.SautoAirplaneMaxAttempts);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            GsmModemService.SautoAirplanePreQueryDelay);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            GsmModemService.SautoAirplaneResponsePollDelay);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            GsmModemService.SautoAirplaneResponseTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            GsmModemService.SautoAirplaneRetryDelay);
    }

    [Fact]
    public void ManualUssd_AllowsLateNetworkPayloadBeforeRfRecovery()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            GsmModemService.SautoManualUssdResponseTimeout);
    }

    [Theory]
    [InlineData("OK", null)]
    [InlineData("\r\nOK\r\n", null)]
    [InlineData("\r\n+CFUN: 4\r\n\r\nOK\r\n", 4)]
    [InlineData("\r\n+CFUN: 1\r\n\r\nOK\r\n", 1)]
    public void AirplaneMode_AdvancesOnlyFromCfunRxReport(
        string response,
        int? expectedMode) =>
        Assert.Equal(
            expectedMode,
            GsmModemService.ParseSautoCfunMode(response));

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
    public void SmsWatchdog_ProbesAllStoredMessagesWithoutBulkDelete()
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
        Assert.DoesNotContain(
            "CMGD",
            GsmModemService.SmsReceiveWatchdogCommand,
            StringComparison.OrdinalIgnoreCase);
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
    [InlineData("+CPIN: NOT READY", true)]
    [InlineData("+CME ERROR: 10", true)]
    [InlineData("+CME ERROR: 100", false)]
    [InlineData("+CME ERROR: 13", false)]
    [InlineData("+CPIN: NOT INSERTED", false)]
    [InlineData("+CPIN: READY", false)]
    public void ControllerRestart_UsesOnlySautoCpinConditions(
        string response,
        bool expected) =>
        Assert.Equal(expected, GsmModemService.RequiresSautoControllerRestart(response));

    [Theory]
    [InlineData("\r\n+CPIN: READY\r\n\r\nOK\r\n", true)]
    [InlineData("\r\n+CPIN: READY\r\n", false)]
    [InlineData("\r\n+CME ERROR: 13\r\n", false)]
    [InlineData("\r\n+CPIN: READY\r\n\r\nERROR\r\n", false)]
    public void CpinReady_AdvancesOnlyAfterReadyAndTerminalOk(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoCpinReadyResponse(response));

    [Theory]
    [InlineData("\r\n+CME ERROR: 13\r\n", true)]
    [InlineData("\r\n+CPIN: NOT INSERTED\r\n", true)]
    [InlineData("\r\n+CPIN: SIM PIN\r\n\r\nOK\r\n", false)]
    [InlineData("\r\n+CME ERROR: 10\r\n", false)]
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
    [InlineData("+CUSD: 1,\"TB :0849882209,Ngay KH:25/12/2024\",15", true)]
    [InlineData("+CUSD: 0,\"So TB 0849882209. TK chinh=100 VND\",15", false)]
    [InlineData("+CUSD: 1,\"Bam 1 de tra cuu\",15", false)]
    [InlineData("OK", false)]
    public void SmsMaintenance_OpensOnlyAfterCompletedAutomatic111Rx(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsSautoAutomatic111Completion(response));

    [Theory]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", true, true)]
    [InlineData(7, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", true, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768850016", false, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768859999", "89840200011768850016", true, false)]
    [InlineData(8, 8, "89840200011768850016", "89840200011768850016", "89840200011768859999", true, false)]
    public void SmsMaintenance_GateRejectsStale111AndIdentityChanges(
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
