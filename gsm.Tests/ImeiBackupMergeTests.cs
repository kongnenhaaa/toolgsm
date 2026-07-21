using gsm.Models;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class ImeiBackupMergeTests
{
    [Fact]
    public void ExistingOriginalImei_IsNeverOverwrittenByAppliedImei()
    {
        var original = new SimBackupEntry
        {
            Ccid = "89840200011750541177",
            Imei = "352054261826334",
            SourceFile = "imei_backup.xlsx"
        };
        var applied = new SimBackupEntry
        {
            Ccid = original.Ccid,
            Imei = "355008370781449",
            PhoneNumber = "0942152795",
            Status = "Active"
        };

        MainViewModel.MergeBackupEntryFirstWriteWins(original, applied);

        Assert.Equal("352054261826334", original.Imei);
        Assert.Equal("0942152795", original.PhoneNumber);
        Assert.Equal("Active", original.Status);
        Assert.Equal("imei_backup.xlsx", original.SourceFile);
    }
}
