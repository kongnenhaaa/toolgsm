using System.Diagnostics;
using gsm.Services;

namespace gsm.Tests;

public sealed class TransientPythonWorkspaceTests
{
    [Fact]
    public async Task Cleanup_RemovesTheCurrentProcessWorkspace()
    {
        var startInfo = new ProcessStartInfo();
        TransientPythonWorkspace.Prepare(startInfo);
        string path = Assert.IsType<string>(
            startInfo.Environment["TOOLGSM_TRANSIENT_PARENT"]);
        string imageDir = Path.Combine(path, "toolgsm_ekyc_test", "anh");
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(imageDir, "sample.jpg"), "test");

        await TransientPythonWorkspace.CleanupAsync(startInfo);

        Assert.False(Directory.Exists(path));
    }
}
