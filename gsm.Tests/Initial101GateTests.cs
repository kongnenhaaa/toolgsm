using gsm.Models;
using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class SautoInitialLookupTests
{
    [Theory]
    [InlineData("VinaPhone", "2G", 55, SimStatus.Active, true)]
    [InlineData("VinaPhone", "3G", 55, SimStatus.Active, true)]
    [InlineData("VinaPhone", "4G", 55, SimStatus.Active, true)]
    [InlineData("VinaPhone", "5G", 55, SimStatus.Active, false)]
    [InlineData("Viettel", "3G", 55, SimStatus.Active, false)]
    [InlineData("VinaPhone", "3G", 0, SimStatus.Active, false)]
    [InlineData("VinaPhone", "3G", 55, SimStatus.WaitingAccept, false)]
    public void Initial111_RunsOnlyAfterVinaPhone3gAndSignal(
        string provider, string networkType, int signal, string status, bool expected)
    {
        var port = new SimPort
        {
            NetworkProvider = provider,
            NetworkType = networkType,
            SignalStrength = signal,
            Status = status
        };

        Assert.Equal(expected, MainViewModel.IsVinaNetworkReadyForInitialLookup(port));
    }

    [Theory]
    [InlineData("0", "2G")]
    [InlineData("3", "2G")]
    [InlineData("2", "3G")]
    [InlineData("6", "3G")]
    [InlineData("7", "4G")]
    [InlineData("9", "4G")]
    public void CopsAccessTechnology_MapsToUiNetworkType(string act, string expected) =>
        Assert.Equal(expected, GsmModemService.MapCopsAccessTechnology(act));

    [Theory]
    [InlineData("+CSQ: 30,99\r\nOK", 30, 97, "GOOD 30")]
    [InlineData("+CSQ: 23,99\r\nOK", 23, 74, "GOOD 23")]
    [InlineData("+CSQ: 15,99\r\nOK", 15, 48, "NORMAL 15")]
    [InlineData("+CSQ: 8,99\r\nOK", 8, 26, "WEAK 8")]
    [InlineData("+CSQ: 99,99\r\nOK", 99, 0, "")]
    public void CsqResponse_ProducesRawAndPercentForUi(
        string response, int expectedRssi, int expectedPercent, string expectedDisplay)
    {
        Assert.True(MainViewModel.TryParseCsqResponse(response, out int rssi, out int percent));
        var port = new SimPort { SignalRssi = rssi, SignalStrength = percent };

        Assert.Equal(expectedRssi, rssi);
        Assert.Equal(expectedPercent, percent);
        Assert.Equal(expectedDisplay, port.SignalDisplay);
    }

    [Theory]
    [InlineData("TB :0949561698,Ngay KH:07/07/2026", "0949561698")]
    [InlineData("Thue bao 84946223826", "0946223826")]
    [InlineData("MSISDN: 0912345678", "0912345678")]
    public void Sauto111Response_ExtractsCompletePhoneNumber(string response, string expected) =>
        Assert.Equal(expected, MainViewModel.ExtractPhoneNumberFromUssd(response));

    [Fact]
    public void Sauto111MenuResponse_ExtractsPhoneAndActivationDateWithoutReadingMenuDigits()
    {
        const string response = """
            +CUSD: 1,"TB :0915496792,Ngay KH:23/06/2026.Bam so tuong ung de tra cuu:
            1-TK bang tien
            2-TK luu luong thoai
            3-TK luu luong SMS
            4-TK luu luong data",15
            """;

        Assert.Equal("0915496792", MainViewModel.ExtractPhoneNumberFromUssd(response));
        Assert.Equal("23/06/2026", MainViewModel.ExtractSimRegDateFromUssd(response));
    }

    [Theory]
    [InlineData("354434778044431", "354434778044431", true)]
    [InlineData("\r\n354434778044431\r\nOK", "354434778044431", true)]
    [InlineData("353982261250411", "354434778044431", false)]
    [InlineData("", "354434778044431", false)]
    [InlineData("354434778044431", "", false)]
    public void RefreshedSession_ResumesOnlyWithPreviouslyVerifiedImei(
        string currentImei, string verifiedImei, bool expected) =>
        Assert.Equal(expected, MainViewModel.IsVerifiedImeiResumeMatch(currentImei, verifiedImei));
}
