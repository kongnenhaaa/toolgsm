namespace gsm.Models;

public class SimBackupEntry
{
    public string Ccid { get; set; } = string.Empty;
    public string Imei { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string LicenseKeySuffix { get; set; } = string.Empty;
    public string KeyMismatch { get; set; } = string.Empty;
}
