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
    public void VerifiedSessionImei_TakesPriorityOverOlderWorkbookValue()
    {
        var result = MainViewModel.ResolveAutomaticImeiResumeCandidate(
            null,
            "352849113989358",
            "353857111854036");

        Assert.Equal("352849113989358", result.Imei);
        Assert.Equal("session", result.Source);
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

    [Theory]
    [InlineData("89840200011639721552", "89840200011639721552", true)]
    [InlineData("", "89840200011639721552", false)]
    [InlineData("ERROR: Timeout", "89840200011639721552", false)]
    [InlineData("89840400011639721552", "89840200011639721552", false)]
    public void PreMutationProbe_RequiresFreshExactLiveCcid(
        string liveCcid,
        string expectedCcid,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainViewModel.HasExactLiveCcidEvidence(
                liveCcid,
                expectedCcid));
    }

    [Fact]
    public void DurableJournalIoFailure_IsClassifiedAsFailClosed()
    {
        Assert.True(MainViewModel.IsDurableImeiJournalFailure(
            new InvalidDataException("corrupt journal")));
        Assert.True(MainViewModel.IsDurableImeiJournalFailure(
            new UnauthorizedAccessException("journal locked")));
        Assert.False(MainViewModel.IsDurableImeiJournalFailure(
            new InvalidOperationException("target conflict")));
    }
}
