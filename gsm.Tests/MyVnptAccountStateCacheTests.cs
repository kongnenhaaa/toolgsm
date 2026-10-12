using gsm.Services;

namespace gsm.Tests;

public sealed class MyVnptAccountStateCacheTests
{
    [Fact]
    public void MarkAccountExists_PersistsOnlyHash_AndReloads()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-account-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        const string phone = "84912345678";
        try
        {
            var first = new MyVnptAccountStateCache(path);
            first.MarkAccountExists(phone);

            string persisted = File.ReadAllText(path);
            Assert.DoesNotContain(phone, persisted, StringComparison.Ordinal);
            Assert.True(new MyVnptAccountStateCache(path).IsKnownAccount(phone));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MarkAccountMissing_RemovesConfirmedAccount()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-account-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        try
        {
            var cache = new MyVnptAccountStateCache(path);
            cache.MarkAccountExists("84912345678");
            cache.MarkAccountMissing("84912345678");

            Assert.False(
                new MyVnptAccountStateCache(path).IsKnownAccount("84912345678"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
