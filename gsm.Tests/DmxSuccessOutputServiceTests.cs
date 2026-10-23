using gsm.Services;

namespace gsm.Tests;

public sealed class DmxSuccessOutputServiceTests
{
    [Fact]
    public void DefaultOutputDirectory_UsesProjectGsmDataFromNestedRelease()
    {
        string directory = CreateDirectory();
        try
        {
            string project = Path.Combine(directory, "gsm");
            string runtime = Path.Combine(project, "bin", "Release",
                "net10.0-windows10.0.19041.0", "win-x64", "gsm");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(project, "gsm.csproj"), "");

            Assert.Equal(Path.Combine(project, "Data"),
                DmxSuccessOutputService.ResolveDefaultOutputDirectory(runtime));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sessions_ResetLatestFileAndKeepDeduplicatedAggregateFile()
    {
        string directory = CreateDirectory();
        try
        {
            var output = new DmxSuccessOutputService(directory);
            output.BeginSession();
            output.RecordSuccess("0911 111 111");
            output.RecordSuccess("0911111111");
            output.RecordSuccess("84922222222");

            Assert.Equal(
                ["0911111111", "0922222222"],
                File.ReadAllLines(output.LatestSessionPath));
            Assert.Equal(
                ["0911111111", "0922222222"],
                File.ReadAllLines(output.AggregatePath));

            output.BeginSession();
            output.RecordSuccess("0933333333");

            Assert.Equal(
                ["0933333333"],
                File.ReadAllLines(output.LatestSessionPath));
            Assert.Equal(
                ["0911111111", "0922222222", "0933333333"],
                File.ReadAllLines(output.AggregatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecordSuccess_IsSafeWhenManyPortsFinishConcurrently()
    {
        string directory = CreateDirectory();
        try
        {
            var output = new DmxSuccessOutputService(directory);
            output.BeginSession();

            Parallel.For(0, 64, index =>
                output.RecordSuccess($"09{index:D8}"));

            string[] latest = File.ReadAllLines(output.LatestSessionPath);
            string[] aggregate = File.ReadAllLines(output.AggregatePath);
            Assert.Equal(64, latest.Length);
            Assert.Equal(64, latest.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                latest.OrderBy(static phone => phone),
                aggregate.OrderBy(static phone => phone));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OldAggregateFile_IsPreservedWhenStartingNewSession()
    {
        string directory = CreateDirectory();
        try
        {
            var output = new DmxSuccessOutputService(directory);
            output.BeginSession();
            output.RecordSuccess("0911111111");
            DateTime nowUtc = new(
                2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(
                output.AggregatePath,
                nowUtc - TimeSpan.FromDays(365));

            output.BeginSession();

            Assert.Empty(File.ReadAllLines(output.LatestSessionPath));
            Assert.Equal(["0911111111"], File.ReadAllLines(output.AggregatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-dmx-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
