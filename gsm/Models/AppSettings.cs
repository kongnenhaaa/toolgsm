using System;

namespace gsm.Models;

public class AppSettings
{
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatIds { get; set; } = "";
    public string TelegramChatId { get; set; } = ""; // Mapped from TelegramChatIds or standalone
    public bool TelegramOnOtp { get; set; } = true;
    public bool TelegramOnSms { get; set; } = false;
    public bool PushOtpToWeb { get; set; } = true;
    public string OtpWebhookUrl { get; set; } = "";
    public bool SoundOnOtp { get; set; } = true;
    public bool SoundOnSms { get; set; } = true;

    public bool ReceiveAllSms { get; set; } = true;
    public bool EnableTelegramNotification { get => true; set { } }
    public bool EnableWebNotification { get => true; set { } }
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
    public bool EnableAutoAnswer { get => true; set { } }

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

    /// <summary>Đường dẫn file .wav phát đi khi đối phương nhận cuộc gọi.</summary>
    public string SoundCallOutPath { get; set; } = @"C:\Users\congn\Downloads\otp_947523_giong_nu\otp_947523_giong_nu.wav";

    // ========== WEBHOOK RULES ==========
    /// <summary>Danh sách các quy tắc tự động forward OTP/SMS qua HTTP webhook.</summary>
    public System.Collections.Generic.List<WebhookRule> WebhookRules { get; set; } = new();


    // ========== IMEI BACKUP & RESTORE ==========
    public bool BlockUnknownSims { get => true; set { } }

    public bool EnableImeiRestore { get => true; set { } }
    public bool EnableNewSimIntakeMode { get => true; set { } }

    // ========== COLUMN VISIBILITY SETTINGS ==========
    public bool ShowColStt { get; set; } = true;
    public bool ShowColPort { get; set; } = true;
    public bool ShowColDevice { get; set; } = true;
    public bool ShowColImei { get; set; } = true;
    public bool ShowColSerial { get; set; } = true;
    public bool ShowColPhone { get; set; } = true;
    public bool ShowColBalance { get; set; } = true;
    public bool ShowColOtp { get; set; } = true;
    public bool ShowColStatus { get; set; } = true;
    public bool ShowColContent { get; set; } = true;
    public bool ShowColCreatedAt { get; set; } = true;
    public bool ShowColConnect { get; set; } = true;
    public bool ShowColHealth { get; set; } = true;
    public bool ShowColTimeout { get; set; } = false;
    public bool ShowColSmsError { get; set; } = false;
    public bool ShowColReconnect { get; set; } = false;
    public bool ShowColLastSms { get; set; } = false;
    public bool ShowColLastError { get; set; } = false;
    public bool ShowColSignal { get; set; } = true;
    public bool ShowColProvider { get; set; } = true;
    public bool ShowColExpiry { get; set; } = true;
    public bool ShowColForward { get; set; } = true;
    public bool ShowColUpdatedAt { get; set; } = false;
    public bool ShowColSender { get; set; } = false;
    public bool ShowColLastReceived { get; set; } = false;
}
