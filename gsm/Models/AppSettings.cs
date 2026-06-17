using System;

namespace gsm.Models;

public class AppSettings
{
    public string TelegramBotToken { get; set; } = "8926115937:AAFpUEvxfFqRpwGDWChbEQEWsn6xkZ-RTCQ";
    public string TelegramChatIds { get; set; } = "-1003586587027";
    public bool ReceiveAllSms { get; set; } = false;
    public bool EnableTelegramNotification { get; set; } = true;
    public bool EnableWebNotification { get; set; } = true;
    public string FirebaseUrl { get; set; } = "https://toolweb-c7702-default-rtdb.firebaseio.com/";
    
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

    // Ping SIM
    public bool EnableSimPing { get; set; } = true;
    public int SimPingIntervalMinutes { get; set; } = 5;

    // Tự học IMEI
    public bool AllowAutoLearningImei { get; set; } = false;
}
