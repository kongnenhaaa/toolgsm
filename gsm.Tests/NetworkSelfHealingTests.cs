using gsm.Models;
using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class NetworkSelfHealingTests
{
    [Theory]
    [InlineData("89840200011639721552", "45202")]
    [InlineData("89840400011639721552", "45204")]
    [InlineData("89840100011639721552", "45201")]
    public void ForcedOperator_IsBoundToCurrentCcid(string ccid, string expected)
    {
        Assert.Equal([expected], GsmModemService.GetOperatorCodesForCcid(ccid));
    }

    [Fact]
    public void UnknownCcid_DoesNotGuessAForcedOperator()
    {
        Assert.Empty(GsmModemService.GetOperatorCodesForCcid(
            "89999900011639721552"));
    }

    [Theory]
    [InlineData("355008370781449", "355008370781449", true)]
    [InlineData("355008370781440", "355008370781449", true)]
    [InlineData("356230111033216", "355008370781449", false)]
    [InlineData("", "355008370781449", false)]
    public void HardResetRecovery_RequiresExactVerifiedImei(
        string observed,
        string expected,
        bool matches)
    {
        Assert.Equal(
            matches,
            GsmModemService.NetworkRecoveryImeiMatches(observed, expected));
    }

    [Theory]
    [InlineData("89840200011639721552", "356230111033216", "89840200011639721552", "356230111033216", true)]
    [InlineData("89840200011639721552", "356230111033216", "89840400011639721552", "356230111033216", false)]
    [InlineData("89840200011639721552", "356230111033216", "89840200011639721552", "355008370781449", false)]
    public void NetworkPollingFence_RequiresBothCcidAndImei(
        string currentCcid,
        string currentImei,
        string expectedCcid,
        string expectedImei,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.NetworkPollingIdentitiesMatch(
                currentCcid,
                currentImei,
                expectedCcid,
                expectedImei));
    }

    [Fact]
    public void ImeiMutationSuspension_DiscardsPreMutationPolling()
    {
        Assert.False(
            GsmModemService.ShouldPreserveNetworkPollingOnSuspension(
                preserveRequested: false));
        Assert.True(
            GsmModemService.ShouldPreserveNetworkPollingOnSuspension(
                preserveRequested: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HardResetImeiMismatch_IsFatalForCurrentPollingLoop(
        bool exactImeiVerified,
        bool mustAbort)
    {
        Assert.Equal(
            mustAbort,
            GsmModemService.ShouldAbortNetworkPollingAfterHardReset(
                exactImeiVerified));
    }

    private const string Ccid = "89840200011639721552";
    private const string Imei = "356230111033216";

    [Fact]
    public void VerifiedIdentity_CanWaitForNetworkWithoutLosingIdentity()
    {
        var port = new SimPort
        {
            PortName = "COM86",
            Serial = Ccid,
            Imei = Imei,
            Status = SimStatus.Connecting
        };

        Assert.True(MainViewModel.IsVerifiedIdentityReadyForNetwork(
            port, Ccid, Imei, sessionCurrent: true));
        Assert.Equal(SimStatus.Connecting, port.Status);
        Assert.Equal(Ccid, port.Serial);
        Assert.Equal(Imei, port.Imei);
    }

    [Fact]
    public void FinalIdentityGate_AcceptsEc20SpareDigitEquivalentImei()
    {
        Assert.True(MainViewModel.IsVerifiedModemIdentity(
            "+CFUN: 1\r\nOK",
            radioMustBeOff: false,
            liveCcid: Ccid,
            expectedCcid: Ccid,
            liveImei: "355008370781440",
            expectedImei: "355008370781449",
            sessionCurrent: true));
    }

    [Fact]
    public void FinalIdentityGate_StillRejectsDifferentPhysicalCcid()
    {
        Assert.False(MainViewModel.IsVerifiedModemIdentity(
            "+CFUN: 1\r\nOK",
            radioMustBeOff: false,
            liveCcid: "89840200011750541177",
            expectedCcid: Ccid,
            liveImei: Imei,
            expectedImei: Imei,
            sessionCurrent: true));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void NetworkRegistration_PromotesOnlyCurrentSessionOutsideImeiInitialization(
        bool initializationInProgress,
        bool sessionCurrent,
        bool expected)
    {
        var port = new SimPort
        {
            PortName = "COM86",
            Serial = Ccid,
            Imei = Imei,
            Status = SimStatus.Connecting
        };

        Assert.Equal(expected, MainViewModel.CanPromoteNetworkRegistration(
            port, initializationInProgress, sessionCurrent));
    }

    [Fact]
    public void NetworkLoss_DemotesButPreservesVerifiedIdentity()
    {
        var port = new SimPort
        {
            PortName = "COM86",
            Serial = Ccid,
            Imei = Imei,
            Status = SimStatus.Active,
            NetworkProvider = "VinaPhone",
            NetworkType = "4G",
            SignalRssi = 27
        };

        bool demoted = MainViewModel.MarkNetworkRegistrationPending(
            port, sessionCurrent: true, "Đang tự khôi phục đăng ký nhà mạng");

        Assert.True(demoted);
        Assert.Equal(SimStatus.Connecting, port.Status);
        Assert.Equal(Ccid, port.Serial);
        Assert.Equal(Imei, port.Imei);
        Assert.Equal(27, port.SignalRssi);
        Assert.Empty(port.NetworkProvider);
        Assert.Empty(port.NetworkType);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void MissingCops_ReopenEscalationIsBounded(
        int recoveryPasses,
        bool expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ShouldRequestNetworkReopen(recoveryPasses));

    [Theory]
    [InlineData("+CREG: 0,1", "+CGREG: 0,2", "+CEREG: 0,2", "2G")]
    [InlineData("+CREG: 0,2", "+CGREG: 0,5", "+CEREG: 0,2", "3G")]
    [InlineData("+CREG: 0,2", "+CGREG: 0,2", "+CEREG: 0,1", "4G")]
    [InlineData("+CREG: 0,2", "+CGREG: 0,2", "+CEREG: 0,2", "")]
    public void RegistrationFallback_AcceptsAnyRegisteredDomain(
        string creg,
        string cgreg,
        string cereg,
        string expected) =>
        Assert.Equal(
            expected,
            GsmModemService.ResolveRegisteredFallbackNetworkType(
                creg, cgreg, cereg));
}
