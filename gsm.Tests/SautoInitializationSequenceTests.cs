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
            "AT+CUSD=1,\"*111#\",15"
        ];
        string[] expected101 =
        [
            "AT+CUSD=2",
            "AT+CUSD=1,\"*101#\",15"
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
