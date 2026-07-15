using gsm.Services;

namespace gsm.Tests;

public sealed class PortSessionRegistryTests
{
    private const string CcidA = "89840123456789012345";
    private const string CcidB = "89840987654321098765";

    [Fact]
    public void Begin_NewSessionOnSameCom_CancelsOldLeaseAndAdvancesEpoch()
    {
        using var registry = new PortSessionRegistry();
        PortSessionLease first = registry.Begin("COM3", CcidA);
        PortSessionLease second = registry.Begin("COM3", CcidB);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Epoch > first.Epoch);
        Assert.False(registry.IsCurrent("COM3", CcidA, first.Epoch));
        Assert.True(registry.IsCurrent("COM3", CcidB, second.Epoch));
    }

    [Fact]
    public void Invalidate_OneCom_DoesNotCancelOtherCom()
    {
        using var registry = new PortSessionRegistry();
        PortSessionLease com3 = registry.Begin("COM3", CcidA);
        PortSessionLease com4 = registry.Begin("COM4", CcidB);

        registry.Invalidate("COM3");

        Assert.True(com3.Token.IsCancellationRequested);
        Assert.False(com4.Token.IsCancellationRequested);
        Assert.True(registry.IsCurrent("COM4", CcidB, com4.Epoch));
    }

    [Fact]
    public void TryGet_NormalizesCcidResponse()
    {
        using var registry = new PortSessionRegistry();
        registry.Begin("COM7", $"+QCCID: {CcidA}\r\nOK");

        Assert.True(registry.TryGet("COM7", out PortSessionLease lease));
        Assert.Equal(CcidA, lease.Ccid);
    }
}
