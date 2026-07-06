namespace BgTournament.Api;

/// <summary>
/// The audit endpoint's envelope (<c>GET /matches/{matchId}/audit</c>): one
/// terminal match's ordered arbitration timeline. Served only once a match is
/// terminal — a running match answers 409 pointed at its live feed.
/// </summary>
/// <param name="MatchId">Server-assigned match id.</param>
/// <param name="EngineOne">Engine occupying seat One (audit events attribute by <see cref="Seat"/>).</param>
/// <param name="EngineTwo">Engine occupying seat Two.</param>
/// <param name="Status">The record's terminal status — the authoritative outcome, mirrored by the timeline's terminal event.</param>
/// <param name="Integrity">
/// Null when the durable record read back whole; otherwise a note naming the
/// damage (e.g. corruption mid-file), in which case <paramref name="Events"/>
/// holds only the trusted prefix — plus the record-derived terminal event.
/// </param>
/// <param name="Events">The timeline, in match order; the last event is always the terminal event.</param>
public sealed record MatchAuditResponse(
    string MatchId,
    string EngineOne,
    string EngineTwo,
    MatchStatus Status,
    string? Integrity,
    IReadOnlyList<AuditEvent> Events);
