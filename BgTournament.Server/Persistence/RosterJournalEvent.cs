using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

/// <summary>
/// Base type for every event in a roster journal segment — the durable record
/// of engine registration under the attestation posture: who was registered,
/// what provenance they declared, when credentials rotated, who was
/// deactivated, and which admin actor did each of it. The registration
/// <em>history</em> is itself arbitration evidence, so events are only ever
/// appended, never rewritten. One JSON object per JSONL line,
/// <c>"type"</c>-discriminated; serialize and deserialize exclusively through
/// <see cref="JournalCodec"/>.
///
/// <para><b>One segment per roster-mutating server session.</b> The roster
/// outlives any one boot, but the store's journals are create-once and
/// append-only — so, like the server journal, each session that mutates the
/// roster opens a fresh segment (lazily, on its first mutation), headed by a
/// <see cref="RosterStartedEvent"/>. The roster's state is the ordered fold
/// of every segment at startup. This also keeps every file under the standard
/// damage policy: a segment is written once and never reopened, so a torn
/// tail is always a true tail — reopen-and-append would turn one boot's torn
/// tail into the next boot's mid-file corruption and poison everything after
/// it.</para>
///
/// <para><b>No plaintext credential, ever.</b> Events carry
/// <see cref="RosterCredential"/> — scheme id, salt, and hash. The issued key
/// exists in plaintext only in the moment of issuance, on the admin response;
/// the roster journal is shareable arbitration evidence and must stay safe to
/// hand an arbiter whole.</para>
/// </summary>
/// <param name="At">When the event occurred (UTC, via the DI <see cref="TimeProvider"/> — never ambient time).</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RosterStartedEvent), "started")]
[JsonDerivedType(typeof(EngineRegisteredEvent), "registered")]
[JsonDerivedType(typeof(AttestationDeclaredEvent), "attestation")]
[JsonDerivedType(typeof(CredentialRotatedEvent), "credentialRotated")]
[JsonDerivedType(typeof(EngineDeactivatedEvent), "deactivated")]
internal abstract record RosterJournalEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At);

/// <summary>
/// The segment header (always line 1): a roster-mutating session began.
/// Carries the roster schema version — versioned independently of the other
/// journal kinds (<see cref="JournalCodec.RosterSchemaVersion"/>); the fold
/// skips a segment with an unknown version whole, loudly.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="SchemaVersion">The roster-journal schema version governing the whole segment.</param>
internal sealed record RosterStartedEvent(
    DateTimeOffset At,
    [property: JsonPropertyOrder(-2)] int SchemaVersion) : RosterJournalEvent(At);

/// <summary>
/// An engine was registered: identity, its declared provenance, and its
/// initial credential. The name is unique on the roster for all time — the
/// fold rejects a second registration of the same name, and there is
/// deliberately no re-registration or reactivation event (a deactivated
/// engine re-enters competition by a conscious future design, not by an
/// event type that could silently resurrect it).
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Name">The engine's roster identity — the name its hello must claim.</param>
/// <param name="Attestation">The provenance declaration, stored as declared.</param>
/// <param name="Credential">The issued key's salted hash — never the key.</param>
/// <param name="Actor">The admin actor who registered it; null when the admin surface served anonymously.</param>
internal sealed record EngineRegisteredEvent(
    DateTimeOffset At,
    string Name,
    RosterAttestation Attestation,
    RosterCredential Credential,
    string? Actor) : RosterJournalEvent(At);

/// <summary>
/// A re-declared provenance attestation (a correction or amendment). The
/// current attestation is the latest declaration; the history of what was
/// declared when — exactly what an originality dispute litigates — is the
/// sequence of these events.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Name">The engine whose attestation was re-declared.</param>
/// <param name="Attestation">The new declaration, stored as declared.</param>
/// <param name="Actor">The admin actor who recorded it; null when the admin surface served anonymously.</param>
internal sealed record AttestationDeclaredEvent(
    DateTimeOffset At,
    string Name,
    RosterAttestation Attestation,
    string? Actor) : RosterJournalEvent(At);

/// <summary>
/// The engine's key was rotated: a fresh credential replaces the old one,
/// which stops verifying immediately. The event that answers "which key was
/// live when" in an arbitration read.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Name">The engine whose key was rotated.</param>
/// <param name="Credential">The new key's salted hash — never the key.</param>
/// <param name="Actor">The admin actor who rotated it; null when the admin surface served anonymously.</param>
internal sealed record CredentialRotatedEvent(
    DateTimeOffset At,
    string Name,
    RosterCredential Credential,
    string? Actor) : RosterJournalEvent(At);

/// <summary>
/// The engine was deactivated: its key stops authenticating and it may no
/// longer connect under a Registered policy. Terminal for the entry — the
/// name stays reserved on the roster and its history stays served.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Name">The engine deactivated.</param>
/// <param name="Actor">The admin actor who deactivated it; null when the admin surface served anonymously.</param>
internal sealed record EngineDeactivatedEvent(
    DateTimeOffset At,
    string Name,
    string? Actor) : RosterJournalEvent(At);

/// <summary>
/// A provenance declaration as journaled — the roster-local mirror of
/// <c>BgTournament.Api.EngineAttestation</c> (journal-local by the standing
/// rule: an Api rename must never silently rewrite the durable format).
/// </summary>
/// <param name="Authors">The people (or teams) who wrote the engine.</param>
/// <param name="Origin">Free-form origin statement, stored as declared.</param>
/// <param name="DerivedFrom">Declared prior work; null declares an original work.</param>
internal sealed record RosterAttestation(
    IReadOnlyList<string> Authors,
    string Origin,
    string? DerivedFrom);

/// <summary>
/// A credential at rest: the salted hash of an issued engine key, plus the
/// scheme that produced it — recorded per credential so a future scheme
/// migration is per-entry data, not a format break.
/// </summary>
/// <param name="Scheme">The hash scheme id (see <c>EngineKeyCredentials.Scheme</c>).</param>
/// <param name="Salt">The per-credential random salt, lowercase hex.</param>
/// <param name="Hash">The salted hash of the key, lowercase hex.</param>
internal sealed record RosterCredential(string Scheme, string Salt, string Hash);
