namespace BgTournament.Protocol;

/// <summary>
/// The Fischer time control governing a match, announced on
/// <see cref="MatchStartedMessage.TimeControl"/> so an engine can budget the
/// whole match (see PROTOCOL.md §10). When present, both players' remaining
/// pools then ride every decision query
/// (<see cref="QueryMessage.YourTimeRemainingSeconds"/> /
/// <see cref="QueryMessage.OpponentTimeRemainingSeconds"/>).
/// </summary>
public sealed record WireTimeControl
{
    /// <summary>Each player's starting pool, in seconds.</summary>
    public required double InitialSeconds { get; init; }

    /// <summary>Seconds credited to a player's pool after each answered decision.</summary>
    public required double IncrementSeconds { get; init; }
}
