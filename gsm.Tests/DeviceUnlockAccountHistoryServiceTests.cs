using gsm.Services;

namespace gsm.Tests;

public sealed class DeviceUnlockAccountHistoryServiceTests
{
    [Fact]
    public void RememberedAccounts_AreLocalDeduplicatedAndPersisted()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-account-history-test-" + Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(directory, "history.json");
        try
        {
            var history = new DeviceUnlockAccountHistoryService(filePath);
            history.RememberCbss("  cbss-one  ", "cbss-pass-one");
            history.RememberCbss("CBSS-ONE");
            history.RememberCbss("cbss-two", "cbss-pass-two");
            history.RememberEmploy("employ-one", "employ-pass-one");

            Assert.Equal(["cbss-two", "CBSS-ONE"], history.GetCbssAccounts());
            Assert.Equal(["employ-one"], history.GetEmployAccounts());
            Assert.Equal("cbss-pass-one", history.GetCbssPassword("cbss-one"));
            Assert.Equal("cbss-pass-two", history.GetCbssPassword("CBSS-TWO"));
            Assert.Equal("employ-pass-one", history.GetEmployPassword("EMPLOY-ONE"));
            Assert.True(File.Exists(filePath));

            var reloaded = new DeviceUnlockAccountHistoryService(filePath);
            Assert.Equal(["cbss-two", "CBSS-ONE"], reloaded.GetCbssAccounts());
            Assert.Equal(["employ-one"], reloaded.GetEmployAccounts());
            Assert.Equal("cbss-pass-one", reloaded.GetCbssPassword("cbss-one"));
            Assert.Equal("cbss-pass-two", reloaded.GetCbssPassword("CBSS-TWO"));
            Assert.Equal("employ-pass-one", reloaded.GetEmployPassword("EMPLOY-ONE"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClearAccounts_OnlyClearsTheRequestedSource()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-account-history-test-" + Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(directory, "history.json");
        try
        {
            var history = new DeviceUnlockAccountHistoryService(filePath);
            history.RememberCbss("cbss-one", "cbss-pass");
            history.RememberEmploy("employ-one", "employ-pass");

            history.ClearCbssAccounts();

            Assert.Empty(history.GetCbssAccounts());
            Assert.Empty(history.GetCbssPassword("cbss-one"));
            Assert.Equal(["employ-one"], history.GetEmployAccounts());
            Assert.Equal("employ-pass", history.GetEmployPassword("employ-one"));
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
