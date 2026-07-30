namespace gsm.Models;

public sealed record ComTableColumnDefinition(string Name, string Header);

public static class ComTableColumnCatalog
{
    public static IReadOnlyList<ComTableColumnDefinition> Default { get; } =
    [
        new("Stt", "STT"),
        new("PortName", "Cổng"),
        new("Status", "Trạng thái"),
        new("NetworkProvider", "Nhà mạng"),
        new("NetworkType", "Mạng"),
        new("Signal", "Tín hiệu"),
        new("LastSignalScan", "Quét sóng lúc"),
        new("Balance", "TKC"),
        new("PhoneNumber", "SĐT"),
        new("SimType", "Loại SIM"),
        new("Imei", "IMEI"),
        new("Serial", "CCID"),
        new("ExpiryDate", "HSD"),
        new("SimRegDate", "Ngày ĐK SIM"),
        new("Lock1C", "Khóa 1C"),
        new("Lock2C", "Khóa 2C"),
        new("ForwardedTo", "Chuyển tiếp"),
        new("VnptStatus", "Trạng thái VNPT"),
        new("Otp", "OTP"),
        new("LastSmsSender", "Người gửi SMS"),
        new("LastMessageContent", "Nội dung")
    ];

}
