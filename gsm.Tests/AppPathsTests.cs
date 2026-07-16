using gsm.Services;

namespace gsm.Tests;

public sealed class AppPathsTests
{
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
}
