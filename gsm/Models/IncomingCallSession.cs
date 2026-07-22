using System;

namespace gsm.Models;

public class IncomingCallSession
{
    public string Port { get; set; } = "";
    public string Caller { get; set; } = "";
    public DateTime RingAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
}
