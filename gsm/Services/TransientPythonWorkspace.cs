using System.Diagnostics;
using System.IO;

namespace gsm.Services;

/// <summary>
/// Gives each eKYC Python process its own temporary parent directory so the
/// .NET caller can remove it even when Python is killed before atexit runs.
/// </summary>
internal static class TransientPythonWorkspace
{
    private const string EnvironmentName = "TOOLGSM_TRANSIENT_PARENT";
    private const string DirectoryPrefix = "run_";

    internal static void Prepare(ProcessStartInfo startInfo)
    {
        string root = RootDirectory();
        startInfo.Environment[EnvironmentName] = Path.Combine(
            root,
            DirectoryPrefix + Guid.NewGuid().ToString("N"));
    }

    internal static async Task CleanupAsync(ProcessStartInfo startInfo)
    {
        if (!startInfo.Environment.TryGetValue(EnvironmentName, out string? configured)
            || string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        string path = Path.GetFullPath(configured);
        string name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), RootDirectory(),
                StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _))
        {
            return;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                if (attempt == 4) return;
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == 4) return;
            }

            await Task.Delay(150 * (attempt + 1));
        }
    }

    private static string RootDirectory() => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "ToolGSM", "DKTTTB"));
}
