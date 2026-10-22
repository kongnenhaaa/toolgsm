using System.Collections.Concurrent;
using System.IO.Ports;
using System.Reflection;
using gsm.Services;

namespace gsm.Tests;

public sealed class SmsReceiveRecoveryTests
{
    [Theory]
    [InlineData("\r\n+CMGF: 1\r\nOK\r\n", true)]
    [InlineData("\r\n+CMGF: 0\r\nOK\r\n", false)]
    [InlineData("\r\nOK\r\n", false)]
    [InlineData("\r\n+CMGF: 1\r\nERROR\r\n", false)]
    public void PduReadRestore_RequiresConfirmedTextMode(
        string response,
        bool expected) =>
        Assert.Equal(expected,
            GsmModemService.IsSmsTextModeConfirmed(response));

    [Fact]
    public async Task ScanTurns_AllowOtherPortsWhileBoundingUsbHubLoad()
    {
        var service = new GsmModemService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heldTurns = new List<IDisposable>();
        try
        {
            for (int i = 0; i < GsmModemService.SmsScanMaxConcurrency; i++)
            {
                heldTurns.Add(await service.AcquireSmsScanTurnAsync(
                    cancellation.Token));
            }

            // The first turn remains held, proving that later COMs can scan
            // concurrently. An additional scan must wait for capacity.
            Task<IDisposable> nextTurn = service.AcquireSmsScanTurnAsync(
                cancellation.Token);
            await Task.Delay(
                GsmModemService.SmsReceiveWatchdogTurnGap
                    + TimeSpan.FromMilliseconds(100),
                cancellation.Token);
            Assert.False(nextTurn.IsCompleted);

            heldTurns[0].Dispose();
            heldTurns.RemoveAt(0);
            heldTurns.Add(await nextTurn.WaitAsync(cancellation.Token));
        }
        finally
        {
            cancellation.Cancel();
            foreach (IDisposable turn in heldTurns)
                turn.Dispose();
        }
    }

    [Fact]
    public async Task CancelledScanStart_DoesNotLeakBankCapacity()
    {
        var service = new GsmModemService();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heldTurns = new List<IDisposable>();
        try
        {
            heldTurns.Add(await service.AcquireSmsScanTurnAsync(
                deadline.Token));
            using var cancelled = new CancellationTokenSource();
            Task<IDisposable> aborted = service.AcquireSmsScanTurnAsync(
                cancelled.Token);
            cancelled.CancelAfter(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => aborted);

            // The cancelled request already held a bank permit while waiting
            // for the start gap. All seven remaining permits must be usable.
            for (int i = 1; i < GsmModemService.SmsScanMaxConcurrency; i++)
            {
                heldTurns.Add(await service.AcquireSmsScanTurnAsync(
                    deadline.Token));
            }
        }
        finally
        {
            deadline.Cancel();
            foreach (IDisposable turn in heldTurns)
                turn.Dispose();
        }
    }

    [Fact]
    public void ConcurrentAnnouncements_StartOneReaderPerPort()
    {
        var service = new GsmModemService();
        using var firstPort = new SerialPort("COM86");
        using var secondPort = new SerialPort("COM87");
        var serialPorts = (ConcurrentDictionary<string, SerialPort>)
            typeof(GsmModemService).GetField(
                "_serialPorts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
        MethodInfo enqueue = typeof(GsmModemService).GetMethod(
            "QueueStoredSmsRead", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo invalidate = typeof(GsmModemService).GetMethod(
            "InvalidateSmsQueueGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        serialPorts["COM86"] = firstPort;
        serialPorts["COM87"] = secondPort;

        try
        {
            Parallel.For(0, 256, index =>
            {
                string portName = index % 2 == 0 ? "COM86" : "COM87";
                enqueue.Invoke(service, [portName, (index / 2).ToString()]);
            });

            Assert.Equal(2, service.SmsReadWorkerStartCount);
        }
        finally
        {
            invalidate.Invoke(service, ["COM86"]);
            invalidate.Invoke(service, ["COM87"]);
            serialPorts.TryRemove("COM86", out _);
            serialPorts.TryRemove("COM87", out _);
        }
    }

    [Theory]
    [InlineData(new[] { 1 }, 2, false)]
    [InlineData(new[] { 1, 3 }, 3, false)]
    [InlineData(new[] { 1, 2, 3 }, 3, true)]
    [InlineData(new[] { 3, 1, 2, 2 }, 3, true)]
    public void MultipartSlot_IsNotEligibleForCleanupUntilEveryPartExists(
        int[] presentSequences,
        int total,
        bool expectedComplete)
    {
        Assert.Equal(
            expectedComplete,
            GsmModemService.IsMultipartAssemblyComplete(
                presentSequences,
                total));
    }

    [Fact]
    public void WorkerExit_ReleasesOnlyClaimsOwnedByItsPortGeneration()
    {
        const char separator = '\u001f';
        var claims = new ConcurrentDictionary<string, int>(
            StringComparer.OrdinalIgnoreCase)
        {
            [$"COM86{separator}7{separator}1"] = 1,
            [$"com86{separator}7{separator}2"] = 2,
            [$"COM86{separator}8{separator}1"] = 1,
            [$"COM860{separator}7{separator}1"] = 1
        };

        int removed = GsmModemService.RemoveSmsQueueClaimsForGeneration(
            claims,
            "COM86",
            generation: 7);

        Assert.Equal(2, removed);
        Assert.DoesNotContain(
            claims.Keys,
            key => key.StartsWith(
                $"COM86{separator}7{separator}",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"COM86{separator}8{separator}1", claims.Keys);
        Assert.Contains($"COM860{separator}7{separator}1", claims.Keys);
    }
}
