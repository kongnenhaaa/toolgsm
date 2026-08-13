using gsm.Services;

namespace gsm.Tests;

public sealed class SmsDirectRecoveryStoreTests
{
    [Fact]
    public void UndecodableRawFrame_SurvivesRestartExactly()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        const string raw = "+CMT: ,32\r\nDEADBEEF\r\n+CMT: �\r\n";

        var firstRun = new SmsDirectRecoveryStore(primary, fallback);
        SmsDirectRecoveryStore.Pending stored = firstRun.Store(
            "COM84",
            "ccid:8984048000000000000",
            raw,
            "complete-frame-undecodable",
            4);

        var afterRestart = new SmsDirectRecoveryStore(primary, fallback);
        SmsDirectRecoveryStore.Pending recovered =
            Assert.Single(afterRestart.GetForPort("com84"));
        Assert.Equal(stored.Id, recovered.Id);
        Assert.Equal(raw, recovered.Raw);
        Assert.Equal("ccid:8984048000000000000", recovered.Scope);
    }

    [Fact]
    public void ValidFallback_RecoversWhenPrimaryCopyIsCorrupt()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        var firstRun = new SmsDirectRecoveryStore(primary, fallback);
        firstRun.Store(
            "COM101",
            "COM101",
            "+CMT: ???\r\nBROKEN\r\n",
            "retry-limit",
            4);
        File.WriteAllText(primary, "{corrupt");

        var recovered = new SmsDirectRecoveryStore(primary, fallback);

        Assert.Single(recovered.GetForPort("COM101"));
    }

    [Fact]
    public void Completion_IsDurableAcrossRestart()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        var store = new SmsDirectRecoveryStore(primary, fallback);
        SmsDirectRecoveryStore.Pending pending = store.Store(
            "COM7",
            "COM7",
            "+CMT: ???\r\nBROKEN\r\n",
            "retry-limit",
            4);

        Assert.True(store.Complete(pending.Id));

        var afterRestart = new SmsDirectRecoveryStore(primary, fallback);
        Assert.Empty(afterRestart.GetForPort("COM7"));
    }

    [Fact]
    public void SameCcid_CanRecoverFrameAfterSimMovesToAnotherPort()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        const string scope = "ccid:8984048000000000000";
        var store = new SmsDirectRecoveryStore(primary, fallback);
        SmsDirectRecoveryStore.Pending pending = store.Store(
            "COM84",
            scope,
            "+CMT: ???\r\nBROKEN\r\n",
            "retry-limit",
            4);

        SmsDirectRecoveryStore.Pending recovered = Assert.Single(
            new SmsDirectRecoveryStore(primary, fallback)
                .GetRecoverable("COM111", scope));

        Assert.Equal(pending.Id, recovered.Id);
    }

    [Fact]
    public void BothCorruptRecoveryCopies_FailClosedWithoutOverwrite()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        var store = new SmsDirectRecoveryStore(primary, fallback);
        store.Store(
            "COM84",
            "COM84",
            "+CMT: ???\r\nBROKEN\r\n",
            "retry-limit",
            4);
        File.WriteAllText(primary, "{primary-corrupt");
        File.WriteAllText(fallback, "{fallback-corrupt");

        var blocked = new SmsDirectRecoveryStore(primary, fallback);

        Assert.Throws<InvalidDataException>(() => blocked.Store(
            "COM84",
            "COM84",
            "+CMT: ???\r\nSECOND\r\n",
            "retry-limit",
            4));
        Assert.Equal("{primary-corrupt", File.ReadAllText(primary));
        Assert.Equal("{fallback-corrupt", File.ReadAllText(fallback));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"toolgsm-direct-recovery-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
