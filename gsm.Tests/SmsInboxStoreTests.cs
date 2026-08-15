using gsm.Services;

namespace gsm.Tests;

public sealed class SmsInboxStoreTests
{
    [Fact]
    public void ProvidedDirectory_IsIgnoredAndNoHistoryFileIsCreated()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"toolgsm-session-inbox-{Guid.NewGuid():N}");
        var firstRun = new SmsInboxStore(directory);

        Assert.True(firstRun.Append(Record(
            "delivery-vietnamese",
            "Thuê bao của Quý khách\r\nOTP 123456")));
        Assert.Single(firstRun.GetRecent(10));
        Assert.False(Directory.Exists(directory));

        var nextRun = new SmsInboxStore(directory);
        Assert.Empty(nextRun.GetRecent(10));
        Assert.Equal(0, nextRun.Count);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DuplicateDeliveryId_IsIdempotentWithinCurrentSession()
    {
        var store = new SmsInboxStore();
        SmsInboxRecord record = Record(
            "stable-delivery-id",
            "OTP của bạn là 123456");

        Assert.True(store.Append(record));
        Assert.False(store.Append(record));
        Assert.Single(store.GetRecent(10));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void DuplicateDeliveryIdWithDifferentPayload_IsRejected()
    {
        var store = new SmsInboxStore();
        Assert.True(store.Append(Record("conflicting-id", "Nội dung ban đầu")));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.Append(Record("conflicting-id", "Nội dung đã thay đổi")));

        Assert.Contains("Conflicting SMS payload", error.Message);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void GetRecent_IsNewestFirstAndBounded()
    {
        var store = new SmsInboxStore();
        DateTimeOffset start = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(store.Append(Record(
                $"delivery-{i}",
                $"Nội dung {i}",
                start.AddMinutes(i))));
        }

        IReadOnlyList<SmsInboxRecord> recent = store.GetRecent(2);

        Assert.Equal(["delivery-4", "delivery-3"],
            recent.Select(record => record.DeliveryId));
    }

    [Fact]
    public void DeleteAndClear_OnlyMutateCurrentSession()
    {
        var store = new SmsInboxStore();
        store.Append(Record("delete-me", "one"));
        store.Append(Record("keep-me", "two"));

        Assert.Equal(1, store.Delete(["delete-me"]));
        Assert.Equal("keep-me", Assert.Single(store.GetRecent(10)).DeliveryId);
        Assert.Equal(1, store.Clear());
        Assert.Empty(store.GetRecent(10));
    }

    [Fact]
    public void DeliveryId_IsStableAndFieldBounded()
    {
        string first = SmsInboxStore.CreateDeliveryId("ab", "c");

        Assert.Equal(first, SmsInboxStore.CreateDeliveryId("ab", "c"));
        Assert.NotEqual(first, SmsInboxStore.CreateDeliveryId("a", "bc"));
    }

    private static SmsInboxRecord Record(
        string deliveryId,
        string content,
        DateTimeOffset? receivedAtUtc = null) => new()
    {
        DeliveryId = deliveryId,
        ReceivedAtUtc = receivedAtUtc
            ?? new DateTimeOffset(2026, 8, 15, 2, 3, 4, TimeSpan.Zero),
        PortName = "COM88",
        ReceiverPhone = "0843257140",
        Sender = "VinaPhone",
        Content = content,
        Otp = "N/A",
        NetworkProvider = "VinaPhone",
        Status = "Hoạt động"
    };
}
