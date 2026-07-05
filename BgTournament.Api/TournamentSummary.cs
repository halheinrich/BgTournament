namespace BgTournament.Api;

/// <summary>A tournament record projection, as returned by the tournament endpoints.</summary>
/// <param name="TournamentId">Server-assigned tournament id; resolves on <c>GET /tournaments/{tournamentId}</c>.</param>
/// <param name="Participants">Participants in seeding order (the final standings tie-break).</param>
/// <param name="MatchLength">Match length in points for every scheduled match.</param>
/// <param name="MatchesPerPairing">How many times each pair meets.</param>
/// <param name="Seed">The tournament seed every match seed derives from; recorded even when server-chosen.</param>
/// <param name="TimeControl">The Fischer time control every scheduled match runs under, or null for the flat per-decision-timeout regime.</param>
/// <param name="Status">Where the tournament is in its lifecycle.</param>
/// <param name="Winner">Tournament winner's name; null until the tournament completes.</param>
/// <param name="Detail">Human-readable outcome detail (abort or fault reason).</param>
/// <param name="Standings">Point-in-time standings, best rank first.</param>
/// <param name="Matches">The full schedule ledger, in schedule order.</param>
/// <param name="StartedAtUtc">When the server created and began hosting the tournament (UTC).</param>
/// <param name="EndedAtUtc">When the tournament reached its terminal status (UTC); null while running — and null on an <see cref="TournamentStatus.Interrupted"/> record, whose true end time died with the server.</param>
public sealed record TournamentSummary(
    string TournamentId,
    IReadOnlyList<string> Participants,
    int MatchLength,
    int MatchesPerPairing,
    int Seed,
    TimeControl? TimeControl,
    TournamentStatus Status,
    string? Winner,
    string? Detail,
    IReadOnlyList<StandingEntry> Standings,
    IReadOnlyList<TournamentMatchEntry> Matches,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);
