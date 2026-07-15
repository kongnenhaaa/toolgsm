using gsm.Models;

namespace gsm.Tests;

public class QuectelModemProfileTests
{
    [Fact]
    public void Ec20_EnablesVoiceSmsAudioAndHttpCapabilities()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Quectel", "EC20F", "EC20CEHCLGR06A04M1G");

        Assert.True(profile.IsQuectel);
        Assert.True(profile.Supports(ModemCapability.VoiceCall));
        Assert.True(profile.Supports(ModemCapability.QuectelStoredSms));
        Assert.True(profile.Supports(ModemCapability.AudioRecord));
        Assert.True(profile.Supports(ModemCapability.HttpData));
        Assert.True(profile.Supports(ModemCapability.NetworkScanConfig));
    }

    [Fact]
    public void Bg95_DoesNotAssumeVoiceAudioOrIms()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Quectel", "BG95-M3", "BG95M3LAR02A03");

        Assert.True(profile.IsQuectel);
        Assert.True(profile.Supports(ModemCapability.HttpData));
        Assert.False(profile.Supports(ModemCapability.VoiceCall));
        Assert.False(profile.Supports(ModemCapability.AudioRecord));
        Assert.False(profile.Supports(ModemCapability.ImsConfig));
    }

    [Fact]
    public void GenericModem_UsesOnlyStandardCommands()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Generic", "LTE Modem", "1.0");

        Assert.False(profile.IsQuectel);
        Assert.Equal(ModemCapability.None, profile.Capabilities);
    }
}
