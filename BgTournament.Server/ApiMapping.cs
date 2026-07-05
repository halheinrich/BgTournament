using BgTournament.Api;
using BgTournament.Core;

namespace BgTournament.Server;

/// <summary>
/// The only home of server-internal → admin-API projections (the admin
/// surface's counterpart to <c>WireMapping</c> on the wire side). The
/// contracts assembly stays free of server internals, and the server never
/// serializes an internal or substrate type directly — everything crossing
/// the HTTP boundary passes through here.
/// </summary>
internal static class ApiMapping
{
    /// <summary>Project a connected engine session onto its admin summary.</summary>
    public static EngineSummary ToSummary(this EngineSession session) =>
        new(session.Name, session.Version, session.Author, session.InMatch);

    /// <summary>Project a hosted-match record onto its admin summary.</summary>
    public static MatchSummary ToSummary(this MatchRecord record) =>
        new(
            record.MatchId,
            record.EngineOne,
            record.EngineTwo,
            record.MatchLength,
            record.MaxGames,
            record.Seed,
            record.TimeControl,
            record.Status,
            record.Winner,
            record.SeatOneScore,
            record.SeatTwoScore,
            record.ForfeitedBy,
            record.Detail,
            record.StartedAtUtc,
            record.EndedAtUtc);

    /// <summary>Project a domain standings row onto its admin shape.</summary>
    public static StandingEntry ToStandingEntry(this StandingsRow row) =>
        new(row.Rank, row.Participant, row.Wins, row.Losses, row.SonnebornBerger);
}
