using gsm.Models;

namespace gsm.Tests;

public sealed class SimPortStatusTests
{
    [Theory]
    [InlineData("USSDOK", "USSD OK")]
    [InlineData(" ussdok ", "USSD OK")]
    [InlineData("USSD OK", "USSD OK")]
    [InlineData("SMS OK", "SMS OK")]
    public void NormalizeOperationStatus_UsesReadableUssdLabel(string input, string expected)
    {
        Assert.Equal(expected, SimPort.NormalizeOperationStatus(input));
    }

    [Fact]
    public void LegacyCompactUssdStatus_IsReadableEverywhere()
    {
        var port = new SimPort
        {
            Status = SimStatus.Active,
            SautoStatus = "USSDOK"
        };

        Assert.Equal("USSD OK", port.StatusDisplay);
        Assert.Equal("USSD OK", port.GetOperationStatus("USSD"));
    }
}
