using System.Text;
using gsm.Services;

namespace gsm.Tests;

public class SmsMultipartAssemblerTests
{
    [Fact]
    public void FullGsm7DeliverPdu_IsDecodedInsteadOfExposedAsHex()
    {
        const string pdu = "069148192050444006D0381C0E000062707180817582A00500035D02015054610A347D83D0F53A08160331D3E3B27B5E06CDEB2072DD7D06ADD16FF719744EBFD32074D80D72BFD32072DD7D0641E5E576BADE06D1E56537C85D7683E861F71964153E9FCB39C81E06C560B0A610440ED3C32F190D0D5AA3D3203ABA5E0689C36F108E96A3D16A385B8C4603CDDF6137C82A7CD640E77A1A84C3E15CA061FD3D06D55C";

        DecodedSmsBody result = SmsBodyDecoder.Decode(pdu);

        Assert.DoesNotMatch("^[0-9A-F]+$", result.Content);
        Assert.StartsWith("(TB) So huu 01 License", result.Content);
        Assert.Equal(1, result.Concatenation?.Sequence);
        Assert.Equal(2, result.Concatenation?.Total);
        Assert.NotNull(result.Concatenation);
        Assert.NotEqual("Unknown", result.Sender);
    }

    [Fact]
    public void Ucs2PayloadMislabelledAsGsm7_IsRecoveredWithoutAtSignCorruption()
    {
        const string prefix = "Thuê bao 84836522379 của Quý khách đã bị NGỪNG CUNG CẤP DỊCH";
        string expected = prefix.PadRight(67, '!');
        string pdu = BuildMislabelledUcs2DeliverPdu(expected);

        DecodedSmsBody result = SmsBodyDecoder.Decode(pdu);

        Assert.Equal(expected, result.Content);
        Assert.Equal(67, result.Content.Length);
        Assert.True(result.RecoveredMislabelledUcs2);
        Assert.DoesNotContain("@¡@", result.Content);
    }

    [Fact]
    public void ShortFinalUcs2SegmentMislabelledAsGsm7_KeepsItsRealLength()
    {
        const string expected = "Quý khách vui lòng liên hệ 18001091 để được hỗ trợ.";
        string pdu = BuildMislabelledUcs2DeliverPdu(expected);

        DecodedSmsBody result = SmsBodyDecoder.Decode(pdu);

        Assert.Equal(expected, result.Content);
        Assert.True(result.Content.Length < 67);
        Assert.True(result.RecoveredMislabelledUcs2);
    }

    [Fact]
    public void MislabelledUcs2WithTrailingAlignmentByte_DoesNotLoseTheLastCharacter()
    {
        const string expected = "Thuê bao 84836522379 của Quý khách đã bị NGỪNG CUNG CẤP DỊCH";
        string pdu = BuildMislabelledUcs2DeliverPdu(expected, appendAlignmentByte: true);

        DecodedSmsBody result = SmsBodyDecoder.Decode(pdu);

        Assert.Equal(expected, result.Content);
        Assert.EndsWith("DỊCH", result.Content);
        Assert.True(result.RecoveredMislabelledUcs2);
    }

    [Fact]
    public void RecoveredMislabelledUcs2Segments_CompleteImplicitMultipartAssembly()
    {
        string firstText = "Thông báo dịch vụ dành cho thuê bao 84836522379".PadRight(67, '.');
        const string finalText = " Quý khách vui lòng liên hệ 18001091.";
        DecodedSmsBody first = SmsBodyDecoder.Decode(BuildMislabelledUcs2DeliverPdu(firstText));
        DecodedSmsBody final = SmsBodyDecoder.Decode(BuildMislabelledUcs2DeliverPdu(finalText));
        var assembler = new SmsImplicitMultipartAssembler();

        SmsAssemblyResult waiting = assembler.Add("COM118", "565656", first.Content, "7");
        SmsAssemblyResult complete = assembler.Add("COM118", "565656", final.Content, "8");

        Assert.Equal(SmsAssemblyStatus.Waiting, waiting.Status);
        Assert.Equal(SmsAssemblyStatus.Completed, complete.Status);
        Assert.Equal(firstText + finalText, complete.Content);
        Assert.Equal(new[] { "7", "8" }, complete.MessageIndices);
    }

