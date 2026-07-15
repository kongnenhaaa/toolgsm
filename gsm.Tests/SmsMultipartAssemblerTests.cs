using System.Text;
using gsm.Services;

namespace gsm.Tests;

public class SmsMultipartAssemblerTests
{
    [Fact]
    public void ShortMultilineSms_IsNotCutToLongestLine()
    {
        string raw = "+CMGR: \"REC UNREAD\",\"ZALO\"\r\nDong dau ngan\r\nMa OTP cua ban la 123456\r\nDong cuoi\r\nOK\r\n";

        DecodedSmsBody result = SmsBodyDecoder.Decode(raw);

        Assert.Equal("Dong dau ngan\nMa OTP cua ban la 123456\nDong cuoi", result.Content);
        Assert.Null(result.Concatenation);
        Assert.Equal("123456", GsmModemService.ExtractOtp(result.Content));
    }

    [Fact]
    public void EchoedCmgrCommand_IsNotIncludedInSmsBody()
    {
        string raw = "AT+QCMGR=3\r\n+QCMGR: \"REC UNREAD\",\"ZALO\"\r\nOTP 123456\r\nOK\r\n";

        Assert.Equal("OTP 123456", SmsBodyDecoder.Decode(raw).Content);
    }

    [Fact]
    public void Udh8Bit_IsDecodedAndPartsAssembleOutOfOrderWithoutChangingText()
    {
        var assembler = new SmsMultipartAssembler();
        DecodedSmsBody second = DecodeUcs2Part(new byte[] { 0x05, 0x00, 0x03, 0xA7, 0x02, 0x02 }, "3456. Khong chia se.");
        DecodedSmsBody first = DecodeUcs2Part(new byte[] { 0x05, 0x00, 0x03, 0xA7, 0x02, 0x01 }, "Ma OTP cua ban la 12");

        Assert.Equal(SmsAssemblyStatus.Waiting, assembler.Add("COM3", "ZALO", second.Concatenation!, second.Content, "22").Status);
        SmsAssemblyResult complete = assembler.Add("COM3", "ZALO", first.Concatenation!, first.Content, "21");

        Assert.Equal(SmsAssemblyStatus.Completed, complete.Status);
        Assert.Equal("Ma OTP cua ban la 123456. Khong chia se.", complete.Content);
        Assert.Equal("123456", GsmModemService.ExtractOtp(complete.Content));
        Assert.Equal(new[] { "21", "22" }, complete.MessageIndices.OrderBy(x => x));
    }

    [Fact]
    public void Udh16Bit_IsParsedCorrectly()
    {
        DecodedSmsBody part = DecodeUcs2Part(new byte[] { 0x06, 0x08, 0x04, 0x12, 0x34, 0x03, 0x02 }, "noi dung");
        Assert.Equal(new SmsConcatInfo(0x1234, 3, 2), part.Concatenation);
        Assert.Equal("noi dung", part.Content);
    }

    [Fact]
    public void Ec20AlignmentByteBeforeUdh_IsIgnoredAndMultipartMetadataSurvives()
    {
        DecodedSmsBody part = DecodeUcs2Part(new byte[] { 0x00, 0x05, 0x00, 0x03, 0x2A, 0x02, 0x01 }, "noi dung");

        Assert.Equal(new SmsConcatInfo(0x2A, 2, 1), part.Concatenation);
        Assert.Equal("noi dung", part.Content);
    }

    [Fact]
    public void QuectelQcmgrMetadata_IsUsedWhenFirmwareRemovesUdhFromTextBody()
    {
        string raw = "+QCMGR: \"REC UNREAD\",\"ZALO\",,\"26/07/15,10:00:00+28\",4660,2,3\r\nphan thu hai\r\nOK\r\n";

        DecodedSmsBody result = SmsBodyDecoder.Decode(raw);

        Assert.Equal("phan thu hai", result.Content);
        Assert.Equal(new SmsConcatInfo(4660, 3, 2), result.Concatenation);
    }

    [Fact]
    public void NormalQcmgrMessage_DoesNotBecomeFalseMultipart()
    {
        string raw = "+QCMGR: \"REC UNREAD\",\"ZALO\",\"\",\"26/07/15,10:00:00+28\",145,0,0,8,\"+84900000000\",145,20\r\nOTP 654321\r\nOK\r\n";
        Assert.Null(SmsBodyDecoder.Decode(raw).Concatenation);
    }

    [Fact]
    public void Ec20DecimalAsciiSender_IsDecodedWithoutChangingPhoneNumbers()
    {
        Assert.Equal("VinaPhone", GsmModemService.DecodeSmsSender("861051109780104111110101"));
        Assert.Equal("84901234567", GsmModemService.DecodeSmsSender("84901234567"));
    }

