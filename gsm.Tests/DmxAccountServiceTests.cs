using gsm.Services;
using System.Text.Json;

namespace gsm.Tests;

public sealed class DmxAccountServiceTests
{
    [Theory]
    [InlineData("{\"createPassword\":true}", null)]
    [InlineData("{\"createPassword\":false}", "createPassword=false")]
    [InlineData("{\"createPassword\":null}", "không trả kết quả")]
    [InlineData("{}", "không trả kết quả")]
    public void PasswordRecovery_ReportsTheActualResponseStage(
        string json, string? expectedError)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        string? error = DmxAccountService.GetPasswordRecoveryFailure(
            document.RootElement);

        if (expectedError == null)
            Assert.Null(error);
        else
            Assert.Contains(expectedError, error);
    }

    [Fact]
    public void DeviceIdentity_IsStableForTheSameComAndSim()
    {
        (string firstId, string firstName) =
            DmxAccountService.CreateStableDeviceIdentity("COM3-8984012345678901234");
        (string secondId, string secondName) =
            DmxAccountService.CreateStableDeviceIdentity("com3-8984012345678901234");

        Assert.Equal(firstId, secondId);
        Assert.Equal(firstName, secondName);
        Assert.True(Guid.TryParse(firstId, out _));
        Assert.False(string.IsNullOrWhiteSpace(firstName));
    }

    [Fact]
    public void DeviceIdentity_ChangesWhenTheSimSessionChanges()
    {
        (string firstId, _) = DmxAccountService.CreateStableDeviceIdentity(
            "COM3-8984012345678901234");
        (string secondId, _) = DmxAccountService.CreateStableDeviceIdentity(
            "COM3-8984099999999999999");

        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void AnonymousRecoveryHeaders_MatchOfficialClientAndDoNotClaimLogin()
    {
        var session = new DmxRegistrationSession(
            "0841234567",
            "5d45c3ea-510c-44ed-aaf1-4447ce72bb6c",
            "SM-G991B",
            DmxAccountService.DefaultOAuthAuthorization,
            string.Empty,
            null,
            null,
            string.Empty,
            "PASSWORD_RECOVERY",
            IsPasswordRecovery: true);

        Dictionary<string, string> headers =
            DmxAccountService.CreateGraphQlHeaders(session);

        Assert.Equal("android", headers["Os"]);
        Assert.Equal(session.DeviceId, headers["deviceid"]);
        Assert.Equal(session.DeviceId, headers["device_id"]);
        Assert.Equal("2", headers["platform"]);
        Assert.Equal("99", headers["SiteId"]);
        Assert.Equal("2.0.26", headers["x-version-app"]);
        Assert.Equal($"Android||14||{session.DeviceId}", headers["User-Agent"]);
        Assert.DoesNotContain("deviceName", headers.Keys);
        Assert.DoesNotContain("username", headers.Keys);
        Assert.DoesNotContain("Authorization", headers.Keys);
        Assert.DoesNotContain("accessToken", headers.Keys);
    }

    [Fact]
    public void AuthenticatedHeaders_IncludeLoginContext()
    {
        var session = new DmxRegistrationSession(
            "0841234567",
            "5d45c3ea-510c-44ed-aaf1-4447ce72bb6c",
            "SM-G991B",
            DmxAccountService.DefaultOAuthAuthorization,
            "access-token",
            null,
            "device-token",
            "12345",
            "OTP_UPDATED");

        Dictionary<string, string> headers =
            DmxAccountService.CreateGraphQlHeaders(session);

        Assert.Equal("Bearer access-token", headers["Authorization"]);
        Assert.Equal("access-token", headers["accessToken"]);
        Assert.Equal("device-token", headers["devicetoken"]);
        Assert.Equal("0841234567", headers["username"]);
        Assert.Equal("12345", headers["customerid"]);
    }

    [Theory]
    [InlineData("Đã có lỗi chức năng quên mật khẩu")]
    [InlineData("Forgot password function error")]
    public void GenericForgotPasswordFailure_IsRetriedOnce(string message)
    {
        Assert.True(DmxAccountService.IsTransientForgotPasswordFailure(message));
    }

    [Theory]
    [InlineData("Số lần gửi OTP đã vượt quá giới hạn")]
    [InlineData("")]
    [InlineData(null)]
    public void SpecificForgotPasswordFailure_IsNotRetried(string? message)
    {
        Assert.False(DmxAccountService.IsTransientForgotPasswordFailure(message));
    }

}