    [Fact]
    public void MultipartLengthsObservedInJuly22Logs_AreJoinedWithoutCutting()
    {
        int[][] loggedShapes =
        [
            [67, 7],       // COM116 / 57494952 -> 74 chars
            [67, 67, 33],  // COM116, COM117 / VinaPhone -> 167 chars
            [153, 53],     // COM119 / 565656 -> 206 chars
            [153, 10]      // COM126, COM152 / 565656 -> 163 chars
        ];

        for (int caseIndex = 0; caseIndex < loggedShapes.Length; caseIndex++)
        {
            var assembler = new SmsImplicitMultipartAssembler();
            string expected = string.Empty;
            SmsAssemblyResult? result = null;
            for (int partIndex = 0; partIndex < loggedShapes[caseIndex].Length; partIndex++)
            {
                string part = new((char)('A' + partIndex), loggedShapes[caseIndex][partIndex]);
                expected += part;
                result = assembler.Add($"COM-LOG-{caseIndex}", "LOG-SENDER", part, partIndex.ToString());
                if (partIndex < loggedShapes[caseIndex].Length - 1)
                {
                    Assert.Equal(SmsAssemblyStatus.Waiting, result.Status);
                    Assert.Null(result.Content);
                }
            }

            Assert.NotNull(result);
            Assert.Equal(SmsAssemblyStatus.Completed, result.Status);
            Assert.Equal(expected, result.Content);
            Assert.Equal(loggedShapes[caseIndex].Sum(), result.Content!.Length);
        }
    }

    [Fact]
    public void IncompleteCom118SequenceFromJuly22Logs_IsNeverEmittedAsPartialText()
    {
        var assembler = new SmsImplicitMultipartAssembler();

        foreach (int index in Enumerable.Range(2, 6))
        {
            SmsAssemblyResult result = assembler.Add(
                "COM118", "565656", new string((char)('A' + index), 153), index.ToString());

            Assert.Equal(SmsAssemblyStatus.Waiting, result.Status);
            Assert.Null(result.Content);
            Assert.Empty(result.MessageIndices);
        }
    }

    [Theory]
    [InlineData("+CTZE: \"+28\",0,\"2026/07/17,10:32:46\" 069148192050444006D0381C0E000062707171236482A0050003B602015054610A347D83D0F53A08160331D3E3B27B5E06CDEB2072DD7D06ADD16FF719744EBFD32074D80D72BFD32072DD7D0641E5E576BADE06D1E56537C85D7683E861F71964153E9FCB39C81E06C560B0A610440ED3C32F190D0D5AA3D3203ABA5E0689C36F108E96A3D566B69CED4603CDDF6137C82A7CD640E77A1A84C3E15CA061FD3D06D55C")]
    [InlineData("+CTZE: \"+28\",0,\"2026/07/17,10:32:46\"\r\n069148192050444006D0381C0E000062707171236482A0050003B602015054610A347D83D0F53A08160331D3E3B27B5E06CDEB2072DD7D06ADD16FF719744EBFD32074D80D72BFD32072DD7D0641E5E576BADE06D1E56537C85D7683E861F71964153E9FCB39C81E06C560B0A610440ED3C32F190D0D5AA3D3203ABA5E0689C36F108E96A3D566B69CED4603CDDF6137C82A7CD640E77A1A84C3E15CA061FD3D06D55C")]
    public void Ec20CtzeUrc_DoesNotContaminateAdjacentPdu(string raw)
    {
        DecodedSmsBody result = SmsBodyDecoder.Decode(raw);

        Assert.StartsWith("(TB) So huu 01 License", result.Content);
        Assert.Equal(new SmsConcatInfo(0xB6, 2, 1), result.Concatenation);
        Assert.NotEqual("Unknown", result.Sender);
    }

