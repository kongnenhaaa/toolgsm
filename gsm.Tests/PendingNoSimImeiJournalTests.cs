using gsm.Services;
using System.Text.Json;

namespace gsm.Tests;

public sealed class PendingNoSimImeiJournalTests
{
    private const string ImeiA = "355008370781449";
    private const string ImeiB = "352054261826334";
    private const string CcidA = "89840200011639721552";
    private const string CcidB = "89840200011750541177";

    [Fact]
    public void CommittedTarget_SurvivesRestart_AndRemovalIsDurable()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");

        var firstProcess = new PendingNoSimImeiJournal(primary, fallback);
        firstProcess.Set("com9", ImeiA);

        var restartedProcess = new PendingNoSimImeiJournal(primary, fallback);
        Assert.True(restartedProcess.TryGetValue("COM9", out string restored));
        Assert.Equal(ImeiA, restored);
        Assert.True(restartedProcess.Remove("COM9", ImeiA));

        var afterRemovalRestart = new PendingNoSimImeiJournal(primary, fallback);
        Assert.False(afterRemovalRestart.TryGetValue("COM9", out _));
    }

    [Fact]
    public void PrimaryUnavailable_FallbackSnapshotSurvivesRestart()
    {
        using var temp = new TemporaryDirectory();
        string blockedParent = Path.Combine(temp.Path, "not-a-directory");
        File.WriteAllText(blockedParent, "block");
        string primary = Path.Combine(blockedParent, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");

        var firstProcess = new PendingNoSimImeiJournal(primary, fallback);
        firstProcess.Set("COM10", ImeiA);

        Assert.True(File.Exists(fallback));
        var restartedProcess = new PendingNoSimImeiJournal(primary, fallback);
        Assert.True(restartedProcess.TryGetValue("COM10", out string restored));
        Assert.Equal(ImeiA, restored);
    }

    [Fact]
    public void NeitherSnapshotWritable_DoesNotMutateInMemoryState()
    {
        using var temp = new TemporaryDirectory();
        string primaryBlocker = Path.Combine(temp.Path, "primary-blocker");
        string fallbackBlocker = Path.Combine(temp.Path, "fallback-blocker");
        File.WriteAllText(primaryBlocker, "block");
        File.WriteAllText(fallbackBlocker, "block");
        var journal = new PendingNoSimImeiJournal(
            Path.Combine(primaryBlocker, "pending.json"),
            Path.Combine(fallbackBlocker, "pending.json"));

        Assert.Throws<IOException>(() => journal.Set("COM11", ImeiA));
        Assert.Equal(0, journal.Count);
        Assert.False(journal.TryGetValue("COM11", out _));
    }

    [Fact]
    public void LateRemoval_CannotEraseNewerTargetForSamePort()
    {
        using var temp = new TemporaryDirectory();
        var journal = new PendingNoSimImeiJournal(
            Path.Combine(temp.Path, "pending.json"),
            Path.Combine(temp.Path, "pending.fallback.json"));
        journal.Set("COM12", ImeiA);
        journal.Set("COM12", ImeiB);

        Assert.False(journal.Remove("COM12", ImeiA));
        Assert.True(journal.TryGetValue("COM12", out string current));
        Assert.Equal(ImeiB, current);
    }

    [Fact]
    public async Task ParallelBatchUpdates_AreAllPresentAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        var journal = new PendingNoSimImeiJournal(primary, fallback);

        await Task.WhenAll(Enumerable.Range(1, 32).Select(index =>
            Task.Run(() => journal.Set($"COM{index}", ImeiA))));

        var restarted = new PendingNoSimImeiJournal(primary, fallback);
        Assert.Equal(32, restarted.Count);
        for (int index = 1; index <= 32; index++)
        {
            Assert.True(restarted.TryGetValue($"COM{index}", out string imei));
            Assert.Equal(ImeiA, imei);
        }
    }

    [Fact]
    public void PreparedVersion2Operation_SurvivesCrashRestart_WithIdentityAndPhase()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        var firstProcess = new PendingNoSimImeiJournal(primary, fallback);

        PendingImeiJournalEntry prepared = firstProcess.Prepare(
            "com86",
            "operation-create-86",
            ImeiA,
            CcidA,
            PendingImeiOperationKind.CreateNew);
        Assert.True(firstProcess.TryMarkPhase(
            "COM86",
            prepared.OperationId,
            ImeiA,
            PendingImeiOperationPhase.SlotVerified));

        var restartedProcess = new PendingNoSimImeiJournal(primary, fallback);

        Assert.True(restartedProcess.TryGetEntry(
            "COM86", out PendingImeiJournalEntry restored));
        Assert.Equal("operation-create-86", restored.OperationId);
        Assert.Equal("COM86", restored.PortName);
        Assert.Equal(ImeiA, restored.TargetImei);
        Assert.Equal(CcidA, restored.ExpectedCcid);
        Assert.Equal(PendingImeiOperationKind.CreateNew, restored.Kind);
        Assert.Equal(PendingImeiOperationPhase.SlotVerified, restored.Phase);
    }

    [Fact]
    public void PrepareExactOperation_IsIdempotent_ButCannotChangeItsTarget()
    {
        using var temp = new TemporaryDirectory();
        var journal = new PendingNoSimImeiJournal(
            Path.Combine(temp.Path, "pending.json"),
            Path.Combine(temp.Path, "pending.fallback.json"));

        PendingImeiJournalEntry first = journal.Prepare(
            "COM87",
            "operation-restore-87",
            ImeiA,
            expectedCcid: null,
            PendingImeiOperationKind.Restore);
        PendingImeiJournalEntry reused = journal.Prepare(
            "com87",
            "operation-restore-87",
            ImeiA,
            expectedCcid: null,
            PendingImeiOperationKind.Restore);

        Assert.Equal(first, reused);
        Assert.Equal(1, journal.Count);
        Assert.Throws<InvalidOperationException>(() => journal.Prepare(
            "COM87",
            "operation-restore-87",
            ImeiB,
            expectedCcid: null,
            PendingImeiOperationKind.Restore));
        Assert.True(journal.TryGetValue("COM87", out string retained));
        Assert.Equal(ImeiA, retained);
    }

    [Fact]
    public void LateRemoveFromOldOperation_CannotEraseNewerOperationWithSameTarget()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        var journal = new PendingNoSimImeiJournal(primary, fallback);
        journal.Prepare(
            "COM88", "operation-old", ImeiA, CcidA,
            PendingImeiOperationKind.CreateNew);
        journal.Prepare(
            "COM88", "operation-new", ImeiA, CcidA,
            PendingImeiOperationKind.CreateNew);

        Assert.False(journal.Remove(
            "COM88", "operation-old", ImeiA, CcidA));
        Assert.True(journal.TryGetEntry(
            "COM88", out PendingImeiJournalEntry current));
        Assert.Equal("operation-new", current.OperationId);

        var restarted = new PendingNoSimImeiJournal(primary, fallback);
        Assert.True(restarted.TryGetEntry(
            "COM88", out PendingImeiJournalEntry afterRestart));
        Assert.Equal("operation-new", afterRestart.OperationId);
        Assert.True(restarted.Remove(
            "COM88", "operation-new", ImeiA, CcidA));
        Assert.False(restarted.TryGetEntry("COM88", out _));
    }

    [Fact]
    public void BoundCcid_IsIdempotent_AndRejectsDifferentPhysicalSim()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        var journal = new PendingNoSimImeiJournal(primary, fallback);
        journal.Prepare(
            "COM89",
            "operation-no-sim-89",
            ImeiA,
            expectedCcid: null,
            PendingImeiOperationKind.CreateNew);

        Assert.True(journal.TryBindExpectedCcid(
            "COM89", "operation-no-sim-89", CcidA));
        Assert.True(journal.TryBindExpectedCcid(
            "com89", "operation-no-sim-89", CcidA));
        Assert.False(journal.TryBindExpectedCcid(
            "COM89", "operation-no-sim-89", CcidB));
        Assert.False(journal.Remove(
            "COM89", "operation-no-sim-89", ImeiA, CcidB));

        var restarted = new PendingNoSimImeiJournal(primary, fallback);
        Assert.True(restarted.TryGetEntry(
            "COM89", out PendingImeiJournalEntry restored));
        Assert.Equal(CcidA, restored.ExpectedCcid);
        Assert.True(restarted.Remove(
            "COM89", "operation-no-sim-89", ImeiA, CcidA));
    }

    [Fact]
    public void ExactCommittedMapping_CanTombstoneAnUnboundPendingOperation()
    {
        using var temp = new TemporaryDirectory();
        var journal = new PendingNoSimImeiJournal(
            Path.Combine(temp.Path, "pending.json"),
            Path.Combine(temp.Path, "pending.fallback.json"));
        journal.Prepare(
            "COM89",
            "operation-unbound-89",
            ImeiA,
            expectedCcid: null,
            PendingImeiOperationKind.CreateNew);

        Assert.True(journal.Remove(
            "COM89",
            "operation-unbound-89",
            ImeiA,
            expectedCcid: null));
        Assert.False(journal.TryGetEntry("COM89", out _));
    }

    [Fact]
    public void Version1Snapshot_IsMigratedToVersion2WithoutLosingTarget()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        File.WriteAllText(
            primary,
            $$"""
            {
              "Version": 1,
              "Revision": 123,
              "Entries": {
                "com90": "{{ImeiA}}"
              }
            }
            """);

        var migrated = new PendingNoSimImeiJournal(primary, fallback);

        Assert.True(migrated.TryGetEntry(
            "COM90", out PendingImeiJournalEntry entry));
        Assert.StartsWith("legacy-", entry.OperationId, StringComparison.Ordinal);
        Assert.Equal(ImeiA, entry.TargetImei);
        Assert.Empty(entry.ExpectedCcid);
        Assert.Equal(PendingImeiOperationKind.LegacyNoSim, entry.Kind);
        Assert.Equal(PendingImeiOperationPhase.Prepared, entry.Phase);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(primary));
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());

        var restarted = new PendingNoSimImeiJournal(primary, fallback);
        Assert.True(restarted.TryGetEntry(
            "COM90", out PendingImeiJournalEntry afterRestart));
        Assert.Equal(entry.OperationId, afterRestart.OperationId);
        Assert.Equal(ImeiA, afterRestart.TargetImei);
    }

    [Fact]
    public void SnapshotCanExcludeCurrentPortWithoutHidingOtherReservations()
    {
        using var temp = new TemporaryDirectory();
        var journal = new PendingNoSimImeiJournal(
            Path.Combine(temp.Path, "pending.json"),
            Path.Combine(temp.Path, "pending.fallback.json"));
        journal.Prepare(
            "COM91", "operation-91", ImeiA, null,
            PendingImeiOperationKind.CreateNew);
        journal.Prepare(
            "COM92", "operation-92", ImeiB, CcidB,
            PendingImeiOperationKind.Restore);

        Assert.Equal([ImeiB], journal.GetImeiSnapshot("com91"));
        PendingImeiJournalEntry remaining = Assert.Single(
            journal.GetEntriesSnapshot("COM91"));
        Assert.Equal("COM92", remaining.PortName);
        Assert.Equal("operation-92", remaining.OperationId);
    }

    [Fact]
    public void HighestRevisionWins_WhenFallbackSnapshotIsNewerThanPrimary()
    {
        using var temp = new TemporaryDirectory();
        string sourcePrimary = Path.Combine(temp.Path, "source.json");
        string sourceFallback = Path.Combine(temp.Path, "source.fallback.json");
        var source = new PendingNoSimImeiJournal(sourcePrimary, sourceFallback);
        source.Prepare(
            "COM93", "operation-old", ImeiA, null,
            PendingImeiOperationKind.CreateNew);
        byte[] olderSnapshot = File.ReadAllBytes(sourcePrimary);
        source.Prepare(
            "COM93", "operation-new", ImeiB, CcidA,
            PendingImeiOperationKind.Restore);
        byte[] newerSnapshot = File.ReadAllBytes(sourcePrimary);

        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        File.WriteAllBytes(primary, olderSnapshot);
        File.WriteAllBytes(fallback, newerSnapshot);

        var restarted = new PendingNoSimImeiJournal(primary, fallback);

        Assert.True(restarted.TryGetEntry(
            "COM93", out PendingImeiJournalEntry restored));
        Assert.Equal("operation-new", restored.OperationId);
        Assert.Equal(ImeiB, restored.TargetImei);
        Assert.Equal(CcidA, restored.ExpectedCcid);
    }

    [Fact]
    public void CorruptExistingSnapshots_FailClosedWithoutGeneratingReplacementState()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        string fallback = Path.Combine(temp.Path, "pending.fallback.json");
        File.WriteAllText(primary, "{broken");
        File.WriteAllText(fallback, "[]");
        var journal = new PendingNoSimImeiJournal(primary, fallback);

        Assert.Throws<InvalidDataException>(() => _ = journal.Count);
        Assert.Throws<InvalidDataException>(() =>
            journal.TryGetEntry("COM94", out _));
        Assert.Throws<InvalidDataException>(() => journal.Prepare(
            "COM94",
            "must-not-replace-corrupt-state",
            ImeiA,
            CcidA,
            PendingImeiOperationKind.CreateNew));
        Assert.Equal("{broken", File.ReadAllText(primary));
        Assert.Equal("[]", File.ReadAllText(fallback));
    }

    [Fact]
    public void MalformedVersion1Entry_InvalidatesWholeSnapshot()
    {
        using var temp = new TemporaryDirectory();
        string primary = Path.Combine(temp.Path, "pending.json");
        File.WriteAllText(
            primary,
            $$"""
            {
              "Version": 1,
              "Revision": 123,
              "Entries": {
                "COM95": "{{ImeiA}}",
                "COM96": null
              }
            }
            """);

        var journal = new PendingNoSimImeiJournal(
            primary,
            Path.Combine(temp.Path, "pending.fallback.json"));

        Assert.Throws<InvalidDataException>(() =>
            journal.TryGetEntry("COM95", out _));
    }

    [Fact]
    public void CrashReplay_PreservesOriginalVersion2OperationKind()
    {
        Assert.Equal(
            PendingImeiOperationKind.CreateNew,
            gsm.ViewModels.MainViewModel.ResolveDurableImeiOperationKind(
                PendingImeiOperationKind.CreateNew,
                PendingImeiOperationKind.Restore));
        Assert.Equal(
            PendingImeiOperationKind.Restore,
            gsm.ViewModels.MainViewModel.ResolveDurableImeiOperationKind(
                PendingImeiOperationKind.Restore,
                PendingImeiOperationKind.CreateNew));
        Assert.Equal(
            PendingImeiOperationKind.CreateNew,
            gsm.ViewModels.MainViewModel.ResolveDurableImeiOperationKind(
                PendingImeiOperationKind.LegacyNoSim,
                PendingImeiOperationKind.CreateNew));
    }

    [Fact]
    public void ImeiRecoveryCounter_IsSharedAcrossEpochsButIsolatedByCcid()
    {
        string firstEpoch = gsm.ViewModels.MainViewModel.BuildImeiRecoveryCounterKey(
            "com86", CcidA);
        string laterEpoch = gsm.ViewModels.MainViewModel.BuildImeiRecoveryCounterKey(
            "COM86", CcidA);
        string differentSim = gsm.ViewModels.MainViewModel.BuildImeiRecoveryCounterKey(
            "COM86", CcidB);

        Assert.Equal(firstEpoch, laterEpoch);
        Assert.NotEqual(firstEpoch, differentSim);
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
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
