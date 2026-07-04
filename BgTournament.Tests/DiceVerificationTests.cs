using BgGame_Lib;
using BgTournament.EngineClient;
using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// The pure fair-dice verifier (<see cref="DiceVerification.VerifyMatchDice"/>):
/// the client- and third-party-facing half of commit-and-reveal. Exercised
/// directly (no SDK session), the way any C# consumer would call it.
/// </summary>
public class DiceVerificationTests
{
    private const string MatchId = "match-abc";

    // A fixed, deterministic key (the vectors' all-zero key), so the derived
    // stream is stable across runs.
    private static DiceKey ZeroKey() =>
        DiceKey.FromHex("0000000000000000000000000000000000000000000000000000000000000000");

    private static DiceCommitment CommitmentFor(DiceKey key, string matchId) =>
        key.Commit(VerifiableDice.ContextFor(matchId));

    /// <summary>Derive the first <paramref name="count"/> rolls of a key's stream.</summary>
    private static IReadOnlyList<(int Die1, int Die2)> DeriveStream(DiceKey key, int count)
    {
        var source = new VerifiableDiceSource(key);
        var rolls = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            rolls[i] = source.Roll();
        }

        return rolls;
    }

    /// <summary>A gapped set of observed rolls at the given indices, taken from the true stream.</summary>
    private static IReadOnlyList<ObservedRoll> ObserveAt(DiceKey key, params int[] indices)
    {
        var stream = DeriveStream(key, indices.Max() + 1);
        return indices.Select(i => new ObservedRoll(i, stream[i].Die1, stream[i].Die2)).ToArray();
    }

    [Fact]
    public void CorrectKeyAndRolls_Verify()
    {
        var key = ZeroKey();
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), VerifiableDice.AlgorithmId, key, ObserveAt(key, 1, 4, 7, 12));

        Assert.Equal(DiceVerificationOutcome.Verified, report.Outcome);
        Assert.True(report.Verified);
        Assert.Equal(4, report.ObservedRollCount);
        Assert.Null(report.Detail);
    }

    [Fact]
    public void NoObservedRolls_VerifyOnCommitmentAlone()
    {
        var key = ZeroKey();
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), VerifiableDice.AlgorithmId, key, []);

        Assert.True(report.Verified);
        Assert.Equal(0, report.ObservedRollCount);
    }

    [Fact]
    public void WrongRevealedKey_IsCommitmentMismatch()
    {
        var committedKey = ZeroKey();
        var revealed = DiceKey.FromHex("1111111111111111111111111111111111111111111111111111111111111111");
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(committedKey, MatchId), VerifiableDice.AlgorithmId, revealed,
            ObserveAt(revealed, 0, 1));

        Assert.Equal(DiceVerificationOutcome.CommitmentMismatch, report.Outcome);
        Assert.False(report.Verified);
    }

    [Fact]
    public void CommitmentForADifferentMatch_IsCommitmentMismatch()
    {
        // Same key, but the commitment was bound to another match id — the
        // context binding is what fails.
        var key = ZeroKey();
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, "some-other-match"), VerifiableDice.AlgorithmId, key, ObserveAt(key, 0));

        Assert.Equal(DiceVerificationOutcome.CommitmentMismatch, report.Outcome);
    }

    [Fact]
    public void TamperedObservedRoll_IsRollMismatch()
    {
        var key = ZeroKey();
        var honest = ObserveAt(key, 2, 5);
        // Corrupt one die of one observed roll.
        var tampered = honest
            .Select((r, i) => i == 1 ? r with { Die1 = r.Die1 % 6 + 1 } : r)
            .ToArray();

        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), VerifiableDice.AlgorithmId, key, tampered);

        Assert.Equal(DiceVerificationOutcome.RollMismatch, report.Outcome);
        Assert.Equal(2, report.ObservedRollCount);
        Assert.NotNull(report.Detail);
    }

    [Fact]
    public void UnknownAlgorithm_IsReportedBeforeAnyRollCheck()
    {
        var key = ZeroKey();
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), "rot13-dice-v9", key, ObserveAt(key, 0));

        Assert.Equal(DiceVerificationOutcome.UnknownAlgorithm, report.Outcome);
    }

    [Fact]
    public void NegativeRollIndex_IsRollMismatch()
    {
        var key = ZeroKey();
        var observed = new ObservedRoll[] { new(-1, 3, 4) };
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), VerifiableDice.AlgorithmId, key, observed);

        Assert.Equal(DiceVerificationOutcome.RollMismatch, report.Outcome);
    }

    [Fact]
    public void ImplausiblyLargeIndex_IsRejectedWithoutDerivingTheWholeStream()
    {
        var key = ZeroKey();
        var observed = new ObservedRoll[] { new(5_000_000, 3, 4) };
        var report = DiceVerification.VerifyMatchDice(
            MatchId, CommitmentFor(key, MatchId), VerifiableDice.AlgorithmId, key, observed);

        Assert.Equal(DiceVerificationOutcome.RollMismatch, report.Outcome);
    }
}
