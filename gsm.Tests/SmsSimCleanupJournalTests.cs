using gsm.Services;

namespace gsm.Tests;

public sealed class SmsSimCleanupJournalTests
{
    [Fact]
    public void PreparedIntent_SurvivesRestart_AndExactCompletionIsDurable()
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

        var restarted = new SmsSimCleanupJournal(primary, fallback);
        Assert.Equal(intent, Assert.Single(restarted.GetForScope(
            "ccid:89840200011639721552")));
        Assert.False(restarted.Complete(intent.IntentId, "wrong-message"));
        Assert.True(restarted.Complete(intent.IntentId, intent.MessageId));
        Assert.Empty(new SmsSimCleanupJournal(primary, fallback).GetForScope(
            "ccid:89840200011639721552"));
    }

    [Fact]
    public void CorruptExistingIntentJournal_FailsClosedWithoutOverwrite()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "cleanup.json");
        string fallback = Path.Combine(temp.Path, "cleanup.pending.json");
        File.WriteAllText(primary, "{broken");
        var journal = new SmsSimCleanupJournal(primary, fallback);

        Assert.Throws<InvalidDataException>(() => journal.Prepare(
            "ccid:89840200011639721552",
            "COM86",
            "7",
            "sms-mp-operation",
            "sms-stored-part"));
        Assert.Equal("{broken", File.ReadAllText(primary));
    }

    [Fact]
    public void OneCorruptSiblingAndOneValidCopy_FailsClosedAsNewestIsAmbiguous()
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
        File.WriteAllText(fallback, "{newer-but-corrupt");

        var restarted = new SmsSimCleanupJournal(primary, fallback);

        Assert.Throws<InvalidDataException>(() => restarted.GetForScope(
            "ccid:89840200011639721552"));
        Assert.Equal("{newer-but-corrupt", File.ReadAllText(fallback));
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
