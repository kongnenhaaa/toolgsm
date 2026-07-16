using gsm.Services;

namespace gsm.Tests;

public sealed class GsmDestinationTests
{
    [Theory]
    [InlineData("900")]
    [InlineData("888")]
    [InlineData("9191")]
    [InlineData("+84912345678")]
    [InlineData("*101#")]
    [InlineData("CarrierCode")]
    public void Sms_AllowsFlexibleCarrierDestinations(string input)
    {
        Assert.True(GsmDestination.TryNormalizeSms(input, out string actual));
        Assert.Equal(input, actual);
    }

    [Theory]
    [InlineData("900")]
    [InlineData("*123#")]
    [InlineData("+84912345678")]
    [InlineData("123,45")]
    public void Dial_AllowsShortCodesAndDialCharacters(string input)
    {
        Assert.True(GsmDestination.TryNormalizeDial(input, out string actual));
        Assert.Equal(input, actual);
    }

    [Theory]
    [InlineData("900\rAT+CFUN=0")]
    [InlineData("900\nAT+CFUN=0")]
    [InlineData("900;AT+CFUN=0")]
    public void Dial_BlocksAtCommandEscape(string input)
    {
        Assert.False(GsmDestination.TryNormalizeDial(input, out _));
    }

    [Theory]
    [InlineData("888\rAT+CFUN=0")]
    [InlineData("888\"\rAT+CFUN=0")]
    public void Sms_BlocksAtCommandEscape(string input)
    {
        Assert.False(GsmDestination.TryNormalizeSms(input, out _));
    }
}
