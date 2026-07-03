namespace BgTournament.Api;

/// <summary>One standings line in a tournament projection.</summary>
/// <param name="Rank">1-based, unique — the standings are a total deterministic order.</param>
/// <param name="Participant">The engine this line ranks.</param>
/// <param name="Wins">Matches won so far.</param>
/// <param name="Losses">Matches lost so far.</param>
/// <param name="SonnebornBerger">Sonneborn-Berger score on wins — the tie-break behind head-to-head.</param>
public sealed record StandingEntry(int Rank, string Participant, int Wins, int Losses, int SonnebornBerger);
