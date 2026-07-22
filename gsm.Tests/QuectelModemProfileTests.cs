using gsm.Models;
using gsm.Services;

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
        Assert.True(profile.Supports(ModemCapability.CallerIdPresentation));
        Assert.True(profile.Supports(ModemCapability.CallStatusIndication));
        Assert.True(profile.Supports(ModemCapability.AudioPlayback));
        Assert.True(profile.Supports(ModemCapability.FileStorage));
        Assert.True(profile.Supports(ModemCapability.Gnss));
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

    [Fact]
    public void Ec20StoredSms_PrefersQcmgrMetadataBeforeStandardCmgrFallback()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Quectel", "EC20F", "EC20CEFILGR06A05M1G");

        Assert.Equal(
            ["AT+QCMGR=7", "AT+CMGF=0", "AT+CMGR=7"],
            GsmModemService.GetStoredSmsReadCommandOrder(profile, "7"));
    }

    [Fact]
    public void GenericStoredSms_UsesStandardCmgrOnly()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Generic", "LTE Modem", "1.0");

        Assert.Equal(
            ["AT+CMGR=3"],
            GsmModemService.GetStoredSmsReadCommandOrder(profile, "3"));
    }

    [Fact]
    public void HdlFirmware_AvoidsOnlyTheVerifiedClipReadbackQuirk()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Quectel",
            "EC20F",
            "Quectel EC20F Revision: EC20CEHDLGR08A05M1G");

        Assert.Equal("EC20CEHDLGR08A05M1G", profile.FirmwareRevision);
        Assert.True(profile.HasQuirk(ModemQuirk.ClipReadHangs));
        Assert.False(profile.Supports(ModemCapability.WifiControl));
    }

    [Fact]
    public void FagrFirmware_EnablesAdvertisedWifiBluetoothAndEmergencyFamilies()
    {
        QuectelModemProfile profile = QuectelModemProfile.FromIdentity(
            "Quectel",
            "EC20F",
            "garbage Quectel EC20F Revision: EC20CEFAGR08A03M4G");

        Assert.Equal("EC20CEFAGR08A03M4G", profile.FirmwareRevision);
        Assert.True(profile.Supports(ModemCapability.WifiControl));
        Assert.True(profile.Supports(ModemCapability.BluetoothControl));
        Assert.True(profile.Supports(ModemCapability.EmergencyCall));
        Assert.False(profile.HasQuirk(ModemQuirk.ClipReadHangs));
    }

    [Fact]
    public void Ec20Playback_UsesCompleteQpsndSyntaxAndNeverLocalOnlyQaudplay()
    {
        IReadOnlyList<string> commands = GsmModemService.GetCallAudioPlaybackCommandOrder("call-play.wav");

        Assert.Equal(
            [
                "AT+QPSND=1,\"call-play.wav\",0,1,1",
                "AT+QPSND=1,\"ufs:call-play.wav\",0,1,1"
            ],
            commands);
        Assert.DoesNotContain(commands, command => command.Contains("QAUDPLAY", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("+CLCC: 1,1,0,1,0,\"\",128\r\nOK", false)]
    [InlineData("+CLCC: 2,0,2,0,0,\"0912345678\",129\r\nOK", false)]
    [InlineData("+CLCC: 2,0,0,0,0,\"0912345678\",129\r\nOK", true)]
    public void ActiveVoiceDetection_IgnoresPermanentImsDataRows(string response, bool expected)
    {
        Assert.Equal(expected, GsmModemService.HasActiveOutgoingVoiceSession(response));
    }
}
