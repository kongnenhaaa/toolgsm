namespace gsm.Models;

public class SimBackupEntry
{
    public string Ccid { get; set; } = string.Empty;
    public string Imei { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;

    public string SourceFile { get; set; } = string.Empty;
    public string SimRegDate { get; set; } = string.Empty;
    public string Lock1C { get; set; } = string.Empty;
    public string Lock2C { get; set; } = string.Empty;
}
