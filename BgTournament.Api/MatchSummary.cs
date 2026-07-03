namespace BgTournament.Api;

/// <summary>A match record projection, as returned by the match endpoints.</summary>
/// <param name="MatchId">Server-assigned match id; resolves on <c>GET /matches/{matchId}</c>.</param>
/// <param name="EngineOne">Engine occupying seat One.</param>
/// <param name="EngineTwo">Engine occupying seat Two.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="MaxGames">The games cap, when one was set.</param>
/// <param name="Seed">The dice seed. Recorded even when server-chosen, so any match can be re-rolled.</param>
/// <param name="Status">Where the match is in its lifecycle.</param>
/// <param name="Winner">Winning engine's name; null while running or when there is no winner.</param>
/// <param name="SeatOneScore">Seat One's final points; null until the match completes naturally.</param>
/// <param name="SeatTwoScore">Seat Two's final points; null until the match completes naturally.</param>
/// <param name="ForfeitedBy">Forfeiting engine's name; null unless <paramref name="Status"/> is <see cref="MatchStatus.Forfeited"/>.</param>
/// <param name="Detail">Human-readable outcome detail (forfeit cause, abort reason).</param>
public sealed record MatchSummary(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? MaxGames,
    int Seed,
    MatchStatus Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    string? Detail);
