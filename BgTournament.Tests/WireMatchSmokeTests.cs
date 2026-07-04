using BgGame_Lib;
using BgInference;
using BgTournament.Api;
using BgTournament.EngineClient;
using BgTournament.Protocol;
using BgTournament.Server;
using Microsoft.Extensions.DependencyInjection;

namespace BgTournament.Tests;

/// <summary>
/// The arc's integration proof: full matches over the real wire, in-proc.
///
/// <para>Smoke (a): the reference random bots play a complete seeded match
/// through TestServer — completion, transcript retention, recorded hits, and
/// same-seeds-same-match reproducibility.</para>
///
/// <para>Smoke (b): the dogfooding milestone — BgInference (OnePlyPlayAgent +
/// ThresholdCubeAgent over the committed parity model) enters a match exactly
/// the way a third-party engine will: through the EngineClient SDK and the
/// wire protocol. The parity model is deliberately untrained, so the smoke
/// asserts a clean complete match, not strength.</para>
/// </summary>
public class WireMatchSmokeTests
{
    private static readonly TimeSpan SmokeDeadline = TimeSpan.FromSeconds(60);

    private sealed record MatchOutcome(
        string? Winner,
        int SeatOneScore,
        int SeatTwoScore,
        IReadOnlyList<(MatchSeat Winner, int Points, int TranscriptLength)> Games);

    private static async Task<(System.Text.Json.JsonElement Summary, MatchRecord Record)> RunWireMatchAsync(
        Func<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, string, Task> connectEngineOne,
        Func<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, string, Task> connectEngineTwo,
        string nameOne,
        string nameTwo,
        int matchLength,
        int seed)
    {
        using var factory = ServerHarness.NewFactory();
        await connectEngineOne(factory, nameOne);
        await connectEngineTwo(factory, nameTwo);
        await ServerHarness.WaitForEnginesAsync(factory, nameOne, nameTwo);

        var match = await ServerHarness.StartMatchAsync(factory, nameOne, nameTwo, matchLength, seed);
        string matchId = match.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId, SmokeDeadline);

