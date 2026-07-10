using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SmsMessage : ObservableObject
{
    public string Sender { get; set; } = string.Empty;
    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    private string _receivedTime = string.Empty;
    public string ReceivedTime
    {
        get => _receivedTime;
        set => SetProperty(ref _receivedTime, value);
    }

    [ObservableProperty]
    private string _receiverPhone = string.Empty;

    private string _otp = string.Empty;
    public string Otp
    {
        get => _otp;
        set => SetProperty(ref _otp, value);
    }
    public string PortName { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string CallCount { get; set; } = string.Empty;
    public string ForwardContent { get; set; } = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    private string _audioFilePath = string.Empty;
    public string AudioFilePath
    {
        get => _audioFilePath;
        set
        {
            if (SetProperty(ref _audioFilePath, value))
            {
                OnPropertyChanged(nameof(HasAudio));
            }
        }
    }

    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioFilePath) && System.IO.File.Exists(AudioFilePath);
}
