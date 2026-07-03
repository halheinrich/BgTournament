namespace BgTournament.Api;

/// <summary>
/// One scheduled match in a tournament projection. <paramref name="MatchId"/>,
/// <paramref name="Status"/>, and <paramref name="Winner"/> are null until the
/// tournament reaches that match; the id then resolves on
/// <c>GET /matches/{matchId}</c>.
/// </summary>
/// <param name="Index">The match's position in the schedule (0-based).</param>
/// <param name="SeatOne">Engine scheduled in seat One.</param>
/// <param name="SeatTwo">Engine scheduled in seat Two.</param>
/// <param name="Seed">The match's derived dice seed.</param>
/// <param name="MatchId">Hosted-match record id; null until the match is reached.</param>
/// <param name="Status">The hosted match's status; null until the match is reached.</param>
/// <param name="Winner">Winning engine's name; null until decided.</param>
public sealed record TournamentMatchEntry(
    int Index,
    string SeatOne,
    string SeatTwo,
    int Seed,
    string? MatchId,
    MatchStatus? Status,
    string? Winner);
