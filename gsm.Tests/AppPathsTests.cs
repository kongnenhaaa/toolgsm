using gsm.Services;

namespace gsm.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void UserDataFile_IsIndependentFromPublishDirectory()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolGSM",
            "Data");

        Assert.Equal(
            Path.Combine(expectedRoot, "imei_pending_no_sim.json"),
            AppPaths.ForUserDataFile("imei_pending_no_sim.json"));
        Assert.NotEqual(
            Path.Combine(AppPaths.RuntimeDirectory, "imei_pending_no_sim.json"),
            AppPaths.ForUserDataFile("imei_pending_no_sim.json"));
    }

    [Fact]
    public void ResolveRuntimeOrAncestorFile_FindsBackupAbovePublishDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "toolgsm-path-test-" + Guid.NewGuid().ToString("N"));
        string publish = Path.Combine(root, "win-x64", "publish");
        Directory.CreateDirectory(publish);
        string backup = Path.Combine(root, "imei_backup.xlsx");
        File.WriteAllText(backup, "test");
        try
        {
            string resolved = AppPaths.ResolveRuntimeOrAncestorFile(
                "imei_backup.xlsx", maxAncestorDepth: 4, runtimeDirectory: publish);

            Assert.Equal(Path.GetFullPath(backup), resolved);
            Assert.Equal(
                Path.Combine(root, "imei_backup.pending.xlsx"),
                AppPaths.ForResolvedFileSibling(
                    "imei_backup.xlsx", "imei_backup.pending.xlsx", 4, publish));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupCleanup_RemovesOnlyRetiredStateFiles()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-obsolete-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string fileName in AppBootstrap.ObsoleteLocalStateFiles)
                File.WriteAllText(Path.Combine(directory, fileName), "old");
            string multipart = Path.Combine(directory, "sms_multipart_journal.json");
            string unrelated = Path.Combine(directory, "imei_pending_no_sim.json");
            string workbook = Path.Combine(directory, "imei_backup.xlsx");
            string recording = Path.Combine(directory, "incoming-COM1.wav");
            string accounts = Path.Combine(
                directory,
                DeviceUnlockAccountHistoryService.FileName);
            string cbssAccounts = Path.Combine(directory, "cbss_accounts.json");
            string cbssSession = Path.Combine(directory, "cbss_session.json");
            string oneBssConfig = Path.Combine(directory, "onebss_config.json");
            File.WriteAllText(multipart, "[]");
            File.WriteAllText(unrelated, "{}");
            File.WriteAllText(workbook, "test");
            File.WriteAllText(recording, "test");
            File.WriteAllText(accounts, "{}");
            File.WriteAllText(cbssAccounts, "{}");
            File.WriteAllText(cbssSession, "{}");
            File.WriteAllText(oneBssConfig, "{}");

            AppBootstrap.DeleteObsoleteLocalStateFiles(directory);

            Assert.All(AppBootstrap.ObsoleteLocalStateFiles, fileName =>
                Assert.False(File.Exists(Path.Combine(directory, fileName))));
            Assert.True(File.Exists(multipart));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(workbook));
            Assert.True(File.Exists(recording));
            Assert.True(File.Exists(accounts));
            Assert.True(File.Exists(cbssAccounts));
            Assert.True(File.Exists(cbssSession));
            Assert.True(File.Exists(oneBssConfig));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LogCleanup_RemovesCompleteLogDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-log-cleanup-test-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(directory, "old");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(directory, "at_commands.log"), "trace");
        File.WriteAllText(Path.Combine(nested, "system_log.txt"), "trace");

        AppBootstrap.DeleteLogDirectory(directory);

        Assert.False(Directory.Exists(directory));
    }
}
