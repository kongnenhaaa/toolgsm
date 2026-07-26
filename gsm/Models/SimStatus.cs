namespace gsm.Models;

public static class SimStatus
{
    public const string Active = "Đang hoạt động";
    public const string Connecting = "Đang kết nối...";
    public const string NoResponse = "Không phản hồi";
    public const string ImeiError = "Lỗi IMEI";
    public const string SecurityBlocked = "Chặn bảo mật";
    /// <summary>SIM mới chưa có trong kho backup, đang chờ user chấp nhận thủ công.</summary>
    public const string WaitingAccept = "Chờ chấp nhận";
    /// <summary>
    /// IMEI đã ghi và xác minh xong nhưng modem không đăng ký được nhà mạng
    /// sau khi đã dùng hết ngân sách tự phục hồi. Trạng thái kết thúc, không
    /// còn spinner "Đang xử lý"; user tự bấm Làm mới khi muốn thử lại.
    /// </summary>
    public const string NetworkUnavailable = "Không có nhà mạng";
}
