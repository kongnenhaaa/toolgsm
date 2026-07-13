using System;

namespace gsm.Models;

/// <summary>
/// Mẫu (Profile) cấu hình phần cứng của một điện thoại cụ thể.
/// </summary>
public class DeviceProfile
{
    public string Brand   { get; set; } = string.Empty;
    public string Model   { get; set; } = string.Empty;
    /// <summary>Mã Type Allocation Code (TAC) - 8 số đầu của IMEI đặc trưng cho dòng máy này.</summary>
    public string TacCode { get; set; } = string.Empty;

    public string DisplayName => $"{Brand} {Model}";
}

/// <summary>
/// Định danh ảo gắn với một SIM card (theo CCID) — KHÔNG theo cổng COM.
/// Lý do: SIM có thể được di chuyển giữa các cổng. IMEI phải nhất quán theo SIM,
/// không được thay đổi khi cắm lại vào cổng khác, vì nhà mạng ghi nhớ lịch sử IMEI theo SIM.
/// </summary>
public class DeviceIdentity
{
    /// <summary>
    /// CCID (Integrated Circuit Card Identifier) của SIM — là khóa chính duy nhất.
    /// Một SIM dù cắm vào bất kỳ cổng COM nào thì CCID vẫn không đổi.
    /// </summary>
    public string Ccid { get; set; } = string.Empty;

    /// <summary>
    /// Cổng COM hiện tại SIM đang cắm vào (chỉ dùng để hiển thị, không phải khóa).
    /// </summary>
    public string LastPortName { get; set; } = string.Empty;

    /// <summary>Tên hiển thị của máy ảo (VD: Apple iPhone 14 Pro Max)</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>IMEI ảo đã được cấp cho SIM này — cố định vĩnh viễn theo CCID</summary>
    public string AssignedImei { get; set; } = string.Empty;

    /// <summary>Thương hiệu (VD: Apple, Samsung)</summary>
    public string Brand { get; set; } = string.Empty;

    /// <summary>Model (VD: iPhone 14 Pro Max)</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Ngày tạo (lần đầu cắm SIM này)</summary>
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Lần cuối cùng xác nhận IMEI đã được ghi thành công vào chip</summary>
    public string LastAppliedAt { get; set; } = string.Empty;

    /// <summary>
    /// Số điện thoại của SIM này (để giúp người dùng nhận biết khi nhìn vào backup).
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;
}
