using System.Text.Json;
using BgInference;

namespace BgTournament.Tests;

/// <summary>
/// The arc's integration proof: a full round-robin tournament over the real
/// wire, in-proc — a ≥3-engine field (BgInference through the third-party
/// door plus reference bots on distinct seeds), K=2 so seat balancing and
/// per-match seed divergence are live, every match to a natural end, and a
/// declared winner backed by consistent standings.
/// </summary>
public class TournamentSmokeTests
{
    private static readonly TimeSpan SmokeDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Tournament_ThreeEngineRoundRobin_OverTheWire_DeclaresAWinner()
    {
        Assert.True(
            File.Exists(ServerHarness.ParityModelPath),
            $"Parity model missing: '{ServerHarness.ParityModelPath}'. The fixtures are committed in BgRLEngine " +
            "(regenerate with 'python -m parity.generate_vectors'); check the sibling checkout.");

        using var evaluator = OnnxEvaluator.Load(ServerHarness.ParityModelPath);
        using var factory = ServerHarness.NewFactory();
        await ServerHarness.RunBgInferenceClientAsync(factory, "BgInferenceOnePly", evaluator);
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomA", seed: 11, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomB", seed: 22, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "BgInferenceOnePly", "RandomA", "RandomB");

        string[] participants = ["BgInferenceOnePly", "RandomA", "RandomB"];
        var started = await ServerHarness.StartTournamentAsync(
            factory, participants, matchLength: 1, matchesPerPairing: 2, seed: 777);
        string tournamentId = started.GetProperty("tournamentId").GetString()!;

        var ended = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId, SmokeDeadline);
        Assert.Equal("completed", ended.GetProperty("status").GetString());

        // A winner was declared, and it leads standings that add up: three
        // pairings × 2 = six matches, so six wins and six losses spread over
        // three participants that played four matches each.
        string winner = ended.GetProperty("winner").GetString()!;
        Assert.Contains(winner, participants);

        var standings = ended.GetProperty("standings").EnumerateArray().ToList();
        Assert.Equal(3, standings.Count);
        Assert.Equal(winner, standings[0].GetProperty("participant").GetString());
        Assert.Equal(
            participants.Order().ToArray(),
            standings.Select(row => row.GetProperty("participant").GetString()).Order().ToArray());
        Assert.Equal([1, 2, 3], standings.Select(row => row.GetProperty("rank").GetInt32()));
        Assert.Equal(6, standings.Sum(row => row.GetProperty("wins").GetInt32()));
        Assert.Equal(6, standings.Sum(row => row.GetProperty("losses").GetInt32()));
        Assert.All(standings, row =>
            Assert.Equal(4, row.GetProperty("wins").GetInt32() + row.GetProperty("losses").GetInt32()));

        // Every scheduled match ran to a natural end under its own derived
        // seed, with the pairing's two encounters seat-swapped.
        var matches = ended.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(6, matches.Count);
        Assert.All(matches, entry => Assert.Equal("completed", entry.GetProperty("status").GetString()));
        Assert.Equal(
            6, matches.Select(entry => entry.GetProperty("seed").GetInt32()).Distinct().Count());

        foreach (var pairing in matches.GroupBy(entry =>
        {
            string one = entry.GetProperty("seatOne").GetString()!;
            string two = entry.GetProperty("seatTwo").GetString()!;
            return string.CompareOrdinal(one, two) < 0 ? (one, two) : (two, one);
        }))
        {
            var orientations = pairing
                .Select(entry => entry.GetProperty("seatOne").GetString())
                .ToList();
            Assert.Equal(2, orientations.Count);
            Assert.NotEqual(orientations[0], orientations[1]);
        }

        // Each per-match record is retained and resolvable, exactly like an
        // admin-started match.
        foreach (var entry in matches)
        {
            string matchId = entry.GetProperty("matchId").GetString()!;
            var match = await ServerHarness.GetMatchAsync(factory, matchId);
            Assert.Equal("completed", match.GetProperty("status").GetString());
            Assert.Equal(entry.GetProperty("winner").GetString(), match.GetProperty("winner").GetString());
        }
    }
}
