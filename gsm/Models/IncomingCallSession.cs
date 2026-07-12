using System;

namespace gsm.Models;

public class IncomingCallSession
{
    public string Port { get; set; } = "";
    public string Caller { get; set; } = "";
    public DateTime RingAt { get; set; } = DateTime.Now;
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? LocalWavPath { get; set; }
    public string? Transcript { get; set; }
    public string? Otp { get; set; }
    public bool Recording { get; set; }
}
