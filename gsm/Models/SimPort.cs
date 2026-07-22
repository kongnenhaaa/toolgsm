using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SimPort : ObservableObject
{
    public string PortName { get; set; } = string.Empty;
    
    public int PhysicalIndex { get; set; } = int.MaxValue;
    
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
    private int _stt;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsRebooting { get; set; } = false;

    [ObservableProperty]
    private string _phoneNumber = string.Empty;
    
    private string _networkProvider = string.Empty;
    public string NetworkProvider
    {
        get => _networkProvider;
        set
        {
            string val = value ?? string.Empty;
            string upper = val.ToUpperInvariant();
            
            if (upper.Contains("VINAPHONE VINAPHONE")) val = "VinaPhone";
            else if (upper.Contains("VINAPHONE") || upper.Contains("VINA")) val = "VinaPhone";
            else if (upper.Contains("VIETTEL")) val = "Viettel";
            else if (upper.Contains("MOBIFONE") || upper.Contains("MOBI")) val = "MobiFone";
            else if (upper.Contains("VIETNAMOBILE") || upper.Contains("VNM")) val = "Vietnamobile";
            else if (upper.Contains("GMOBILE")) val = "Gmobile";
            else if (upper.Contains("WINTEL")) val = "Wintel";
            else if (upper.Contains("ITELECOM") || upper.Contains("ITEL")) val = "iTel";
            else if (val == "45204") val = "Viettel";
            else if (val == "45202") val = "VinaPhone";
            else if (val == "45201") val = "MobiFone";
            else if (val == "45205") val = "Vietnamobile";
            
            SetProperty(ref _networkProvider, val);
        }
    }
    [ObservableProperty]
    private string _lastReceivedTime = string.Empty;
    
    [ObservableProperty]
    private string _lastSweepTime = string.Empty;
    
    [ObservableProperty]
    private string _otp = string.Empty;
    
    [ObservableProperty]
    private string _lastMessageContent = string.Empty;

    [ObservableProperty]
    private string _vnptStatus = string.Empty;

    [ObservableProperty]
    private string _sender = string.Empty;
    
    private string _status = "Chờ cắm SIM";
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                StatusChangedAt = DateTime.Now;
            }
        }
    }
    public DateTime StatusChangedAt { get; set; } = DateTime.Now;

    [ObservableProperty]
    private string _lastCommandResult = string.Empty;

    public string LastUssdResult { get; set; } = string.Empty;
    public string LastSmsResult { get; set; } = string.Empty;
    [ObservableProperty]
    private string _lastSmsSender = string.Empty;
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

    public string HealthSummary => $"TO:{TimeoutCount}  SMS:{SmsErrorCount}  RC:{ReconnectCount}";

    partial void OnTimeoutCountChanged(int value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnSmsErrorCountChanged(int value) => OnPropertyChanged(nameof(HealthSummary));
    partial void OnReconnectCountChanged(int value) => OnPropertyChanged(nameof(HealthSummary));
    
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

    [ObservableProperty]
    private string _modemManufacturer = string.Empty;

    [ObservableProperty]
    private string _modemModel = string.Empty;

    [ObservableProperty]
    private string _modemFirmware = string.Empty;

    [ObservableProperty]
    private string _modemCapabilities = string.Empty;

    public string DeviceDisplayName =>
        !string.IsNullOrWhiteSpace(ModemModel)
            ? $"{ModemManufacturer} {ModemModel}".Trim()
            : !string.IsNullOrWhiteSpace(HardwareName)
            ? HardwareName
            : (!string.IsNullOrWhiteSpace(DeviceName) ? DeviceName : "GSM Modem");

    partial void OnDeviceNameChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    partial void OnHardwareNameChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    partial void OnModemManufacturerChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    partial void OnModemModelChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    
    [ObservableProperty]
    private string _serial = string.Empty;

    partial void OnSerialChanged(string value) => OnPropertyChanged(nameof(HasData));
    
    public bool HasData => !string.IsNullOrEmpty(Imei) || !string.IsNullOrEmpty(Serial);
    
    [ObservableProperty]
    private string _balance = string.Empty;

    [ObservableProperty]
    private bool _isBalanceLoading;
    
    [ObservableProperty]
    private string _expiryDate = string.Empty;
    
    [ObservableProperty]
    private string _updatedAt = string.Empty;
    
    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private string _simRegDate = string.Empty;

    [ObservableProperty]
    private string _simType = string.Empty;

    [ObservableProperty]
    private string _lock1C = string.Empty;

    [ObservableProperty]
    private string _lock2C = string.Empty;

    
    
    // New Pro Features
    [ObservableProperty]
    private int _signalStrength = 0; // 0 to 100

    [ObservableProperty]
    private int _signalRssi = 99; // Raw +CSQ RSSI: 0..31, 99 = unknown

    [ObservableProperty]
    private DateTime? _lastSignalScanAt;

    public string LastSignalScanDisplay => LastSignalScanAt?.ToString("HH:mm:ss") ?? string.Empty;

    public string SignalDisplay => SignalRssi switch
    {
        99 => string.Empty,
        >= 20 and <= 31 => $"GOOD {SignalRssi}",
        >= 15 => $"NORMAL {SignalRssi}",
        >= 1 => $"WEAK {SignalRssi}",
        _ => "NO SIGNAL"
    };

    partial void OnSignalRssiChanged(int value) => OnPropertyChanged(nameof(SignalDisplay));
    partial void OnLastSignalScanAtChanged(DateTime? value) => OnPropertyChanged(nameof(LastSignalScanDisplay));

    [ObservableProperty]
    private string _networkType = string.Empty;

    [ObservableProperty]
    private string _forwardedTo = string.Empty; // SĐT đang được chuyển hướng cuộc gọi đến

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
