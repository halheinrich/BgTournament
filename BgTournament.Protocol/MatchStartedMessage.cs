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
}
