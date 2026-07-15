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
    VoiceCall = 1 << 10
}

public sealed record QuectelModemProfile(
    string Manufacturer,
    string Model,
    string Firmware,
    ModemCapability Capabilities)
{
    public bool IsQuectel => Capabilities.HasFlag(ModemCapability.Quectel);
    public bool Supports(ModemCapability capability) => Capabilities.HasFlag(capability);
    public string CapabilityText => string.Join(",", Enum.GetValues<ModemCapability>()
        .Where(value => value != ModemCapability.None && value != ModemCapability.Quectel && Supports(value)));

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
            | ModemCapability.UrcPortRouting;

        bool voiceFamily = upper.Contains("EC2") || upper.Contains("EG9")
            || upper.Contains("EG2") || upper.Contains("EG0") || upper.Contains("UC2");
        bool legacyLte = upper.Contains("EC20") || upper.Contains("EC21") || upper.Contains("EC25");
        bool packetFamily = voiceFamily || upper.Contains("BG9") || upper.Contains("BG7")
            || upper.Contains("RG") || upper.Contains("RM") || upper.Contains("EM") || upper.Contains("EP");

        if (voiceFamily)
            capabilities |= ModemCapability.VoiceCall | ModemCapability.DtmfDetection;
        if (legacyLte || upper.Contains("EG9") || upper.Contains("EG2"))
            capabilities |= ModemCapability.QuectelStoredSms | ModemCapability.AudioRecord;
        if (packetFamily)
            capabilities |= ModemCapability.HttpData;
        if (!upper.Contains("BG95") && !upper.Contains("BG77"))
            capabilities |= ModemCapability.ImsConfig;

        return new(cleanManufacturer, cleanModel, cleanFirmware, capabilities);
    }

    private static string Clean(string value) => string.Join(" ", (value ?? string.Empty)
        .Replace("OK", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("ATI", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
