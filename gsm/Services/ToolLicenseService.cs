using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.Win32;

namespace gsm.Services;

public sealed record ToolLicenseActivationResult(bool Success, string Message);

public sealed class ToolLicenseService
{
    internal const string SecretSalt =
        "VNPT_DIGISHOP_TOOL_EKYC_2026_@PHAT_SECRET";
    private const string KeyPrefix = "TGSM";
    private const string LicenseFileName = "toolgsm_license.json";
    private static readonly Regex CurrentKeyPattern = new(
        @"^TGSM-(?<nonce>[0-9]{6,12})-(?<hash>[A-F0-9]{24})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyKeyPattern = new(
        @"^(?<limit>[0-9]+)-(?<nonce>[0-9]{6})-(?<hash>[A-F0-9]{20})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _licenseFilePath;

    public ToolLicenseService()
        : this(CreateHardwareId(), AppPaths.ForUserDataFile(LicenseFileName))
    {
    }

    internal ToolLicenseService(string hardwareId, string licenseFilePath)
    {
        HardwareId = FormatHardwareId(NormalizeHardwareId(hardwareId));
        _licenseFilePath = licenseFilePath;
        LoadSavedLicense();
    }

    public string HardwareId { get; }
    public bool IsActivated { get; private set; }
    public string ActivatedKey { get; private set; } = string.Empty;
    public event EventHandler? ActivationChanged;

    public ToolLicenseActivationResult Activate(string? key)
    {
        string normalizedKey = NormalizeKey(key);
        if (!TryValidateKey(HardwareId, normalizedKey, out string error))
            return new(false, error);

        if (!PersistLicense(normalizedKey))
        {
            return new(
                false,
                "Key hợp lệ nhưng không lưu được trạng thái kích hoạt trên máy.");
        }

        bool changed = !IsActivated
                       || !string.Equals(
                           ActivatedKey,
                           normalizedKey,
                           StringComparison.Ordinal);
        IsActivated = true;
        ActivatedKey = normalizedKey;
        if (changed) ActivationChanged?.Invoke(this, EventArgs.Empty);
        return new(true, "Kích hoạt ToolGSM thành công. Key có hiệu lực vĩnh viễn trên máy này.");
    }

    internal static string CreateKeyForHardwareId(
        string hardwareId,
        string nonce)
    {
        string normalizedHardwareId = NormalizeHardwareId(hardwareId);
        string normalizedNonce = (nonce ?? string.Empty).Trim();
        string raw = $"{normalizedHardwareId}_{normalizedNonce}_{SecretSalt}";
        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(raw)))[..24];
        return $"{KeyPrefix}-{normalizedNonce}-{hash}";
    }

    internal static string CreateLegacyKeyForHardwareId(
        string hardwareId,
        string usageLimit,
        string nonce)
    {
        string formattedHardwareId = FormatHardwareId(
            NormalizeHardwareId(hardwareId));
        string normalizedLimit = (usageLimit ?? string.Empty).Trim();
        string normalizedNonce = (nonce ?? string.Empty).Trim();
        string raw = $"{formattedHardwareId}_{normalizedLimit}_{normalizedNonce}_{SecretSalt}";
        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(raw)))[..20];
        return $"{normalizedLimit}-{normalizedNonce}-{hash}";
    }

    internal static bool TryValidateKey(
        string hardwareId,
        string? key,
        out string error)
    {
        string normalizedKey = NormalizeKey(key);
        Match currentMatch = CurrentKeyPattern.Match(normalizedKey);
        if (currentMatch.Success)
        {
            string expected = CreateKeyForHardwareId(
                hardwareId,
                currentMatch.Groups["nonce"].Value);
            bool currentMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(normalizedKey));
            error = currentMatches
                ? string.Empty
                : "Key không hợp lệ hoặc không thuộc HWID của máy này.";
            return currentMatches;
        }

        Match legacyMatch = LegacyKeyPattern.Match(normalizedKey);
        if (!legacyMatch.Success)
        {
            error = "Key không đúng định dạng ToolGSM.";
            return false;
        }

        // Key cũ có trường "số lượt" để tương thích với trình tạo key gốc.
        // ToolGSM chỉ dùng trường này để xác minh chữ ký, không trừ hay giới hạn lượt.
        string legacyExpected = CreateLegacyKeyForHardwareId(
            hardwareId,
            legacyMatch.Groups["limit"].Value,
            legacyMatch.Groups["nonce"].Value);
        bool legacyMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(legacyExpected),
            Encoding.ASCII.GetBytes(normalizedKey));
        error = legacyMatches
            ? string.Empty
            : "Key không hợp lệ hoặc không thuộc HWID của máy này.";
        return legacyMatches;
    }

    private void LoadSavedLicense()
    {
        try
        {
            if (!File.Exists(_licenseFilePath)) return;
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_licenseFilePath));
            JsonElement root = document.RootElement;
            string savedHardwareId = root.TryGetProperty(
                    "hardwareId",
                    out JsonElement hardwareElement)
                ? hardwareElement.GetString() ?? string.Empty
                : string.Empty;
            string savedKey = root.TryGetProperty("key", out JsonElement keyElement)
                ? keyElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(
                    NormalizeHardwareId(savedHardwareId),
                    NormalizeHardwareId(HardwareId),
                    StringComparison.Ordinal)
                || !TryValidateKey(HardwareId, savedKey, out _))
            {
                return;
            }

            IsActivated = true;
            ActivatedKey = NormalizeKey(savedKey);
        }
        catch
        {
            IsActivated = false;
            ActivatedKey = string.Empty;
        }
    }

    private bool PersistLicense(string key)
    {
        string? directory = Path.GetDirectoryName(_licenseFilePath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        Directory.CreateDirectory(directory);
        string temporaryPath = $"{_licenseFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(
                new
                {
                    hardwareId = HardwareId,
                    key,
                    activatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _licenseFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static string CreateHardwareId()
    {
        string source = string.Empty;
        try
        {
            using RegistryKey? key = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            source = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(source))
            source = $"{Environment.MachineName}|{Environment.UserName}";

        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("ToolGSM|" + source.Trim().ToUpperInvariant())));
        return FormatHardwareId(hash[..24]);
    }

    private static string NormalizeHardwareId(string? hardwareId) =>
        new((hardwareId ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string FormatHardwareId(string normalizedHardwareId)
    {
        string value = NormalizeHardwareId(normalizedHardwareId);
        return string.Join(
            "-",
            Enumerable.Range(0, (value.Length + 3) / 4)
                .Select(index => value.Substring(
                    index * 4,
                    Math.Min(4, value.Length - index * 4))));
    }

    private static string NormalizeKey(string? key) =>
        Regex.Replace(
                (key ?? string.Empty).Trim().ToUpperInvariant(),
                @"\s+",
                string.Empty)
            .Replace('–', '-')
            .Replace('—', '-');
}
