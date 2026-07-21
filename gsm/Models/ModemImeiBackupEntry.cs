namespace gsm.Models;

/// <summary>
/// Latest IMEI observed on a modem immediately before a Create New IMEI action.
/// This sheet is used when no SIM/CCID is available yet.
/// </summary>
public class ModemImeiBackupEntry
{
    public string PortName { get; set; } = string.Empty;
    public string Imei { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string HardwareName { get; set; } = string.Empty;
    public string ModemManufacturer { get; set; } = string.Empty;
    public string ModemModel { get; set; } = string.Empty;
    public string ModemFirmware { get; set; } = string.Empty;
    public string SourceFile { get; set; } = "imei_backup.xlsx";
}
