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
    [InlineData("ERROR: Timeout waiting for lock")]
    [InlineData("ERROR: Another command is already in progress")]
    public void NetworkPolling_ContentionResponseIsDeferred(string response)
    {
        Assert.True(GsmModemService.IsDeferredNetworkPollingResponse(response));
        Assert.False(GsmModemService.ShouldReportNetworkLoss(
            response,
            GsmModemService.NetworkLossConfirmationMisses + 10));
    }

    [Theory]
    [InlineData("ERROR: Timeout (device did not return OK/ERROR)", 1, false)]
    [InlineData("ERROR: Timeout (device did not return OK/ERROR)", 2, false)]
    [InlineData("ERROR: Timeout (device did not return OK/ERROR)", 3, true)]
    [InlineData("+COPS: 0", 3, true)]
    [InlineData("+COPS: 0,0,\"VinaPhone VINAPHONE\",7", 10, false)]
    public void NetworkLoss_RequiresRepeatedNonContentionCopsMisses(
        string response,
        int consecutiveMisses,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.ShouldReportNetworkLoss(
                response,
                consecutiveMisses));
    }

    [Fact]
    public void NetworkLoss_ContentionPreservesButDoesNotIncreaseEvidence()
    {
        int misses = GsmModemService.NextNetworkLossMissCount(
            0,
            "ERROR: Timeout (device did not return OK/ERROR)");
        Assert.Equal(1, misses);

        misses = GsmModemService.NextNetworkLossMissCount(
            misses,
            "ERROR: Timeout waiting for lock");
        Assert.Equal(1, misses);

        misses = GsmModemService.NextNetworkLossMissCount(
            misses,
            "ERROR: Timeout (device did not return OK/ERROR)");
        Assert.Equal(2, misses);
        Assert.False(GsmModemService.ShouldReportNetworkLoss(
            "ERROR: Timeout (device did not return OK/ERROR)",
            misses));
    }

    [Theory]
    [InlineData("+CREG: 0,0", "+CGREG: 0,2", "+CEREG: 0,3", true)]
    [InlineData("+CREG: 0,1", "+CGREG: 0,2", "+CEREG: 0,3", false)]
    [InlineData("+CREG: 0,2", "ERROR: Timeout", "+CEREG: 0,3", false)]
    public void ExplicitRegistrationLoss_RequiresAllDomainsToConfirmUnregistered(
        string creg,
        string cgreg,
        string cereg,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.AreAllRegistrationDomainsExplicitlyUnregistered(
                creg,
                cgreg,
                cereg));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(15, 15)]
    [InlineData(300, 15)]
    public void RegistrationProbeCadence_IsCappedForFastRecovery(
        int configuredSeconds,
        int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            GsmModemService.GetNetworkRegistrationProbeInterval(
                configuredSeconds));
    }

    [Fact]
    public void NetworkLoss_RegisteredFallbackKeepsNetworkConfirmed()
    {
        string networkType =
            GsmModemService.ResolveRegisteredFallbackNetworkType(
                "+CREG: 0,2",
                "+CGREG: 0,5",
                "+CEREG: 0,2");

        Assert.Equal("3G", networkType);
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
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(9, true)]
    public void MissingCops_ReopenLoopStopsAfterBudget(
        int reopenAttempt,
        bool expected) =>
        Assert.Equal(
            expected,
            MainViewModel.ShouldAbandonNetworkReopen(reopenAttempt));

    [Fact]
    public void ReopenBudget_IsScopedToPortAndCcid()
    {
        Assert.Equal(
            MainViewModel.BuildNetworkReopenKey("COM93", Ccid),
            MainViewModel.BuildNetworkReopenKey("COM93", $" {Ccid} "));
        Assert.NotEqual(
            MainViewModel.BuildNetworkReopenKey("COM93", Ccid),
            MainViewModel.BuildNetworkReopenKey("COM93", "89840200011735319913"));
        Assert.NotEqual(
            MainViewModel.BuildNetworkReopenKey("COM93", Ccid),
            MainViewModel.BuildNetworkReopenKey("COM107", Ccid));
    }

    [Theory]
    [InlineData("[NETWORK_REOPEN_REQUIRED] reason=identity-reverify; expected_ccid=1", true)]
    [InlineData("[NETWORK_REOPEN_REQUIRED] Đã thử 6 lượt auto-select/detach/RF", false)]
    public void IdentityReverifyReopen_IsExemptFromRegistrationBudget(
        string data,
        bool expected) =>
        Assert.Equal(expected, MainViewModel.IsIdentityReverifyReopen(data));

    [Fact]
    public void ExhaustedReopenBudget_EndsNetworkPhaseWithoutTouchingImei()
    {
        var port = new SimPort
        {
            PortName = "COM93",
            Serial = Ccid,
            Imei = Imei,
            Status = SimStatus.Connecting,
            NetworkProvider = "VinaPhone",
            NetworkType = "4G",
            SignalRssi = 27
        };

        MainViewModel.MarkNetworkRegistrationUnavailable(
            port, "Có sóng nhưng không đăng ký được nhà mạng");

        Assert.Equal(SimStatus.NetworkUnavailable, port.Status);
        Assert.NotEqual(SimStatus.Connecting, port.Status);
        Assert.Equal(Ccid, port.Serial);
        Assert.Equal(Imei, port.Imei);
        Assert.Equal(27, port.SignalRssi);
        Assert.Empty(port.NetworkProvider);
        Assert.Empty(port.NetworkType);
    }

    [Fact]
    public void AbandonedNetworkPhase_StillPromotesOnLateRegistration()
    {
        var port = new SimPort
        {
            PortName = "COM93",
            Serial = Ccid,
            Imei = Imei,
            Status = SimStatus.NetworkUnavailable
        };

        Assert.True(MainViewModel.CanPromoteNetworkRegistration(
            port,
            initializationInProgress: false,
            sessionCurrent: true));
    }

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
