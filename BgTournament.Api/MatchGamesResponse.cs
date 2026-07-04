namespace BgTournament.Api;

/// <summary>
/// A match's per-game transcripts, as returned by
/// <c>GET /matches/{matchId}/games</c>. Served for every terminal match — the
/// full set for a <see cref="MatchStatus.Completed"/> one, and the games that
/// finished before the break for a forfeited/aborted/faulted one. A running
/// match answers 409 (its games stream live from
/// <c>GET /matches/{matchId}/live</c>). <paramref name="Status"/> qualifies the
/// list: any status other than <see cref="MatchStatus.Completed"/> means it is
/// partial — the interrupted game and anything after it are absent.
/// </summary>
/// <param name="MatchId">The match these games belong to.</param>
/// <param name="EngineOne">The engine in seat One — every <see cref="Seat"/> value resolves against this pair.</param>
/// <param name="EngineTwo">The engine in seat Two.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="Status">
/// The match's terminal status; anything but <see cref="MatchStatus.Completed"/>
/// means <paramref name="Games"/> is partial. Richer terminal detail (winner,
/// forfeit side) lives on the match summary, not here.
/// </param>
/// <param name="Games">The retained games, in play order.</param>
public sealed record MatchGamesResponse(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    MatchStatus Status,
    IReadOnlyList<GameReplay> Games);
