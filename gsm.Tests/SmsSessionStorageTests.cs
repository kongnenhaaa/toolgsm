using gsm.Services;
using gsm.Models;
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

    [Fact]
    public void LiveInboxOrdering_UsesAppIngestTimeInsteadOfStaleCarrierTime()
    {
        DateTimeOffset now = new(2026, 8, 14, 2, 0, 0, TimeSpan.Zero);
        var justIngested = new SmsMessage
        {
            DeliveryId = "new",
            ReceivedAtUtc = now,
            SmsTimestampUtc = now.AddDays(-3)
        };
        var historic = new SmsMessage
        {
            DeliveryId = "old",
            ReceivedAtUtc = now.AddMinutes(-1),
            SmsTimestampUtc = now.AddDays(3)
        };

        SmsMessage[] ordered = new[] { historic, justIngested }
            .OrderByDescending(MainViewModel.GetSmsDisplayTime)
            .ToArray();

        Assert.Equal("new", ordered[0].DeliveryId);
    }

    [Theory]
    [InlineData(true, "delivery-1", "delivery-1", true)]
    [InlineData(false, "delivery-1", "delivery-1", false)]
    [InlineData(true, "delivery-1", "delivery-2", false)]
    [InlineData(true, "", "delivery-1", false)]
    public void DeliveryAck_RequiresDurableCommitAndExactUiRow(
        bool inboxRecorded,
        string deliveryId,
        string visibleDeliveryId,
        bool expected)
    {
        SmsMessage[] messages =
        [
            new SmsMessage { DeliveryId = visibleDeliveryId }
        ];

        Assert.Equal(
            expected,
            MainViewModel.CanAcknowledgeSmsDelivery(
                inboxRecorded,
                deliveryId,
                messages));
    }

    private static SmsInboxRecord Record(string deliveryId) => new()
    {
        DeliveryId = deliveryId,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        PortName = "COM1",
        Sender = "BANK",
        Content = "OTP 123456"
    };
}
