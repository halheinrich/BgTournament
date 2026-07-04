namespace BgTournament.Api;

/// <summary>
/// A completed match's per-game transcripts, as returned by
/// <c>GET /matches/{matchId}/games</c>. Served for
/// <see cref="MatchStatus.Completed"/> matches only — a running match has no
/// settled transcripts yet, and forfeited/aborted/faulted matches retain none
/// (v1 limitation).
/// </summary>
/// <param name="MatchId">The match these games belong to.</param>
/// <param name="EngineOne">The engine in seat One — every <see cref="Seat"/> value resolves against this pair.</param>
/// <param name="EngineTwo">The engine in seat Two.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="Games">The completed games, in play order.</param>
public sealed record MatchGamesResponse(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    IReadOnlyList<GameReplay> Games);
