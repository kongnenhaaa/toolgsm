using gsm.Services;

namespace gsm.Tests;

public class SmsReceiveFrameTests
{
    [Theory]
    [InlineData("AT+CMGS=\"888\"", false)]
    [InlineData("AT+CMGR=1", false)]
    [InlineData("AT+CMGL=\"ALL\"", false)]
    [InlineData("AT+CMGD=0,0", false)]
    [InlineData("AT+CPMS?", false)]
    [InlineData("AT+CUSD=1,\"*101#\",15", false)]
    [InlineData("AT+QCMGR=1", false)]
    // Bước ghi payload sau dấu nhắc '>': +CMS ERROR chính là câu trả lời của nó.
    [InlineData("SMS_PAYLOAD", false)]
    // Lệnh không thuộc dịch vụ tin nhắn: +CMS ERROR về muộn là rác của lệnh trước.
    [InlineData("AT+CGMR", true)]
    [InlineData("AT+CGMI", true)]
    [InlineData("AT+EGMR=0,7;", true)]
    [InlineData("AT+COPS?", true)]
    [InlineData("AT+CPIN?", true)]
    public void LateCmsErrorOnlyTerminatesMessagingCommands(
        string pendingCommand,
        bool expectedStale)
    {
        Assert.Equal(
            expectedStale,
            GsmModemService.IsStaleCmsErrorTerminator(
                "+CMS ERROR: 350\r\n", pendingCommand));
    }

