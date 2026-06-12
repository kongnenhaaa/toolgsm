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
    
    [ObservableProperty]
    private int _callCount = 0;
    
    [ObservableProperty]
    private int _forwardCount = 0;

    [ObservableProperty]
    private string _promotionBalance = string.Empty;
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
