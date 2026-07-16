using gsm.Services;

namespace gsm.Tests;

public sealed class MyVnptServiceTests
{
    [Theory]
    [InlineData("0942 152 795", "84942152795")]
    [InlineData("84942152795", "84942152795")]
    [InlineData("+84 942 152 795", "84942152795")]
    public void NormalizePhone_AcceptsSupportedVietnameseFormats(string input, string expected)
    {
        Assert.Equal(expected, MyVnptService.NormalizePhone(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("900")]
    [InlineData("1234567890")]
    [InlineData("8494215279500")]
    public void NormalizePhone_RejectsInvalidDestinations(string input)
    {
        Assert.Empty(MyVnptService.NormalizePhone(input));
    }

    [Theory]
    [InlineData("Ma OTP MyVNPT cua ban la 123456")]
    [InlineData("ma otp myvnpt cua ban la 123456")]
    [InlineData("MY VNPT: ma xac thuc 123456")]
    public void IsMyVnptOtpMessage_IsCaseInsensitive(string content)
    {
        Assert.True(MyVnptService.IsMyVnptOtpMessage(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OTP Zalo cua ban la 123456")]
    public void IsMyVnptOtpMessage_RejectsUnrelatedSms(string? content)
    {
        Assert.False(MyVnptService.IsMyVnptOtpMessage(content));
    }

}
