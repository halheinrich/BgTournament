using BgGame_Lib;
using BgTournament.Protocol;

namespace BgTournament.EngineClient;

/// <summary>The verdict of verifying a fair-mode match's dice against the revealed key.</summary>
public enum DiceVerificationOutcome
{
    /// <summary>The revealed key matched the commitment and every observed roll sat at its claimed index.</summary>
    Verified = 1,

    /// <summary>The revealed key does not reproduce the pre-match commitment — the key was not what was committed to.</summary>
    CommitmentMismatch = 2,

    /// <summary>The key matched the commitment, but an observed roll differs from the committed stream at its index.</summary>
    RollMismatch = 3,

    /// <summary>The dice algorithm identifier is not one this verifier implements, so the rolls cannot be checked.</summary>
    UnknownAlgorithm = 4,
}

/// <summary>
/// One roll an engine observed on a <c>playQuery</c> in fair mode: the dice, at
/// their stream index (<c>rollIndex</c>). The pair is <c>(Die1, Die2)</c> in the
/// order the server delivered them, matching the committed stream's roll at
/// <see cref="Index"/>.
/// </summary>
/// <param name="Index">The roll's 0-based position in the match dice stream.</param>
/// <param name="Die1">The first die, 1–6.</param>
/// <param name="Die2">The second die, 1–6.</param>
public sealed record ObservedRoll(int Index, int Die1, int Die2);

/// <summary>The immutable result of verifying a fair-mode match's dice.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="ObservedRollCount">How many observed rolls were checked.</param>
/// <param name="Detail">Human-readable detail on a non-<see cref="DiceVerificationOutcome.Verified"/> outcome; null when verified.</param>
public sealed record DiceVerificationReport(DiceVerificationOutcome Outcome, int ObservedRollCount, string? Detail)
{
    /// <summary>True iff the outcome is <see cref="DiceVerificationOutcome.Verified"/>.</summary>
    public bool Verified => Outcome == DiceVerificationOutcome.Verified;
}

/// <summary>
/// The pure, standalone verifier for commit-and-reveal fair dice (PROTOCOL.md,
/// "Provably-fair dice"). Given a match's commitment, the revealed key, and the
/// rolls an engine observed with their stream indices, it confirms the key
/// matched the commitment and that every observed roll came from the committed
/// stream at its claimed position.
///
/// <para>Deliberately side-effect-free and dependency-light: any C# client can
/// call it directly, without the SDK's session plumbing (the
/// <see cref="EngineClient"/> serve loop calls it automatically and delivers the
/// report through its optional hook). It reproduces the language-neutral recipe
/// in PROTOCOL.md; a third party in another language re-implements the same
/// steps.</para>
///
/// <para><b>What it proves.</b> A <see cref="DiceVerificationOutcome.Verified"/>
/// result means every roll you were shown came from the single sequence the
/// server committed to before roll one — the dice were fixed in advance and not
/// adapted to your play. It does not prove the server did not <em>choose</em> a
/// favorable committed sequence (non-selection), nor does it audit rolls you
/// never saw (opponent turns, dances) — those are auditable from the server's
/// transcript, not the wire. See PROTOCOL.md for the residual and the v2
/// contributory-nonce path.</para>
/// </summary>
public static class DiceVerification
{
    // A match cannot plausibly reach this many rolls; a larger claimed index is
    // treated as a failure rather than derived (bounds work against a hostile
    // or buggy server sending an absurd rollIndex).
    private const int MaxPlausibleRolls = 1_000_000;

    /// <summary>
    /// Verify a fair-mode match's dice. Pure — no I/O, no shared state.
    /// </summary>
    /// <param name="matchId">The match id, from which the commitment context is reconstructed (<see cref="VerifiableDice.ContextFor"/>).</param>
    /// <param name="commitment">The pre-match commitment (from <c>matchStarted.diceCommitment</c>).</param>
    /// <param name="algorithm">The dice algorithm id (from <c>matchStarted.diceAlgorithm</c>).</param>
    /// <param name="revealedKey">The revealed key (from <c>matchEnded.diceKey</c>).</param>
    /// <param name="observedRolls">The rolls observed on play queries, each with its <c>rollIndex</c>. Order does not matter.</param>
    /// <returns>The verification report; never throws on a verification failure — it reports it.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="matchId"/> is empty.</exception>
    public static DiceVerificationReport VerifyMatchDice(
        string matchId,
        DiceCommitment commitment,
        string algorithm,
        DiceKey revealedKey,
        IReadOnlyList<ObservedRoll> observedRolls)
    {
        ArgumentException.ThrowIfNullOrEmpty(matchId);
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(revealedKey);
        ArgumentNullException.ThrowIfNull(observedRolls);

        int count = observedRolls.Count;

        // The algorithm names the derivation this verifier must re-implement; an
        // unrecognized one means we cannot honestly claim to have checked the rolls.
        if (!string.Equals(algorithm, VerifiableDice.AlgorithmId, StringComparison.Ordinal))
        {
            return new DiceVerificationReport(
                DiceVerificationOutcome.UnknownAlgorithm, count,
                $"Unrecognized dice algorithm '{algorithm}'; this verifier implements '{VerifiableDice.AlgorithmId}'.");
        }

        // The commitment binds the key to this match: the revealed key must
        // reproduce it under the match's own context.
        if (!commitment.Verifies(revealedKey, VerifiableDice.ContextFor(matchId)))
        {
            return new DiceVerificationReport(
                DiceVerificationOutcome.CommitmentMismatch, count,
                "The revealed key does not reproduce the pre-match commitment.");
        }

        if (count == 0)
        {
            return new DiceVerificationReport(DiceVerificationOutcome.Verified, 0, null);
        }

        int maxIndex = 0;
        foreach (var roll in observedRolls)
        {
            if (roll.Index < 0)
            {
                return new DiceVerificationReport(
                    DiceVerificationOutcome.RollMismatch, count, $"Roll index {roll.Index} is negative.");
            }

            if (roll.Index >= MaxPlausibleRolls)
            {
                return new DiceVerificationReport(
                    DiceVerificationOutcome.RollMismatch, count,
                    $"Roll index {roll.Index} is implausibly large (over {MaxPlausibleRolls}).");
            }

            maxIndex = Math.Max(maxIndex, roll.Index);
        }

        // Re-derive the committed stream up to the highest observed index, then
        // confirm every observed roll matches its position exactly.
        var derived = new (int Die1, int Die2)[maxIndex + 1];
        var source = new VerifiableDiceSource(revealedKey);
        for (int i = 0; i <= maxIndex; i++)
        {
            derived[i] = source.Roll();
        }

        foreach (var roll in observedRolls)
        {
            var (die1, die2) = derived[roll.Index];
            if (die1 != roll.Die1 || die2 != roll.Die2)
            {
                return new DiceVerificationReport(
                    DiceVerificationOutcome.RollMismatch, count,
                    $"Roll {roll.Index} was delivered as ({roll.Die1},{roll.Die2}) "
                        + $"but the committed stream yields ({die1},{die2}).");
            }
        }

        return new DiceVerificationReport(DiceVerificationOutcome.Verified, count, null);
    }
}
