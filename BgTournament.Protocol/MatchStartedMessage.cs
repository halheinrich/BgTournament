namespace BgTournament.Protocol;

/// <summary>
/// Server → engine notification (no reply): you have been seated in a match.
/// Decision queries for this match follow until <see cref="MatchEndedMessage"/>.
/// </summary>
public sealed record MatchStartedMessage : ProtocolMessage
{
    /// <summary>Server-assigned match identifier, echoed in <see cref="MatchEndedMessage"/>.</summary>
    public required string MatchId { get; init; }

    /// <summary>The opposing engine's registered name.</summary>
    public required string Opponent { get; init; }

    /// <summary>Match length in points; 0 means a money session.</summary>
    public required int MatchLength { get; init; }

    /// <summary>Games cap, when configured (always present for money sessions).</summary>
    public int? MaxGames { get; init; }

    /// <summary>
    /// Fair-mode only: the pre-match commitment to this match's dice key, as a
    /// lowercase-hex SHA-256 digest (see PROTOCOL.md, "Provably-fair dice").
    /// Present together with <see cref="DiceAlgorithm"/> when the server drives
    /// the match with verifiable dice; omitted for explicit-seed (dev/repro)
    /// matches, which carry no commitment. The revealing key arrives on
    /// <see cref="MatchEndedMessage.DiceKey"/> at match end.
    /// </summary>
    public string? DiceCommitment { get; init; }

    /// <summary>
    /// Fair-mode only: the identifier of the dice derivation the commitment
    /// covers (<see cref="VerifiableDice.AlgorithmId"/>), so a verifier knows
    /// which algorithm to re-implement. Present iff <see cref="DiceCommitment"/> is.
    /// </summary>
    public string? DiceAlgorithm { get; init; }

    /// <summary>
    /// Time-control matches only: the Fischer clock governing this match (see
    /// PROTOCOL.md §10). Present, it <em>replaces</em> the flat per-decision
    /// timeout — a player's remaining pool is the only limit on any single
    /// decision, and an emptied pool forfeits the match (flag fall). Omitted
    /// when the match runs on the flat per-decision timeout instead.
    /// </summary>
    public WireTimeControl? TimeControl { get; init; }
}
