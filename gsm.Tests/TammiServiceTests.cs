using System.Text.Json;
using gsm.Services;

namespace gsm.Tests;

public sealed class TammiServiceTests
{
    [Theory]
    [InlineData("0338 924 271", "0338924271")]
    [InlineData("+84 338 924 271", "0338924271")]
    [InlineData("84338924271", "0338924271")]
    public void NormalizePhone_AcceptsVietnameseFormats(string input, string expected)
    {
        Assert.Equal(expected, TammiService.NormalizePhone(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("900")]
    [InlineData("1234567890")]
    [InlineData("033892427100")]
    public void NormalizePhone_RejectsInvalidValues(string input)
    {
        Assert.Empty(TammiService.NormalizePhone(input));
    }

    [Fact]
    public void DeviceId_IsRandomAndroidIdShape()
    {
        string first = TammiService.CreateDeviceId();
        string second = TammiService.CreateDeviceId();

        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.Matches("^[0-9a-f]{16}$", second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("Tammi: Ma OTP cua ban la 123456")]
    [InlineData("Viettel Tammi - mã xác thực 654321")]
    public void IsTammiOtpMessage_AcceptsBrandedMessages(string content)
    {
        Assert.True(TammiService.IsTammiOtpMessage(content));
    }

    [Fact]
    public void IsTammiOtpMessage_AcceptsViettelSenderAndOtpText()
    {
        Assert.True(TammiService.IsTammiOtpMessage(
            "Ma OTP cua ban la 123456",
            "VIETTEL"));
    }

    [Theory]
    [InlineData("OTP Zalo cua ban la 123456", "ZALO")]
    [InlineData("Tin nhan Viettel khong co ma", "VIETTEL")]
    [InlineData("", "TAMMI")]
    public void IsTammiOtpMessage_RejectsUnrelatedMessages(string content, string sender)
    {
        Assert.False(TammiService.IsTammiOtpMessage(content, sender));
    }

    [Theory]
    [InlineData("123456a@A", true)]
    [InlineData("12345678", false)]
    [InlineData("abcdefgh@", false)]
    [InlineData("ABCDEFG1@", false)]
    [InlineData("Abc1234", false)]
    public void PasswordValidation_MatchesTammiRules(string password, bool expected)
    {
        Assert.Equal(expected, TammiService.IsValidPassword(password, out _));
    }

    [Fact]
    public void ResponseHelpers_ReadSuccessAndMessage()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"success\":true,\"message\":\"Thành công.\"}");

        Assert.True(TammiService.IsSuccess(document.RootElement));
        Assert.Equal(
            "Thành công.",
            TammiService.GetMessage(document.RootElement, "fallback"));
    }

    [Fact]
    public void ApiConcurrency_IsBoundedLikeOtherAccountWorkflows()
    {
        Assert.Equal(20, TammiService.ApiConcurrencyLimit);
    }
}
