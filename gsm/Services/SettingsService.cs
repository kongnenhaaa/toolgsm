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

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Current = Normalize(settings);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Current, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception)
        {
            // Ignored
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
        settings.SignalScanIntervalSeconds = Math.Clamp(
            settings.SignalScanIntervalSeconds, 5, 300);
        if (string.IsNullOrWhiteSpace(settings.MachineId))
            settings.MachineId = Environment.MachineName;
        return settings;
    }
}
