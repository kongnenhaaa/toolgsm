using gsm.Services;

namespace gsm.Tests;

public sealed class DmxSuccessOutputServiceTests
{
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
    public void Cleanup_DeletesOnlyKnownOutputFilesAfterSevenDays()
    {
        string directory = CreateDirectory();
        try
        {
            var output = new DmxSuccessOutputService(directory);
            output.BeginSession();
            output.RecordSuccess("0911111111");
            string unrelated = Path.Combine(directory, "ghi_chu.txt");
            File.WriteAllText(unrelated, "keep");

            DateTime nowUtc = new(
                2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(
                output.LatestSessionPath,
                nowUtc - TimeSpan.FromDays(7));
            File.SetLastWriteTimeUtc(
                output.AggregatePath,
                nowUtc - TimeSpan.FromDays(8));
            File.SetLastWriteTimeUtc(
                unrelated,
                nowUtc - TimeSpan.FromDays(30));

            DmxSuccessOutputService.DeleteExpiredOutputs(directory, nowUtc);

            Assert.False(File.Exists(output.LatestSessionPath));
            Assert.False(File.Exists(output.AggregatePath));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_KeepsFilesNewerThanSevenDays()
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
                output.LatestSessionPath,
                nowUtc - TimeSpan.FromDays(6));
            File.SetLastWriteTimeUtc(
                output.AggregatePath,
                nowUtc - TimeSpan.FromDays(6));

            DmxSuccessOutputService.DeleteExpiredOutputs(directory, nowUtc);

            Assert.True(File.Exists(output.LatestSessionPath));
            Assert.True(File.Exists(output.AggregatePath));
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
