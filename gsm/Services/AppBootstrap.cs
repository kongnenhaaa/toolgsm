using System.Diagnostics;
using System.IO;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

public static class AppBootstrap
{
    public static string AppDir => AppContext.BaseDirectory;

    public static string SettingsPath => Path.Combine(AppDir, "appsettings.json");
    public static string DataDir => Path.Combine(AppDir, "Data");
    public static string LogsDir => Path.Combine(AppDir, "Logs");
    public static string RecordingsDir => Path.Combine(AppDir, "Recordings");
    public static string ConfigDir => Path.Combine(AppDir, "Config");

    internal static IReadOnlyList<string> ObsoleteLocalStateFiles { get; } =
    [
        "sms_multipart_journal.json.legacy-migration.json",
        "sms_multipart_journal.json.legacy-migration.json.tmp",
        "sms_multipart_journal.json.tmp",
        "sms_sim_cleanup_journal.json",
        "sms_sim_cleanup_journal.pending.json",
        "sms_direct_recovery.json",
        "sms_direct_recovery.backup.json",
        "telegram_outbox.json",
        "telegram_outbox.backup.json"
    ];

    private static readonly string[] RuntimeLogPatterns =
    [
        "*.log",
        "bootstrap_error.txt",
        "system_log.txt",
        "system_log_*.txt",
        "myvnpt_passwords.txt",
        "tele_error.txt",
        "webhook_errors.txt"
    ];

    /// <summary>
    /// Keeps normal application state and features intact, but removes logs
    /// left by older versions before any service starts.
    /// </summary>
    public static void EnsureAll()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(RecordingsDir);
            Directory.CreateDirectory(ConfigDir);
            DeleteLogDirectory(LogsDir);
            DeleteLogDirectory(Path.Combine(AppPaths.UserDataDirectory, "Logs"));
            DeleteKnownRuntimeLogs(AppDir);
            DeleteKnownRuntimeLogs(AppPaths.UserDataDirectory);
            DeleteObsoleteLocalStateFiles();
            EnsureSettingsFile();
        }
        catch (Exception ex)
        {
            // Startup diagnostics stay in the debugger/RAM only.
            Debug.WriteLine($"[BOOTSTRAP] {ex}");
        }
    }

    internal static void DeleteObsoleteLocalStateFiles(
        string? dataDirectory = null)
    {
        string directory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(dataDirectory)
                ? AppPaths.UserDataDirectory
                : dataDirectory);
        foreach (string fileName in ObsoleteLocalStateFiles)
        {
            string path = Path.Combine(directory, fileName);
            TryDeleteFile(path);
        }
    }

    internal static void DeleteLogDirectory(string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) return;

            foreach (string file in Directory.EnumerateFiles(
                         root, "*", SearchOption.AllDirectories))
            {
                TryDeleteFile(file);
            }

            foreach (string child in Directory.EnumerateDirectories(
                         root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
            {
                TryDeleteEmptyDirectory(child);
            }
            TryDeleteEmptyDirectory(root);
        }
        catch
        {
            // Locked files are retried on the next startup.
        }
    }

    private static void DeleteKnownRuntimeLogs(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string pattern in RuntimeLogPatterns)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(
                             directory,
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    TryDeleteFile(path);
                }
            }
            catch
            {
            }
        }
    }

    private static void EnsureSettingsFile()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                string text = File.ReadAllText(SettingsPath).Trim();
                if (text.Length > 2) return;
            }
            catch
            {
            }
        }

        var defaults = new AppSettings
        {
            MachineId = Environment.MachineName,
            InstallationId = Guid.NewGuid().ToString("N"),
            WriteOtpToFirebase = false,
            FirebaseSyncPreferenceVersion = 1,
            FirebaseUrl = FirebaseService.DatabaseUrl,
            FirebaseDbUrl = FirebaseService.DatabaseUrl,
            FirebaseAuthToken = ""
        };

        string json = JsonSerializer.Serialize(
            defaults,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
        }
    }
}
