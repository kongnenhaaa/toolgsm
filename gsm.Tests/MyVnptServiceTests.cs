using gsm.Services;
using System.Net;

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

    [Theory]
    [InlineData("Bạn đang gửi OTP")]
    [InlineData("Ban dang gui OTP, vui long cho")]
    public void IsOtpAlreadyPendingMessage_TreatsExistingRequestAsPending(string content)
    {
        Assert.True(MyVnptService.IsOtpAlreadyPendingMessage(content));
    }

    [Theory]
    [InlineData("reg_nok", "Đăng ký không thành công")]
    [InlineData("1", "Thuê bao đã có tài khoản trên hệ thống")]
    [InlineData("1", "Tai khoan da ton tai")]
    [InlineData("1", "Account already exists")]
    public void IsAccountAlreadyExistsResponse_RecognizesRegisterConflicts(string code, string message)
    {
        Assert.True(MyVnptService.IsAccountAlreadyExistsResponse(code, message));
    }

    [Fact]
    public void IsAccountAlreadyExistsResponse_RejectsMissingAccountMessage()
    {
        Assert.False(MyVnptService.IsAccountAlreadyExistsResponse("1", "Chưa có tài khoản VNPortal"));
    }

    [Fact]
    public void GetFriendlyExceptionMessage_ExplainsServiceUnavailable()
    {
        var exception = new HttpRequestException(
            "VNPT HTTP 503: Service Temporarily Unavailable",
            null,
            HttpStatusCode.ServiceUnavailable);

        string message = MyVnptService.GetFriendlyExceptionMessage(exception);

        Assert.Contains("VNPT", message);
        Assert.DoesNotContain("HTTP 503", message);
    }

}
