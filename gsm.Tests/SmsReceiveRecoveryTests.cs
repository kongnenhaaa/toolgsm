using System.Collections.Concurrent;
using gsm.Services;

namespace gsm.Tests;

public sealed class SmsReceiveRecoveryTests
{
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
