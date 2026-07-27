using gsm.Services;
using System.Text.Json;

namespace gsm.Tests;

public sealed class SmsInboxStoreTests
{
    [Fact]
    public void UnicodeAndMultilineContent_RoundTripsAfterRestart()
    {
        string directory = TempDirectory();
        try
        {
            var expected = Record(
                "delivery-vietnamese",
                "Thuê bao của Quý khách đã bị NGỪNG CUNG CẤP DỊCH.\r\n" +
                "Vui lòng xác thực lại “thông tin” — CSKH: 18001091.");
            var firstRun = new SmsInboxStore(directory);

            Assert.True(firstRun.Append(expected));

            var afterRestart = new SmsInboxStore(directory);
            SmsInboxRecord actual = Assert.Single(afterRestart.GetRecent(10));
            Assert.Equal(expected, actual);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DuplicateDeliveryId_IsIdempotentAcrossRestart()
    {
        string directory = TempDirectory();
        try
        {
            SmsInboxRecord record = Record("stable-delivery-id", "OTP của bạn là 123456");
            var firstRun = new SmsInboxStore(directory);

            Assert.True(firstRun.Append(record));
            Assert.False(firstRun.Append(record));

            var afterRestart = new SmsInboxStore(directory);
            Assert.False(afterRestart.Append(record));
            Assert.Equal(1, afterRestart.Count);
            Assert.Single(afterRestart.GetRecent(10));
            Assert.Equal(1, CountPhysicalRecords(directory));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DuplicateDeliveryIdWithDifferentPayload_IsRejected()
    {
        string directory = TempDirectory();
        try
        {
            var store = new SmsInboxStore(directory);
            Assert.True(store.Append(Record("conflicting-id", "Nội dung ban đầu")));

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                store.Append(Record("conflicting-id", "Nội dung đã bị thay đổi")));

            Assert.Contains("Conflicting SMS payload", error.Message);
            Assert.Equal(1, store.Count);
            Assert.Equal(1, CountPhysicalRecords(directory));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Delete_RemovesRecordPermanentlyAcrossRestart()
    {
        string directory = TempDirectory();
        try
        {
            var store = new SmsInboxStore(directory);
            Assert.True(store.Append(Record("delete-me", "old")));
            Assert.True(store.Append(Record("keep-me", "keep")));

            Assert.Equal(1, store.Delete(["delete-me"]));
            Assert.DoesNotContain(
                store.GetRecent(10),
                record => record.DeliveryId == "delete-me");
            Assert.Single(store.GetRecent(10));

            var afterRestart = new SmsInboxStore(directory);
            Assert.DoesNotContain(
                afterRestart.GetRecent(10),
                record => record.DeliveryId == "delete-me");
            Assert.Equal(1, afterRestart.Count);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Clear_RemovesAllDurableRecordsAcrossRestart()
    {
        string directory = TempDirectory();
        try
        {
            var store = new SmsInboxStore(directory);
            Assert.True(store.Append(Record("clear-one", "one")));
            Assert.True(store.Append(Record("clear-two", "two")));

            Assert.Equal(2, store.Clear());
            Assert.Empty(store.GetRecent(10));
            Assert.Equal(0, store.Count);

            var afterRestart = new SmsInboxStore(directory);
            Assert.Empty(afterRestart.GetRecent(10));
            Assert.Equal(0, afterRestart.Count);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DuplicateDeliveryConflict_IsRejectedAfterRestart()
    {
        string directory = TempDirectory();
        try
        {
            var firstRun = new SmsInboxStore(directory);
            Assert.True(firstRun.Append(Record("restart-conflict", "original payload")));

            var afterRestart = new SmsInboxStore(directory);
            Assert.Throws<InvalidDataException>(() =>
                afterRestart.Append(Record("restart-conflict", "different payload")));
            Assert.Equal(1, afterRestart.Count);
            Assert.Equal(1, CountPhysicalRecords(directory));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TornFinalLine_IsPreservedAndNewRecordsUseARecoveryFile()
    {
        string directory = TempDirectory();
        try
        {
            var firstRun = new SmsInboxStore(directory);
            Assert.True(firstRun.Append(Record("before-torn-line", "Bản ghi còn nguyên")));
            string originalFile = Assert.Single(
                Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl"));
            File.AppendAllText(originalFile, "{\"deliveryId\":\"torn");

            var afterRestart = new SmsInboxStore(directory);
            Assert.Single(afterRestart.RecoveryWarnings);
            Assert.Equal("before-torn-line", Assert.Single(afterRestart.GetRecent(10)).DeliveryId);
            Assert.True(afterRestart.Append(Record("after-torn-line", "Bản ghi phục hồi")));

            Assert.Equal(2, afterRestart.Count);
            Assert.Equal(2, Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl").Count());
            Assert.Equal(
                ["after-torn-line", "before-torn-line"],
                afterRestart.GetRecent(10).Select(record => record.DeliveryId));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CorruptMiddleLine_IsPreservedAndDoesNotBlockLaterRecords()
    {
        string directory = TempDirectory();
        try
        {
            var firstRun = new SmsInboxStore(directory);
            Assert.True(firstRun.Append(Record("before-corrupt-line", "valid record before")));
            string originalFile = Assert.Single(
                Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl"));
            File.AppendAllText(originalFile, "{not-valid-json}\n");
            File.AppendAllText(
                originalFile,
                JsonSerializer.Serialize(
                    Record("after-corrupt-line", "valid record after"),
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }) + "\n");

            var afterRestart = new SmsInboxStore(directory);

            Assert.Equal(2, afterRestart.Count);
            Assert.Single(afterRestart.RecoveryWarnings);
            Assert.Contains("skipped corrupt SMS inbox line", afterRestart.RecoveryWarnings[0]);
            Assert.Equal(
                ["after-corrupt-line", "before-corrupt-line"],
                afterRestart.GetRecent(10).Select(record => record.DeliveryId));
            Assert.True(afterRestart.Append(Record("after-recovery", "continued writing")));
            Assert.Single(Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void FailedAppend_MovesNextRetryToARecoveryFile()
    {
        string directory = TempDirectory();
        try
        {
            bool failBeforeFirstFlush = true;
            var store = new SmsInboxStore(
                directory,
                durableWrites: true,
                beforeFlushForTests: () =>
                {
                    if (!failBeforeFirstFlush) return;
                    failBeforeFirstFlush = false;
                    throw new IOException("simulated flush failure");
                });

            Assert.Throws<IOException>(() =>
                store.Append(Record("uncertain-write", "write that may be partial")));
            Assert.True(store.Append(Record("recovered-write", "write after recovery")));

            string[] files = Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl").ToArray();
            Assert.Equal(2, files.Length);
            Assert.Single(store.RecoveryWarnings);
            Assert.Contains(
                files,
                file => File.ReadAllText(file).Contains("uncertain-write", StringComparison.Ordinal)
                    && !File.ReadAllText(file).Contains("recovered-write", StringComparison.Ordinal));
            Assert.Contains(
                files,
                file => File.ReadAllText(file).Contains("recovered-write", StringComparison.Ordinal)
                    && !File.ReadAllText(file).Contains("uncertain-write", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ValidFinalJsonWithoutNewline_IsPreservedAndNeverConcatenated()
    {
        string directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            string originalFile = Path.Combine(
                directory,
                "sms-inbox-20260726.jsonl");
            File.WriteAllText(
                originalFile,
                JsonSerializer.Serialize(
                    Record("no-final-newline", "Bản ghi hợp lệ"),
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }));

            var recovered = new SmsInboxStore(directory);
            Assert.Single(recovered.RecoveryWarnings);
            Assert.Equal(1, recovered.Count);
            Assert.True(recovered.Append(
                Record("after-no-newline", "Bản ghi kế tiếp")));

            Assert.Equal(2, Directory.EnumerateFiles(
                directory, "sms-inbox-*.jsonl").Count());
            Assert.Equal(
                ["after-no-newline", "no-final-newline"],
                recovered.GetRecent(10).Select(record => record.DeliveryId));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void InvalidUtf8File_DoesNotBrickStartupAndUsesRecoveryFile()
    {
        string directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            string corruptFile = Path.Combine(
                directory,
                "sms-inbox-20260726.jsonl");
            File.WriteAllBytes(corruptFile, [0xC3, 0x28]);

            var recovered = new SmsInboxStore(directory);

            Assert.Equal(0, recovered.Count);
            Assert.Single(recovered.RecoveryWarnings);
            Assert.True(recovered.Append(
                Record("after-invalid-utf8", "Không mất bản ghi mới")));
            Assert.Equal(2, Directory.EnumerateFiles(
                directory, "sms-inbox-*.jsonl").Count());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ConflictingPhysicalDuplicate_DoesNotBrickStartup()
    {
        string directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            string path = Path.Combine(directory, "sms-inbox-20260726.jsonl");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    Record("physical-conflict", "first"), options)
                + "\n"
                + JsonSerializer.Serialize(
                    Record("physical-conflict", "second"), options)
                + "\n");

            var recovered = new SmsInboxStore(directory);

            Assert.Equal(1, recovered.Count);
            Assert.Single(recovered.RecoveryWarnings);
            Assert.Equal(2, recovered.GetRecent(10).Count);
            Assert.False(recovered.Append(
                Record("physical-conflict", "first")));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TenThousandRecords_RemainOnDiskWhileGetRecentIsBounded()
    {
        string directory = TempDirectory();
        try
        {
            // Production always uses WriteThrough + Flush(true). This high-volume
            // retention test uses the internal buffered test mode to avoid 10,000
            // physical fsync calls while exercising identical JSONL framing/indexing.
            var store = new SmsInboxStore(directory, durableWrites: false);
            DateTimeOffset start = new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
            for (int i = 0; i < 10_000; i++)
            {
                Assert.True(store.Append(Record(
                    $"delivery-{i:D5}",
                    $"Nội dung SMS số {i}",
                    start.AddSeconds(i))));
            }

            var afterRestart = new SmsInboxStore(directory);
            IReadOnlyList<SmsInboxRecord> recent = afterRestart.GetRecent(37);

            Assert.Equal(10_000, afterRestart.Count);
            Assert.Equal(10_000, CountPhysicalRecords(directory));
            Assert.Equal(37, recent.Count);
            Assert.Equal("delivery-09999", recent[0].DeliveryId);
            Assert.Equal("delivery-09963", recent[^1].DeliveryId);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static SmsInboxRecord Record(
        string deliveryId,
        string content,
        DateTimeOffset? receivedAtUtc = null) => new()
    {
        DeliveryId = deliveryId,
        ReceivedAtUtc = receivedAtUtc ?? new DateTimeOffset(2026, 7, 26, 2, 3, 4, TimeSpan.Zero),
        PortName = "COM88",
        ReceiverPhone = "0843257140",
        Sender = "VinaPhone",
        Content = content,
        Otp = "N/A",
        NetworkProvider = "VinaPhone",
        Status = "Hoạt động",
        CallCount = "0",
        ForwardContent = string.Empty
    };

    private static int CountPhysicalRecords(string directory) =>
        Directory.EnumerateFiles(directory, "sms-inbox-*.jsonl")
            .Sum(file => File.ReadLines(file).Count(line => !string.IsNullOrWhiteSpace(line)));

    private static string TempDirectory() => Path.Combine(
        Path.GetTempPath(),
        $"toolgsm-sms-inbox-{Guid.NewGuid():N}");

    private static void DeleteTempDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("toolgsm-sms-inbox-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected test directory '{fullPath}'.");
        }

        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }
}
