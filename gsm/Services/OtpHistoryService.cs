using System;
using System.Collections.Generic;

namespace gsm.Services;

/// <summary>
/// Compatibility facade. OTP history is deliberately session-only and owned
/// by MainViewModel; this type performs no file I/O.
/// </summary>
public static class OtpHistoryService
{
    /// <summary>
    /// Thêm một bản ghi OTP mới và tự động dọn dẹp bản ghi cũ hơn 10 ngày.
    /// </summary>
    public static void Append(string port, string simPhone, string sender, string otp, string content)
    {
        // Intentionally no-op: do not persist OTP/SMS content.
    }

    /// <summary>
    /// Trả về N bản ghi OTP gần nhất (dùng cho REST API).
    /// </summary>
    public static List<OtpRecord> GetRecent(int count = 50)
    {
        return new List<OtpRecord>();
    }
}

public class OtpRecord
{
    public string Timestamp { get; set; } = string.Empty;
    public string Port      { get; set; } = string.Empty;
    public string SimPhone  { get; set; } = string.Empty;
    public string Sender    { get; set; } = string.Empty;
    public string Otp       { get; set; } = string.Empty;
    public string Content   { get; set; } = string.Empty;
}
