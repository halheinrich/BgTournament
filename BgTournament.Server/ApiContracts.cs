using BgTournament.Core;

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
    MatchStatus Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    string? Detail)
{
    public static MatchSummary From(MatchRecord record) =>
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

/// <summary>
/// The admin request that starts a round-robin tournament. Participants are
/// connected-engine names in seeding order; the optional seed makes the whole
/// tournament reproducible (server-chosen and recorded otherwise).
/// </summary>
internal sealed record StartTournamentRequest(
    IReadOnlyList<string>? Participants,
    int MatchLength,
    int MatchesPerPairing,
    int? Seed = null);

/// <summary>One standings line in a tournament projection.</summary>
internal sealed record StandingEntry(int Rank, string Participant, int Wins, int Losses, int SonnebornBerger)
{
    public static StandingEntry From(StandingsRow row) =>
        new(row.Rank, row.Participant, row.Wins, row.Losses, row.SonnebornBerger);
}

/// <summary>
/// One scheduled match in a tournament projection. MatchId, Status, and
/// Winner are null until the tournament reaches that match; the id then
/// resolves on <c>GET /matches/{matchId}</c>.
/// </summary>
internal sealed record TournamentMatchEntry(
    int Index,
    string SeatOne,
    string SeatTwo,
    int Seed,
    string? MatchId,
    MatchStatus? Status,
    string? Winner);

/// <summary>A tournament record projection, as returned by the tournament endpoints.</summary>
internal sealed record TournamentSummary(
    string TournamentId,
    IReadOnlyList<string> Participants,
    int MatchLength,
    int MatchesPerPairing,
    int Seed,
    TournamentStatus Status,
    string? Winner,
    string? Detail,
    IReadOnlyList<StandingEntry> Standings,
    IReadOnlyList<TournamentMatchEntry> Matches);
