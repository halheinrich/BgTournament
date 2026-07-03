namespace BgTournament.Server;

/// <summary>
/// The admin HTTP surface's request/response shapes (camelCase JSON). These
/// are server-API contracts, distinct from the engine wire protocol — engines
/// never see them.
/// </summary>
internal sealed record StartMatchRequest(
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? Seed = null,
    int? MaxGames = null);

/// <summary>A connected engine, as listed by <c>GET /engines</c>.</summary>
internal sealed record EngineSummary(string Name, string? Version, string? Author, bool InMatch)
{
    public static EngineSummary From(EngineSession session) =>
        new(session.Name, session.Version, session.Author, session.InMatch);
}

/// <summary>A match record projection, as returned by the match endpoints.</summary>
internal sealed record MatchSummary(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? MaxGames,
    int Seed,
    TournamentMatchStatus Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    string? Detail)
{
    public static MatchSummary From(TournamentMatchRecord record) =>
        new(
            record.MatchId,
            record.EngineOne,
            record.EngineTwo,
            record.MatchLength,
            record.MaxGames,
            record.Seed,
            record.Status,
            record.Winner,
            record.SeatOneScore,
            record.SeatTwoScore,
            record.ForfeitedBy,
            record.Detail);
}
