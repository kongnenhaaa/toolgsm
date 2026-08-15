using gsm.Services;

namespace gsm.Tests;

public sealed class SmsDirectRecoveryStoreTests
{
    [Fact]
    public void RawFrame_IsHeldOnlyInCurrentSessionAndCreatesNoFiles()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        const string raw = "+CMT: ,32\r\nDEADBEEF\r\n+CMT: �\r\n";
        var store = new SmsDirectRecoveryStore(primary, fallback);

        SmsDirectRecoveryStore.Pending pending = store.Store(
            "COM84",
            "ccid:8984048000000000000",
            raw,
            "complete-frame-undecodable",
            4);

        Assert.Equal(raw, Assert.Single(store.GetForPort("com84")).Raw);
        Assert.False(File.Exists(primary));
        Assert.False(File.Exists(fallback));
        Assert.Empty(new SmsDirectRecoveryStore(primary, fallback)
            .GetForPort("COM84"));
        Assert.True(store.Complete(pending.Id));
    }

    [Fact]
    public void SameCcid_CanRecoverFrameOnAnotherPortWithinSession()
    {
        const string scope = "ccid:8984048000000000000";
        var store = SmsDirectRecoveryStore.CreateInMemory();
        SmsDirectRecoveryStore.Pending pending = store.Store(
            "COM84",
            scope,
            "+CMT: ???\r\nBROKEN\r\n",
            "retry-limit",
            4);

        SmsDirectRecoveryStore.Pending recovered = Assert.Single(
            store.GetRecoverable("COM111", scope));

        Assert.Equal(pending.Id, recovered.Id);
    }

    [Fact]
    public void InvalidFrame_IsRejectedWithoutCreatingFiles()
    {
        using var temp = new TempDirectory();
        string primary = Path.Combine(temp.Path, "direct.json");
        string fallback = Path.Combine(temp.Path, "direct.backup.json");
        var store = new SmsDirectRecoveryStore(primary, fallback);

        Assert.Throws<InvalidDataException>(() => store.Store(
            "COM84",
            "COM84",
            "not-a-cmt-frame",
            "retry-limit",
            4));
        Assert.False(File.Exists(primary));
        Assert.False(File.Exists(fallback));
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
            }
        }
    }
}
