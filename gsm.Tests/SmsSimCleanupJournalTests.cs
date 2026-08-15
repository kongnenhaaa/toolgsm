using gsm.Services;

namespace gsm.Tests;

public sealed class SmsSimCleanupJournalTests
{
    [Fact]
    public void PreparedIntent_IsSessionOnlyAndCreatesNoFiles()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "cleanup.json");
        string fallback = Path.Combine(temp.Path, "cleanup.pending.json");
        var first = new SmsSimCleanupJournal(primary, fallback);
        SmsSimCleanupJournal.Intent intent = first.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part");

        Assert.False(File.Exists(primary));
        Assert.False(File.Exists(fallback));
        Assert.False(first.Complete(intent.IntentId, "wrong-message"));
        Assert.True(first.Complete(intent.IntentId, intent.MessageId));
        Assert.Empty(first.GetForScope(
            "ccid:89840200011639721552"));
        Assert.Empty(new SmsSimCleanupJournal(primary, fallback).GetForScope(
            "ccid:89840200011639721552"));
    }

    [Fact]
    public void ExistingFile_IsIgnoredAndNeverOverwritten()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "cleanup.json");
        string fallback = Path.Combine(temp.Path, "cleanup.pending.json");
        File.WriteAllText(primary, "{broken");
        var journal = new SmsSimCleanupJournal(primary, fallback);

        journal.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part");
        Assert.Equal("{broken", File.ReadAllText(primary));
        Assert.False(File.Exists(fallback));
    }

    [Fact]
    public void NewInstance_DoesNotRestorePreviousSessionIntent()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "cleanup.json");
        string fallback = Path.Combine(temp.Path, "cleanup.pending.json");
        var first = new SmsSimCleanupJournal(primary, fallback);
        first.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part");
        var restarted = new SmsSimCleanupJournal(primary, fallback);

        Assert.Empty(restarted.GetForScope(
            "ccid:89840200011639721552"));
        Assert.False(File.Exists(primary));
        Assert.False(File.Exists(fallback));
    }

    [Fact]
    public void ExactIntent_CanResumeAfterSameSimMovesToAnotherPort()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "cleanup.json");
        string fallback = Path.Combine(temp.Path, "cleanup.pending.json");
        var journal = new SmsSimCleanupJournal(primary, fallback);
        SmsSimCleanupJournal.Intent original = journal.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part");

        SmsSimCleanupJournal.Intent resumed = journal.Prepare(
            "ccid:89840200011639721552",
            "COM106",
            "7",
            "sms-mp-operation",
            "sms-stored-part");

        Assert.Equal(original, resumed);
        Assert.True(journal.Complete(resumed.IntentId, resumed.MessageId));
    }

    [Fact]
    public void SamePartIdentity_OnDifferentSim_IsRejected()
    {
        using var temp = new TemporaryDirectory();
        var journal = new SmsSimCleanupJournal(
            Path.Combine(temp.Path, "cleanup.json"),
            Path.Combine(temp.Path, "cleanup.pending.json"));
        journal.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part");

        Assert.Throws<InvalidDataException>(() => journal.Prepare(
            "ccid:89840200011639729999",
            "COM106",
            "7",
            "sms-mp-operation",
            "sms-stored-part"));
    }

    [Fact]
    public void TrustedCmglSnapshot_ParsesEveryStoredIndex()
    {
        const string response =
            "AT+CMGL=4\r\n"
            + "+CMGL: 0,1,,3\r\n00010203\r\n"
            + "+CMGL: 17,0,,4\r\n0001020304\r\nOK";

        Assert.True(GsmModemService.TryParseTrustedPduStoredSmsIndexSnapshot(
            response, out IReadOnlySet<string> indices));
        Assert.Equal(["0", "17"], indices.OrderBy(x => int.Parse(x)));
    }

    [Theory]
    [InlineData("+CMGL: 7,1,,3\r\n00010203")]
    [InlineData("+CMS ERROR: 500")]
    [InlineData("")]
    public void IncompleteOrFailedCmglSnapshot_IsNeverTrusted(string response)
    {
        Assert.False(GsmModemService.TryParseTrustedPduStoredSmsIndexSnapshot(
            response, out IReadOnlySet<string> indices));
        Assert.Empty(indices);
    }

    [Fact]
    public void EmptySuccessfulCmglSnapshot_ProvesNoStoredSlots()
    {
        Assert.True(GsmModemService.TryParseTrustedPduStoredSmsIndexSnapshot(
            "AT+CMGL=4\r\nOK", out IReadOnlySet<string> indices));
        Assert.Empty(indices);
    }

    [Theory]
    [InlineData("+CMGL: 7,\"REC READ\",\"888\"\r\nbody\r\nOK")]
    [InlineData("+CMGL: ???\r\nOK")]
    [InlineData("+CMGL: 7,1,,3\r\nNOT-A-PDU\r\nOK")]
    [InlineData("+CMGL: 7,1,,3\r\n00010203\r\n+CMS ERROR: 500\r\nOK")]
    [InlineData("+CMGL: 7,1,,3\r\n00010203\r\nstray\r\nOK")]
    public void MalformedOrNonPduSnapshot_IsNeverTrusted(string response)
    {
        Assert.False(GsmModemService.TryParseTrustedPduStoredSmsIndexSnapshot(
            response, out IReadOnlySet<string> indices));
        Assert.Empty(indices);
    }

    [Fact]
    public void PendingCmglCommand_RoutesIndicesWithoutStrippingRawSnapshot()
    {
        const string raw =
            "AT+CMGL=4\r\n+CMGL: 7,1,,3\r\n00010203\r\nOK\r\n";

        GsmModemService.CmglRoutingResult routing =
            GsmModemService.RouteCmglData(raw, "AT+CMGL=4");

        Assert.True(routing.PreservedForPendingCommand);
        Assert.Equal(raw, routing.CommandResponseData);
        Assert.Equal(["7"], routing.Indices);
        Assert.True(GsmModemService.TryParseTrustedPduStoredSmsIndexSnapshot(
            routing.CommandResponseData,
            out IReadOnlySet<string> indices));
        Assert.Contains("7", indices);
    }

    [Fact]
    public void UnsolicitedCmgl_RoutesIndexButDoesNotLeaveHeaderInCommandBuffer()
    {
        const string raw = "+CMGL: 7,1,,3\r\n00010203\r\nOK\r\n";

        GsmModemService.CmglRoutingResult routing =
            GsmModemService.RouteCmglData(raw, pendingCommand: null);

        Assert.False(routing.PreservedForPendingCommand);
        Assert.Equal(["7"], routing.Indices);
        Assert.DoesNotContain("+CMGL: 7", routing.CommandResponseData);
    }

    [Fact]
    public void DestructiveFingerprintGuard_AcceptsReadStatusChangeOnly()
    {
        const string scope = "ccid:89840200011639721552";
        const string unread =
            "+CMGR: \"REC UNREAD\",\"888\",\"\",\"26/07/26,10:30:00+28\"\r\ndata\r\nOK";
        const string read =
            "+CMGR: \"REC READ\",\"888\",\"\",\"26/07/26,10:30:00+28\"\r\ndata\r\nOK";
        const string recycled =
            "+CMGR: \"REC READ\",\"888\",\"\",\"26/07/26,10:30:00+28\"\r\nother\r\nOK";
        string expected = GsmModemService.BuildStoredSmsDeliveryId(
            scope, "7", unread);

        Assert.NotEmpty(expected);
        Assert.True(GsmModemService.StoredSmsMatchesExpectedIdentity(
            scope, "7", expected, read));
        Assert.False(GsmModemService.StoredSmsMatchesExpectedIdentity(
            scope, "7", expected, recycled));
        Assert.False(GsmModemService.StoredSmsMatchesExpectedIdentity(
            scope, "8", expected, read));
    }

    [Fact]
    public void DestructiveFingerprintGuard_AcceptsPduAndTextViewsOfSameSms()
    {
        const string scope = "ccid:89840200011775811480";
        const string pdu =
            "+CMGR: 0,,145\r\n"
            + "06914819205033240BD153F41B5E2E030003627092907025829053E4135A2CEA404E74180E6A8741F8F018D44EBBD120D8ED06ABE140C4228818741E41CB2C881A4C8296C867D0E90235C3A0F11B844E97EB20767D0CA2CBDFEE33285603C1D175BA0BB4443E9D47D0189D0E83E665503B0C7287F320FB3B0D729FEBEF34688D0E8F59A07519340E83DCE8B01B644F97DDA029FA0D2F975D\r\n\r\nOK";
        const string pduMarkedRead =
            "+CMGR: 1,,145\r\n"
            + "06914819205033240BD153F41B5E2E030003627092907025829053E4135A2CEA404E74180E6A8741F8F018D44EBBD120D8ED06ABE140C4228818741E41CB2C881A4C8296C867D0E90235C3A0F11B844E97EB20767D0CA2CBDFEE33285603C1D175BA0BB4443E9D47D0189D0E83E665503B0C7287F320FB3B0D729FEBEF34688D0E8F59A07519340E83DCE8B01B644F97DDA029FA0D2F975D\r\n\r\nOK";
        const string text =
            "+QCMGR: \"REC READ\",\"83104111112101101\",,\"26/07/29,09:07:52+28\"\r\n"
            + "SHOPEE: Nhap ma xac minh 077058 DE DANG KY TAI KHOAN. Ma co hieu luc trong 15 phut. KHONG chia se ma nay voi nguoi khac, ke ca nhan vien Shopee.\r\n\r\nOK";

        DecodedSmsBody decodedPdu = SmsBodyDecoder.Decode(pdu);
        DecodedSmsBody decodedText = SmsBodyDecoder.Decode(text);
        string expected = GsmModemService.BuildStoredSmsDeliveryId(
            scope,
            "0",
            pdu);

        Assert.Equal(decodedText.Content, decodedPdu.Content);
        Assert.Equal(decodedText.SmsTimestampUtc, decodedPdu.SmsTimestampUtc);
        Assert.NotEmpty(expected);
        Assert.True(GsmModemService.StoredSmsMatchesExpectedIdentity(
            scope, "0", expected, pduMarkedRead));
        Assert.True(GsmModemService.StoredSmsMatchesExpectedIdentity(
            scope, "0", expected, text));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ToolGSM.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch { }
        }
    }
}
