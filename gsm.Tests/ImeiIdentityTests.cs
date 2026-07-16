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

    [Theory]
    [InlineData("351488165212710", "351488165212715")]
    [InlineData("355008370781440", "355008370781449")]
    public void LegacyBackupSpareZero_IsCanonicalizedBeforeRestore(string stored, string expected)
    {
        Assert.True(ImeiManagementService.TryNormalizeBackupImei(stored, out string canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("+EGMR: \"351488165212710\"\r\nOK", "351488165212715", true)]
    [InlineData("+EGMR: \"355008370781449\"\r\nOK", "351488165212715", false)]
    [InlineData("ERROR", "351488165212715", true)]
    [InlineData("OK", "351488165212715", true)]
    public void StoredImei_WhenReported_MustMatchTarget(
        string response,
        string expected,
        bool matches)
    {
        Assert.Equal(matches, ImeiManagementService.StoredImeiMatchesOrUnavailable(response, expected));
    }

    [Theory]
    [InlineData("+CFUN: 0\r\nOK", true)]
    [InlineData("+CFUN: 4\r\nOK", true)]
    [InlineData("+CFUN: 1\r\nOK", false)]
    [InlineData("TIMEOUT", false)]
    [InlineData("ERROR", false)]
    public void CcidVerification_IsDeferredOnlyWhenRadioStackIsExplicitlyDisabled(
        string response,
        bool expected)
    {
        Assert.Equal(expected, MainViewModel.IsRadioStackDisabled(response));
    }
}
