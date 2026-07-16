using gsm.Services;

namespace gsm.Tests;

public sealed class SimPresenceEvidenceTests
{
    [Theory]
    [InlineData("+CPIN: NOT READY", false)]
    [InlineData("+CME ERROR: 10", false)]
    [InlineData("+CME ERROR: 13", false)]
    public void TransientCpinFailure_WithoutRemovalUrc_DoesNotRemoveActiveSim(
        string response,
        bool expected)
    {
        Assert.Equal(expected, GsmModemService.ShouldVerifySimRemoval(
            response, stackDisabledByTool: false, removalUrcPending: false));
    }

    [Theory]
    [InlineData("+CPIN: NOT INSERTED", false, false, true)]
    [InlineData("+CPIN: NOT READY", false, true, true)]
    [InlineData("+CME ERROR: 10", false, true, true)]
    [InlineData("+CPIN: NOT INSERTED", true, true, false)]
    public void RemovalCandidate_RequiresReliableEvidenceAndEnabledStack(
        string response,
        bool stackDisabled,
        bool urcPending,
        bool expected)
    {
        Assert.Equal(expected, GsmModemService.ShouldVerifySimRemoval(
            response, stackDisabled, urcPending));
    }

    [Theory]
    [InlineData("+QCCID: 89840200011750541573\r\nOK", true)]
    [InlineData("8984020001175054112\r\nOK", true)]
    [InlineData("+CME ERROR: SIM failure", false)]
    [InlineData("OK", false)]
    public void ReadableCcid_IsStrongEvidenceThatSimIsStillPresent(string response, bool expected)
    {
        Assert.Equal(expected, GsmModemService.HasReadableCcid(response));
    }
}
