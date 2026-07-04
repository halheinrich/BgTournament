using BgGame_Lib;
using BgTournament.Protocol;

namespace BgTournament.EngineClient;

/// <summary>
/// Accumulates one match's fair-dice observations from the wire — the
/// commitment and algorithm on <c>matchStarted</c>, the indexed dice on each
/// <c>playQuery</c> — and, on <c>matchEnded</c>, runs the pure
/// <see cref="DiceVerification"/> against the revealed key. One instance per
/// serve loop; reset between matches (a connection plays many).
///
/// <para>Never throws on a malformed or missing field: a server that sends
/// unparseable hex or omits the reveal is reported as a failed verification, not
/// an exception that would drop an otherwise-healthy session. Explicit-seed
/// matches (no commitment) produce no report.</para>
/// </summary>
internal sealed class DiceAuditRecorder
{
    private readonly List<ObservedRoll> _rolls = new();
    private string? _matchId;
    private string? _algorithm;
    private DiceCommitment? _commitment;
    private string? _commitmentError;   // set when the commitment hex was unparseable

    /// <summary>Begin recording a new match, capturing its fair-mode commitment if present.</summary>
    public void OnMatchStarted(MatchStartedMessage message)
    {
        Reset();
        _matchId = message.MatchId;

        // Fair mode sends both fields together; otherwise this is an explicit-seed
        // match with nothing to verify.
        if (message.DiceCommitment is null || message.DiceAlgorithm is null)
        {
            return;
        }

        _algorithm = message.DiceAlgorithm;
        try
        {
            _commitment = DiceCommitment.FromHex(message.DiceCommitment);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _commitmentError = ex.Message;
        }
    }

    /// <summary>Record a play query's roll at its stream index (fair mode with an index only).</summary>
    public void OnPlayQuery(PlayQueryMessage message)
    {
        if (IsFairMatch && message.RollIndex is int index)
        {
            _rolls.Add(new ObservedRoll(index, message.Die1, message.Die2));
        }
    }

    /// <summary>
    /// Verify the finished match against its revealed key, or return null when
    /// there is nothing to verify (an explicit-seed match). Resets the recorder
    /// for the next match either way.
    /// </summary>
    public DiceVerificationReport? OnMatchEnded(MatchEndedMessage message)
    {
        if (!IsFairMatch)
        {
            Reset();
            return null;
        }

        DiceVerificationReport report = BuildReport(message);
        Reset();
        return report;
    }

    // A fair-mode match either parsed a commitment or saw a malformed one — both
    // are matches the server claimed to commit; only a total absence is seeded mode.
    private bool IsFairMatch => _matchId is not null && (_commitment is not null || _commitmentError is not null);

    private DiceVerificationReport BuildReport(MatchEndedMessage message)
    {
        if (_commitmentError is not null)
        {
            return new DiceVerificationReport(
                DiceVerificationOutcome.CommitmentMismatch, _rolls.Count,
                $"The published commitment was not valid hex: {_commitmentError}");
        }

        if (message.DiceKey is null)
        {
            return new DiceVerificationReport(
                DiceVerificationOutcome.CommitmentMismatch, _rolls.Count,
                "The match ended without revealing the dice key, so the commitment cannot be checked.");
        }

        DiceKey key;
        try
        {
            key = DiceKey.FromHex(message.DiceKey);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return new DiceVerificationReport(
                DiceVerificationOutcome.CommitmentMismatch, _rolls.Count,
                $"The revealed key was not valid hex: {ex.Message}");
        }

        return DiceVerification.VerifyMatchDice(_matchId!, _commitment!, _algorithm!, key, _rolls);
    }

    private void Reset()
    {
        _matchId = null;
        _algorithm = null;
        _commitment = null;
        _commitmentError = null;
        _rolls.Clear();
    }
}
