using System;
using System.Collections.Generic;

namespace gsm.Models;

public class ApiSmsRequest
{
    public string? MachineId { get; set; }
    public string PortId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Content { get; set; } = "";
    public string Type { get; set; } = "sms";
    public string? CommandId { get; set; }
}

public class ApiSmsResponse
{
    public bool Ok { get; set; }
    public string? CommandId { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class ApiPortDto
{
    public string PortId { get; set; } = "";
    public string PortName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Phone { get; set; }
    public string? Operator { get; set; }
    public string? Balance { get; set; }
    public string? Imei { get; set; }
    public string? Ccid { get; set; }
    public string? Otp { get; set; }
    public string? LastContent { get; set; }
    public string? UpdatedAt { get; set; }
}

public class ApiPortsResponse
{
    public string MachineId { get; set; } = "";
    public List<ApiPortDto> Ports { get; set; } = new();
    public DateTime Time { get; set; } = DateTime.UtcNow;
}
