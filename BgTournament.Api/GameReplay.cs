namespace BgTournament.Api;

/// <summary>
/// One completed game inside <c>GET /matches/{matchId}/games</c>: the outcome
/// plus every decision moment in play order, ready for step-through replay.
/// </summary>
/// <param name="GameNumber">1-based position of this game within the match.</param>
/// <param name="Winner">The seat that won the game.</param>
/// <param name="ResultKind">How decisively it was won.</param>
/// <param name="CubeValue">The cube value the game was scored at.</param>
/// <param name="Points">Points awarded to the winner (<paramref name="CubeValue"/> × the result multiplier — pre-multiplied server-side).</param>
/// <param name="SeatOneScore">Seat One's match score entering this game.</param>
/// <param name="SeatTwoScore">Seat Two's match score entering this game.</param>
/// <param name="IsCrawford">True iff this is the Crawford game (no doubling).</param>
/// <param name="Entries">Every decision moment, in play order.</param>
/// <param name="FinalState">The position the game ended in (seat-One frame) — the step after the last entry.</param>
public sealed record GameReplay(
    int GameNumber,
    Seat Winner,
    GameResultKind ResultKind,
    int CubeValue,
    int Points,
    int SeatOneScore,
    int SeatTwoScore,
    bool IsCrawford,
    IReadOnlyList<GameEntry> Entries,
    GamePosition FinalState);