    [Fact]
    public void InterleavedUssdUrc_DoesNotReplaceStoredSmsBody()
    {
        const string pdu = "069148192050444006D0381C0E000062707171236482A0050003B602015054610A347D83D0F53A08160331D3E3B27B5E06CDEB2072DD7D06ADD16FF719744EBFD32074D80D72BFD32072DD7D0641E5E576BADE06D1E56537C85D7683E861F71964153E9FCB39C81E06C560B0A610440ED3C32F190D0D5AA3D3203ABA5E0689C36F108E96A3D566B69CED4603CDDF6137C82A7CD640E77A1A84C3E15CA061FD3D06D55C";
        string raw = "+CMGR: 0,,23\r\n+CUSD: 0,\"0053006F002000540042\",15\r\n" + pdu + "\r\nOK\r\n";

        DecodedSmsBody result = SmsBodyDecoder.Decode(raw);

        Assert.StartsWith("(TB) So huu 01 License", result.Content);
        Assert.Equal(new SmsConcatInfo(0xB6, 2, 1), result.Concatenation);
        Assert.True(GsmModemService.IsCompleteStoredSmsResponse(raw));
    }

    [Theory]
    [InlineData("+CTZE: \"+28\",0,\"2026/07/17,10:32:46\"\r\nOK\r\n")]
    [InlineData("+CUSD: 0,\"0053006F002000540042\",15\r\nOK\r\n")]
    [InlineData("+COPS: 0,0,\"VinaPhone VINAPHONE\",7\r\nOK\r\n")]
    public void StoredSmsCompletion_RejectsUrcOrCommandNoiseWithoutCmgrHeader(string raw)
    {
        Assert.False(GsmModemService.IsCompleteStoredSmsResponse(raw));
    }

    [Fact]
    public void DirectCmtEnvelope_DecodesBodyWithoutLeakingHeader()
    {
        const string raw = "+CMT: \"ZALO\",\"\",\"26/07/17,09:31:44+28\"\r\nMa OTP cua ban la 123456\r\n";

        DecodedSmsBody result = SmsBodyDecoder.Decode(raw);

        Assert.Equal("Ma OTP cua ban la 123456", result.Content);
        Assert.Equal("123456", GsmModemService.ExtractOtp(result.Content));
    }

    [Fact]
    public void ShortSmsBodyEqualToOk_IsNotDiscardedAsTransportTerminator()
    {
        const string raw = "+CMGR: \"REC UNREAD\",\"505751\"\r\nOK\r\nOK\r\n";

        Assert.Equal("OK", SmsBodyDecoder.Decode(raw).Content);
    }

    [Fact]
    public void ExactMultipart_AllowsSimIndexReuseForANewMessage()
    {
        var assembler = new SmsMultipartAssembler();
        Assert.Equal(SmsAssemblyStatus.Waiting, assembler.Add("COM34", "888", new(10, 2, 1), "old-a", "0").Status);
        Assert.Equal(SmsAssemblyStatus.Completed, assembler.Add("COM34", "888", new(10, 2, 2), "old-b", "1").Status);

        Assert.Equal(SmsAssemblyStatus.Waiting, assembler.Add("COM34", "888", new(11, 2, 1), "new-a", "0").Status);
        SmsAssemblyResult next = assembler.Add("COM34", "888", new(11, 2, 2), "new-b", "1");

        Assert.Equal(SmsAssemblyStatus.Completed, next.Status);
        Assert.Equal("new-anew-b", next.Content);
    }

    [Fact]
    public void ImplicitMultipart_AllowsSimIndexReuseForDifferentContent()
    {
        var assembler = new SmsImplicitMultipartAssembler();
        string oldPart = new('A', 67);
        string newPart = new('B', 67);
        Assert.Equal(SmsAssemblyStatus.Waiting, assembler.Add("COM54", "Unknown", oldPart, "0").Status);
        Assert.Equal(SmsAssemblyStatus.Completed, assembler.Add("COM54", "Unknown", "old-end", "1").Status);

        Assert.Equal(SmsAssemblyStatus.Waiting, assembler.Add("COM54", "Unknown", newPart, "0").Status);
        SmsAssemblyResult next = assembler.Add("COM54", "Unknown", "new-end", "1");

        Assert.Equal(SmsAssemblyStatus.Completed, next.Status);
        Assert.Equal(newPart + "new-end", next.Content);
    }

    [Fact]
    public void LongUndecodableHex_IsRetainedInsteadOfPublishedAndDeleted()
    {
        DecodedSmsBody result = SmsBodyDecoder.Decode("DEADBEEFDEADBEEFDEADBEEFDEADBEEF");

        Assert.True(result.WasHex);
        Assert.Empty(result.Content);
        Assert.Equal("123456", SmsBodyDecoder.Decode("123456").Content);
    }

