using gsm.Services;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class SimPresenceEvidenceTests
{
    [Fact]
    public void RemovedSim_ClearsSimTypeAndLastSignalScanFromUiState()
    {
        var port = new SimPort
        {
            SimType = "VINA690",
            PhoneNumber = "0912345678",
            Balance = "10000",
            SautoStatus = "USSDOK",
            LastSignalScanAt = new DateTime(2026, 7, 22, 14, 5, 9)
        };

        MainViewModel.ClearSimScopedState(port);

        Assert.Empty(port.SimType);
        Assert.Empty(port.PhoneNumber);
        Assert.Empty(port.Balance);
        Assert.Empty(port.SautoStatus);
        Assert.Equal(port.Status, port.StatusDisplay);
        Assert.Null(port.LastSignalScanAt);
        Assert.Empty(port.LastSignalScanDisplay);
    }

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

    [Fact]
    public void Successful111_DisplaysUssdOkWithoutChangingOperationalActiveState()
    {
        var port = new SimPort { Status = SimStatus.Active, SautoStatus = "USSDOK" };

        Assert.Equal(SimStatus.Active, port.Status);
        Assert.Equal("USSDOK", port.StatusDisplay);

        port.Status = SimStatus.Connecting;
        Assert.Empty(port.SautoStatus);
        Assert.Equal(SimStatus.Connecting, port.StatusDisplay);
    }

    [Theory]
    [InlineData("+CPIN: NOT READY", "+QSIMSTAT: 1,0", "ERROR", "+CFUN: 1", false, true)]
    [InlineData("+CME ERROR: 10", "+QSIMSTAT: 1,0", "ERROR", "+CFUN: 1", false, true)]
    [InlineData("+CPIN: NOT INSERTED", "", "ERROR", "+CFUN: 1", false, true)]
    public void PollingCycle_ConfirmsRemovalFromIndependentEvidence(
        string cpin,
        string qsimstat,
        string ccid,
        string cfun,
        bool stackDisabled,
        bool expected)
    {
        Assert.Equal(expected, GsmModemService.IsConfirmedSimAbsentDuringPolling(
            cpin, qsimstat, ccid, cfun, stackDisabled));
    }

    [Theory]
    [InlineData("+CPIN: NOT READY", "+QSIMSTAT: 1,0", "ERROR", "+CFUN: 4", false)]
    [InlineData("+CME ERROR: 10", "+QSIMSTAT: 1,0", "ERROR", "+CFUN: 1", true)]
    [InlineData("+CPIN: NOT READY", "+QSIMSTAT: 1,1", "ERROR", "+CFUN: 1", false)]
    [InlineData("+CPIN: NOT READY", "+QSIMSTAT: 1,0", "+QCCID: 89840200011750541573", "+CFUN: 1", false)]
    [InlineData("+CPIN: READY", "+QSIMSTAT: 1,0", "ERROR", "+CFUN: 1", false)]
    public void PollingCycle_DoesNotRemoveSimDuringTransientOrPresentState(
        string cpin,
        string qsimstat,
        string ccid,
        string cfun,
        bool stackDisabled)
    {
        Assert.False(GsmModemService.IsConfirmedSimAbsentDuringPolling(
            cpin, qsimstat, ccid, cfun, stackDisabled));
    }
}