    [Theory]
    [InlineData("\r\nQuectel\r\nEC20F\r\nRevision: EC20CEHDLGR08A05M1G\r\n\r\nOK", true)]
    [InlineData("\r\nEC20F\r\n\r\nOK", true)]
    [InlineData("\r\nOK", false)]
    [InlineData("+CMS ERROR: 350", false)]
    [InlineData("ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)", false)]
    [InlineData("", false)]
    public void EmptyAtiIsDetectedSoIdentityCanBeReprobed(
        string atiResponse,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.HasReadableModemIdentity(atiResponse));
    }

    [Theory]
    [InlineData("\r\nOK\r\n")]
    [InlineData("\r\nERROR\r\n")]
    [InlineData("+CME ERROR: 13\r\n")]
    public void NonCmsTerminatorsAreNeverTreatedAsStale(string terminator)
    {
        Assert.False(GsmModemService.IsStaleCmsErrorTerminator(
            terminator, "AT+CGMR"));
    }

    [Fact]
    public void TerminalArrivingAfterCompletedOkCannotLeakIntoNextCommand()
    {
        const string trailing =
            "\r\n+CME ERROR: 264\r\n\r\n+CREG: 0\r\n";

        string remaining =
            GsmModemService.RemoveLeadingUnownedCommandResponseFrames(
                trailing,
                out IReadOnlyList<string> removed);

        Assert.Equal(["+CME ERROR: 264"], removed);
        Assert.Equal("\r\n+CREG: 0\r\n", remaining);
    }

    [Fact]
    public void OrphanCleanupPreservesUrcAndAnythingAfterIt()
    {
        const string urcThenError =
            "\r\n+CREG: 0\r\n\r\n+CME ERROR: 264\r\n";

        string remaining =
            GsmModemService.RemoveLeadingUnownedCommandResponseFrames(
                urcThenError,
                out IReadOnlyList<string> removed);

        Assert.Empty(removed);
        Assert.Equal(urcThenError, remaining);
    }

    [Fact]
    public void EveryLeadingUnownedTerminalIsRemovedAsOneRxBoundary()
    {
        const string trailing =
            "\r\nOK\r\n\r\n+CME ERROR: 264\r\n\r\n+CSQ: 31,99\r\n";

        string remaining =
            GsmModemService.RemoveLeadingUnownedCommandResponseFrames(
                trailing,
                out IReadOnlyList<string> removed);

        Assert.Equal(["OK", "+CME ERROR: 264"], removed);
        Assert.Equal("\r\n+CSQ: 31,99\r\n", remaining);
    }

    [Fact]
    public void WriteOnlyPollingResponsesAreDrainedBeforeNextTransaction()
    {
        const string buffered =
            "\r\n+CPIN: READY\r\n\r\nOK\r\n"
            + "\r\n+CSQ: 31,99\r\n\r\nOK\r\n"
            + "\r\n+CUSD: 0,\"TKC: 10000d\",15\r\n";

        string remaining =
            GsmModemService.RemoveLeadingUnownedCommandResponseFrames(
                buffered,
                out IReadOnlyList<string> removed);

        Assert.Equal(["+CPIN+OK", "+CSQ+OK"], removed);
        Assert.Equal(
            "\r\n+CUSD: 0,\"TKC: 10000d\",15\r\n",
            remaining);
    }

    [Fact]
    public void UnownedCleanupNeverConsumesDirectSmsFrame()
    {
        const string directSms =
            "\r\n+CMT: \"Shopee\",\"\",\"26/07/29,18:00:00+28\"\r\n"
            + "Ma OTP 123456\r\n\r\nOK\r\n";

        string remaining =
            GsmModemService.RemoveLeadingUnownedCommandResponseFrames(
                directSms,
                out IReadOnlyList<string> removed);

        Assert.Empty(removed);
        Assert.Equal(directSms, remaining);
    }

    [Theory]
    [InlineData("\r\n+CPIN: READY\r\n\r\nOK\r\n", "AT+CPMS?", false)]
    [InlineData("\r\n+CSQ: 31,99\r\n\r\nOK\r\n", "AT+CFUN?", false)]
    [InlineData("\r\n+CSQ: 31,99\r\n\r\nOK\r\n", "AT+COPS?", false)]
    [InlineData("\r\n+CPMS: \"SM\",0,30,\"SM\",0,30,\"SM\",0,30\r\n\r\nOK\r\n", "AT+CPMS?", true)]
    [InlineData("\r\n+CFUN: 4\r\n\r\nOK\r\n", "AT+CFUN?", true)]
    [InlineData("\r\n+COPS: 0,0,\"VINAPHONE\",2\r\n\r\nOK\r\n", "AT+COPS?", true)]
    [InlineData("\r\n+QCFG: \"ims/ut\",0,0,0\r\n\r\nOK\r\n", "AT+QCFG=\"ims/ut\"", true)]
    [InlineData("\r\n+QCFG: \"ims\",2,0\r\n\r\nOK\r\n", "AT+QCFG=\"ims/ut\"", false)]
    [InlineData("\r\n+QCFG: \"nwscanmode\",0\r\n\r\nOK\r\n", "AT+QCFG=\"ims/ut\"", false)]
    public void QueryOnlyCompletesOnItsOwnPayload(
        string frame,
        string command,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.CanTerminalFrameCompletePendingCommand(
                frame,
                "\r\nOK\r\n",
                command));
    }

    [Fact]
    public void UnknownPendingCommandKeepsAcceptingCmsErrorSoNothingHangs()
    {
        Assert.False(GsmModemService.IsStaleCmsErrorTerminator(
            "+CMS ERROR: 350\r\n", null));
        Assert.False(GsmModemService.IsStaleCmsErrorTerminator(
            "+CMS ERROR: 350\r\n", "   "));
    }

    [Theory]
    [InlineData("+CPMS: \"SM\",3,50,\"SM\",3,50,\"SM\",3,50\r\n\r\nOK", 3, 50)]
    [InlineData("+CPMS: \"ME\",50,50,\"SM\",0,50,\"MT\",50,50\r\n\r\nOK", 50, 50)]
    public void SimStorageUsageIsReadFromCpms(
        string response,
        int expectedUsed,
        int expectedTotal)
    {
        Assert.True(GsmModemService.TryParseSimStorageUsage(
            response, out int used, out int total));
        Assert.Equal(expectedUsed, used);
        Assert.Equal(expectedTotal, total);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("+CPMS: \"SM\",3,0")]
    [InlineData("ERROR: Timeout")]
    [InlineData("")]
    public void UnparsableCpmsNeverReportsStorageUsage(string response)
    {
        Assert.False(GsmModemService.TryParseSimStorageUsage(
            response, out _, out _));
    }

    [Theory]
    [InlineData("+CMS ERROR: 302")]
    [InlineData("+CMS ERROR: 322")]
    [InlineData("Memory Full")]
    public void SmsMemoryFullResponsesTriggerSafeSweep(string response)
    {
        Assert.True(GsmModemService.IsSmsMemoryFullResponse(response));
    }

    [Theory]
    [InlineData("+CMS ERROR: 350")]
    [InlineData("ERROR")]
    public void UnrelatedSmsErrorsDoNotTriggerMemoryCleanup(string response)
    {
        Assert.False(GsmModemService.IsSmsMemoryFullResponse(response));
    }

    [Fact]
    public void ShortDirectCmtBeforeSignalUrc_IsExtractedImmediately()
    {
        const string data = "+CMT: \"505751\",\"\",\"26/07/25,07:47:42+28\"\r\n609998\r\n+CSQ: 25,99\r\n";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data);

        Assert.Single(frames);
        Assert.Contains("609998", frames[0]);
    }

    [Fact]
    public void SplitDirectCmtWithoutBoundary_IsRetainedUntilMoreBytesArrive()
    {
        const string partial = "+CMT: \"505751\"\r\n609";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(partial);

        Assert.Empty(frames);
    }

    [Fact]
    public void DirectCmtWithOk_IsExtractedWithoutConsumingFollowingUrc()
    {
        const string data = "+CMT: \"505751\"\r\nMa OTP la 609998\r\nOK\r\n+CSQ: 25,99\r\n";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data);

        Assert.Single(frames);
        Assert.Contains("Ma OTP la 609998", frames[0]);
        Assert.DoesNotContain("+CSQ", frames[0]);
    }

    [Fact]
    public void VietnameseInstructionLineStartingWithPlus_IsNotTreatedAsUrc()
    {
        const string data =
            "+CMT: \"VinaPhone\"\r\n"
            + "Quy khach vui long thuc hien:\r\n"
            + "+ Cach 1 - Mo ung dung MyVNPT\r\n"
            + "+CSQ: 25,99\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data));

        Assert.Contains("+ Cach 1", frame);
        Assert.DoesNotContain("+CSQ", frame);
    }

    [Fact]
    public void FirstLineAtCallbackEnd_IsHeldUntilIdleSoMultilineSmsIsNotCut()
    {
        const string firstChunk =
            "+CMT: \"VinaPhone\"\r\nDong dau tien\r\n";
        Assert.Empty(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(firstChunk));

        const string completeBuffer =
            firstChunk + "+ Cach 1 - Dong thu hai\r\n";
        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(
                completeBuffer,
                allowIdleEndOfBuffer: true));

        Assert.Contains("Dong dau tien", frame);
        Assert.Contains("+ Cach 1 - Dong thu hai", frame);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("ERROR")]
    public void DirectCmtBodyEqualToCommandWord_IsDeliveredAsSmsText(
        string body)
    {
        string data =
            "+CMT: \"505751\"\r\n"
            + body
            + "\r\n+CSQ: 25,99\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data));

        Assert.Equal(
            body,
            GsmModemService.DecodeDirectCmtContentForTest(frame));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("ERROR")]
    public void MultilineDirectCmtEndingInCommandWord_KeepsFinalBodyLine(
        string finalLine)
    {
        string data =
            "+CMT: \"VinaPhone\"\r\n"
            + "Dong dau tien\r\n"
            + finalLine
            + "\r\n+COPS: 0,0,\"VinaPhone\",7\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data));

        Assert.Equal(
            "Dong dau tien\n" + finalLine,
            GsmModemService.DecodeDirectCmtContentForTest(frame));
    }

    [Theory]
    [InlineData("OK", "OK")]
    [InlineData("ERROR", "OK")]
    [InlineData("OK", "ERROR")]
    public void PendingCommand_DirectTerminalBodyAndCommandTerminator_AreSeparated(
        string smsBody,
        string commandTerminator)
    {
        string data =
            "+CMT: \"505751\"\r\n"
            + smsBody
            + "\r\n"
            + commandTerminator
            + "\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(
                data,
                commandPending: true));

        Assert.Equal(
            smsBody,
            GsmModemService.DecodeDirectCmtContentForTest(frame));
        Assert.EndsWith(smsBody, frame, StringComparison.Ordinal);
        Assert.DoesNotContain(
            smsBody + "\r\n" + commandTerminator,
            frame,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "\r\n" + commandTerminator,
            data[frame.Length..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void PendingCommand_MultilineSmsEndingOk_UsesSecondOkForCommand()
    {
        const string data =
            "+CMT: \"VinaPhone\"\r\n"
            + "Noi dung\r\n"
            + "OK\r\n"
            + "OK\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(
                data,
                commandPending: true));

        Assert.Equal(
            "Noi dung\nOK",
            GsmModemService.DecodeDirectCmtContentForTest(frame));
    }

    [Fact]
    public void UndecodableDirectPdu_IsRecognizedAsDecodeFailure()
    {
        const string data =
            "+CMT: ,32\r\n"
            + "DEADBEEFDEADBEEFDEADBEEFDEADBEEF\r\n"
            + "+CSQ: 25,99\r\n";

        string frame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data));

        Assert.Empty(
            GsmModemService.DecodeDirectCmtContentForTest(frame));
    }

    [Theory]
    [InlineData(1, 1, 100, false)]
    [InlineData(4, 1, 100, true)]
    [InlineData(1, 13, 100, true)]
    [InlineData(1, 1, 16384, true)]
    public void DirectCmtQuarantinePolicy_IsBoundedByRetryAgeAndSize(
        int attempts,
        int ageSeconds,
        int observedChars,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService.ShouldQuarantineDirectCmtForTest(
                attempts,
                TimeSpan.FromSeconds(ageSeconds),
                observedChars));
    }

    [Fact]
    public void QuarantiningMalformedCmt_PreservesCommandAndFollowingValidCmt()
    {
        const string data =
            "AT+CSQ\r\r\n"
            + "+CMT: ???\r\n\r\n"
            + "OK\r\n"
            + "+CMT: \"505751\"\r\n123456\r\n"
            + "+CSQ: 25,99\r\n";

        (string quarantined, string remaining) =
            GsmModemService.SplitDirectCmtForQuarantineForTest(
                data,
                commandPending: true);

        Assert.StartsWith("+CMT: ???", quarantined, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", quarantined, StringComparison.Ordinal);
        Assert.Contains("\r\nOK\r\n", remaining, StringComparison.Ordinal);
        string validFrame = Assert.Single(
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(
                remaining,
                commandPending: true));
        Assert.Equal(
            "123456",
            GsmModemService.DecodeDirectCmtContentForTest(validFrame));
    }

    [Fact]
    public void OversizedMalformedHeader_DoesNotConsumeNextDirectFrame()
    {
        string data =
            "+CMT: "
            + new string('X', 17_000)
            + "\r\n+CMT: \"505751\"\r\n654321\r\n+CSQ: 20,99\r\n";

        (string quarantined, string remaining) =
            GsmModemService.SplitDirectCmtForQuarantineForTest(data);

        Assert.True(
            GsmModemService.ShouldQuarantineDirectCmtForTest(
                attempts: 1,
                age: TimeSpan.Zero,
                observedChars: quarantined.Length));
        Assert.DoesNotContain("654321", quarantined, StringComparison.Ordinal);
        Assert.Contains("+CMT: \"505751\"", remaining, StringComparison.Ordinal);
    }
}