        var matches = factory.Services.GetRequiredService<MatchService>();
        Assert.True(matches.TryGetRecord(matchId, out var record));
        return (summary, record);
    }

    private static MatchOutcome OutcomeOf(MatchRecord record)
    {
        Assert.NotNull(record.Result);
        return new MatchOutcome(
            record.Winner,
            record.Result!.SeatOneScore,
            record.Result.SeatTwoScore,
            record.Result.Games
                .Select(g => (g.Winner, g.Result.Points, g.Transcript.Entries.Count))
                .ToList());
    }

    private static void AssertCompletedMatchInvariants(
        MatchRecord record, string nameOne, string nameTwo, int matchLength)
    {
        Assert.Equal(MatchStatus.Completed, record.Status);
        Assert.NotNull(record.Result);
        var result = record.Result!;

        // A winner exists and the scores say why.
        Assert.True(record.Winner == nameOne || record.Winner == nameTwo, $"Unexpected winner '{record.Winner}'.");
        Assert.True(
            Math.Max(result.SeatOneScore, result.SeatTwoScore) >= matchLength,
            $"No side reached the match length: {result.SeatOneScore}–{result.SeatTwoScore}.");

        // The full decision-by-decision record was retained.
        Assert.NotEmpty(result.Games);
        Assert.All(result.Games, game => Assert.NotEmpty(game.Transcript.Entries));

        // Scores fold from the per-game records.
        Assert.Equal(
            result.SeatOneScore,
            result.Games.Where(g => g.Winner == MatchSeat.One).Sum(g => g.Result.Points));
        Assert.Equal(
            result.SeatTwoScore,
            result.Games.Where(g => g.Winner == MatchSeat.Two).Sum(g => g.Result.Points));
    }

    private static bool TranscriptContainsAHit(MatchRecord record) =>
        record.Result!.Games
            .SelectMany(g => g.Transcript.Entries)
            .OfType<PlayTranscriptEntry>()
            .Any(entry => Enumerable.Range(0, entry.ChosenPlay.Count)
                .Any(i => entry.ChosenPlay[i].ToPt < 0));

    private static Func<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, string, Task> RandomBot(int seed) =>
        (factory, name) => ServerHarness.RunWellBehavedClientAsync(factory, name, seed, CancellationToken.None);

    [Fact]
    public async Task SmokeA_RandomVsRandom_FullMatchOverTheWire()
    {
        var (summary, record) = await RunWireMatchAsync(
            RandomBot(101), RandomBot(202), "RandomA", "RandomB", matchLength: 5, seed: 4242);

        Assert.Equal("completed", summary.GetProperty("status").GetString());
        AssertCompletedMatchInvariants(record, "RandomA", "RandomB", matchLength: 5);

        // Hits crossed the wire sign-free and were recorded hit-encoded —
        // deterministic for this seed pair.
        Assert.True(TranscriptContainsAHit(record), "Expected at least one recorded hit for this seed.");
    }

    [Fact]
    public async Task SmokeA_SameSeeds_SameMatch()
    {
        async Task<MatchOutcome> RunOnceAsync()
        {
            var (_, record) = await RunWireMatchAsync(
                RandomBot(101), RandomBot(202), "RandomA", "RandomB", matchLength: 5, seed: 4242);
            return OutcomeOf(record);
        }

        var first = await RunOnceAsync();
        var second = await RunOnceAsync();

        Assert.Equal(first.Winner, second.Winner);
        Assert.Equal(first.SeatOneScore, second.SeatOneScore);
        Assert.Equal(first.SeatTwoScore, second.SeatTwoScore);
        Assert.Equal(first.Games, second.Games);
    }

    /// <summary>
    /// The fairness proof, both halves. A seedless (fair-mode) match plays to
    /// completion over the real wire while each engine's SDK verifier checks
    /// every roll it observed against the revealed key — the player-verifiable
    /// half. Then the test re-derives the whole committed stream from the
    /// revealed key and walks it against the server's retained transcript — the
    /// auditor-verifiable half, covering the rolls no single engine saw.
    /// </summary>
    [Fact]
    public async Task SmokeC_FairMode_PlayersVerifyObservedRolls_AndTheFullStreamMatchesTheTranscript()
    {
        using var factory = ServerHarness.NewFactory();
        var reportOne = new TaskCompletionSource<DiceVerificationReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reportTwo = new TaskCompletionSource<DiceVerificationReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await ServerHarness.RunVerifyingClientAsync(
            factory, "FairOne", seed: 101, r => reportOne.TrySetResult(r), CancellationToken.None);
        await ServerHarness.RunVerifyingClientAsync(
            factory, "FairTwo", seed: 202, r => reportTwo.TrySetResult(r), CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "FairOne", "FairTwo");

        var match = await ServerHarness.StartFairMatchAsync(factory, "FairOne", "FairTwo", matchLength: 5);
        string matchId = match.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId, SmokeDeadline);
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        var record = RecordOf(factory, matchId);
        AssertCompletedMatchInvariants(record, "FairOne", "FairTwo", matchLength: 5);

        // Player-verifiable half: each engine confirmed every roll it observed
        // sat at its committed stream position.
        var verdictOne = await reportOne.Task.WaitAsync(SmokeDeadline);
        var verdictTwo = await reportTwo.Task.WaitAsync(SmokeDeadline);
        Assert.True(verdictOne.Verified, $"FairOne: {verdictOne.Outcome} — {verdictOne.Detail}");
        Assert.True(verdictTwo.Verified, $"FairTwo: {verdictTwo.Outcome} — {verdictTwo.Detail}");
        Assert.True(verdictOne.ObservedRollCount > 0, "FairOne observed no rolls to verify.");
        Assert.True(verdictTwo.ObservedRollCount > 0, "FairTwo observed no rolls to verify.");

        // Auditor-verifiable half: the committed key reproduces the whole
        // transcript, including rolls neither engine was shown.
        AssertTranscriptMatchesCommittedStream(record);
    }

    private static MatchRecord RecordOf(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string matchId)
    {
        var matches = factory.Services.GetRequiredService<MatchService>();
        Assert.True(matches.TryGetRecord(matchId, out var record));
        return record;
    }

    /// <summary>
    /// Re-derive the match's full committed dice stream from the revealed key and
    /// confirm it reproduces every recorded roll. Per game the stream opens with
    /// zero or more re-rolled ties (doubles, never transcripted) up to the opening
    /// non-double — the game's first play — after which every play consumes exactly
    /// one roll in order (dances included).
    /// </summary>
    private static void AssertTranscriptMatchesCommittedStream(MatchRecord record)
    {
        Assert.NotNull(record.DiceKey);
        Assert.NotNull(record.Result);

        // The commitment published before roll one matches the revealed key.
        Assert.True(
            record.Commitment!.Verifies(record.DiceKey!, VerifiableDice.ContextFor(record.MatchId)),
            "The revealed key does not reproduce the published commitment.");

        var stream = new VerifiableDiceSource(record.DiceKey!);
        foreach (var game in record.Result!.Games)
        {
            var plays = game.Transcript.Entries.OfType<PlayTranscriptEntry>().ToList();
            Assert.NotEmpty(plays);

            // Opening: skip re-rolled ties to the opening non-double roll.
            (int Die1, int Die2) roll;
            do
            {
                roll = stream.Roll();
            }
            while (roll.Die1 == roll.Die2);
            Assert.Equal((plays[0].Die1, plays[0].Die2), roll);

            // Every subsequent play consumes one roll, in order.
            for (int i = 1; i < plays.Count; i++)
            {
                roll = stream.Roll();
                Assert.Equal((plays[i].Die1, plays[i].Die2), roll);
            }
        }
    }

    [Fact]
    public async Task SmokeB_BgInferenceEntersOverTheWire_LikeAnyThirdPartyEngine()
    {
        Assert.True(
            File.Exists(ServerHarness.ParityModelPath),
            $"Parity model missing: '{ServerHarness.ParityModelPath}'. The fixtures are committed in BgRLEngine " +
            "(regenerate with 'python -m parity.generate_vectors'); check the sibling checkout.");

        using var evaluator = OnnxEvaluator.Load(ServerHarness.ParityModelPath);
        Func<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, string, Task> bgInference =
            (factory, name) => ServerHarness.RunBgInferenceClientAsync(factory, name, evaluator);

        var (summary, record) = await RunWireMatchAsync(
            bgInference, RandomBot(7), "BgInferenceOnePly", "RandomBaseline", matchLength: 5, seed: 99);

        // The parity model is untrained — the milestone is a clean complete
        // match through the third-party door, not strength.
        Assert.Equal("completed", summary.GetProperty("status").GetString());
        AssertCompletedMatchInvariants(record, "BgInferenceOnePly", "RandomBaseline", matchLength: 5);
    }
}
