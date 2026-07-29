using gsm.Models;
using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class SautoInitialLookupTests
{
    [Theory]
    [InlineData("0", "2G")]
    [InlineData("1", "GSM")]
    [InlineData("3", "2G")]
    [InlineData("2", "3G")]
    [InlineData("6", "3G")]
    [InlineData("7", "4G")]
    [InlineData("9", "Unknown")]
    public void CopsAccessTechnology_MapsToUiNetworkType(string act, string expected) =>
        Assert.Equal(expected, GsmModemService.MapSautoCopsAccessTechnology(act));

    [Theory]
    [InlineData("VinaPhone", "*101#")]
    [InlineData("Viettel", "")]
    [InlineData("MobiFone", "")]
    [InlineData("Vietnamobile", "")]
    [InlineData("VNSKY", "")]
    [InlineData("45202", "")]
    public void AutomaticCarrierUssd_UsesSautoMapping(string provider, string expected) =>
        Assert.Equal(expected, GsmModemService.GetSautoAutomaticUssdCode(provider));

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
    [InlineData("TKKM: 25.000d", "25.000")]
    [InlineData("Tai khoan khuyen mai 15000 VND", "15000")]
    [InlineData("Khuyến mãi: 9,500đ", "9,500")]
    [InlineData("SMS khuyen mai hom nay", "")]
    public void UssdPromotionBalance_IsParsedOnlyFromLabeledCurrency(
        string response,
        string expected) =>
        Assert.Equal(expected, MainViewModel.ExtractPromotionBalanceFromUssd(response));

    [Theory]
    [InlineData("89840200000000000003", "89840200000000000003", SimStatus.Active, true, true)]
    [InlineData("89840200000000000003", "89840200000000000003", SimStatus.Active, false, false)]
    [InlineData("89840200000000000003", "89840200000000000003", SimStatus.Connecting, true, false)]
    [InlineData("89840200000000000003", "89840200000000000003", SimStatus.WaitingAccept, true, false)]
    [InlineData("89840200000000000003", "89840200000000000003", SimStatus.SecurityBlocked, true, false)]
    [InlineData("89840200000000000003", "89840200000000000004", SimStatus.Active, true, false)]
    public void DetectedCcid_IsIgnoredOnlyWhenSameSimIsAlreadyActive(
        string currentCcid,
        string detectedCcid,
        string status,
        bool currentSessionMatches,
        bool expected) =>
        Assert.Equal(
            expected,
            MainViewModel.ShouldIgnoreDetectedCcid(
                currentCcid,
                detectedCcid,
                status,
                currentSessionMatches));
}
