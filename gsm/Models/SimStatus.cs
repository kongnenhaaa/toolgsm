namespace gsm.Models;

public static class SimStatus
{
    public const string Active = "Đang hoạt động";
    public const string Connecting = "Đang kết nối...";
    public const string NoResponse = "Không phản hồi";
    public const string ImeiError = "Lỗi IMEI";
    public const string SecurityBlocked = "Chặn bảo mật";
    public const string Quarantined = "Đã cách ly";
    /// <summary>SIM mới chưa có trong kho backup, đang chờ user chấp nhận thủ công.</summary>
    public const string WaitingAccept = "Chờ chấp nhận";
}
