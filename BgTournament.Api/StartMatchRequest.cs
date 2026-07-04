namespace BgTournament.Api;

/// <summary>
/// The admin request that starts a standalone match (<c>POST /matches</c>)
/// between two connected engines.
/// </summary>
/// <param name="EngineOne">Connected-engine name to seat in seat One.</param>
/// <param name="EngineTwo">Connected-engine name to seat in seat Two.</param>
/// <param name="MatchLength">Match length in points (≥ 1), or 0 for a money session (which then requires <paramref name="MaxGames"/>).</param>
/// <param name="Seed">Optional dice seed. Supplied ⇒ reproducible dev/repro mode (uncommitted <c>SeededDiceSource</c>). Omitted ⇒ fair mode: the server drives verifiable dice from a per-match key it commits to before the first roll and reveals at match end (see PROTOCOL.md, "Provably-fair dice"). A seed is still recorded either way as an admin datum.</param>
/// <param name="MaxGames">Optional cap on games played; required for a money session.</param>
public sealed record StartMatchRequest(
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? Seed = null,
    int? MaxGames = null);
