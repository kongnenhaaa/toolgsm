using System.Text;
using gsm.Services;

namespace gsm.Tests;

public sealed class SmsEncodingSelectionTests
{
    [Theory]
    [InlineData("hello 123", true)]
    [InlineData("Symbols !\"#%&'()*+,-./:;<=>?", true)]
    [InlineData("[Zalo] KJr9hd9c0eeh43InHea3dEklrIHU7pru", true)]
    [InlineData("A]B", true)]
    [InlineData("price$1", true)]
    [InlineData("mail@example.com", true)]
    [InlineData("a_b", true)]
    [InlineData("slash\\test", true)]
    [InlineData("brace{test}", true)]
    [InlineData("grave`accent", false)]
    [InlineData("Tiếng Việt", false)]
    public void IraTextMode_AcceptsGsmRepresentableAscii(
        string message,
        bool expected)
    {
        Assert.Equal(expected, GsmModemService.CanSendSmsInIraTextMode(message));
    }

    [Fact]
    public void ZaloVerificationMessage_UsesIraAndPreservesLiteralSquareBrackets()
    {
        const string message = "[Zalo] KJr9hd9c0eeh43InHea3dEklrIHU7pru";

        Assert.True(GsmModemService.CanSendSmsInIraTextMode(message));

        byte[] payload = Encoding.ASCII.GetBytes(message);

        Assert.Equal((byte)'[', payload[0]);
        Assert.Equal((byte)']', payload[5]);
        Assert.Equal(message, Encoding.ASCII.GetString(payload));
    }
}
