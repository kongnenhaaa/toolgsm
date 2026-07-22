namespace gsm.Models;

[Flags]
public enum ModemCapability
{
    None = 0,
    Quectel = 1 << 0,
    NetworkScanConfig = 1 << 1,
    ImsConfig = 1 << 2,
    SimStatusUrc = 1 << 3,
    SimHotplugConfig = 1 << 4,
    QuectelStoredSms = 1 << 5,
    AudioRecord = 1 << 6,
    HttpData = 1 << 7,
    DtmfDetection = 1 << 8,
    UrcPortRouting = 1 << 9,
    VoiceCall = 1 << 10,
    CallerIdPresentation = 1 << 11,
    CallStatusIndication = 1 << 12,
    DtmfSend = 1 << 13,
    AudioPlayback = 1 << 14,
    FileStorage = 1 << 15,
    Gnss = 1 << 16,
    StandardSms = 1 << 17,
    Ussd = 1 << 18,
    Phonebook = 1 << 19,
    SupplementaryServices = 1 << 20,
    PacketData = 1 << 21,
    WifiControl = 1 << 22,
    BluetoothControl = 1 << 23,
    EmergencyCall = 1 << 24
}

[Flags]
public enum ModemQuirk
{
    None = 0,
    ClipReadHangs = 1 << 0,
    ClacHasNoTerminator = 1 << 1
}

public sealed record QuectelModemProfile(
    string Manufacturer,
    string Model,
    string Firmware,
    ModemCapability Capabilities,
    ModemQuirk Quirks = ModemQuirk.None)
{
    public bool IsQuectel => Capabilities.HasFlag(ModemCapability.Quectel);
    public bool Supports(ModemCapability capability) => Capabilities.HasFlag(capability);
    public bool HasQuirk(ModemQuirk quirk) => Quirks.HasFlag(quirk);
    public string FirmwareRevision => ExtractFirmwareRevision(Firmware);
    public string CapabilityText => string.Join(",", Enum.GetValues<ModemCapability>()
        .Where(value => value != ModemCapability.None && value != ModemCapability.Quectel && Supports(value)));
    public string QuirkText => Quirks == ModemQuirk.None
        ? "None"
        : string.Join(",", Enum.GetValues<ModemQuirk>()
            .Where(value => value != ModemQuirk.None && HasQuirk(value)));

    public static QuectelModemProfile FromIdentity(string manufacturer, string model, string firmware)
    {
        string cleanManufacturer = Clean(manufacturer);
        string cleanModel = Clean(model);
        string cleanFirmware = Clean(firmware);
        string upper = $"{cleanManufacturer} {cleanModel}".ToUpperInvariant();
        if (!upper.Contains("QUECTEL"))
            return new(cleanManufacturer, cleanModel, cleanFirmware, ModemCapability.None);

        ModemCapability capabilities = ModemCapability.Quectel
            | ModemCapability.NetworkScanConfig
            | ModemCapability.SimStatusUrc
            | ModemCapability.SimHotplugConfig
            | ModemCapability.UrcPortRouting
            | ModemCapability.StandardSms
            | ModemCapability.Ussd
            | ModemCapability.Phonebook
            | ModemCapability.PacketData;

        bool voiceFamily = upper.Contains("EC2") || upper.Contains("EG9")
            || upper.Contains("EG2") || upper.Contains("EG0") || upper.Contains("UC2");
        bool legacyLte = upper.Contains("EC20") || upper.Contains("EC21") || upper.Contains("EC25");
        bool packetFamily = voiceFamily || upper.Contains("BG9") || upper.Contains("BG7")
            || upper.Contains("RG") || upper.Contains("RM") || upper.Contains("EM") || upper.Contains("EP");

        if (voiceFamily)
            capabilities |= ModemCapability.VoiceCall
                | ModemCapability.CallerIdPresentation
                | ModemCapability.CallStatusIndication
                | ModemCapability.DtmfDetection
                | ModemCapability.DtmfSend
                | ModemCapability.SupplementaryServices;
        if (legacyLte || upper.Contains("EG9") || upper.Contains("EG2"))
            capabilities |= ModemCapability.QuectelStoredSms
                | ModemCapability.AudioRecord
                | ModemCapability.AudioPlayback
                | ModemCapability.FileStorage
                | ModemCapability.Gnss;
        if (packetFamily)
            capabilities |= ModemCapability.HttpData;
        if (!upper.Contains("BG95") && !upper.Contains("BG77"))
            capabilities |= ModemCapability.ImsConfig;

        string revision = ExtractFirmwareRevision(cleanFirmware);
        ModemQuirk quirks = legacyLte
            ? ModemQuirk.ClacHasNoTerminator
            : ModemQuirk.None;
        if (revision.Equals("EC20CEHDLGR08A05M1G", StringComparison.OrdinalIgnoreCase))
        {
            // A few units of the physical 32-port bank either reject CLIP? or do
            // not terminate it. CLIP=1 itself is fast and was accepted on 32/32.
            // Never read CLIP back during startup.
            quirks |= ModemQuirk.ClipReadHangs;
        }
        else if (revision.Equals("EC20CEFAGR08A03M4G", StringComparison.OrdinalIgnoreCase))
        {
            // This firmware advertises and accepts Quectel Bluetooth/Wi-Fi and
            // emergency-call command families in addition to the EC20 baseline.
            capabilities |= ModemCapability.WifiControl
                | ModemCapability.BluetoothControl
                | ModemCapability.EmergencyCall;
        }

        return new(cleanManufacturer, cleanModel, cleanFirmware, capabilities, quirks);
    }

    private static string ExtractFirmwareRevision(string value)
    {
        string input = value ?? string.Empty;
        System.Text.RegularExpressions.Match revision = System.Text.RegularExpressions.Regex.Match(
            input,
            @"\b(?:EC|EG|BG|RG|RM|EM|EP|UC)[A-Z0-9-]{6,}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return revision.Success ? revision.Value.ToUpperInvariant() : Clean(input);
    }

    private static string Clean(string value) => string.Join(" ", (value ?? string.Empty)
        .Replace("OK", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("ATI", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
