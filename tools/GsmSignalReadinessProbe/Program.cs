// GsmSignalReadinessProbe — READ-ONLY AT probe for Quectel EC20 (and similar)
// after IMEI acceptance. Goal: report whether the firmware configuration is
// already friendly to fast carrier acquisition on Vietnamese carriers
// (Vinaphone 111# / MobiFone 101#), without mutating any persistent state.
//
// We never call: AT+CFUN=anything, AT+COPS=anything, AT+QCFG="...",write,
// AT+EGMR=anything, AT+CUSD=anything, AT+QSIMDET/SIMDROP. Read-only queries only.
//
// Output: JSON file with per-port readiness verdict.

using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record ProbeCommand(string Key, string Command, int TimeoutMs);

internal sealed record ProbeResult(
    string Key,
    string Command,
    string Status,
    long ElapsedMs,
    string Response);

internal sealed class PortReadiness
{
    public string Port { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Opened { get; set; }
    public string? OpenError { get; set; }
    public string FirmwareRevision { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Imei { get; set; }
    public string? Ccid { get; set; }
    public string? Cim { get; set; }
    public string? Cpin { get; set; }
    public string? Qsimstat { get; set; }
    public string? Cfun { get; set; }
    public string? Csq { get; set; }
    public int? CsqRssi { get; set; }
    public int? CsqBer { get; set; }
    public string? Cops { get; set; }
    public string? Creg { get; set; }
    public string? Cgreg { get; set; }
    public string? Cereg { get; set; }
    public string? Qnwinfo { get; set; }
    public string? NwscanMode { get; set; }
    public string? NwscanSeq { get; set; }
    public string? Ims { get; set; }
    public string? Band { get; set; }
    public string? Ratacqorder { get; set; }
    public bool UrcPortUsb { get; set; }
    public List<string> Findings { get; set; } = new();
    public bool Ready { get; set; }
    public int Score { get; set; }
    public List<ProbeResult> Results { get; set; } = new();
}

internal static class Program
{
    private const int DefaultBaud = 115_200;

    // Read-only commands only. No state-changing AT commands allowed here.
    private static readonly ProbeCommand[] Commands =
    [
        new("identity",      "AT",                 2_000),
        new("revision",      "AT+CGMR",            2_000),
        new("manufacturer",  "AT+CGMI",            2_000),
        new("model",         "AT+CGMM",            2_000),
        new("imei",          "AT+GSN",             2_000),
        new("cfun",          "AT+CFUN?",           3_000),
        new("cpin",          "AT+CPIN?",           2_000),
        new("qsimstat",      "AT+QSIMSTAT?",       2_000),
        new("ccid",          "AT+QCCID",           2_000),
        new("cim",           "AT+CIMI",            2_000),
        new("csq",           "AT+CSQ",             3_000),
        new("cops",          "AT+COPS?",           3_000),
        new("creg",          "AT+CREG?",           3_000),
        new("cgreg",         "AT+CGREG?",          3_000),
        new("cereg",         "AT+CEREG?",          3_000),
        new("qnwinfo",       "AT+QNWINFO",         3_000),
        new("nwscanmode",    "AT+QCFG=\"nwscanmode\"", 3_000),
        new("nwscanseq",     "AT+QCFG=\"nwscanseq\"",  3_000),
        new("ims",           "AT+QCFG=\"ims\"",        3_000),
        new("band",          "AT+QCFG=\"band\"",       3_000),
        new("ratacqorder",   "AT+QCFG=\"ratacqorder\"",3_000),
        new("urcport",       "AT+QURCCFG=\"urcport\"", 3_000),
    ];

    public static async Task<int> Main(string[] args)
    {
        var ports = new List<string>();
        int start = 86, end = 104;
        string? output = null;
        int baud = DefaultBaud;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ports":
                    i++;
                    while (i < args.Length && !args[i].StartsWith("--")) ports.Add(args[i++]);
                    i--;
                    break;
                case "--range":
                    i++;
                    var range = args[i].Split('-');
                    start = int.Parse(range[0]);
                    end = int.Parse(range[1]);
                    break;
                case "--output":
                    i++;
                    output = args[i];
                    break;
                case "--baud":
                    i++;
                    baud = int.Parse(args[i]);
                    break;
            }
        }

        if (ports.Count == 0)
        {
            for (int n = start; n <= end; n++) ports.Add($"COM{n}");
        }

        output ??= Path.Combine(Environment.CurrentDirectory,
            $"signal-readiness-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        Console.WriteLine($"Probing {ports.Count} ports at {baud} baud (READ-ONLY)…");

        var reports = new List<PortReadiness>();
        foreach (var port in ports)
        {
            Console.WriteLine($"\n=== {port} ===");
            var report = await ProbeAsync(port, baud);
            reports.Add(report);
            Console.WriteLine($"  firmware={report.FirmwareRevision} sim={report.Cpin}/{report.Qsimstat} " +
                              $"cfun={report.Cfun} csq={report.Csq} cops={report.Cops} " +
                              $"ready={report.Ready} score={report.Score}");
            foreach (var f in report.Findings)
                Console.WriteLine($"    - {f}");
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(reports,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nReport: {Path.GetFullPath(output)}");

        return 0;
    }

    private static async Task<PortReadiness> ProbeAsync(string portName, int baud)
    {
        var report = new PortReadiness
        {
            Port = portName,
            StartedAt = DateTimeOffset.Now,
        };

        SerialPort? serial = null;
        try
        {
            serial = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 250,
                WriteTimeout = 5_000,
                DtrEnable = true,
                RtsEnable = true,
                NewLine = "\r\n",
                Encoding = Encoding.UTF8,
                WriteBufferSize = 1024,
            };
            serial.Open();
            await Task.Delay(250);
            _ = serial.ReadExisting();
            report.Opened = true;
        }
        catch (Exception ex)
        {
            report.OpenError = ex.Message;
            report.FinishedAt = DateTimeOffset.Now;
            return report;
        }

        try
        {
            foreach (var probe in Commands)
            {
                var result = await SendAsync(serial, probe);
                report.Results.Add(result);
                ExtractInto(result, report);

                string compact = Regex.Replace(result.Response ?? "", @"\s+", " ").Trim();
                if (compact.Length > 120) compact = compact[..120] + "…";
                Console.WriteLine($"  [{probe.Key,-11}] {probe.Command,-32} {result.Status,-12} {result.ElapsedMs,5}ms  {compact}");
                await Task.Delay(40);
            }
        }
        finally
        {
            try { serial.Close(); } catch { }
            serial.Dispose();
        }

        report.FinishedAt = DateTimeOffset.Now;
        Evaluate(report);
        return report;
    }

    private static async Task<ProbeResult> SendAsync(SerialPort serial, ProbeCommand probe)
    {
        _ = serial.ReadExisting();
        var sw = Stopwatch.StartNew();
        serial.Write(probe.Command + "\r\n");
        var sb = new StringBuilder();

        try
        {
            while (sw.ElapsedMilliseconds < probe.TimeoutMs)
            {
                string chunk;
                try { chunk = serial.ReadExisting(); }
                catch (TimeoutException) { chunk = string.Empty; }
                if (chunk.Length > 0)
                {
                    sb.Append(chunk);
                    if (HasTerminal(sb.ToString())) break;
                }
                await Task.Delay(20);
            }
        }
        catch (Exception ex)
        {
            sb.Append("[READ_ERROR:").Append(ex.Message).Append(']');
        }

        sw.Stop();
        string raw = sb.ToString().Replace("\0", string.Empty);
        string status = Classify(raw, sw.ElapsedMilliseconds >= probe.TimeoutMs);
        return new ProbeResult(probe.Key, probe.Command, status, sw.ElapsedMilliseconds, raw.Trim());
    }

    private static bool HasTerminal(string s) => Regex.IsMatch(
        s,
        @"(?:^|\r?\n)\s*(?:OK|ERROR|\+CME ERROR:[^\r\n]*|\+CMS ERROR:[^\r\n]*)\s*(?:\r?\n|$)",
        RegexOptions.IgnoreCase);

    private static string Classify(string s, bool timedOut)
    {
        if (Regex.IsMatch(s, @"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)", RegexOptions.IgnoreCase)) return "ok";
        if (s.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase)) return "cme-error";
        if (s.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase)) return "cms-error";
        if (Regex.IsMatch(s, @"(?:^|\r?\n)\s*ERROR\s*(?:\r?\n|$)", RegexOptions.IgnoreCase)) return "error";
        return timedOut ? "timeout" : "incomplete";
    }

