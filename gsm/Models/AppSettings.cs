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
    
    // Thêm cấu hình tự động chuyển hướng cuộc gọi
    public bool EnableAutoCallForwarding { get; set; } = false;
    public string ForwardPhoneNumber { get; set; } = "";
}
