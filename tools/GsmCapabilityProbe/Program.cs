using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record ProbeCommand(string Category, string Command, int TimeoutMs = 5_000);

internal sealed record ProbeResult(
    string Category,
    string Command,
    string Status,
    long ElapsedMs,
    string Response);

internal sealed record PortProbeReport(
    string Port,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string FirmwareRevision,
    IReadOnlyList<string> AdvertisedCommands,
    IReadOnlyList<ProbeResult> Results);

internal static class Program
{
    private static readonly ProbeCommand[] Commands =
    [
        new("identity", "AT"),
        new("identity", "ATI"),
        new("identity", "AT+CGMI"),
        new("identity", "AT+CGMM"),
        new("identity", "AT+CGMR"),
        new("identity", "AT+CLAC", 25_000),

        new("sim", "AT+CPIN?"),
        new("sim", "AT+QSIMSTAT?"),
        new("sim", "AT+QCCID"),
        new("sim", "AT+CIMI"),
        new("sim", "AT+CNUM"),
        new("sim", "AT+CPBS?"),
        new("sim", "AT+CPBR=?"),

        new("network", "AT+CFUN?"),
        new("network", "AT+CSQ"),
        new("network", "AT+CREG?"),
        new("network", "AT+CGREG?"),
        new("network", "AT+CEREG?"),
        new("network", "AT+COPS?"),
        new("network", "AT+QNWINFO"),
        new("network", "AT+QCFG=\"nwscanmode\""),
        new("network", "AT+QCFG=\"nwscanseq\""),
        new("network", "AT+QCFG=\"ims\""),
        new("network", "AT+QURCCFG=\"urcport\""),

        new("ussd", "AT+CUSD?"),
        new("ussd", "AT+CUSD=?"),

        new("sms", "AT+CMGF?"),
        new("sms", "AT+CMGF=?"),
        new("sms", "AT+CPMS?"),
        new("sms", "AT+CPMS=?"),
        new("sms", "AT+CNMI?"),
        new("sms", "AT+CNMI=?"),
        new("sms", "AT+CSCS?"),
        new("sms", "AT+CSCS=?"),
        new("sms", "AT+CSCA?"),
        new("sms", "AT+QCMGR=?"),

        new("call", "AT+CLCC"),
        new("call", "AT+CLCC=?"),
        new("call", "AT+CLIP?", 20_000),
        new("call", "AT+CLIP=?"),
        new("call", "AT+COLP?", 20_000),
        new("call", "AT+CRC?"),
        new("call", "AT+CHLD=?"),
        new("call", "AT+VTS=?"),
        new("call", "AT+QVTS=?"),
        new("call", "AT+CCFC=?"),
        new("call", "AT+CCWA=?"),
        new("call", "AT+CEER"),

        new("audio", "AT+CLVL?"),
        new("audio", "AT+CLVL=?"),
        new("audio", "AT+CMUT?"),
        new("audio", "AT+QDAI?"),
        new("audio", "AT+QDAI=?"),
        new("audio", "AT+QAUDMOD?"),
        new("audio", "AT+QAUDMOD=?"),
        new("audio", "AT+QAUDRD=?"),
        new("audio", "AT+QAUDPLAY=?"),
        new("audio", "AT+QPSND=?"),
        new("audio", "AT+QTONEDET=?"),

        new("filesystem", "AT+QFLST"),
        new("filesystem", "AT+QFLST=?"),
        new("filesystem", "AT+QFLDS"),
        new("filesystem", "AT+QFUPL=?"),

        new("packet-data", "AT+CGATT?"),
        new("packet-data", "AT+CGDCONT?"),
        new("packet-data", "AT+QIACT?"),
        new("packet-data", "AT+QICSGP=?"),
        new("packet-data", "AT+QHTTPCFG=?"),
        new("packet-data", "AT+QHTTPURL=?"),

        new("gnss", "AT+QGPS?"),
        new("gnss", "AT+QGPSCFG=?"),

        new("device", "AT+CCLK?"),
        new("device", "AT+QLTS=2"),
        new("device", "AT+QCFG=\"usbnet\"")
    ];

    private static readonly ProbeCommand[] VoiceConfigurationCommands =
    [
        new("identity", "ATI"),
        new("voice-config", "AT+CLIP=1"),
        new("voice-config", "AT^DSCI=1"),
        new("voice-config", "AT+QTONEDET=1"),
        new("voice-config", "AT+CRC=1")
    ];

