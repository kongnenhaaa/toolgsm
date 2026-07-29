using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class ImeiIdentityTests
{
    [Theory]
    [InlineData("352054261826334")]
    [InlineData("358226401760615")]
    [InlineData("351488165212715")]
    [InlineData("355008370781449")]
    public void BackupImei_WithCorrectLuhn_IsValid(string imei)
    {
        Assert.True(ImeiManagementService.IsValidImei(imei));
    }

    [Theory]
    [InlineData("353009115223120")]
    [InlineData("352936301037170")]
    [InlineData("356847842519710")]
    public void BackupImei_WithWrongCheckDigit_IsRejected(string imei)
    {
        Assert.False(ImeiManagementService.IsValidImei(imei));
    }

    [Theory]
    [InlineData("351488165212715", "351488165212710")]
    [InlineData("355008370781449", "355008370781440")]
    public void NetworkSpareZero_IsEquivalentToPrintedCheckDigit(string backup, string networkForm)
    {
        Assert.True(ImeiManagementService.AreEquivalentImei(backup, networkForm));
        Assert.True(ImeiManagementService.IsUsableObservedImei(networkForm));
        Assert.Equal(backup, ImeiManagementService.ToCanonicalImei(networkForm));
    }

    [Fact]
    public void DifferentTacAndSerial_AreNotEquivalent()
    {
        Assert.False(ImeiManagementService.AreEquivalentImei(
            "351488165212715",
            "356847842519710"));
    }

    [Fact]
    public void NofakeNetworkPromotion_AcceptsObservedSpareZeroImei()
    {
        var port = new gsm.Models.SimPort
        {
            Status = gsm.Models.SimStatus.Connecting,
            Serial = "89840200000000000003",
            Imei = "351488165212710"
        };

        Assert.True(MainViewModel.CanPromoteNetworkRegistration(
            port,
            initializationInProgress: false,
            sessionCurrent: true));
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("351488165212715", "", true)]
    [InlineData("351488165212715", "351488165212710", true)]
    [InlineData("356847842519710", "351488165212715", false)]
    public void NofakeNetworkRecovery_TreatsMissingExpectedImeiAsInformational(
        string observedImei,
        string expectedImei,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.NetworkRecoveryImeiMatches(
                observedImei,
                expectedImei));
    }
}
