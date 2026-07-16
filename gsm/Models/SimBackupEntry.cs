namespace gsm.Models;

public class SimBackupEntry
{
    public string Ccid { get; set; } = string.Empty;
    public string Imei { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string Balance { get; set; } = string.Empty;
    public string PromotionBalance { get; set; } = string.Empty;
    public string ExpiryDate { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    public string SourceFile { get; set; } = string.Empty;
    public string SimRegDate { get; set; } = string.Empty;
    public string Lock1C { get; set; } = string.Empty;
    public string Lock2C { get; set; } = string.Empty;
    public string LastPortName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string HardwareName { get; set; } = string.Empty;
    public string ModemManufacturer { get; set; } = string.Empty;
    public string ModemModel { get; set; } = string.Empty;
    public string ModemFirmware { get; set; } = string.Empty;
    public string ModemCapabilities { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int SignalStrength { get; set; }
}
