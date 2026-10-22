namespace gsm.Models;

public sealed class DmxResultItem
{
    public DateTime Time { get; set; }
    public string Port { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly BirthDate { get; set; }
    public string Password { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool IsPasswordRecovery { get; set; }
    public string Response { get; set; } = string.Empty;
}
