using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class SimPort : ObservableObject
{
    public string PortName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string LastReceivedTime { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string LastMessageContent { get; set; } = string.Empty;
    
    [ObservableProperty]
    private string _status = "Active"; // Active, Inactive, Error
    
    // Additional properties based on UI
    public int CallCount { get; set; }
    public int ForwardCount { get; set; }
    
    // Tab 2 Info
    public string Imei { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    
    [ObservableProperty]
    private string _balance = string.Empty;
    
    public string ExpiryDate { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    
    // New Pro Features
    [ObservableProperty]
    private int _signalStrength = 0; // 0 to 100
}
