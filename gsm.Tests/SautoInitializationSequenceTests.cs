using gsm.Services;

namespace gsm.Tests;

public sealed class SautoInitializationSequenceTests
{
    [Fact]
    public void InitializationCommandOrder_PreservesStoredSms()
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
            "AT+QURCCFG=\"urcport\",\"uart1\"",
            "AT+CPIN?"
        ];

        Assert.Equal(expected, GsmModemService.SautoInitializationCommandOrder);
        Assert.DoesNotContain(GsmModemService.SautoInitializationCommandOrder,
            command => command.StartsWith("AT+CMGD=", StringComparison.OrdinalIgnoreCase));
    }
}