    private static void ExtractInto(ProbeResult r, PortReadiness p)
    {
        string resp = r.Response ?? string.Empty;
        switch (r.Key)
        {
            case "manufacturer":
                p.Manufacturer = Regex.Match(resp, @"\+CGMI:\s*([^\r\n]+)").Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(p.Manufacturer))
                    p.Manufacturer = Regex.Replace(resp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.Contains("OK")) ?? "", @"\s+", " ").Trim();
                break;
            case "model":
                p.Model = Regex.Match(resp, @"\+CGMM:\s*([^\r\n]+)").Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(p.Model))
                    p.Model = Regex.Replace(resp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.Contains("OK")) ?? "", @"\s+", " ").Trim();
                break;
            case "revision":
                p.FirmwareRevision = Regex.Match(resp, @"(?im)Revision:\s*([^\r\n]+)").Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(p.FirmwareRevision))
                    p.FirmwareRevision = Regex.Replace(resp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.Contains("OK")) ?? "", @"\s+", " ").Trim();
                break;
            case "imei":
                p.Imei = FirstDigits(resp, 14, 16);
                break;
            case "ccid":
                p.Ccid = FirstDigits(resp, 18, 22);
                break;
            case "cim":
                p.Cim = FirstDigits(resp, 14, 16);
                break;
            case "cpin":
                p.Cpin = Regex.Match(resp, @"\+CPIN:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "qsimstat":
                p.Qsimstat = Regex.Match(resp, @"\+QSIMSTAT:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "cfun":
                p.Cfun = Regex.Match(resp, @"\+CFUN:\s*(\d+)").Groups[1].Value;
                break;
            case "csq":
                var m = Regex.Match(resp, @"\+CSQ:\s*(\d+)\s*,\s*(\d+)");
                if (m.Success)
                {
                    p.CsqRssi = int.Parse(m.Groups[1].Value);
                    p.CsqBer = int.Parse(m.Groups[2].Value);
                    p.Csq = $"{m.Groups[1].Value},{m.Groups[2].Value}";
                }
                else p.Csq = resp.Replace("\r", "").Replace("\n", " ").Trim();
                break;
            case "cops":
                p.Cops = Regex.Match(resp, @"\+COPS:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "creg":
                p.Creg = Regex.Match(resp, @"\+CREG:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "cgreg":
                p.Cgreg = Regex.Match(resp, @"\+CGREG:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "cereg":
                p.Cereg = Regex.Match(resp, @"\+CEREG:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "qnwinfo":
                p.Qnwinfo = Regex.Match(resp, @"\+QNWINFO:\s*([^\r\n]+)").Groups[1].Value.Trim();
                break;
            case "nwscanmode":
                p.NwscanMode = ParseQcfgValue(resp, "nwscanmode");
                break;
            case "nwscanseq":
                p.NwscanSeq = ParseQcfgValue(resp, "nwscanseq");
                break;
            case "ims":
                p.Ims = ParseQcfgValue(resp, "ims");
                break;
            case "band":
                p.Band = ParseQcfgValue(resp, "band");
                break;
            case "ratacqorder":
                p.Ratacqorder = ParseQcfgValue(resp, "ratacqorder");
                break;
            case "urcport":
            {
                var v = ParseQcfgValue(resp, "urcport");
                if (string.IsNullOrEmpty(v))
                    v = Regex.Match(resp, @"\+QURCCFG:\s*""urcport""\s*,\s*([^\r\n]+)").Groups[1].Value.Trim();
                p.UrcPortUsb = v.Contains("usb", StringComparison.OrdinalIgnoreCase)
                             || v.Equals("1", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
    }

    private static string FirstDigits(string s, int min, int max)
    {
        var m = Regex.Match(s, @"\d{" + min + @"," + max + @"}");
        return m.Success ? m.Value : string.Empty;
    }

    private static string ParseQcfgValue(string response, string key)
    {
        // +QCFG: "key",value
        var m = Regex.Match(response, "\\+QCFG:\\s*\"" + Regex.Escape(key) + "\"\\s*,\\s*([^,\\r\\n]+)");
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(response, "\\+QCFG:\\s*([^,\\r\\n]+)\\s*,\\s*([^,\\r\\n]+)");
        return m.Success && m.Groups[1].Value.Contains(key, StringComparison.OrdinalIgnoreCase)
            ? m.Groups[2].Value.Trim()
            : string.Empty;
    }

    private static void Evaluate(PortReadiness p)
    {
        int score = 0;
        var notes = new List<string>();

        if (!p.Opened) { notes.Add("PORT_OPEN_FAIL: " + p.OpenError); p.Ready = false; p.Score = -100; p.Findings = notes; return; }

        if (p.Cpin?.Contains("READY", StringComparison.OrdinalIgnoreCase) == true) score += 25;
        else notes.Add($"CPIN not READY: '{p.Cpin}'");

        if (p.Qsimstat?.StartsWith("1,", StringComparison.OrdinalIgnoreCase) == true) score += 10;
        else if (!string.IsNullOrEmpty(p.Qsimstat)) notes.Add($"QSIMSTAT not 1,x: '{p.Qsimstat}'");
        else notes.Add("QSIMSTAT empty (may be older firmware)");

        if (p.Cfun == "1") score += 15;
        else if (p.Cfun == "4") { notes.Add("CFUN=4 (airplane). Modem RF OFF."); }
        else if (p.Cfun == "0") { notes.Add("CFUN=0 (minimum functionality)."); }
        else if (!string.IsNullOrEmpty(p.Cfun)) notes.Add($"CFUN={p.Cfun} (unexpected).");
        else notes.Add("CFUN unknown.");

        // CREG/CGREG/CEREG must show registered/home (1,5,6,7) for at least one RAT.
        if (IsRegistered(p.Creg) || IsRegistered(p.Cgreg) || IsRegistered(p.Cereg)) score += 20;
        else notes.Add($"Not registered anywhere: CREG='{p.Creg}' CGREG='{p.Cgreg}' CEREG='{p.Cereg}'");

        if (p.CsqRssi is int rssi)
        {
            if (rssi == 99) notes.Add("CSQ=99 (unknown).");
            else if (rssi >= 10) score += 10;       // -81 dBm or stronger (CSQ 10 ≈ -81)
            else if (rssi >= 5) score += 5;         // -91 dBm
            else notes.Add($"CSQ weak ({rssi} ≈ {(rssi * 2) - 113} dBm).");
        }
        else notes.Add("CSQ unreadable.");

        // nwscanmode: 0 = auto (LTE/3G/2G) is friendliest for VN carriers.
        if (p.NwscanMode == "0") score += 10;
        else if (string.IsNullOrEmpty(p.NwscanMode)) notes.Add("nwscanmode empty (use default = auto).");
        else notes.Add($"nwscanmode={p.NwscanMode} (manual).");

        // nwscanseq must contain LTE first for fast acquisition on 4G VN carriers.
        if (p.NwscanSeq?.StartsWith("0", StringComparison.OrdinalIgnoreCase) == true) score += 5;
        else if (string.IsNullOrEmpty(p.NwscanSeq)) notes.Add("nwscanseq empty.");
        else notes.Add($"nwscanseq={p.NwscanSeq} (LTE not first).");

        // band should include at least one VN band (B1/B3/B7/B8/B28/B40 for Vinaphone/MobiFone).
        if (string.IsNullOrEmpty(p.Band)) notes.Add("band empty (factory default = all).");
        else if (!Regex.IsMatch(p.Band, @"\b(1|3|7|8|28|40)\b"))
        {
            notes.Add($"band='{p.Band}' does not include common VN LTE bands (1/3/7/8/28/40).");
        }
        else score += 5;

        p.Score = score;
        p.Ready = score >= 60;
        p.Findings = notes;
    }

    private static bool IsRegistered(string? reg)
    {
        if (string.IsNullOrEmpty(reg)) return false;
        var parts = reg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        return parts[^1] is "1" or "5" or "6" or "7";
    }
}