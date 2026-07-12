using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

/// <summary>
/// Base type for every event in a server journal — the durable record of
/// server-level gate evidence: engine connects, disconnects, and handshake
/// rejections (<c>EngineRegistry</c> itself is deliberately ephemeral), plus
/// the admin surface's refusals (schema v2 — accepted admin actions are not
/// recorded here; their actor stamps the match/tournament <c>created</c>
/// header instead, the one durable home per action). One JSON object per
/// JSONL line, <c>"type"</c>-discriminated; serialize and deserialize
/// exclusively through <see cref="JournalCodec"/>.
///
/// <para><b>One segment per server session.</b> Unlike a match journal, this
/// record has no natural end — so each process start opens a fresh segment
/// file (preserving the store's create-once, append-only contract) headed by
/// a <see cref="ServerStartedEvent"/> and, on graceful shutdown, closed by a
/// <see cref="ServerStoppedEvent"/>. A segment without a <c>stopped</c> event
/// is a server that died — crash evidence by absence, the interrupted-match
/// rule one level up.</para>
///
/// <para><b>Evidence-only.</b> Sessions are ephemeral, so nothing rehydrates
/// from these files — they exist for arbitration and operations (and
/// pre-stage the registration arc), read by humans, not by the fold.</para>
/// </summary>
/// <param name="At">When the event occurred (UTC, via the DI <see cref="TimeProvider"/> — never ambient time).</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ServerStartedEvent), "started")]
[JsonDerivedType(typeof(EngineConnectedEvent), "engineConnected")]
[JsonDerivedType(typeof(EngineDisconnectedEvent), "engineDisconnected")]
[JsonDerivedType(typeof(HandshakeRejectedEvent), "handshakeRejected")]
[JsonDerivedType(typeof(AdminRejectedEvent), "adminRejected")]
[JsonDerivedType(typeof(ServerStoppedEvent), "stopped")]
internal abstract record ServerJournalEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At);

/// <summary>
/// The segment header (always line 1): a server session began. Carries the
/// server-journal schema version — versioned independently of the
/// match/tournament format (<see cref="JournalCodec.ServerSchemaVersion"/>).
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="SchemaVersion">The server-journal schema version governing the whole segment.</param>
internal sealed record ServerStartedEvent(
    DateTimeOffset At,
    [property: JsonPropertyOrder(-2)] int SchemaVersion) : ServerJournalEvent(At);

/// <summary>An engine completed the hello handshake and registered.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="EngineName">The engine's unique handshake name.</param>
/// <param name="Version">Engine version, when the hello carried one.</param>
/// <param name="Author">Engine author, when the hello carried one.</param>
internal sealed record EngineConnectedEvent(
    DateTimeOffset At,
    string EngineName,
    string? Version,
    string? Author) : ServerJournalEvent(At);

/// <summary>A registered engine's connection ended (peer close, transport failure, or violation close).</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="EngineName">The engine's handshake name.</param>
internal sealed record EngineDisconnectedEvent(
    DateTimeOffset At,
    string EngineName) : ServerJournalEvent(At);

/// <summary>
/// A connection was rejected at the handshake gate. Every rejection the
/// endpoint names on the wire is journaled here with the same reason string —
/// one vocabulary for the peer and the record.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Reason">The exact reason sent in the wire <c>rejected</c> message.</param>
/// <param name="EngineName">The name the hello claimed, when one was readable; null when the rejection preceded a usable hello (timeout, malformed frame, wrong first message, empty name).</param>
internal sealed record HandshakeRejectedEvent(
    DateTimeOffset At,
    string Reason,
    string? EngineName) : ServerJournalEvent(At);

/// <summary>
/// An admin HTTP request was refused at the identity gate (schema v2) — the
/// admin counterpart of <see cref="HandshakeRejectedEvent"/>, with the same
/// one-funnel rule: the reason is the exact string the refused response
/// carried. The presented key value is deliberately never recorded — a
/// mistyped real secret must not land in a durable file.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Reason">The exact reason sent in the response's <c>ErrorResponse</c> body.</param>
/// <param name="Method">The refused request's HTTP method.</param>
/// <param name="Path">The refused request's path — what was being attempted.</param>
internal sealed record AdminRejectedEvent(
    DateTimeOffset At,
    string Reason,
    string Method,
    string Path) : ServerJournalEvent(At);

/// <summary>
/// The server session ended gracefully — always the segment's last event when
/// present. Its absence is the evidence: the process died without a clean
/// shutdown.
/// </summary>
/// <param name="At">Event time (UTC).</param>
internal sealed record ServerStoppedEvent(DateTimeOffset At) : ServerJournalEvent(At);
