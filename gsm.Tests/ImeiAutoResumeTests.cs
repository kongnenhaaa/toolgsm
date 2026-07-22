using gsm.ViewModels;

namespace gsm.Tests;

public sealed class ImeiAutoResumeTests
{
    [Fact]
    public void NoSimImei_TakesPriorityAndAutoResumesWhenSimIsInserted()
    {
        var result = MainViewModel.ResolveAutomaticImeiResumeCandidate(
            "351928119719786",
            "352849113989358",
            "353857111854036");

        Assert.Equal("351928119719786", result.Imei);
        Assert.Equal("no-sim", result.Source);
    }

    [Fact]
    public void PersistedBackup_AutoResumesOnLaterApplicationStart()
    {
        var result = MainViewModel.ResolveAutomaticImeiResumeCandidate(
            null,
            null,
            "358485114057764");

        Assert.Equal("358485114057764", result.Imei);
        Assert.Equal("xlsx", result.Source);
    }

    [Fact]
    public void InvalidCandidates_StillRequireManualImeiAction()
    {
        var result = MainViewModel.ResolveAutomaticImeiResumeCandidate(
            "invalid",
            "",
            "123");

        Assert.Empty(result.Imei);
        Assert.Empty(result.Source);
    }
}
