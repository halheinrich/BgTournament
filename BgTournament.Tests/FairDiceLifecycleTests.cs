using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// The fair-dice lifecycle at the wire boundary: an explicit-seed match carries
/// none of the fair-dice fields (seeded mode is untouched), and a seedless match
/// publishes the commitment + algorithm on <c>matchStarted</c>. End-to-end
/// verification (roll indices, key reveal, client + server re-derivation) is the
/// fair-mode smoke's job; these pin the branch and the seeded-mode invariant.
/// </summary>
public class FairDiceLifecycleTests
{
    [Fact]
    public async Task SeededMatch_CarriesNoFairDiceFields()
    {
        using var factory = ServerHarness.NewFactory();
        await using var seatOne = await TestEngine.ConnectAsync(factory, "SeededA");
        await using var seatTwo = await TestEngine.ConnectAsync(factory, "SeededB");
        await ServerHarness.WaitForEnginesAsync(factory, "SeededA", "SeededB");

        // An explicit seed selects seeded mode; the qualifying seed makes seat One
        // win the opening, so seat One is the engine that gets the first play query.
        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        await ServerHarness.StartMatchAsync(factory, "SeededA", "SeededB", matchLength: 5, seed);

        var started = await seatOne.ExpectAsync<MatchStartedMessage>();
        Assert.Null(started.DiceCommitment);
        Assert.Null(started.DiceAlgorithm);

        var query = await seatOne.ExpectPlayQueryAsync();
        Assert.Null(query.RollIndex);
    }

    [Fact]
    public async Task FairMatch_MatchStartedPublishesCommitmentAndAlgorithm()
    {
        using var factory = ServerHarness.NewFactory();
        await using var seatOne = await TestEngine.ConnectAsync(factory, "FairA");
        await using var seatTwo = await TestEngine.ConnectAsync(factory, "FairB");
        await ServerHarness.WaitForEnginesAsync(factory, "FairA", "FairB");

        // No seed → fair mode.
        await ServerHarness.StartFairMatchAsync(factory, "FairA", "FairB", matchLength: 5);

        // Both parties are told the same commitment before any roll — it is the
        // pre-match binding, so both must agree byte-for-byte.
        var startedOne = await seatOne.ExpectAsync<MatchStartedMessage>();
        var startedTwo = await seatTwo.ExpectAsync<MatchStartedMessage>();

        Assert.NotNull(startedOne.DiceCommitment);
        Assert.Equal(VerifiableDice.AlgorithmId, startedOne.DiceAlgorithm);
        Assert.Equal(64, startedOne.DiceCommitment!.Length);   // 32-byte digest, lowercase hex
        Assert.Equal(startedOne.DiceCommitment, startedTwo.DiceCommitment);
        Assert.Equal(startedOne.DiceAlgorithm, startedTwo.DiceAlgorithm);
    }
}
