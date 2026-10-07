using gsm.ViewModels;
using System.Net;

namespace gsm.Tests;

public sealed class TelegramSmsNotificationTests
{
    [Fact]
    public void NormalSms_IsAlwaysRenderedWithFullEncodedContent()
    {
        DateTime receivedAt = new(2026, 8, 14, 10, 20, 30);

        string text = MainViewModel.BuildTelegramSmsNotification(
            "COM110",
            "0912345678",
            "VinaPhone",
            "N/A",
            "Nội dung thường & không bị <cắt>",
            receivedAt);

        Assert.StartsWith("📩 SMS mới\n", text);
        Assert.Contains("Port: COM110", text);
        Assert.Contains(
            "Nội dung: Nội dung thường & không bị <cắt>",
            WebUtility.HtmlDecode(text));
        Assert.DoesNotContain("OTP: <b>", text);
        Assert.EndsWith("Time: 10:20:30 14/08", text);
    }

    [Fact]
    public void OtpSms_ContainsOtpAndTheEntireOriginalMessage()
    {
        const string content = "Dòng 1\nMã OTP 609998\nDòng cuối";

        string text = MainViewModel.BuildTelegramSmsNotification(
            "COM83",
            "0832029939",
            "ZALO",
            "609998",
            content,
            new DateTime(2026, 8, 14, 11, 0, 0));

        Assert.StartsWith("🔐 OTP mới\n", text);
        Assert.Contains("OTP: <b>609998</b>", text);
        Assert.Contains("Nội dung: " + content, WebUtility.HtmlDecode(text));
    }

    [Fact]
    public void SamePhysicalSms_UsesOneStableTelegramDeduplicationKey()
    {
        DateTimeOffset carrierTimestamp = new(
            2026, 9, 3, 13, 20, 0, TimeSpan.FromHours(7));

        string first = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "com51",
            "9114",
            "Số thuê bao 84848797228 đã được phê duyệt TTTB thành công!",
            carrierTimestamp);
        string replay = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "COM51",
            "9114",
            "Số thuê bao 84848797228 đã được phê duyệt TTTB thành công!",
            carrierTimestamp);

        Assert.Equal(first, replay);
    }

    [Fact]
    public void IdenticalSmsAtDifferentCarrierTimes_RemainsARealNewNotification()
    {
        string first = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "COM51",
            "9114",
            "Cùng nội dung",
            new DateTimeOffset(2026, 9, 3, 13, 20, 0, TimeSpan.FromHours(7)));
        string later = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "COM51",
            "9114",
            "Cùng nội dung",
            new DateTimeOffset(2026, 9, 3, 13, 21, 0, TimeSpan.FromHours(7)));

        Assert.NotEqual(first, later);
    }

    [Fact]
    public void MissingCarrierTimestamp_StillSuppressesSameSessionReplay()
    {
        string first = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "COM51", "9114", "Tin không có timestamp", null);
        string replay = MainViewModel.BuildTelegramSmsDeduplicationKey(
            "COM51", "9114", "Tin không có timestamp", null);

        Assert.Equal(first, replay);
    }
}
