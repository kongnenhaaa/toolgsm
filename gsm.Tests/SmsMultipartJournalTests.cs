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

    [Fact]
    public async Task TransientDestinationLockIsRetriedWithoutLosingPart()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);
            var concat = new SmsConcatInfo(27, 2, 1);
            journal.RecordAndGetParts("COM86", "VinaPhone", concat, "phan-1");

            Task<IReadOnlyList<SmsMultipartJournal.Part>> pending;
            using (var destinationLock = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                pending = Task.Run(() => journal.RecordAndGetParts(
                    "COM86", "VinaPhone", concat with { Sequence = 2 }, "phan-2"));

                Assert.True(
                    SpinWait.SpinUntil(() => File.Exists(path + ".tmp"), TimeSpan.FromSeconds(2)),
                    "The replacement attempt did not reach its durable temp-file stage.");
            }

            IReadOnlyList<SmsMultipartJournal.Part> parts =
                await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { 1, 2 }, parts.Select(x => x.Sequence));
            var reloaded = new SmsMultipartJournal(path);
            Assert.Equal(
                "phan-1phan-2",
                string.Concat(reloaded.GetParts("COM86", "VinaPhone", concat)
                    .Select(x => x.Content)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void CarrierAliasHandoff_MergesDurablePartsAcrossRestart()
    {
        string path = TempJournalPath();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        string[] parts = ["part-1", "part-2", "part-3", "part-4", "part-5"];
        try
        {
            new SmsMultipartJournal(path).RecordAndGetParts(
                "COM110", "888", new(69, 5, 1), parts[0], start);

            var afterRestart = new SmsMultipartJournal(path);
            IReadOnlyList<SmsMultipartJournal.Part> durableParts = Array.Empty<SmsMultipartJournal.Part>();
            for (int sequence = 2; sequence <= 5; sequence++)
            {
                durableParts = afterRestart.RecordAndGetParts(
                    "COM110",
                    "565656",
                    new(69, 5, sequence),
                    parts[sequence - 1],
                    start.AddSeconds(sequence));
            }

            Assert.Equal(Enumerable.Range(1, 5), durableParts.Select(part => part.Sequence));
            Assert.Equal(string.Concat(parts), string.Concat(durableParts.Select(part => part.Content)));

            var reloaded = new SmsMultipartJournal(path);
            Assert.Equal(5, reloaded.GetParts("COM110", "888", new(69, 5, 1)).Count);
            Assert.Equal(5, reloaded.GetParts("COM110", "565656", new(69, 5, 5)).Count);

            reloaded.Complete("COM110", "565656", new(69, 5, 5));
            Assert.Empty(new SmsMultipartJournal(path)
                .GetParts("COM110", "888", new(69, 5, 1)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void PreexistingSplitAliasEntries_AreResolvedWithoutLosingEitherSide()
    {
        string path = TempJournalPath();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            var legacySnapshot = new object[]
            {
                new
                {
                    Scope = "COM110",
                    Sender = "888",
                    Reference = 69,
                    Total = 5,
                    LastUpdated = now,
                    Parts = new Dictionary<int, string> { [1] = "part-1" }
                },
                new
                {
                    Scope = "COM110",
                    Sender = "565656",
                    Reference = 69,
                    Total = 5,
                    LastUpdated = now.AddSeconds(2),
                    Parts = new Dictionary<int, string>
                    {
                        [2] = "part-2",
                        [3] = "part-3",
                        [4] = "part-4",
                        [5] = "part-5"
                    }
                }
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(legacySnapshot));

            var journal = new SmsMultipartJournal(path);
            IReadOnlyList<SmsMultipartJournal.Part> parts =
                journal.GetParts("COM110", "565656", new(69, 5, 5));

            Assert.Equal("part-1part-2part-3part-4part-5",
                string.Concat(parts.Select(part => part.Content)));

            journal.Complete("COM110", "565656", new(69, 5, 5));
            Assert.Empty(new SmsMultipartJournal(path)
                .GetParts("COM110", "888", new(69, 5, 1)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void KnownAliasesWithConflictingPart_RemainSeparateInJournal()
    {
        string path = TempJournalPath();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        try
        {
            var journal = new SmsMultipartJournal(path);
            journal.RecordAndGetParts(
                "COM110", "888", new(69, 2, 1), "message-a:", start);
            journal.RecordAndGetParts(
                "COM110", "565656", new(69, 2, 1), "message-b:", start.AddSeconds(1));

            IReadOnlyList<SmsMultipartJournal.Part> messageA = journal.RecordAndGetParts(
                "COM110", "888", new(69, 2, 2), "end-a", start.AddSeconds(2));
            IReadOnlyList<SmsMultipartJournal.Part> messageB = journal.RecordAndGetParts(
                "COM110", "565656", new(69, 2, 2), "end-b", start.AddSeconds(3));

            Assert.Equal("message-a:end-a", string.Concat(messageA.Select(part => part.Content)));
            Assert.Equal("message-b:end-b", string.Concat(messageB.Select(part => part.Content)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void KnownAliasesOutsideHandoffWindow_RemainSeparateInJournal()
    {
        string path = TempJournalPath();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        try
        {
            var journal = new SmsMultipartJournal(path);
            journal.RecordAndGetParts(
                "COM110", "888", new(69, 2, 1), "old-1", start);
            IReadOnlyList<SmsMultipartJournal.Part> later = journal.RecordAndGetParts(
                "COM110",
                "565656",
                new(69, 2, 2),
                "new-2",
                start.Add(SmsMultipartSenderAliases.HandoffWindow).AddSeconds(1));

            Assert.Single(later);
            Assert.Equal(2, later[0].Sequence);
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void ReusedDirectReferenceOutsideCorrelationWindow_NeverCreatesFrankensteinMessage()
    {
        string path = TempJournalPath();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-2);
        const string scope = "ccid:89840200000000000009";
        try
        {
            var journal = new SmsMultipartJournal(path);
            journal.RecordAndGetParts(
                scope,
                "888",
                new(47, 2, 1),
                "old-1",
                start,
                partIdentity: "old-part-1");
            string oldMessageId = journal.GetMessageIdForPartIdentity(
                scope,
                "old-part-1");

            DateTimeOffset newGenerationAt = start
                .Add(SmsMultipartJournal.CorrelationWindow)
                .AddSeconds(1);
            IReadOnlyList<SmsMultipartJournal.Part> newSecond =
                journal.RecordAndGetParts(
                    scope,
                    "888",
                    new(47, 2, 2),
                    "new-2",
                    newGenerationAt,
                    partIdentity: "new-part-2");
            string newMessageId = journal.GetMessageIdForPartIdentity(
                scope,
                "new-part-2");

            Assert.Single(newSecond);
            Assert.Equal(2, newSecond[0].Sequence);
            Assert.NotEqual(oldMessageId, newMessageId);

            IReadOnlyList<SmsMultipartJournal.Part> completedNew =
                journal.RecordAndGetParts(
                    scope,
                    "888",
                    new(47, 2, 1),
                    "new-1",
                    newGenerationAt.AddSeconds(1),
                    partIdentity: "new-part-1");
            Assert.Equal(
                "new-1new-2",
                string.Concat(completedNew.Select(part => part.Content)));
            Assert.Equal(
                "new-1new-2",
                Assert.Single(journal.GetCompletedSnapshots(scope)).Content);

            // A durable identity is stronger than age. Re-reading the original
            // SIM slot must resolve its original generation, not make a third one.
            IReadOnlyList<SmsMultipartJournal.Part> replayedOld =
                journal.RecordAndGetParts(
                    scope,
                    "888",
                    new(47, 2, 1),
                    "old-1",
                    DateTimeOffset.UtcNow,
                    partIdentity: "old-part-1");
            Assert.Single(replayedOld);
            Assert.Equal("old-1", replayedOld[0].Content);
            Assert.Equal(
                oldMessageId,
                journal.GetMessageIdForPartIdentity(scope, "old-part-1"));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void UnrelatedSendersWithSameReference_DoNotShareDurableParts()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);
            journal.RecordAndGetParts("COM1", "BANK_A", new(33, 2, 1), "A1");
            IReadOnlyList<SmsMultipartJournal.Part> bankB = journal.RecordAndGetParts(
                "COM1", "BANK_B", new(33, 2, 2), "B2");

            Assert.Single(bankB);
            Assert.Equal(2, bankB[0].Sequence);
            Assert.Single(journal.GetParts("COM1", "BANK_A", new(33, 2, 1)));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void CommittedPartIdentityReplayNeverExpiresAfterSimSlotWasReleased()
    {
        string path = TempJournalPath();
        DateTimeOffset twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
        try
        {
            var firstRun = new SmsMultipartJournal(path, TimeSpan.FromMilliseconds(1));
            firstRun.RecordAndGetParts(
                    "ccid:89840200000000000001",
                    "VinaPhone",
                    new(72, 2, 1),
                    "phan-cu-",
                    twoDaysAgo,
                    "COM101",
                    "sms-stored-part-1");
            string messageId = firstRun.GetMessageIdForPartIdentity(
                "ccid:89840200000000000001",
                "sms-stored-part-1");

            IReadOnlyList<SmsMultipartJournal.Part> parts =
                new SmsMultipartJournal(path, TimeSpan.FromMilliseconds(1))
                    .RecordAndGetParts(
                        "ccid:89840200000000000001",
                        "VinaPhone",
                        new(72, 2, 1),
                        "phan-cu-",
                        DateTimeOffset.UtcNow,
                        "COM101",
                        "sms-stored-part-1");

            Assert.Single(parts);
            Assert.Equal("phan-cu-", parts[0].Content);
            Assert.Equal(
                messageId,
                new SmsMultipartJournal(path).GetMessageIdForPartIdentity(
                    "ccid:89840200000000000001",
                    "sms-stored-part-1"));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void LegacyCom101Journal_RebindsToCcidAndCompletesRemainingParts()
    {
        string path = TempJournalPath();
        const string scope = "ccid:89840200011639721552";
        try
        {
            var legacy = new[]
            {
                new
                {
                    Scope = "COM101",
                    Sender = "VinaPhone",
                    AcceptedSenders = new[] { "VinaPhone" },
                    Reference = 72,
                    Total = 12,
                    LastUpdated = DateTimeOffset.UtcNow.AddHours(-1),
                    Parts = Enumerable.Range(1, 7)
                        .ToDictionary(sequence => sequence, sequence => $"p{sequence}-")
                }
            };
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(legacy));

            var journal = new SmsMultipartJournal(path);
            journal.RebindLegacyPortScope("COM101", scope);
            for (int sequence = 8; sequence <= 12; sequence++)
            {
                journal.RecordAndGetParts(
                    scope,
                    "VinaPhone",
                    new(72, 12, sequence),
                    $"p{sequence}-",
                    portName: "COM101",
                    partIdentity: $"sms-stored-part-{sequence}");
            }

            SmsMultipartJournal.CompletedSnapshot completed = Assert.Single(
                journal.GetCompletedSnapshots(scope));
            Assert.Equal(
                string.Concat(Enumerable.Range(1, 12).Select(x => $"p{x}-")),
                completed.Content);
            Assert.Equal("COM101", completed.PortName);
            Assert.True(completed.RequiresSimCleanup);
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void ReusedCarrierReference_HasIndependentDeliveryGeneration()
    {
        string path = TempJournalPath();
        const string scope = "ccid:89840200000000000002";
        try
        {
            var journal = new SmsMultipartJournal(path);
            var first = new SmsConcatInfo(17, 2, 1);
            journal.RecordAndGetParts(
                scope, "888", first, "A1", partIdentity: "a1");
            journal.RecordAndGetParts(
                scope, "888", first with { Sequence = 2 }, "A2", partIdentity: "a2");
            string firstId = journal.GetMessageIdForPartIdentity(scope, "a2");

            journal.RecordAndGetParts(
                scope, "888", first, "B1", partIdentity: "b1");
            journal.RecordAndGetParts(
                scope, "888", first with { Sequence = 2 }, "B2", partIdentity: "b2");
            string secondId = journal.GetMessageIdForPartIdentity(scope, "b2");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(2, journal.GetCompletedSnapshots(scope).Count);

            journal.MarkDeliveryAcknowledged(firstId);
            journal.Complete(firstId);

            SmsMultipartJournal.CompletedSnapshot remaining = Assert.Single(
                journal.GetCompletedSnapshots(scope));
            Assert.Equal(secondId, remaining.MessageId);
            Assert.Equal("B1B2", remaining.Content);
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void DirectMultipartPortScope_RebindsAndCanBeCleanedWithoutSimSlot()
    {
        string path = TempJournalPath();
        const string scope = "ccid:89840200000000000003";
        try
        {
            var journal = new SmsMultipartJournal(path);
            journal.RecordAndGetParts(
                "COM86",
                "BANK",
                new(91, 2, 1),
                "hello ",
                portName: "COM86",
                partIdentity: "sms-direct-part-one");
            journal.RecordAndGetParts(
                "COM86",
                "BANK",
                new(91, 2, 2),
                "world",
                portName: "COM86",
                partIdentity: "sms-direct-part-two");

            journal.RebindLegacyPortScope("COM86", scope);

            SmsMultipartJournal.CompletedSnapshot completed = Assert.Single(
                journal.GetCompletedSnapshots(scope));
            Assert.Equal("hello world", completed.Content);
            Assert.False(completed.RequiresSimCleanup);

            journal.MarkDeliveryAcknowledged(completed.MessageId);
            journal.Complete(completed.MessageId);
            Assert.Empty(journal.GetCompletedSnapshots(
                scope,
                includeAcknowledged: true));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void LegacyPublishJournal_IsImportedOnlyOnceAndNeverResurrected()
    {
        string stablePath = TempJournalPath();
        string legacyPath = TempJournalPath();
        try
        {
            var legacy = new[]
            {
                new
                {
                    Scope = "COM101",
                    Sender = "VinaPhone",
                    Reference = 72,
                    Total = 2,
                    LastUpdated = DateTimeOffset.UtcNow,
                    Parts = new Dictionary<int, string>
                    {
                        [1] = "old-1",
                        [2] = "old-2"
                    }
                }
            };
            File.WriteAllText(
                legacyPath,
                System.Text.Json.JsonSerializer.Serialize(legacy));

            var firstRun = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);
            SmsMultipartJournal.CompletedSnapshot imported = Assert.Single(
                firstRun.GetCompletedSnapshots("COM101"));
            firstRun.Complete(imported.MessageId);

            var secondRun = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);
            Assert.Empty(secondRun.GetCompletedSnapshots(
                "COM101",
                includeAcknowledged: true));
        }
        finally
        {
            DeleteJournalFiles(stablePath);
            DeleteJournalFiles(legacyPath);
        }
    }

    [Fact]
    public void ExistingStableJournalWithoutManifest_EstablishesBaselineWithoutResurrection()
    {
        string stablePath = TempJournalPath();
        string legacyPath = TempJournalPath();
        try
        {
            File.WriteAllText(stablePath, "[]");
            File.WriteAllText(
                legacyPath,
                System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Scope = "COM101",
                        Sender = "VinaPhone",
                        Reference = 73,
                        Total = 1,
                        LastUpdated = DateTimeOffset.UtcNow,
                        Parts = new Dictionary<int, string> { [1] = "already-handled" }
                    }
                }));

            var upgraded = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);

            Assert.Empty(upgraded.GetCompletedSnapshots(
                "COM101",
                includeAcknowledged: true));
            Assert.True(File.Exists(stablePath + ".legacy-migration.json"));
            Assert.Empty(new SmsMultipartJournal(
                    stablePath,
                    legacyPaths: [legacyPath])
                .GetCompletedSnapshots("COM101", includeAcknowledged: true));
        }
        finally
        {
            DeleteJournalFiles(stablePath);
            DeleteJournalFiles(legacyPath);
        }
    }

    [Fact]
    public void MigrationManifest_RetriesAConfiguredSourceThatWasNotYetImported()
    {
        string stablePath = TempJournalPath();
        string firstLegacyPath = TempJournalPath();
        string lateLegacyPath = TempJournalPath();
        try
        {
            File.WriteAllText(
                firstLegacyPath,
                LegacyEntryJson("COM101", 81, "first-source"));

            var firstRun = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [firstLegacyPath, lateLegacyPath]);
            Assert.Single(firstRun.GetCompletedSnapshots("COM101"));

            // The stable file now exists, but the sidecar still knows that this
            // source was missing and must be retried rather than skipped forever.
            File.WriteAllText(
                lateLegacyPath,
                LegacyEntryJson("COM102", 82, "late-source"));
            var secondRun = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [firstLegacyPath, lateLegacyPath]);

            Assert.Single(secondRun.GetCompletedSnapshots("COM101"));
            Assert.Equal(
                "late-source",
                Assert.Single(secondRun.GetCompletedSnapshots("COM102")).Content);
        }
        finally
        {
            DeleteJournalFiles(stablePath);
            DeleteJournalFiles(firstLegacyPath);
            DeleteJournalFiles(lateLegacyPath);
        }
    }

    [Fact]
    public void LegacySourceWithValidPrefixAndNullTail_RollsBackAndRetriesAfterRepair()
    {
        string stablePath = TempJournalPath();
        string legacyPath = TempJournalPath();
        try
        {
            string validEntry = LegacyEntryObjectJson(
                "COM103",
                83,
                "must-not-partially-import");
            File.WriteAllText(legacyPath, $"[{validEntry},null]");

            var failedMigration = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);
            Assert.Throws<InvalidDataException>(() =>
                failedMigration.GetCompletedSnapshots("COM103"));
            Assert.Throws<InvalidDataException>(() =>
                failedMigration.Complete("unrelated-message-id"));
            Assert.False(File.Exists(stablePath));

            File.WriteAllText(legacyPath, $"[{validEntry}]");
            var repairedMigration = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);
            Assert.Equal(
                "must-not-partially-import",
                Assert.Single(repairedMigration.GetCompletedSnapshots("COM103")).Content);
        }
        finally
        {
            DeleteJournalFiles(stablePath);
            DeleteJournalFiles(legacyPath);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not-json")]
    public void CorruptLegacySource_FailsClosedWithoutPublishingPartialState(string json)
    {
        string stablePath = TempJournalPath();
        string legacyPath = TempJournalPath();
        try
        {
            File.WriteAllText(legacyPath, json);
            var journal = new SmsMultipartJournal(
                stablePath,
                legacyPaths: [legacyPath]);

            Assert.Throws<InvalidDataException>(() => journal.GetParts(
                "COM9", "888", new(1, 2, 1)));
            Assert.Throws<InvalidDataException>(() => journal.RecordAndGetParts(
                "COM9", "888", new(1, 2, 1), "part-one"));
            Assert.False(File.Exists(stablePath));
        }
        finally
        {
            DeleteJournalFiles(stablePath);
            DeleteJournalFiles(legacyPath);
        }
    }

    [Fact]
    public void DirectSingleMessage_IsWriteAheadLoggedWithStableDeliveryId()
    {
        string path = TempJournalPath();
        const string deliveryId = "sms-direct-stable-id";
        try
        {
            var firstRun = new SmsMultipartJournal(path);
            firstRun.RecordAndGetParts(
                "COM86",
                "BANK",
                new(12345, 1, 1),
                "OTP 609998",
                portName: "COM86",
                partIdentity: deliveryId,
                messageIdHint: deliveryId);

            SmsMultipartJournal.CompletedSnapshot recovered = Assert.Single(
                new SmsMultipartJournal(path).GetCompletedSnapshots("COM86"));
            Assert.Equal(deliveryId, recovered.MessageId);
            Assert.Equal("OTP 609998", recovered.Content);
            Assert.False(recovered.RequiresSimCleanup);
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void CompletedStoredMessage_RemainsUntilEveryKnownSlotWasCleaned()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);
            const string scope = "ccid:89840200000000000004";
            journal.RecordAndGetParts(
                scope,
                "VinaPhone",
                new(61, 2, 1),
                "part-1",
                partIdentity: "sms-stored-slot-1");
            journal.RecordAndGetParts(
                scope,
                "VinaPhone",
                new(61, 2, 2),
                "part-2",
                partIdentity: "sms-stored-slot-2");
            string messageId = journal.GetMessageIdForPartIdentity(
                scope,
                "sms-stored-slot-2");
            journal.MarkDeliveryAcknowledged(messageId);

            journal.MarkPartCleaned(messageId, "sms-stored-slot-2");
            Assert.False(journal.IsSimCleanupConfirmed(messageId));
            SmsMultipartJournal.CompletedSnapshot retained = Assert.Single(
                new SmsMultipartJournal(path).GetCompletedSnapshots(
                    scope,
                    includeAcknowledged: true));
            Assert.True(retained.RequiresSimCleanup);
            Assert.False(retained.SimCleanupConfirmed);

            journal.MarkPartCleaned(messageId, "sms-stored-slot-1");
            Assert.True(journal.IsSimCleanupConfirmed(messageId));
            journal.Complete(messageId);
            Assert.Empty(new SmsMultipartJournal(path).GetCompletedSnapshots(
                scope,
                includeAcknowledged: true));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void MarkPartCleaned_MissingMessage_FailsClosed()
    {
        string path = TempJournalPath();
        try
        {
            var journal = new SmsMultipartJournal(path);

            Assert.Throws<InvalidDataException>(() =>
                journal.MarkPartCleaned(
                    "missing-message",
                    "sms-stored-missing-part"));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Fact]
    public void IdentitylessLegacyPart_NeverReportsVacuousSimCleanup()
    {
        string path = TempJournalPath();
        try
        {
            File.WriteAllText(path,
                "[{\"Scope\":\"COM86\",\"Sender\":\"888\",\"Reference\":44," +
                "\"Total\":1,\"LastUpdated\":\"2026-07-26T00:00:00Z\"," +
                "\"DeliveryAcknowledged\":true,\"Parts\":{\"1\":\"legacy\"}}]");

            var journal = new SmsMultipartJournal(path);
            SmsMultipartJournal.CompletedSnapshot snapshot = Assert.Single(
                journal.GetCompletedSnapshots("COM86", includeAcknowledged: true));

            Assert.True(snapshot.RequiresSimCleanup);
            Assert.False(snapshot.SimCleanupConfirmed);
            Assert.False(journal.IsSimCleanupConfirmed(snapshot.MessageId));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Scope\":\"COM9\",\"Sender\":\"888\",\"Reference\":1,\"Total\":2,\"Parts\":null}]")]
    public void MalformedExistingJournal_IsPreservedAndFailsClosed(string json)
    {
        string path = TempJournalPath();
        try
        {
            File.WriteAllText(path, json);
            var journal = new SmsMultipartJournal(path);

            Assert.Throws<InvalidDataException>(() => journal.RecordAndGetParts(
                "COM9", "888", new(1, 2, 1), "part-one"));
            Assert.Throws<InvalidDataException>(() => journal.GetParts(
                "COM9", "888", new(1, 2, 1)));
            Assert.Throws<InvalidDataException>(() =>
                journal.GetCompletedSnapshots("COM9"));
            Assert.Throws<InvalidDataException>(() =>
                journal.Complete("some-message-id"));
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            DeleteJournalFiles(path);
        }
    }

    private static string TempJournalPath() => Path.Combine(
        Path.GetTempPath(), $"toolgsm-sms-journal-{Guid.NewGuid():N}.json");

    private static string LegacyEntryJson(
        string scope,
        int reference,
        string content) =>
        $"[{LegacyEntryObjectJson(scope, reference, content)}]";

    private static string LegacyEntryObjectJson(
        string scope,
        int reference,
        string content) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Scope = scope,
            Sender = "VinaPhone",
            Reference = reference,
            Total = 1,
            LastUpdated = DateTimeOffset.UtcNow,
            Parts = new Dictionary<int, string> { [1] = content }
        });

    private static void DeleteJournalFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (File.Exists(path + ".legacy-migration.json"))
            File.Delete(path + ".legacy-migration.json");
        if (File.Exists(path + ".legacy-migration.json.tmp"))
            File.Delete(path + ".legacy-migration.json.tmp");
    }
}
