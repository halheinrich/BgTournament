namespace BgTournament.Protocol;

/// <summary>
/// Server → engine notification (no reply): the match is over. After a
/// forfeit by this engine the server closes the connection; otherwise the
/// engine stays registered for future matches.
/// </summary>
public sealed record MatchEndedMessage : ProtocolMessage
{
    /// <summary>The identifier from <see cref="MatchStartedMessage"/>.</summary>
    public required string MatchId { get; init; }

    /// <summary>Why the match ended.</summary>
    public required MatchEndReason Reason { get; init; }

    /// <summary>Points you scored over the match or session.</summary>
    public required int YourPoints { get; init; }

    /// <summary>Points your opponent scored over the match or session.</summary>
    public required int OpponentPoints { get; init; }

    /// <summary>
    /// Whether you won. Absent when there is no winner — a money session or a
    /// games-cap stop with no side at the match length.
    /// </summary>
    public bool? YouWon { get; init; }

    /// <summary>Who forfeited; present iff <see cref="Reason"/> is <see cref="MatchEndReason.Forfeit"/>.</summary>
    public ForfeitSide? ForfeitedBy { get; init; }

    /// <summary>Optional human-readable detail (e.g. the nature of a forfeit).</summary>
    public string? Detail { get; init; }
}
