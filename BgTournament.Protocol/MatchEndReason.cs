namespace BgTournament.Protocol;

/// <summary>
/// Why a match ended, as reported by <see cref="MatchEndedMessage"/>
/// (serialized as <c>"matchComplete"</c> / <c>"gamesCapReached"</c> /
/// <c>"forfeit"</c>).
/// </summary>
public enum MatchEndReason
{
    /// <summary>A side reached the match length.</summary>
    MatchComplete,

    /// <summary>The configured games cap stopped the session first (always the case for money sessions).</summary>
    GamesCapReached,

    /// <summary>A side forfeited — contract violation, timeout, or disconnect. See <see cref="MatchEndedMessage.ForfeitedBy"/>.</summary>
    Forfeit,
}