    [Theory]
    [InlineData("ÄÃ£ phÃ¡t hiá»‡n tin nháº¯n", "Đã phát hiện tin nhắn")]
    [InlineData("Sá»‘ dÆ°: 900 Ä‘", "Số dư: 900 đ")]
    [InlineData("Nội dung tiếng Việt đúng", "Nội dung tiếng Việt đúng")]
    public void MojibakeRepair_RepairsOnlyBrokenUtf8(string input, string expected)
    {
        Assert.Equal(expected, TextEncodingNormalizer.RepairMojibake(input));
    }
    [Fact]
    public async Task MoreThanSixtyFourPorts_AssembleWithoutCrossPortBlocking()
    {
        var assembler = new SmsMultipartAssembler();
        const int portCount = BackendConcurrency.BaselineConcurrentPorts * 2;

        SmsAssemblyResult[] results = await Task.WhenAll(Enumerable.Range(1, portCount).Select(i => Task.Run(() =>
        {
            assembler.Add($"COM{i}", "ZALO", new(i, 2, 1), $"A{i}", "1");
            return assembler.Add($"COM{i}", "ZALO", new(i, 2, 2), $"B{i}", "2");
        })));

        Assert.All(results, result => Assert.Equal(SmsAssemblyStatus.Completed, result.Status));
    }

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
    public void RepeatedSweep_DoesNotKeepIncompleteMultipartAliveForever()
    {
        var a = new SmsMultipartAssembler(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        SmsConcatInfo part = new(137, 12, 1);

        Assert.Equal(SmsAssemblyStatus.Waiting,
            a.Add("COM29", "VinaPhone", part, new string('A', 67), "0", start).Status);
        Assert.Equal(SmsAssemblyStatus.Duplicate,
            a.Add("COM29", "VinaPhone", part, new string('A', 67), "0", start.AddSeconds(4)).Status);

        Assert.Equal(1, a.RemoveExpired(start.AddSeconds(6)));
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
    public void RejectedDeliveryCanForgetCompletionAndRetryFromDurableParts()
    {
        var a = new SmsMultipartAssembler();
        var first = new SmsConcatInfo(23, 2, 1);
        var second = new SmsConcatInfo(23, 2, 2);
        a.Add("COM8\u001f8984", "ZALO", first, "one", "0");
        Assert.Equal(SmsAssemblyStatus.Completed,
            a.Add("COM8\u001f8984", "ZALO", second, "two", "1").Status);

        a.ForgetMessage("COM8\u001f8984", "ZALO", second);

        Assert.Equal(SmsAssemblyStatus.Waiting,
            a.Add("COM8\u001f8984", "ZALO", first, "one", string.Empty).Status);
        Assert.Equal("onetwo",
            a.Add("COM8\u001f8984", "ZALO", second, "two", "1").Content);
    }

    [Fact]
    public void SmsDeliveryRequiresExplicitConsumerAcknowledgement()
    {
        var delivery = new GsmDataEventArgs();

        Assert.False(delivery.DeliveryAccepted);
        delivery.DeliveryAccepted = true;
        Assert.True(delivery.DeliveryAccepted);
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

    private static string BuildMislabelledUcs2DeliverPdu(string content, bool appendAlignmentByte = false)
    {
        byte[] text = Encoding.BigEndianUnicode.GetBytes(content);
        byte[] payload = appendAlignmentByte ? text.Append((byte)0).ToArray() : text;
        Assert.InRange(payload.Length, 2, 223);

        // SMS-DELIVER with DCS=0 but UTF-16BE payload. The faulty carrier writes UDL
        // as the number of septets that occupy the same bytes, which turns 134 bytes
        // (67 UCS2 chars) into 153 apparent GSM-7 characters in a naive decoder.
        var pdu = new List<byte>
        {
            0x00,                         // no SMSC address
            0x00,                         // SMS-DELIVER, no UDH
            0x0A, 0x91,                   // 10-digit international sender
            0x48, 0x09, 0x21, 0x43, 0x65,
            0x00,                         // PID
            0x00,                         // incorrect DCS: GSM 7-bit
            0x62, 0x70, 0x72, 0x20, 0x10, 0x10, 0x00,
            (byte)(payload.Length * 8 / 7)
        };
        pdu.AddRange(payload);
        return Convert.ToHexString(pdu.ToArray());
    }
}
