using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace gsm.Services;

/// <summary>
/// Hiển thị Windows Toast Notification (pop-up góc phải màn hình) khi nhận OTP.
/// Dùng WinRT API có sẵn trong .NET 10 Windows — không cần cài thêm gói.
/// </summary>
public static class ToastService
{
    private const string AppId = "gsm.OtpTool";

    /// <summary>
    /// Hiện thông báo Toast với tiêu đề và nội dung tùy chỉnh.
    /// </summary>
    public static void Show(string title, string body)
    {
        try
        {
            bool enableSound = SettingsService.Current.EnableToastSound;
            string audioTag = enableSound 
                ? "<audio src=\"ms-winsoundevent:Notification.SMS\"/>" 
                : "<audio silent=\"true\"/>";

            string xml = $"""
                <toast duration="short">
                    <visual>
                        <binding template="ToastGeneric">
                            <text>{EscapeXml(title)}</text>
                            <text>{EscapeXml(body)}</text>
                        </binding>
                    </visual>
                    {audioTag}
                </toast>
                """;

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            var toast    = new ToastNotification(xmlDoc);

            notifier.Show(toast);
        }
        catch
        {
            // Toast không hỗ trợ trên một số cấu hình máy → bỏ qua, không crash app
        }
    }

    /// <summary>
    /// Phím tắt: hiện Toast khi nhận được OTP mới.
    /// </summary>
    public static void ShowOtp(string portName, string simPhone, string otp, string sender)
    {
        Show(
            $"🔑 OTP Mới — {portName}",
            $"SIM: {simPhone} | Từ: {sender}\nOTP: {otp}"
        );
    }

    /// <summary>
    /// Toast cảnh báo SIM mất kết nối.
    /// </summary>
    public static void ShowSimOffline(string portName)
    {
        Show($"⚠️ SIM Mất Kết Nối", $"Cổng {portName} không phản hồi!");
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