    [Fact]
    public void ConcurrentMessages_AreSeparatedByPortSenderReferenceAndTotal()
    {
        var a = new SmsMultipartAssembler();
        a.Add("COM1", "ZALO", new(1, 2, 1), "A1", "1");
        a.Add("COM1", "ZALO", new(2, 2, 1), "B1", "2");
        a.Add("COM2", "ZALO", new(1, 2, 1), "C1", "3");

        Assert.Equal("B1B2", a.Add("COM1", "ZALO", new(2, 2, 2), "B2", "4").Content);
        Assert.Equal("A1A2", a.Add("COM1", "ZALO", new(1, 2, 2), "A2", "5").Content);
        Assert.Equal("C1C2", a.Add("COM2", "ZALO", new(1, 2, 2), "C2", "6").Content);
    }

    [Fact]
    public void DuplicateAndInvalidParts_CannotCreateCorruptCompletion()
    {
        var a = new SmsMultipartAssembler();
        Assert.Equal(SmsAssemblyStatus.Waiting, a.Add("COM1", "WA", new(9, 2, 1), "abc", "10").Status);
        Assert.Equal(SmsAssemblyStatus.Duplicate, a.Add("COM1", "WA", new(9, 2, 1), "abc", "10").Status);
        Assert.Equal(SmsAssemblyStatus.Conflict, a.Add("COM1", "WA", new(9, 2, 1), "changed", "11").Status);
        Assert.Equal(SmsAssemblyStatus.Invalid, a.Add("COM1", "WA", new(9, 2, 3), "bad", "12").Status);
        Assert.Equal("abcdef", a.Add("COM1", "WA", new(9, 2, 2), "def", "13").Content);
    }

    [Fact]
    public void MissingPart_ExpiresWithoutEmittingPartialOtp()
    {
        var a = new SmsMultipartAssembler(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        a.Add("COM1", "ZALO", new(7, 2, 1), "OTP 123", "7", start);

        Assert.Equal(1, a.RemoveExpired(start.AddSeconds(6)));
        SmsAssemblyResult result = a.Add("COM1", "ZALO", new(7, 2, 2), "456", "8", start.AddSeconds(7));
        Assert.Equal(SmsAssemblyStatus.Waiting, result.Status);
        Assert.Null(result.Content);
    }

    [Fact]
    public void CompletedStoredParts_AreNotEmittedTwiceDuringOverlappingSweeps()
    {
        var a = new SmsMultipartAssembler();
        a.Add("COM1", "ZALO", new(5, 2, 1), "one", "31");
        Assert.Equal(SmsAssemblyStatus.Completed, a.Add("COM1", "ZALO", new(5, 2, 2), "two", "32").Status);
        Assert.Equal(SmsAssemblyStatus.Duplicate, a.Add("COM1", "ZALO", new(5, 2, 1), "one", "31").Status);
        Assert.Equal(SmsAssemblyStatus.Duplicate, a.Add("COM1", "ZALO", new(5, 2, 2), "two", "32").Status);
    }

    [Fact]
    public void Ec20WithoutUdh_HoldsFullSizedPartsUntilShortFinalPartArrives()
    {
        var a = new SmsImplicitMultipartAssembler();
        string p1 = new('A', 67);
        string p2 = new('B', 67);

        Assert.Equal(SmsAssemblyStatus.Waiting, a.Add("COM74", "VinaPhone", p1, "0").Status);
        Assert.Equal(SmsAssemblyStatus.Waiting, a.Add("COM74", "VinaPhone", p2, "1").Status);
        SmsAssemblyResult result = a.Add("COM74", "VinaPhone", "cuoi", "2");

        Assert.Equal(SmsAssemblyStatus.Completed, result.Status);
        Assert.Equal(p1 + p2 + "cuoi", result.Content);
        Assert.Equal(new[] { "0", "1", "2" }, result.MessageIndices);
        Assert.Equal(SmsAssemblyStatus.Duplicate, a.Add("COM74", "VinaPhone", p1, "0").Status);
    }

    [Fact]
    public void Ec20WithoutUdh_NeverEmitsOrDeletesIncompleteLongMessage()
    {
        var a = new SmsImplicitMultipartAssembler(TimeSpan.FromSeconds(5));
        DateTimeOffset start = DateTimeOffset.UtcNow;
        SmsAssemblyResult waiting = a.Add("COM18", "VinaPhone", new string('X', 67), "3", start);

        Assert.Equal(SmsAssemblyStatus.Waiting, waiting.Status);
        Assert.Null(waiting.Content);
        Assert.Empty(waiting.MessageIndices);
        Assert.Equal(1, a.RemoveExpired(start.AddSeconds(6)));
    }

    private static DecodedSmsBody DecodeUcs2Part(byte[] udh, string content)
    {
        byte[] text = Encoding.BigEndianUnicode.GetBytes(content);
        return SmsBodyDecoder.Decode(Convert.ToHexString(udh.Concat(text).ToArray()));
    }
}
