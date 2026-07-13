using System;

namespace gsm.Models;

/// <summary>
/// Quy tắc webhook: khi nhận OTP/SMS khớp điều kiện, tự động POST JSON đến URL đã cấu hình.
/// </summary>
public class WebhookRule
{
    /// <summary>ID duy nhất (GUID) của quy tắc.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Tên hiển thị của quy tắc (VD: "Forward OTP Zalo").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Bật/tắt quy tắc này.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lọc người gửi (sender). Để trống = áp dụng cho tất cả.
    /// Hỗ trợ nhiều từ khóa phân cách bằng dấu phẩy (VD: "Zalo,ZALO,8500").
    /// </summary>
    public string SenderFilter { get; set; } = string.Empty;

    /// <summary>URL endpoint nhận HTTP POST (VD: https://webhook.site/xxx).</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Header bí mật tùy chọn (format: "Header-Name: value").
    /// VD: "X-Secret: my_secret_token" hoặc "Authorization: Bearer token123".
    /// </summary>
    public string SecretHeader { get; set; } = string.Empty;

    /// <summary>Nếu true, chỉ gửi webhook khi tin nhắn có OTP. Nếu false, gửi cho tất cả SMS khớp filter.</summary>
    public bool OtpOnly { get; set; } = true;
}
