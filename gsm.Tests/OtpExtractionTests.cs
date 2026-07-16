using gsm.Services;

namespace gsm.Tests;

public sealed class OtpExtractionTests
{
    [Fact]
    public void VinaphoneAirtimeOffer_DoesNotTreatMoneyAsOtp()
    {
        const string content = "TKC cua Quy Khach sap het. De duoc ung 15 phut thoai (19980d, hieu luc su dung tai khoan Airtime 1400 phut), soan A gui 9345 va dong y cho VNPT xu ly du lieu KH theo chinh sach tai https://my.vnpt.com.vn/tinh-nang/chinh-sach-rieng-tu. Sau 1400 phut, so phut thoai con lai se duoc tu dong thu hoi neu QK khong su dung het. Tu choi nhan loi moi, soan TC A gui 9345. Chi tiet LH 18001091 (mien phi)";

        Assert.Null(GsmModemService.ExtractOtp(content));
    }

    [Theory]
    [InlineData("Ma OTP cua ban la: 123456", "123456")]
    [InlineData("Your verification code is 654321", "654321")]
    [InlineData("123456 la ma xac thuc cua ban", "123456")]
    [InlineData("123456 is your security code", "123456")]
    [InlineData("419955", "419955")]
    [InlineData("(Zalo) Day la ma xac thuc OTP cho SDT (***7003): 419955", "419955")]
    public void RealOtpFormats_AreStillExtracted(string content, string expected)
    {
        Assert.Equal(expected, GsmModemService.ExtractOtp(content));
    }

    [Theory]
    [InlineData("Giao dich 123456 VND da thanh cong")]
    [InlineData("Goi cuoc co 1400 phut, soan DK gui 9345")]
    [InlineData("Lien he 18001091 de biet them chi tiet")]
    public void OrdinaryNumericSms_DoesNotCreateOtp(string content)
    {
        Assert.Null(GsmModemService.ExtractOtp(content));
    }
}
