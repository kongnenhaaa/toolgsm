using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SmsMessage : ObservableObject
{
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ReceivedTime { get; set; } = string.Empty;
    [ObservableProperty]
    private string _receiverPhone = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string CallCount { get; set; } = string.Empty;
    public string ForwardContent { get; set; } = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;
}
