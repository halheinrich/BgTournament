namespace BgTournament.EngineClient;

/// <summary>
/// Both players' remaining pools as of the moment the current decision query
/// was issued (PROTOCOL.md §10). A reading is a stamp, not a live clock: the
/// pending decision's own thinking time is not yet debited, and network
/// latency is on the queried player's clock — budget for the round trip. The
/// server's measurement is authoritative. Delivered only in clocked matches;
/// the flat regime has no pools, and none is ever fabricated for it.
/// </summary>
/// <param name="YourTimeRemaining">This engine's own remaining pool.</param>
/// <param name="OpponentTimeRemaining">The opponent's remaining pool.</param>
public sealed record ClockReading(TimeSpan YourTimeRemaining, TimeSpan OpponentTimeRemaining);
