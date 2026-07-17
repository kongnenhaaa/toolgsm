using System;

namespace gsm.Models;

public class AppSettings
{
    public bool DarkMode { get; set; } = false;

    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatIds { get; set; } = "";
    public string TelegramChatId { get; set; } = ""; // Mapped from TelegramChatIds or standalone
    public bool TelegramOnOtp { get; set; } = true;
    public bool TelegramOnSms { get; set; } = false;
    public bool PushOtpToWeb { get; set; } = true;
    public string OtpWebhookUrl { get; set; } = "";
    public bool SoundOnOtp { get; set; } = true;
    public bool SoundOnSms { get; set; } = true;
    public bool SoundOnCall { get; set; } = true;

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

    // Legacy settings retained only so old settings files can still be imported.
    // No HTTP server is started by the application.
    public bool EnableApiServer { get; set; } = false;
    public int ApiServerPort { get; set; } = 5000;
    
    // Web to GSM Bridge & Firebase
    public string MachineId { get; set; } = Environment.MachineName;
    public string FirebaseDbUrl { get; set; } = "https://toolweb-c7702-default-rtdb.firebaseio.com/";
    public string FirebaseAuthToken { get; set; } = "";
    public bool WriteOtpToFirebase { get; set; } = true;

    // Tự động bắt máy và Ghi âm STT
    public bool EnableAutoAnswer { get => AutoAnswerIncoming; set => AutoAnswerIncoming = value; }
    public bool AutoAnswerIncoming { get; set; } = true;
    public bool RecordIncoming { get; set; } = true;
    public bool SttIncoming { get; set; } = true;
    public string SttEngine { get; set; } = "whisper";    // whisper | windows
    public string WhisperApiUrl { get; set; } = "http://127.0.0.1:8080/inference";


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
    public string SoundCallOutPath { get; set; } = "";

    // ========== WEBHOOK RULES ==========
    /// <summary>Danh sách các quy tắc tự động forward OTP/SMS qua HTTP webhook.</summary>
    public System.Collections.Generic.List<WebhookRule> WebhookRules { get; set; } = new();


    // ========== IMEI BACKUP & RESTORE ==========
    public bool BlockUnknownSims { get => BlockUnknown; set => BlockUnknown = value; }

    public bool EnableImeiRestore { get => AutoRestoreImei; set => AutoRestoreImei = value; }
    public bool EnableNewSimIntakeMode { get => NewSimIntake; set => NewSimIntake = value; }

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

    // Extra Settings from settings.json UI
    public bool NewSimIntake { get; set; } = true;
    public bool AutoRestoreImei { get; set; } = true;
    public bool BlockUnknown { get; set; } = false;
    public bool AutoAccept { get; set; } = false;
    public bool Prefer4G { get; set; } = true;
    public bool EnableVolte { get; set; } = false;
    public bool AutoRecovery { get; set; } = true;
    public bool ForceCfun1AfterReboot { get; set; } = false;
    public int RecoveryThreshold { get; set; } = 12;
    public int WatchdogSeconds { get; set; } = 50;
    public bool AutoCheckBalanceAfterSms { get; set; } = true;
    public bool AutoUssdOnNetwork { get; set; } = true;
    public int UssdRetrySeconds { get; set; } = 35;
    public int SmsTimeout { get; set; } = 30000;
    public bool TelegramOnCall { get; set; } = true;
    public bool TelegramOnError { get; set; } = true;
    public int BaudRate { get; set; } = 115200;
    public int CommandTimeout { get; set; } = 10000;
    public bool EnableLog { get; set; } = true;
    public bool AutoConnectOnStart { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    // Custom Columns and Ports filters
    public System.Collections.Generic.List<string> ColumnOrder { get; set; } = new();
    public System.Collections.Generic.List<string> HiddenColumns { get; set; } = new();
    public System.Collections.Generic.List<string> HiddenComPorts { get; set; } = new();
}
