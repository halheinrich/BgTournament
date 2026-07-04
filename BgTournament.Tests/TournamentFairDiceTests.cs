using BgTournament.Server;
using Microsoft.Extensions.DependencyInjection;

namespace BgTournament.Tests;

/// <summary>
/// Fair mode at the tournament layer: an unseeded tournament runs every match on
/// its own freshly generated committed key, while a seeded tournament keeps the
/// reproducible <c>SeededDiceSource</c> (no commitment). The scheduled per-match
/// seed stays structural in both — it is never the fair-mode dice driver.
/// </summary>
public class TournamentFairDiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task UnseededTournament_RunsEveryMatchOnItsOwnFairKey()
    {
        using var factory = ServerHarness.NewFactory();
        await ServerHarness.RunWellBehavedClientAsync(factory, "FairP1", seed: 11, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "FairP2", seed: 22, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "FairP1", "FairP2");

        // No seed → fair mode; matchLength 1 keeps the pairing's two matches quick.
        var started = await ServerHarness.StartFairTournamentAsync(
            factory, ["FairP1", "FairP2"], matchLength: 1, matchesPerPairing: 2);
        string tournamentId = started.GetProperty("tournamentId").GetString()!;
        var ended = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId, Deadline);
        Assert.Equal("completed", ended.GetProperty("status").GetString());

        var matches = factory.Services.GetRequiredService<MatchService>();
        var keys = new List<string>();
        foreach (var entry in ended.GetProperty("matches").EnumerateArray())
        {
            string matchId = entry.GetProperty("matchId").GetString()!;
            Assert.True(matches.TryGetRecord(matchId, out var record));
            Assert.NotNull(record.DiceKey);   // fair mode drove this match
            keys.Add(record.DiceKey!.ToHex());
        }

        // Each match got its own generated key — not a shared or derived one.
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task SeededTournament_RunsEveryMatchOnSeededDice_NoKey()
    {
        using var factory = ServerHarness.NewFactory();
        await ServerHarness.RunWellBehavedClientAsync(factory, "SeedP1", seed: 33, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "SeedP2", seed: 44, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "SeedP1", "SeedP2");

        var started = await ServerHarness.StartTournamentAsync(
            factory, ["SeedP1", "SeedP2"], matchLength: 1, matchesPerPairing: 2, seed: 555);
        string tournamentId = started.GetProperty("tournamentId").GetString()!;
        var ended = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId, Deadline);
        Assert.Equal("completed", ended.GetProperty("status").GetString());

        var matches = factory.Services.GetRequiredService<MatchService>();
        foreach (var entry in ended.GetProperty("matches").EnumerateArray())
        {
            string matchId = entry.GetProperty("matchId").GetString()!;
            Assert.True(matches.TryGetRecord(matchId, out var record));
            Assert.Null(record.DiceKey);   // seeded mode — no committed key
        }
    }
}
