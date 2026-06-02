using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SimPort : ObservableObject
{
    public string PortName { get; set; } = string.Empty;
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

    [ObservableProperty]
    private string _sender = string.Empty;
    
    [ObservableProperty]
    private string _status = "Active"; // Active, Inactive, Error
    
    // Additional properties based on UI
    public int CallCount { get; set; }
    public int ForwardCount { get; set; }
    
    // Tab 2 Info
    [ObservableProperty]
    private string _imei = string.Empty;
    
    [ObservableProperty]
    private string _serial = string.Empty;
    
    [ObservableProperty]
    private string _balance = string.Empty;
    
    [ObservableProperty]
    private string _expiryDate = string.Empty;
    
    [ObservableProperty]
    private string _updatedAt = string.Empty;
    
    // New Pro Features
    [ObservableProperty]
    private int _signalStrength = 0; // 0 to 100
}
