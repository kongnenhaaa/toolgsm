using System.IO;
using gsm.Services;

namespace gsm.Tests;

public sealed class SmsMultipartJournalTests
{
    [Fact]
    public void PartsSurviveJournalRecreationAndCanCompleteAfterRestart()
    {
        string path = TempJournalPath();
        try
        {
            var firstRun = new SmsMultipartJournal(path);
            firstRun.RecordAndGetParts("COM3\u001f8984", "VinaPhone", new(71, 2, 1), "phan-1");

            var secondRun = new SmsMultipartJournal(path);
            IReadOnlyList<SmsMultipartJournal.Part> parts = secondRun.RecordAndGetParts(
                "COM3\u001f8984", "VinaPhone", new(71, 2, 2), "phan-2");

            Assert.Equal(new[] { 1, 2 }, parts.Select(x => x.Sequence));
            Assert.Equal("phan-1phan-2", string.Concat(parts.Select(x => x.Content)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void CompleteRemovesDurableParts()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);
            var concat = new SmsConcatInfo(12, 2, 1);
            journal.RecordAndGetParts("COM7\u001f8984", "888", concat, "one");

            journal.Complete("COM7\u001f8984", "888", concat);

            var reloaded = new SmsMultipartJournal(path);
            Assert.Empty(reloaded.GetParts("COM7\u001f8984", "888", concat));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void ConflictingPartDoesNotOverwriteDurableContent()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);
            var concat = new SmsConcatInfo(19, 2, 1);
            journal.RecordAndGetParts("COM9\u001f8984", "ZALO", concat, "original");

            Assert.Throws<InvalidDataException>(() =>
                journal.RecordAndGetParts("COM9\u001f8984", "ZALO", concat, "changed"));

            var reloaded = new SmsMultipartJournal(path);
            Assert.Equal("original", Assert.Single(
                reloaded.GetParts("COM9\u001f8984", "ZALO", concat)).Content);
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    private static string TempJournalPath() => Path.Combine(
        Path.GetTempPath(), $"toolgsm-sms-journal-{Guid.NewGuid():N}.json");

    private static void DeleteJournalFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
