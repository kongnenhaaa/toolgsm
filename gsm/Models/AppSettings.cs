using System;

namespace gsm.Models;

public class AppSettings
{
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatIds { get; set; } = "";
    public bool ReceiveAllSms { get; set; } = true;
    public bool EnableTelegramNotification { get; set; } = true;
    public bool EnableWebNotification { get; set; } = true;
    public string FirebaseUrl { get; set; } = "";
    
    // Tự động chuyển hướng cuộc gọi
    public bool EnableAutoCallForwarding { get; set; } = false;
    public string ForwardPhoneNumber { get; set; } = "";

    // Whitelist/Blacklist người gửi SMS (phân cách bằng dấu phẩy)
    public bool EnableSenderBlacklist { get; set; } = false;
    public string SenderBlacklist { get; set; } = "";  // VD: "QCBMB, spam123, 1900xxxx"

    public bool EnableSenderWhitelist { get; set; } = false;
    public string SenderWhitelist { get; set; } = "";  // Chỉ nhận từ các số này
    
    // HTTP API Server
    public bool EnableApiServer { get; set; } = true;
    public int ApiServerPort { get; set; } = 8080;


    // Tự động bắt máy
    public bool EnableAutoAnswer { get; set; } = true;

    // Tự động Watchdog (Khởi động lại modem khi lỗi)
    public bool EnableAutoWatchdog { get; set; } = true;

    // ========== SOUND ALERT ==========
    /// <summary>Bật/tắt toàn bộ âm thanh cảnh báo.</summary>
    public bool EnableSoundAlert { get; set; } = true;

    /// <summary>Bật/tắt âm thanh mặc định của Windows Toast Notification.</summary>
    public bool EnableToastSound { get; set; } = true;


    /// <summary>Đường dẫn file .wav khi nhận OTP. Để trống = dùng âm hệ thống.</summary>
    public string SoundOtpPath { get; set; } = "";

    /// <summary>Đường dẫn file .wav khi nhận SMS thường. Để trống = dùng âm hệ thống.</summary>
    public string SoundSmsPath { get; set; } = "";

    /// <summary>Đường dẫn file .wav khi có cuộc gọi đến. Để trống = dùng âm hệ thống.</summary>
    public string SoundCallPath { get; set; } = "";

    // ========== WEBHOOK RULES ==========
    /// <summary>Danh sách các quy tắc tự động forward OTP/SMS qua HTTP webhook.</summary>
    public System.Collections.Generic.List<WebhookRule> WebhookRules { get; set; } = new();



}
