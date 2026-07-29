using gsm.Services;

namespace gsm.Tests;

public sealed class SmsSubmitDispositionTests
{
    [Theory]
    [InlineData("Gửi thành công", SmsSubmitDisposition.Confirmed)]
    [InlineData("+CMGS: 7\r\nOK", SmsSubmitDisposition.Confirmed)]
    [InlineData("OK", SmsSubmitDisposition.Confirmed)]
    [InlineData("ERROR: SMS operation cancelled before Ctrl+Z", SmsSubmitDisposition.CancelledBeforePayload)]
    [InlineData("ERROR: +CMS ERROR: 350", SmsSubmitDisposition.FailedBeforePayload)]
    public void ClassifySubmitResult_MapsTerminalResult(
        string response,
        SmsSubmitDisposition expected)
    {
        Assert.Equal(expected, GsmSmsService.ClassifySubmitResult(response));
    }

    [Theory]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] SMS operation cancelled after Ctrl+Z")]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] [SMS_CHANNEL_RECOVERY_REQUIRED] cancelled\r\nOK")]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] Multipart stopped after 1/2 confirmed parts")]
    public void ClassifySubmitResult_PayloadMarkerAlwaysPreventsRetry(string response)
    {
        Assert.Equal(
            SmsSubmitDisposition.PayloadSubmittedUncertain,
            GsmSmsService.ClassifySubmitResult(response));
    }
}
