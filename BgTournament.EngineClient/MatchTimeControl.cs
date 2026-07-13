namespace BgTournament.EngineClient;

/// <summary>
/// The Fischer time control governing a match, as announced at match start
/// (PROTOCOL.md §10), in .NET units: each player's initial pool — spanning the
/// whole match, every game, every decision — and the increment credited per
/// answered decision (play and cube replies alike; unused time banks, so a
/// pool may grow beyond <see cref="Initial"/>). Server-reported values,
/// represented faithfully.
/// </summary>
/// <param name="Initial">Each player's starting pool for the whole match.</param>
/// <param name="Increment">The credit per answered decision.</param>
public sealed record MatchTimeControl(TimeSpan Initial, TimeSpan Increment);
