using gsm.Services;
using gsm.ViewModels;

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
            "AT+CPMS=\"ME\",\"ME\",\"ME\"",
            "AT+CPMS=\"SM\",\"SM\",\"SM\"",
            "AT+CPMS?",
            "AT+CNMI=1,1,0,0,0",
            "AT+QCFG=\"nwscanmode\",0,1",
            "AT+QURCCFG=\"urcport\",\"uart1\"",
            "AT+CUSD=1",
            "AT+CPIN?"
        ];

        Assert.Equal(expected, GsmModemService.SautoInitializationCommandOrder);
        Assert.DoesNotContain(GsmModemService.SautoInitializationCommandOrder, command =>
            command.Equals("AT+CMGD=1,4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InitialUssdCommandOrder_MatchesCapturedSautoSequence()
    {
        string[] expected111 =
        [
            "AT+CUSD=2",
            "AT+CUSD=1",
            "AT+CUSD=1,\"*111#\",15"
        ];
        string[] expected101 =
        [
            "AT+CUSD=2",
            "AT+CUSD=1",
            "AT+CUSD=1,\"002A0031003000310023\",15"
        ];

        Assert.Equal(expected111, MainViewModel.SautoInitial111CommandOrder);
        Assert.Equal(expected101, MainViewModel.SautoInitial101CommandOrder);
        Assert.DoesNotContain(MainViewModel.SautoInitial111CommandOrder, command =>
            command.Contains("*101#", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CREG", StringComparison.OrdinalIgnoreCase)
            || command.Contains("COPS=", StringComparison.OrdinalIgnoreCase)
            || command.Contains("ims", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CFUN", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("+COPS: 0,0,\"VINAPHONE\",2\r\nOK", "VINAPHONE", "2")]
    [InlineData("+COPS: 0,2,45202,7\r\nOK", "45202", "7")]
    [InlineData("AT+COPS?\r\n+COPS: 0,1,\"VINA\"\r\nOK", "VINA", "")]
    public void CopsParser_AcceptsQuotedAndUnquotedOperatorFormats(
        string response, string expectedOperator, string expectedAct)
    {
        bool parsed = GsmModemService.TryParseCopsResponse(
            response, out string operatorName, out string act);

        Assert.True(parsed);
        Assert.Equal(expectedOperator, operatorName);
        Assert.Equal(expectedAct, act);
    }

    [Fact]
    public void FreshBalanceResponse_RequiresParsedTkc()
    {
        Assert.False(MainViewModel.HasFreshSautoBalanceResponse(
            "+CUSD: 0,\"Dich vu dang ban\",15", "", "Dich vu dang ban", "", ""));
        Assert.False(MainViewModel.HasFreshSautoBalanceResponse(
            "+CUSD: 0,\"Dich vu dang ban\",15", "", "Dich vu dang ban", "10000", "10000"));
        Assert.False(MainViewModel.HasFreshSautoBalanceResponse(
            "+CUSD: 0,\"Dich vu tam loi\",15", "TKC: 10000d", "TKC: 10000d", "10000", "10000"));
        Assert.True(MainViewModel.HasFreshSautoBalanceResponse(
            "+CUSD: 0,\"TKC: 0d\",15", "", "TKC: 0d", "", "0"));
        Assert.True(MainViewModel.HasFreshSautoBalanceResponse(
            "+CUSD: 0,\"TKC: 10000d\",15", "", "TKC: 10000d", "10000", "10000"));
    }

    [Theory]
    [InlineData("+CREG: 2,1\r\nOK", true)]
    [InlineData("+CGREG: 0,5\r\nOK", true)]
    [InlineData("+CEREG: 2,2\r\nOK", false)]
    [InlineData("ERROR", false)]
    public void RegistrationParser_RecognizesHomeAndRoaming(
        string response, bool expected)
    {
        Assert.Equal(expected, GsmModemService.IsNetworkRegistered(response));
    }

    [Theory]
    [InlineData("+CME ERROR: 13", "ERROR", true)]
    [InlineData("+CPIN: NOT READY", "+CME ERROR: 13", true)]
    [InlineData("+CPIN: READY", "+QCCID: 89840200011834605261\r\nOK", false)]
    [InlineData("+CPIN: SIM PIN", "ERROR", false)]
    [InlineData("+CPIN: SIM PUK", "ERROR", false)]
    public void StartupSimRecovery_RestartsOfflineOnlyWhenIdentityIsUnreadableAndUnlocked(
        string cpinResponse,
        string ccidResponse,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldAttemptStartupOfflineSimRecovery(
                cpinResponse,
                ccidResponse));

    [Fact]
    public void PortHealth_DefersIntentionalPortOwnershipAndLockContention()
    {
        Assert.True(GsmModemService.ShouldDeferPortHealthProbe(
            backgroundSuspended: false,
            callInProgress: false,
            commandPending: false,
            coordinatedRecoveryOwnsPort: true));
        Assert.False(GsmModemService.ShouldDeferPortHealthProbe(
            backgroundSuspended: false,
            callInProgress: false,
            commandPending: false,
            coordinatedRecoveryOwnsPort: false));
        Assert.True(GsmModemService.IsDeferredPortHealthProbeResponse(
            "ERROR: Timeout waiting for lock"));
        Assert.True(GsmModemService.IsDeferredPortHealthProbeResponse(
            "ERROR: Another command is already in progress"));
        Assert.False(GsmModemService.IsDeferredPortHealthProbeResponse(
            "ERROR: Timeout (device did not return OK/ERROR)"));
    }

    [Fact]
    public void PortHealth_FailureEvidenceSurvivesLockContention()
    {
        int failures = GsmModemService.NextPortHealthFailureCount(
            0,
            "ERROR: Timeout (device did not return OK/ERROR)");
        Assert.Equal(1, failures);
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            GsmModemService.GetPortHealthProbeInterval(failures));

        failures = GsmModemService.NextPortHealthFailureCount(
            failures,
            "ERROR: Timeout waiting for lock");
        Assert.Equal(1, failures);

        failures = GsmModemService.NextPortHealthFailureCount(
            failures,
            "ERROR: Timeout (device did not return OK/ERROR)");
        Assert.Equal(2, failures);

        failures = GsmModemService.NextPortHealthFailureCount(
            failures,
            "OK");
        Assert.Equal(0, failures);
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            GsmModemService.GetPortHealthProbeInterval(failures));
    }

    [Theory]
    [InlineData("+CME ERROR: 13", true)]
    [InlineData("+CPIN: NOT READY", true)]
    [InlineData("+CME ERROR: 10", false)]
    [InlineData("+CPIN: NOT INSERTED", false)]
    [InlineData("ERROR: Timeout", false)]
    public void HotplugOfflineRecovery_RetriesOnlyRecoverableSimStackStates(
        string response,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.IsOfflineRecoverableSimStackResponse(response));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(6, true)]
    public void HotplugOfflineRecovery_UsesBoundedCooldown(
        int pass,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldRunHotplugOfflineRecovery(pass));

    [Theory]
    [InlineData("89840200011797965884", "VinaPhone")]
    [InlineData("89840412345678901234", "Viettel")]
    [InlineData("89840112345678901234", "MobiFone")]
    [InlineData("unknown", "")]
    public void CcidProviderFallback_UsesVietnamIssuerPrefix(
        string ccid, string expected)
    {
        Assert.Equal(expected, MainViewModel.ResolveNetworkProviderFromCcid(ccid));
    }
}
