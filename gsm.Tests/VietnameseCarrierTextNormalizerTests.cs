using gsm.Services;
using gsm.Models;

namespace gsm.Tests;

public sealed class VietnameseCarrierTextNormalizerTests
{
    [Fact]
    public void RestoreForDisplay_RestoresKnownRemainingDataTemplate()
    {
        const string input =
            "Dung luong Data con lai cua goi MI_BIGKM_TR_OCS: 0 MB, HSD: 09:17 ngay 13/04/2027. " +
            "HSD goi cuoc: 09:17 ngay 13/04/2027. QK co the tra cuu chi tiet dung luong con lai va cac UU DAI.";

        string result = VietnameseCarrierTextNormalizer.RestoreForDisplay(input);

        Assert.Equal(
            "Dung lượng Data còn lại của gói MI_BIGKM_TR_OCS: 0 MB, HSD: 09:17 ngày 13/04/2027. " +
            "HSD gói cước: 09:17 ngày 13/04/2027. QK có thể tra cứu chi tiết dung lượng còn lại và các ưu đãi.",
            result);
    }

    [Fact]
    public void RestoreForDisplay_RestoresKnownNoPackageTemplateWithoutChangingUrlOrCodes()
    {
        const string input =
            "Quy khach hien khong dang ky su dung goi cuoc. Vui long soan tin CTKM gui 900 hoac truy cap " +
            "My VNPT tai https://my.vnpt.com.vn/adv/muagoicuoc de tham khao cac goi cuoc UU DAI.";

        string result = VietnameseCarrierTextNormalizer.RestoreForDisplay(input);

        Assert.Equal(
            "Quý khách hiện không đăng ký sử dụng gói cước. Vui lòng soạn tin CTKM gửi 900 hoặc truy cập " +
            "My VNPT tại https://my.vnpt.com.vn/adv/muagoicuoc để tham khảo các gói cước ưu đãi.",
            result);
    }

    [Theory]
    [InlineData("Ma OTP cua ban la 123456")]
    [InlineData("Nội dung tiếng Việt đã có dấu")]
    [InlineData("Ban goi ma nao thi gui lai ma do.")]
    public void RestoreForDisplay_LeavesUnknownOrNonTemplateTextUnchanged(string input)
    {
        Assert.Equal(input, VietnameseCarrierTextNormalizer.RestoreForDisplay(input));
    }

    [Fact]
    public void RestoreForDisplay_RestoresKnownTemplateRegardlessOfSender()
    {
        const string input = "Dung luong Data con lai cua goi TEST";

        Assert.Equal(
            "Dung lượng Data còn lại của gói TEST",
            VietnameseCarrierTextNormalizer.RestoreForDisplay(input));
    }

    [Fact]
    public void RestoreForDisplay_IsIdempotent()
    {
        const string input =
            "Dung luong Data con lai cua goi TEST: 0 MB. HSD goi cuoc: 09:17 ngay 13/04/2027.";

        string once = VietnameseCarrierTextNormalizer.RestoreForDisplay(input);

        Assert.Equal(once, VietnameseCarrierTextNormalizer.RestoreForDisplay(once));
    }

    [Fact]
    public void RestoreForDisplay_DoesNotGuessAmbiguousVietnamese()
    {
        const string ambiguous = "Ban goi ma nao thi gui lai ma do.";

        Assert.Equal(
            ambiguous,
            VietnameseCarrierTextNormalizer.RestoreForDisplay(ambiguous));
    }

    [Fact]
    public void SmsMessage_PreservesRawCarrierContentWhileDisplayingRestoredText()
    {
        const string raw = "Quy khach hien khong dang ky su dung goi cuoc.";
        var message = new SmsMessage { Sender = "888", Content = raw };

        Assert.Equal(raw, message.Content);
        Assert.Equal(
            "Quý khách hiện không đăng ký sử dụng gói cước.",
            message.DisplayContent);
    }
}
