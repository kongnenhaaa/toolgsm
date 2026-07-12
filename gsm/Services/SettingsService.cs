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
                return settings ?? new AppSettings();
            }
            catch (Exception)
            {
                // Ignored
            }
        }

        // Return default settings
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Current = settings;
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception)
        {
            // Ignored
        }
    }
}
