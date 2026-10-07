using gsm.Services;
using System.Diagnostics;

namespace gsm.Tests;

public sealed class DeviceUnlockServiceTests
{
    [Fact]
    public void TryParseResultLine_ParsesSuccessfulBridgeResult()
    {
        const string line =
            "TOOLGSM_RESULT_JSON:{\"success\":true,\"alreadyCompleted\":false,\"fullName\":\"NGUYEN VAN A\",\"msisdn\":\"84912345678\",\"message\":\"Thành công\"}";

        bool parsed = DeviceUnlockService.TryParseResultLine(
            line,
            out DeviceUnlockRunResult result);

        Assert.True(parsed);
        Assert.True(result.Success);
        Assert.False(result.AlreadyCompleted);
        Assert.Equal("NGUYEN VAN A", result.FullName);
        Assert.Equal("84912345678", result.Msisdn);
        Assert.Equal("Thành công", result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không phải kết quả")]
    [InlineData("TOOLGSM_RESULT_JSON:{sai-json}")]
    public void TryParseResultLine_RejectsInvalidOutput(string? line)
    {
        Assert.False(DeviceUnlockService.TryParseResultLine(line, out _));
    }

    [Theory]
    [InlineData("Authorization: Bearer abc")]
    [InlineData("session_id=secret")]
    [InlineData("x-signature: value")]
    public void SanitizeProgressLine_RemovesSensitiveLines(string line)
    {
        Assert.Null(DeviceUnlockService.SanitizeProgressLine(line));
    }

    [Fact]
    public void SanitizeProgressLine_KeepsUsefulProgressAndLimitsLength()
    {
        Assert.Equal(
            "Đang xử lý DKTTTB",
            DeviceUnlockService.SanitizeProgressLine("  Đang xử lý DKTTTB  "));
        Assert.Equal(
            400,
            DeviceUnlockService.SanitizeProgressLine(new string('x', 500))!.Length);
    }

    [Fact]
    public void AuthResultParser_ParsesStatusWithoutExposingCredentials()
    {
        const string line =
            "TOOLGSM_AUTH_RESULT_JSON:{\"success\":true,\"message\":\"OK\",\"cbssConfigured\":true,\"employConfigured\":false,\"cbssUsername\":\"cbss-user\",\"employUsername\":\"\",\"challenge\":\"\",\"deviceId\":\"\"}";

        bool parsed = DeviceUnlockAuthService.TryParseResultLine(
            line,
            out DeviceUnlockAuthResult result);

        Assert.True(parsed);
        Assert.True(result.Success);
        Assert.True(result.CbssConfigured);
        Assert.False(result.EmployConfigured);
        Assert.Equal("cbss-user", result.CbssUsername);
        Assert.Equal(string.Empty, result.Challenge);
    }

    [Fact]
    public void AuthResultParser_ParsesLiveSessionPingResults()
    {
        const string line =
            "TOOLGSM_AUTH_RESULT_JSON:{\"success\":true,\"message\":\"Đã kiểm tra phiên\",\"cbssConfigured\":true,\"employConfigured\":false," +
            "\"cbssUsername\":\"cbss-user\",\"employUsername\":\"onebss-user\",\"challenge\":\"\",\"deviceId\":\"\"," +
            "\"cbssPinged\":true,\"cbssSessionValid\":true,\"cbssHttpStatus\":200,\"cbssSessionMessage\":\"HTTP 200\"," +
            "\"employPinged\":true,\"employSessionValid\":false,\"employHttpStatus\":401,\"employSessionMessage\":\"HTTP 401\"}";

        bool parsed = DeviceUnlockAuthService.TryParseResultLine(
            line,
            out DeviceUnlockAuthResult result);

        Assert.True(parsed);
        Assert.True(result.CbssPinged);
        Assert.True(result.CbssSessionValid);
        Assert.Equal(200, result.CbssHttpStatus);
        Assert.Equal("HTTP 200", result.CbssSessionMessage);
        Assert.True(result.EmployPinged);
        Assert.False(result.EmploySessionValid);
        Assert.Equal(401, result.EmployHttpStatus);
        Assert.Equal("HTTP 401", result.EmploySessionMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOOLGSM_AUTH_RESULT_JSON:{bad-json}")]
    public void AuthResultParser_RejectsInvalidOutput(string? line)
    {
        Assert.False(DeviceUnlockAuthService.TryParseResultLine(line, out _));
    }

    [Fact]
    public void AuthProcessFailure_ReportsUsefulSafePythonDiagnostic()
    {
        string message = DeviceUnlockAuthService.BuildProcessFailureMessage(
            1,
            "Traceback (most recent call last):\nModuleNotFoundError: No module named 'requests'\npassword=hidden");

        Assert.Equal(
            "Python đăng nhập nguồn ảnh kết thúc với mã 1: ModuleNotFoundError: No module named 'requests'",
            message);
    }

    [Fact]
    public void AuthProcessFailure_UsesNonProtocolStandardOutputWhenNeeded()
    {
        string message = DeviceUnlockAuthService.BuildProcessFailureMessage(
            1,
            string.Empty,
            ["Đang khởi tạo", "ImportError: missing dependency"]);

        Assert.Equal(
            "Python đăng nhập nguồn ảnh kết thúc với mã 1: ImportError: missing dependency",
            message);
    }

    [Fact]
    public void AuthPythonProcess_IsForcedToUseUtf8()
    {
        var startInfo = new ProcessStartInfo();

        DeviceUnlockAuthService.ConfigureUtf8PythonEnvironment(startInfo);

        Assert.Equal("1", startInfo.Environment["PYTHONUTF8"]);
        Assert.Equal("utf-8", startInfo.Environment["PYTHONIOENCODING"]);
        Assert.Empty(DeviceUnlockAuthService.Utf8WithoutBom.GetPreamble());
    }

    [Fact]
    public void BridgeRequest_ReusesTheOtpRequestFingerprint()
    {
        var session = new MyVnptLoginOtpSession(
            "84912345678",
            "device-id|device-id|unknown|Android||3.3.99.Prd|CPH2179|11|",
            "CPH2179",
            "11",
            new string('F', 168),
            "0123456789abcdef",
            "okhttp/4.7.2");

        string json = DeviceUnlockService.BuildBridgeRequestJson(
            session.Phone,
            "123456",
            session);

        Assert.Contains("\"deviceProfile\"", json);
        Assert.Contains("\"model\":\"CPH2179\"", json);
        Assert.Contains("\"android_ver\":\"11\"", json);
        Assert.Contains("\"fcm_token\":\"", json);
        Assert.Contains("\"mac\":\"0123456789abcdef\"", json);
        Assert.Contains(session.DeviceInfo, json);
    }

    [Fact]
    public void OtpRequestParser_ReturnsTheEkycGeneratedFingerprint()
    {
        string line = DeviceUnlockService.OtpResultPrefix +
            "{\"success\":true,\"phone\":\"84912345678\",\"httpStatus\":200," +
            "\"errorCode\":\"0\",\"message\":\"OTP accepted\"," +
            "\"userAgent\":\"okhttp/4.7.2\",\"deviceProfile\":{" +
            "\"di\":\"device-a|device-a|unknown|Android||3.3.99.Prd|CPH2179|11|\"," +
            "\"model\":\"CPH2179\",\"android_ver\":\"11\"," +
            "\"fcm_token\":\"fcm-a\",\"mac\":\"0123456789abcdef\"}}";

        bool parsed = DeviceUnlockService.TryParseOtpRequestResultLine(
            line,
            "84912345678",
            out DeviceUnlockOtpRequestResult result);

        Assert.True(parsed);
        Assert.True(result.Success);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("0", result.ErrorCode);
        Assert.NotNull(result.Session);
        Assert.Equal("CPH2179", result.Session.DeviceModel);
        Assert.Equal("fcm-a", result.Session.FcmToken);
    }

    [Fact]
    public void OtpRequestParser_PreservesServerErrorDetails()
    {
        string line = DeviceUnlockService.OtpResultPrefix +
            "{\"success\":false,\"phone\":\"84912345678\",\"httpStatus\":200," +
            "\"errorCode\":\"3G_LOGIN_FAILED\"," +
            "\"message\":\"Đăng nhập 3G không thành công.\",\"deviceProfile\":null}";

        bool parsed = DeviceUnlockService.TryParseOtpRequestResultLine(
            line,
            "84912345678",
            out DeviceUnlockOtpRequestResult result);

        Assert.True(parsed);
        Assert.False(result.Success);
        Assert.Null(result.Session);
        Assert.Contains("HTTP 200", result.Message);
        Assert.Contains("ec=3G_LOGIN_FAILED", result.Message);
        Assert.Contains("Đăng nhập 3G không thành công", result.Message);
    }
}
