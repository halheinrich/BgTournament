namespace BgTournament.Api;

/// <summary>A match record projection, as returned by the match endpoints.</summary>
/// <param name="MatchId">Server-assigned match id; resolves on <c>GET /matches/{matchId}</c>.</param>
/// <param name="EngineOne">Engine occupying seat One.</param>
/// <param name="EngineTwo">Engine occupying seat Two.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="MaxGames">The games cap, when one was set.</param>
/// <param name="Seed">The recorded dice seed. In dev/repro mode (an explicit seed was supplied) it re-rolls the match. In fair mode (no seed supplied — see PROTOCOL.md, "Provably-fair dice") it is a server-chosen recorded value that does <b>not</b> drive the dice: the committed key does, and only the revealed key reproduces the match. Do not treat a fair-mode seed as a reproduction key.</param>
/// <param name="TimeControl">The Fischer time control the match runs under, or null for the flat per-decision-timeout regime.</param>
/// <param name="Status">Where the match is in its lifecycle.</param>
/// <param name="Winner">Winning engine's name; null while running or when there is no winner.</param>
/// <param name="SeatOneScore">Seat One's final points; null until the match completes naturally.</param>
/// <param name="SeatTwoScore">Seat Two's final points; null until the match completes naturally.</param>
/// <param name="ForfeitedBy">Forfeiting engine's name; null unless <paramref name="Status"/> is <see cref="MatchStatus.Forfeited"/>.</param>
/// <param name="Detail">Human-readable outcome detail (forfeit cause, abort reason).</param>
/// <param name="StartedAtUtc">When the server created and began hosting the match (UTC).</param>
/// <param name="EndedAtUtc">When the match reached its terminal status (UTC); null while running — and null on an <see cref="MatchStatus.Interrupted"/> record, whose true end time died with the server.</param>
public sealed record MatchSummary(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? MaxGames,
    int Seed,
    TimeControl? TimeControl,
    MatchStatus Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    string? Detail,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);
