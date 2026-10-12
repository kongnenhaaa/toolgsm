using gsm.Services;

namespace gsm.Tests;

public sealed class MyVnptDiagnosticLoggerTests
{
    [Theory]
    [InlineData("[SMS_QUEUE] index=1")]
    [InlineData("+CMTI: \"SM\",2")]
    [InlineData("AT+CMGL=\"ALL\"")]
    [InlineData("+CMGR: 1")]
    [InlineData("+CMS ERROR: 500")]
    public void IsSmsTransportEvent_AcceptsReceivePipelineSignals(string message)
    {
        Assert.True(MyVnptDiagnosticLogger.IsSmsTransportEvent(message));
    }

    [Fact]
    public void IsSmsTransportEvent_RejectsUnrelatedTelemetry()
    {
        Assert.False(MyVnptDiagnosticLogger.IsSmsTransportEvent("+CSQ: 18,99"));
    }

    [Theory]
    [InlineData("[VNPT_HTTP] service=otp_send event=request_start")]
    [InlineData("[COM7] Bắt đầu MyVNPT")]
    public void ShouldCapture_RecognizesMyVnptDiagnostics(string message)
    {
        Assert.True(MyVnptDiagnosticLogger.ShouldCapture(message));
    }

    [Fact]
    public void Sanitize_RemovesPhoneAndOtpSizedNumbers()
    {
        string value = MyVnptDiagnosticLogger.Sanitize(
            "[COM7] MyVNPT phone=84942152795 OTP=607718 status=200");

        Assert.DoesNotContain("84942152795", value);
        Assert.DoesNotContain("607718", value);
        Assert.Contains("status=200", value);
        Assert.Contains("[COM7]", value);
    }

    [Fact]
    public void ShouldCapture_RejectsUnrelatedOperationalLog()
    {
        Assert.False(MyVnptDiagnosticLogger.ShouldCapture(
            "[COM7] Cập nhật cường độ sóng"));
    }
}
