using System.Diagnostics;
using System.IO;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

public static class AppBootstrap
{
    internal static readonly TimeSpan LogRetention = TimeSpan.FromDays(1);
    private static int _logCleanupRunning;

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
    /// older than the retention window before any service starts.
    /// </summary>
    public static void EnsureAll()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(RecordingsDir);
            Directory.CreateDirectory(ConfigDir);
            DeleteExpiredLogs();
            DeleteObsoleteLocalStateFiles();
            EnsureSettingsFile();
        }
        catch (Exception ex)
        {
            // Startup diagnostics stay in the debugger/RAM only.
            Debug.WriteLine($"[BOOTSTRAP] {ex}");
        }
    }

    /// <summary>
    /// Removes only known ToolGSM log/diagnostic files older than 24 hours.
    /// Safe to call from the hourly maintenance timer as well as startup.
    /// </summary>
    internal static void DeleteExpiredLogs(DateTime? utcNow = null)
    {
        if (Interlocked.Exchange(ref _logCleanupRunning, 1) != 0) return;

        try
        {
            DateTime cutoffUtc = (utcNow ?? DateTime.UtcNow) - LogRetention;
            DeleteLogDirectory(LogsDir, cutoffUtc);
            DeleteLogDirectory(
                Path.Combine(AppPaths.UserDataDirectory, "Logs"),
                cutoffUtc);
            DeleteLogDirectory(
                Path.Combine(AppPaths.UserDataDirectory, "Diagnostics"),
                cutoffUtc);
            DeleteLogDirectory(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "ToolGSM",
                    "HardwareFix",
                    "Logs"),
                cutoffUtc);
            DeleteKnownRuntimeLogs(AppDir, cutoffUtc);
            DeleteKnownRuntimeLogs(AppPaths.UserDataDirectory, cutoffUtc);
        }
        finally
        {
            Volatile.Write(ref _logCleanupRunning, 0);
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

    internal static void DeleteLogDirectory(
        string directory,
        DateTime cutoffUtc)
    {
        try
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) return;

            foreach (string file in Directory.EnumerateFiles(
                         root, "*", SearchOption.AllDirectories))
            {
                TryDeleteFileIfExpired(file, cutoffUtc);
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

    internal static void DeleteKnownRuntimeLogs(
        string directory,
        DateTime cutoffUtc)
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
                    TryDeleteFileIfExpired(path, cutoffUtc);
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

    private static void TryDeleteFileIfExpired(
        string path,
        DateTime cutoffUtc)
    {
        try
        {
            if (File.Exists(path)
                && File.GetLastWriteTimeUtc(path) <= cutoffUtc)
            {
                File.Delete(path);
            }
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
