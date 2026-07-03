namespace BgTournament.Api;

/// <summary>
/// The admin request that starts a round-robin tournament
/// (<c>POST /tournaments</c>) among connected engines.
/// </summary>
/// <param name="Participants">Connected-engine names in seeding order (the final standings tie-break).</param>
/// <param name="MatchLength">Match length in points for every scheduled match (≥ 1).</param>
/// <param name="MatchesPerPairing">How many times each pair meets (≥ 1); an even count balances the opening-roll seat exactly.</param>
/// <param name="Seed">Optional tournament seed making the whole tournament reproducible; server-chosen and recorded when omitted.</param>
public sealed record StartTournamentRequest(
    IReadOnlyList<string>? Participants,
    int MatchLength,
    int MatchesPerPairing,
    int? Seed = null);
