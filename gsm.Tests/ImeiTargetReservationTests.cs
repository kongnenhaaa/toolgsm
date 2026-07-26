using System.Collections.Concurrent;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class ImeiTargetReservationTests
{
    private const string Candidate = "355008370781449";

    [Fact]
    public async Task ParallelOwners_CannotReserveSameGeneratedImei()
    {
        var reservations = new ConcurrentDictionary<string, string>(
            StringComparer.Ordinal);
        Task<bool>[] attempts = Enumerable.Range(1, 32)
            .Select(index => Task.Run(() =>
                MainViewModel.TryReserveImeiCandidate(
                    reservations,
                    Candidate,
                    $"PORT:COM{index}",
                    Array.Empty<string>())))
            .ToArray();

        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.Single(reservations);
    }

    [Fact]
    public void ExistingEquivalentSpareDigit_BlocksGeneratedCandidate()
    {
        var reservations = new ConcurrentDictionary<string, string>(
            StringComparer.Ordinal);

        bool reserved = MainViewModel.TryReserveImeiCandidate(
            reservations,
            Candidate,
            "PORT:COM9",
            ["355008370781440"]);

        Assert.False(reserved);
        Assert.Empty(reservations);
    }

    [Fact]
    public void SameOwner_CanPassReservationIntoWritePipeline()
    {
        var reservations = new ConcurrentDictionary<string, string>(
            StringComparer.Ordinal);
        const string owner = "SIM:89840200011750541177";

        Assert.True(MainViewModel.TryReserveImeiCandidate(
            reservations,
            Candidate,
            owner,
            Array.Empty<string>()));
        Assert.True(MainViewModel.TryReserveImeiCandidate(
            reservations,
            Candidate,
            owner,
            Array.Empty<string>()));
        Assert.True(MainViewModel.IsImeiReservationOwnedByCurrentOperation(
            reservations[Candidate],
            "COM83",
            "89840200011750541177"));
    }

    [Theory]
    [InlineData("SIM:89840200011750541177", "COM83", "89840200011750541177", true)]
    [InlineData("SIM:89840200011750541178", "COM83", "89840200011750541177", false)]
    [InlineData("BULK:run-1:89840200011750541177", "COM83", "89840200011750541177", true)]
    [InlineData("BULK:run-1:89840200011750541178", "COM83", "89840200011750541177", false)]
    [InlineData("PORT:COM83", "COM83", "", true)]
    [InlineData("PORT:COM84", "COM83", "", false)]
    public void WritePipeline_AcceptsOnlyItsExactReservationOwner(
        string reservedOwner,
        string portName,
        string ccid,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainViewModel.IsImeiReservationOwnedByCurrentOperation(
                reservedOwner,
                portName,
                ccid));
    }

    [Fact]
    public void Generator_RetriesCollision_AndReservesNextUniqueCandidate()
    {
        var reservations = new ConcurrentDictionary<string, string>(
            StringComparer.Ordinal);
        var generated = new Queue<string>(
            [Candidate, "352054261826334"]);

        string result = MainViewModel.GenerateUniqueReservedImeiTarget(
            reservations,
            "PORT:COM9",
            [Candidate],
            generated.Dequeue,
            maxAttempts: 2);

        Assert.Equal("352054261826334", result);
        Assert.Equal(
            "PORT:COM9",
            reservations["352054261826334"]);
    }
}
