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
}
