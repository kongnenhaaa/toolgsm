using gsm.Services;
using gsm.ViewModels;

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
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] [SMS_CHANNEL_RECOVERY_REQUIRED] Timeout sending SMS payload; SMS channel recovery failed")]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] [SMS_CHANNEL_RECOVERY_REQUIRED] Timeout sending SMS payload; SMS result uncertain")]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] Multipart stopped after 1/2 confirmed parts")]
    public void ClassifySubmitResult_PayloadMarkerAlwaysPreventsRetry(string response)
    {
        Assert.Equal(
            SmsSubmitDisposition.PayloadSubmittedUncertain,
            GsmSmsService.ClassifySubmitResult(response));
    }

    [Fact]
    public void SmsChannelRecoveryRequired_LogsWithoutSignallingPhysicalDisconnect()
    {
        var modem = new GsmModemService();
        GsmDataEventArgs? recoveryLog = null;
        int disconnectEvents = 0;
        modem.LogMessage += (_, args) => recoveryLog = args;
        modem.PortDisconnected += (_, _) => disconnectEvents++;

        modem.ReportSmsChannelRecoveryRequired("COM120");

        Assert.NotNull(recoveryLog);
        Assert.Equal("COM120", recoveryLog.PortName);
        Assert.StartsWith("[SMS_CHANNEL_RECOVERY_REQUIRED]", recoveryLog.Data);
        Assert.Contains("không phải lỗi SIM", recoveryLog.Data);
        Assert.Equal(0, disconnectEvents);
    }

    [Theory]
    [InlineData("[SMS_CHANNEL_RECOVERY_FAILED] COM mất phản hồi sau xác minh cuối", true)]
    [InlineData("[SMS_CHANNEL_RECOVERY_REQUIRED] Đang đồng bộ lại kênh lệnh COM", false)]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] [SMS_CHANNEL_RECOVERY_REQUIRED] Timeout sending SMS payload", false)]
    public void OnlyFinalSmsChannelFailureRequestsPlannedReconnect(
        string data,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainViewModel.IsFinalSmsChannelRecoveryFailure(data));
    }

    [Fact]
    public async Task FinalSmsChannelVerification_CleanChannelDoesNotRunFailureHandler()
    {
        int failures = 0;

        bool recovered = await GsmModemService.VerifySmsChannelOrHandleFailureAsync(
            _ => Task.FromResult(true),
            _ => failures++,
            CancellationToken.None);

        Assert.True(recovered);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task FinalSmsChannelVerification_FalseRunsFailureHandlerOnce()
    {
        var diagnostics = new List<string>();

        bool recovered = await GsmModemService.VerifySmsChannelOrHandleFailureAsync(
            _ => Task.FromResult(false),
            diagnostics.Add,
            CancellationToken.None);

        Assert.False(recovered);
        Assert.Equal(["FINAL_VERIFICATION_RETURNED_FALSE"], diagnostics);
    }

    [Fact]
    public async Task FinalSmsChannelVerification_ExceptionRunsFailureHandlerOnce()
    {
        var diagnostics = new List<string>();

        bool recovered = await GsmModemService.VerifySmsChannelOrHandleFailureAsync(
            _ => Task.FromException<bool>(new TimeoutException()),
            diagnostics.Add,
            CancellationToken.None);

        Assert.False(recovered);
        Assert.Equal([nameof(TimeoutException)], diagnostics);
    }

    [Fact]
    public async Task FinalSmsChannelVerification_CancellationRunsFailureHandlerOnce()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var diagnostics = new List<string>();

        bool recovered = await GsmModemService.VerifySmsChannelOrHandleFailureAsync(
            token => Task.FromCanceled<bool>(token),
            diagnostics.Add,
            cts.Token);

        Assert.False(recovered);
        Assert.Equal([nameof(TaskCanceledException)], diagnostics);
    }

    [Fact]
    public async Task FinalSmsChannelVerification_FailureHandlerCannotReplaceSmsResult()
    {
        int failures = 0;

        bool recovered = await GsmModemService.VerifySmsChannelOrHandleFailureAsync(
            _ => Task.FromResult(false),
            _ =>
            {
                failures++;
                throw new InvalidOperationException("UI callback failed");
            },
            CancellationToken.None);

        Assert.False(recovered);
        Assert.Equal(1, failures);
    }
}
