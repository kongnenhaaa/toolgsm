namespace gsm.Models;

public class SmsMessage
{
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ReceivedTime { get; set; } = string.Empty;
    public string ReceiverPhone { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string CallCount { get; set; } = string.Empty;
    public string ForwardContent { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
