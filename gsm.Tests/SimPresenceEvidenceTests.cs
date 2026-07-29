using gsm.Models;
using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class SimPresenceEvidenceTests
{
    [Fact]
    public void RemovedSim_ClearsSimScopedUiState()
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
        Assert.Null(port.LastSignalScanAt);
        Assert.Empty(port.LastSignalScanDisplay);
    }

    [Fact]
    public void WaitingForSim_PreservesFreshlyReadModemImei()
    {
        var port = new SimPort
        {
            Imei = "old",
            Serial = "89840200011750541573",
            PhoneNumber = "0912345678"
        };

        MainViewModel.ClearSimScopedState(
            port,
            "current slot 7 = 353982266666926");

        Assert.Equal("353982266666926", port.Imei);
        Assert.Empty(port.Serial);
        Assert.Empty(port.PhoneNumber);
    }

    [Theory]
    [InlineData("+QCCID: 89840200011750541573\r\nOK", true)]
    [InlineData("8984020001175054112\r\nOK", true)]
    [InlineData("+CME ERROR: SIM failure", false)]
    [InlineData("OK", false)]
    public void ReadableCcid_IsRecognized(string response, bool expected) =>
        Assert.Equal(expected, GsmModemService.HasReadableCcid(response));

    [Fact]
    public void Successful111_DoesNotReplaceOperationalActiveState()
    {
        var port = new SimPort { Status = SimStatus.Active, SautoStatus = "USSDOK" };

        Assert.Equal(SimStatus.Active, port.Status);
        Assert.Equal("USSD OK", port.StatusDisplay);
    }
}
