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

    [Fact]
    public void LiveInboxOrdering_MessageWithoutTimestampDoesNotStayAboveNewMessages()
    {
        DateTimeOffset now = new(2026, 8, 23, 2, 0, 0, TimeSpan.Zero);
        var callEndedWithoutTimestamp = new SmsMessage
        {
            DeliveryId = "call-ended",
            Content = "Cuộc gọi đến đã kết thúc."
        };
        var newlyReceivedSms = new SmsMessage
        {
            DeliveryId = "new-sms",
            ReceivedAtUtc = now
        };

        SmsMessage[] ordered = new[] { callEndedWithoutTimestamp, newlyReceivedSms }
            .OrderByDescending(MainViewModel.GetSmsDisplayTime)
            .ToArray();

        Assert.Equal("new-sms", ordered[0].DeliveryId);
        Assert.Equal(
            DateTimeOffset.MinValue,
            MainViewModel.GetSmsDisplayTime(callEndedWithoutTimestamp));
    }

    [Fact]
    public void ReceivedOtp_UpdatesEveryPortColumnUsedByTheSmsTable()
    {
        var port = new SimPort { Otp = "old-otp" };
        DateTimeOffset receivedAt = new(
            2026, 8, 25, 3, 4, 5, TimeSpan.Zero);

        MainViewModel.ApplyReceivedSmsToPort(
            port,
            "VinaPhone",
            "123456",
            "Ma OTP cua ban la 123456",
            receivedAt);

        Assert.Equal("VinaPhone", port.Sender);
        Assert.Equal("VinaPhone", port.LastSmsSender);
        Assert.Equal("123456", port.Otp);
        Assert.Equal("Ma OTP cua ban la 123456", port.LastMessageContent);
        Assert.Equal(
            receivedAt.ToLocalTime().ToString("HH:mm:ss"),
            port.LastReceivedTime);
    }

    [Fact]
    public void OrdinarySms_DoesNotOverwriteTheMostRecentOtp()
    {
        var port = new SimPort { Otp = "654321" };

        MainViewModel.ApplyReceivedSmsToPort(
            port,
            "INFO",
            "N/A",
            "Thong bao thong thuong",
            DateTimeOffset.UtcNow);

        Assert.Equal("654321", port.Otp);
        Assert.Equal("INFO", port.LastSmsSender);
        Assert.Equal("Thong bao thong thuong", port.LastMessageContent);
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
