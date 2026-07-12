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

        /// <summary>Gọi đầu tiên khi mở tool – tạo folder + settings nếu thiếu.</summary>
        public static void EnsureAll()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(LogsDir);
                Directory.CreateDirectory(RecordingsDir);
                Directory.CreateDirectory(ConfigDir);
                Directory.CreateDirectory(Path.Combine(DataDir, "ImeiBackup"));

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
            defaults.EnableApiServer = true;
            defaults.ApiServerPort = 5000;
            defaults.MachineId = Environment.MachineName; // hoặc "machine-1"
            defaults.WriteOtpToFirebase = false; // tắt đến khi user điền URL
            defaults.FirebaseDbUrl = "";
            defaults.FirebaseAuthToken = "";
            
            var json = JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
