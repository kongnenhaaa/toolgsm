using System;
using System.IO;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

public static class SettingsService
{
    private static readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    public static AppSettings Current { get; private set; } = new AppSettings();

    static SettingsService()
    {
        Current = LoadSettings();
    }

    public static AppSettings LoadSettings()
    {
        AppBootstrap.EnsureAll();

        if (File.Exists(SettingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return Normalize(settings ?? new AppSettings());
            }
            catch (Exception)
            {
                // Ignored
            }
        }

        // Return default settings
        return Normalize(new AppSettings());
    }

    public static bool SaveSettings(AppSettings settings)
    {
        string temporaryPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            AppSettings normalized = Normalize(settings);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(normalized, options);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            Current = normalized;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A temporary file is never considered saved settings.
            }
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.EnableApiServer = false;
        settings.OtpWebhookUrl = "";
        settings.PushOtpToWeb = false;
        settings.FirebaseUrl = FirebaseService.DatabaseUrl;
        settings.FirebaseDbUrl = FirebaseService.DatabaseUrl;
        settings.FirebaseAuthToken = "";
        settings.WriteOtpToFirebase = true;
        // Incoming GSM messages are operational data, not optional marketing
        // notifications. Once Telegram has a destination, every received SMS
        // must be mirrored regardless of whether OTP extraction succeeded.
        settings.TelegramOnOtp = true;
        settings.TelegramOnSms = true;
        settings.SignalScanIntervalSeconds = Math.Clamp(
            settings.SignalScanIntervalSeconds, 5, 300);
        if (string.IsNullOrWhiteSpace(settings.MachineId))
            settings.MachineId = Environment.MachineName;
        return settings;
    }
}
