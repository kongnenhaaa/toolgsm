namespace gsm.Models;

/// <summary>
/// Latest IMEI observed on a modem immediately before a Create New IMEI action.
/// This sheet is used when no SIM/CCID is available yet.
/// </summary>
public class ModemImeiBackupEntry
{
    public string PortName { get; set; } = string.Empty;
    public string Imei { get; set; } = string.Empty;
}
