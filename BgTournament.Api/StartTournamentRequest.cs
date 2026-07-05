namespace BgTournament.Api;

/// <summary>
/// The admin request that starts a round-robin tournament
/// (<c>POST /tournaments</c>) among connected engines.
/// </summary>
/// <param name="Participants">Connected-engine names in seeding order (the final standings tie-break).</param>
/// <param name="MatchLength">Match length in points for every scheduled match (≥ 1).</param>
/// <param name="MatchesPerPairing">How many times each pair meets (≥ 1); an even count balances the opening-roll seat exactly.</param>
/// <param name="Seed">Optional tournament seed. It always governs schedule <em>structure</em> (pairings, seat alternation, per-match seeds). Supplied ⇒ it also drives the dice (reproducible dev/repro). Omitted ⇒ fair mode: each scheduled match runs on its own committed key (see PROTOCOL.md, "Provably-fair dice"), and a structural seed is server-chosen and recorded.</param>
/// <param name="TimeControl">Optional Fischer time control, applied to every scheduled match. Supplied ⇒ each player runs a match clock per match and an empty pool forfeits that match (flag fall), replacing the flat per-decision timeout. Omitted ⇒ the server's flat per-decision timeout governs (see PROTOCOL.md §10).</param>
public sealed record StartTournamentRequest(
    IReadOnlyList<string>? Participants,
    int MatchLength,
    int MatchesPerPairing,
    int? Seed = null,
    TimeControl? TimeControl = null);
