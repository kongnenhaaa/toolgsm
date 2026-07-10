using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SimPort : ObservableObject
{
    public string PortName { get; set; } = string.Empty;
    
    public int PortNumber
    {
        get
        {
            if (string.IsNullOrEmpty(PortName)) return int.MaxValue;
            var match = System.Text.RegularExpressions.Regex.Match(PortName, @"\d+");
            return match.Success ? int.Parse(match.Value) : int.MaxValue;
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    public bool IsRebooting { get; set; } = false;

    [ObservableProperty]
    private string _phoneNumber = string.Empty;
    
    [ObservableProperty]
    private string _networkProvider = string.Empty;
    [ObservableProperty]
    private string _lastReceivedTime = string.Empty;
    
    
    [ObservableProperty]
    private string _otp = string.Empty;
    
    [ObservableProperty]
    private string _lastMessageContent = string.Empty;

    private string _lastAudioFilePath = string.Empty;
    public string LastAudioFilePath
    {
        get => _lastAudioFilePath;
        set
        {
            if (SetProperty(ref _lastAudioFilePath, value))
            {
                OnPropertyChanged(nameof(HasAudio));
            }
        }
    }

    public bool HasAudio => !string.IsNullOrWhiteSpace(LastAudioFilePath) && System.IO.File.Exists(LastAudioFilePath);

    [ObservableProperty]
    private string _sender = string.Empty;
    
    [ObservableProperty]
    private string _status = "Active"; // Active, Inactive, Error

    [ObservableProperty]
    private string _lastCommandResult = string.Empty;

    public string LastUssdResult { get; set; } = string.Empty;
    public string LastSmsResult { get; set; } = string.Empty;
    public string LastCallResult { get; set; } = string.Empty;
    public string LastMmsResult { get; set; } = string.Empty;
    public string LastImeiResult { get; set; } = string.Empty;
    public string LastDataResult { get; set; } = string.Empty;
    public string LastDelayResult { get; set; } = string.Empty;

    public void UpdateDisplayResult(string currentTab)
    {
        if (currentTab == "USSD") LastCommandResult = LastUssdResult;
        else if (currentTab == "SMS") LastCommandResult = LastSmsResult;
        else if (currentTab == "Call") LastCommandResult = LastCallResult;
        else if (currentTab == "MMS") LastCommandResult = LastMmsResult;
        else if (currentTab == "IMEI") LastCommandResult = LastImeiResult;
        else if (currentTab == "Data") LastCommandResult = LastDataResult;
        else if (currentTab == "Delay" || currentTab == "Trễ") LastCommandResult = LastDelayResult;
        else LastCommandResult = "";
    }

    [ObservableProperty]
    private int _timeoutCount;

    [ObservableProperty]
    private int _smsErrorCount;

    [ObservableProperty]
    private int _reconnectCount;

    [ObservableProperty]
    private string _lastSmsSentAt = string.Empty;

    [ObservableProperty]
    private string _lastError = string.Empty;


    
    [ObservableProperty]
    private int _callCount = 0;
    
    [ObservableProperty]
    private int _forwardCount = 0;

    [ObservableProperty]
    private string _promotionBalance = string.Empty;
    // Tab 2 Info
    [ObservableProperty]
    private string _imei = string.Empty;
    
    partial void OnImeiChanged(string value) => OnPropertyChanged(nameof(HasData));

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _hardwareName = string.Empty;

    public string DeviceDisplayName =>
        !string.IsNullOrWhiteSpace(HardwareName)
            ? HardwareName
            : (!string.IsNullOrWhiteSpace(DeviceName) ? DeviceName : "GSM Modem");

    partial void OnDeviceNameChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    partial void OnHardwareNameChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    
    [ObservableProperty]
    private string _serial = string.Empty;

    partial void OnSerialChanged(string value) => OnPropertyChanged(nameof(HasData));
    
    public bool HasData => !string.IsNullOrEmpty(Imei) || !string.IsNullOrEmpty(Serial);
    
    [ObservableProperty]
    private string _balance = string.Empty;
    
    [ObservableProperty]
    private string _expiryDate = string.Empty;
    
    [ObservableProperty]
    private string _updatedAt = string.Empty;
    
    [ObservableProperty]
    private string _createdAt = string.Empty;

    // New Pro Features
    [ObservableProperty]
    private int _signalStrength = 0; // 0 to 100

    // #8: true nếu hết hạn trong vòng 7 ngày
    public bool IsExpiringSoon
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExpiryDate)) return false;
            foreach (var fmt in new[] { "dd/MM/yy", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy" })
            {
                if (DateTime.TryParseExact(ExpiryDate, fmt, null,
                    System.Globalization.DateTimeStyles.None, out var expiry))
                    return (expiry - DateTime.Today).TotalDays <= 7;
            }
            return false;
        }
    }
}
