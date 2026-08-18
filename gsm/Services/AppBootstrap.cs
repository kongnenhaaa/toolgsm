using System;
using System.IO;
using gsm.Models;
using gsm.Services;
using System.Text.Json;

namespace gsm.Services
{
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

        /// <summary>Gọi đầu tiên khi mở tool – tạo folder + settings nếu thiếu.</summary>
        public static void EnsureAll()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(LogsDir);
                Directory.CreateDirectory(RecordingsDir);
                Directory.CreateDirectory(ConfigDir);
                DeleteObsoleteLocalStateFiles();
                EnsureSettingsFile();
            }
            catch (Exception ex)
            {
                // Không crash app – log ra file tạm
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppDir, "bootstrap_error.txt"),
                        $"{DateTime.Now:o} {ex}\n");
                }
                catch { /* ignore */ }
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
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    // A locked old file can be retried on the next startup.
                }
            }
        }

        static void EnsureSettingsFile()
        {
            if (File.Exists(SettingsPath))
            {
                // File có nhưng rỗng / hỏng → không ghi đè (tránh mất config user)
                try
                {
                    var txt = File.ReadAllText(SettingsPath).Trim();
                    if (txt.Length > 2) return; // đã có nội dung
                }
                catch { /* rewrite default */ }
            }

            var defaults = new AppSettings(); // class settings của bạn – giá trị mặc định
            
            // Gán default an toàn cho máy mới
            defaults.MachineId = Environment.MachineName; // hoặc "machine-1"
            defaults.InstallationId = Guid.NewGuid().ToString("N");
            defaults.WriteOtpToFirebase = true;
            defaults.FirebaseUrl = FirebaseService.DatabaseUrl;
            defaults.FirebaseDbUrl = FirebaseService.DatabaseUrl;
            defaults.FirebaseAuthToken = "";
            
            var json = JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
