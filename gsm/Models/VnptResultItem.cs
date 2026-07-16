using System;

namespace gsm.Models;

public class VnptResultItem
{
    public DateTime Time { get; set; }
    public string Port { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Success { get; set; }
    public string Response { get; set; } = "";
}