    public static async Task<int> Main(string[] args)
    {
        string[] ports = GetOptionValues(args, "--ports");
        if (ports.Length == 0)
        {
            Console.Error.WriteLine("Usage: GsmCapabilityProbe --ports COM86 COM92 [--output report.json]");
            return 2;
        }

        string output = GetSingleOption(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, $"gsm-capabilities-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        IReadOnlyList<ProbeCommand> selectedCommands = args.Any(value =>
            value.Equals("--voice-config", StringComparison.OrdinalIgnoreCase))
            ? VoiceConfigurationCommands
            : Commands;
        string[] distinctPorts = ports
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ToArray();

        PortProbeReport?[] completed = await Task.WhenAll(distinctPorts.Select(async port =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Opening {port}...");
            try
            {
                return await ProbePortAsync(port, selectedCommands);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{port}] FATAL: {ex.Message}");
                return null;
            }
        }));

        List<PortProbeReport> reports = completed
            .OfType<PortProbeReport>()
            .OrderBy(report => PortNumber(report.Port))
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(reports, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Console.WriteLine($"Report: {Path.GetFullPath(output)}");
        return reports.Count == distinctPorts.Length ? 0 : 1;
    }

    private static async Task<PortProbeReport> ProbePortAsync(
        string portName,
        IReadOnlyList<ProbeCommand> commands)
    {
        DateTimeOffset startedAt = DateTimeOffset.Now;
        using var serial = new SerialPort(portName, 115_200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 250,
            WriteTimeout = 5_000,
            DtrEnable = true,
            RtsEnable = true,
            NewLine = "\r\n"
        };
        serial.Open();
        await Task.Delay(250);
        _ = serial.ReadExisting();

        var results = new List<ProbeResult>(commands.Count);
        foreach (ProbeCommand probe in commands)
        {
            ProbeResult result = await SendQueryAsync(serial, probe);
            results.Add(result);
            string compact = Regex.Replace(result.Response, @"\s+", " ").Trim();
            if (compact.Length > 140) compact = compact[..140] + "...";
            Console.WriteLine($"[{portName}] {probe.Command,-28} {result.Status,-25} {result.ElapsedMs,5} ms  {compact}");
            await Task.Delay(50);
        }

        string identity = results.FirstOrDefault(result => result.Command == "ATI")?.Response ?? string.Empty;
        string revision = Regex.Match(identity, @"Revision:\s*([^\r\n]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        string clac = results.FirstOrDefault(result => result.Command == "AT+CLAC")?.Response ?? string.Empty;
        string[] advertised = Regex.Matches(
                clac,
                @"(?m)^\s*((?:[+&\\%$*^][A-Z0-9_]+)|S\d+|[A-Z])\s*$",
                RegexOptions.IgnoreCase)
            .Select(match => "AT" + match.Groups[1].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PortProbeReport(
            portName,
            startedAt,
            DateTimeOffset.Now,
            revision,
            advertised,
            results);
    }

    private static async Task<ProbeResult> SendQueryAsync(SerialPort serial, ProbeCommand probe)
    {
        _ = serial.ReadExisting();
        var stopwatch = Stopwatch.StartNew();
        serial.Write(probe.Command + "\r\n");
        var response = new System.Text.StringBuilder();

        while (stopwatch.ElapsedMilliseconds < probe.TimeoutMs)
        {
            string chunk = serial.ReadExisting();
            if (chunk.Length > 0)
            {
                response.Append(chunk);
                if (HasTerminal(response.ToString())) break;
            }
            await Task.Delay(20);
        }

        stopwatch.Stop();
        string raw = response.ToString().Replace("\0", string.Empty);
        string status = Classify(raw, stopwatch.ElapsedMilliseconds >= probe.TimeoutMs);
        return new ProbeResult(probe.Category, probe.Command, status, stopwatch.ElapsedMilliseconds, raw.Trim());
    }

    private static bool HasTerminal(string response) => Regex.IsMatch(
        response,
        @"(?:^|\r?\n)\s*(?:OK|ERROR|\+CME ERROR:[^\r\n]*|\+CMS ERROR:[^\r\n]*)\s*(?:\r?\n|$)",
        RegexOptions.IgnoreCase);

    private static string Classify(string response, bool timedOut)
    {
        // EC20 CLAC streams its advertised command list but does not append an
        // OK/ERROR terminator on the two verified R08 firmware revisions.
        if (Regex.Matches(response, @"(?m)^\s*[+&\\%$*^][A-Z0-9_]+\s*$", RegexOptions.IgnoreCase).Count > 20)
            return "supported-no-terminator";
        if (Regex.IsMatch(response, @"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
            return "supported";
        if (response.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase)
            || response.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
            return "recognized-but-unavailable";
        if (Regex.IsMatch(response, @"(?:^|\r?\n)\s*ERROR\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
            return "rejected-or-unsupported";
        return timedOut ? "timeout" : "incomplete";
    }

    private static string[] GetOptionValues(string[] args, string option)
    {
        int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return [];
        return args.Skip(index + 1).TakeWhile(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
    }

    private static string? GetSingleOption(string[] args, string option) =>
        GetOptionValues(args, option).FirstOrDefault();

    private static int PortNumber(string portName) =>
        int.TryParse(Regex.Match(portName, @"\d+").Value, out int number) ? number : int.MaxValue;
}
