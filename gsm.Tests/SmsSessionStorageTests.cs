using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class SmsSessionStorageTests
{
    [Fact]
    public void SessionInbox_NeverCreatesAFileAndStartsEmptyEachRun()
    {
        var firstRun = SmsInboxStore.CreateInMemory();
        firstRun.Append(Record("delivery-1"));

        Assert.Single(firstRun.GetRecent(10));
        Assert.Empty(firstRun.DirectoryPath);

        var nextRun = SmsInboxStore.CreateInMemory();
        Assert.Empty(nextRun.GetRecent(10));
        Assert.Empty(nextRun.DirectoryPath);
    }

    [Theory]
    [InlineData("[SMS_UI_RECEIVED] delivery=abc")]
    [InlineData("Đã bắt được OTP: 123456")]
    [InlineData("+CMGR: payload")]
    [InlineData("Zalo message")]
    public void SmsRelatedLogs_AreExcludedFromDiskLog(string message) =>
        Assert.True(MainViewModel.ContainsSmsSensitiveLogData(message));

    private static SmsInboxRecord Record(string deliveryId) => new()
    {
        DeliveryId = deliveryId,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        PortName = "COM1",
        Sender = "BANK",
        Content = "OTP 123456"
    };
}
