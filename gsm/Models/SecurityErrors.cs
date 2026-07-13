namespace gsm.Models;

public static class SecurityErrors
{
    public const string WrongImei = "Sai IMEI";
    public const string ReadCcidFailed = "Lỗi đọc SIM CCID";
    public const string RadioOffFailed = "Không tắt được sóng trước khi ghi IMEI";
    public const string RadioOnFailed = "Không bật lại được sóng sau khi ghi IMEI";
}
