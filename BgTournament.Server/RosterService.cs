using BgTournament.Api;
using BgTournament.Server.Persistence;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server;

/// <summary>One registered engine's current roster state — the fold of its journal history.</summary>
internal sealed class RosterEngineRecord
{
    public required string Name { get; init; }

    /// <summary>The current declaration — the latest one declared.</summary>
    public required EngineAttestation Attestation { get; set; }

    /// <summary>The live credential; a rotation replaces it whole.</summary>
    public required EngineCredential Credential { get; set; }

    /// <summary>False once deactivated — the key stops authenticating and Registered-policy connects are refused.</summary>
    public bool Active { get; set; } = true;

    public required DateTimeOffset RegisteredAtUtc { get; init; }

    /// <summary>The admin actor who registered the engine; null when the admin surface served anonymously.</summary>
    public string? RegisteredBy { get; init; }

    public DateTimeOffset? KeyRotatedAtUtc { get; set; }

    public DateTimeOffset? DeactivatedAtUtc { get; set; }
}

/// <summary>Why a roster mutation was refused.</summary>
internal enum RosterError
{
    /// <summary>No error — done.</summary>
    None,

    /// <summary>The request shape is invalid (blank name, empty attestation, …).</summary>
    InvalidRequest,

    /// <summary>The name is already on the roster (names are unique for all time).</summary>
    AlreadyRegistered,

    /// <summary>No engine of that name is on the roster.</summary>
    NotRegistered,

    /// <summary>The engine is deactivated — a terminal state this mutation cannot apply to.</summary>
    Deactivated,
}

/// <summary>
/// The engine roster: registration under the attestation posture. Holds the
/// folded state (name → entry), serializes every mutation, and owns the
/// write path to the roster journal — the durable, append-only registration
/// history that <em>is</em> the roster across restarts (rehydration folds it
/// back through <see cref="Restore"/> before anything serves).
///
/// <para><b>Durable before answered.</b> Every mutation journals its event
/// synchronously (see <see cref="RosterJournal"/>) <em>before</em> touching
/// in-memory state or returning — a registration response carries the
/// show-once key, so the credential must be on disk before the key is shown.
/// A journal failure fails the mutation; state never runs ahead of the
/// record.</para>
///
/// <para><b>One transition SSOT.</b> Runtime mutations and startup fold apply
/// events through the same <see cref="Apply"/> path, so a rehydrated roster
/// cannot differ from the one the mutations built live.</para>
/// </summary>
internal sealed class RosterService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RosterEngineRecord> _engines = new(StringComparer.Ordinal);
    private readonly List<RosterEngineRecord> _order = [];
    private readonly RosterJournal _journal;
    private readonly TimeProvider _time;
    private readonly ILogger<RosterService> _logger;

    public RosterService(IJournalStore store, TimeProvider time, ILogger<RosterService> logger)
    {
        _journal = new RosterJournal(store, time);
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Adopt the journaled registration history at startup, before anything
    /// serves — events arrive in segment order (the rehydrator's job) and
    /// fold through the same transitions runtime mutations use. An event the
    /// state machine cannot apply (evidence of a damaged segment — the
    /// running server never writes one) is skipped loudly; the fold never
    /// poisons the entries it already built.
    /// </summary>
    public void Restore(IReadOnlyList<RosterJournalEvent> events)
    {
        lock (_gate)
        {
            foreach (var journalEvent in events)
            {
                if (Apply(journalEvent) is { } problem)
                {
                    _logger.LogError(
                        "Skipping a roster journal event that cannot fold ({EventType}): {Problem}",
                        journalEvent.GetType().Name, problem);
                }
            }
        }

        if (_order.Count > 0)
        {
            _logger.LogInformation(
                "Rehydrated the engine roster: {Count} entr(y/ies), {Active} active.",
                _order.Count, _order.Count(engine => engine.Active));
        }
    }

    /// <summary>Every roster entry, in registration order (stable across restarts — the fold replays in event order).</summary>
    public IReadOnlyList<RosterEngineRecord> List()
    {
        lock (_gate)
        {
            return _order.ToArray();
        }
    }

    /// <summary>Fetch one entry by its exact (ordinal) name.</summary>
    public bool TryGet(string name, out RosterEngineRecord engine)
    {
        lock (_gate)
        {
            return _engines.TryGetValue(name, out engine!);
        }
    }

    /// <summary>Whether any active engine is registered — the Registered-policy boot check reads this.</summary>
    public bool HasActiveEngines
    {
        get
        {
            lock (_gate)
            {
                return _order.Any(engine => engine.Active);
            }
        }
    }

    /// <summary>
    /// Resolve a presented engine key to the roster entry it belongs to — the
    /// wire handshake's identity resolution. Resolution is credential
    /// verification against every entry (active or not, so the caller can
    /// name deactivation precisely); each check is fixed-time.
    /// </summary>
    public bool TryResolveKey(string presentedKey, out RosterEngineRecord engine)
    {
        lock (_gate)
        {
            foreach (var candidate in _order)
            {
                if (EngineKeyCredentials.Verifies(presentedKey, candidate.Credential))
                {
                    engine = candidate;
                    return true;
                }
            }
        }

        engine = null!;
        return false;
    }

    /// <summary>
    /// Register an engine: record the attestation, issue a fresh key. The
    /// returned key is the <b>only</b> time it exists in plaintext — the
    /// journal and the state hold its salted hash.
    /// </summary>
    public (RosterEngineRecord? Engine, string? EngineKey, RosterError Error, string? Detail) Register(
        string? name, EngineAttestation? attestation, string? actor)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null, RosterError.InvalidRequest, "name must be non-empty.");
        }

        if (ValidateAttestation(attestation) is { } invalid)
        {
            return (null, null, RosterError.InvalidRequest, invalid);
        }

        string engineKey = EngineKeyCredentials.GenerateKey();
        lock (_gate)
        {
            if (_engines.ContainsKey(name))
            {
                return (
                    null, null, RosterError.AlreadyRegistered,
                    $"An engine named '{name}' is already registered (roster names are unique for all time).");
            }

            // Durable before answered: the journal write precedes both the
            // state change and the show-once response; a write failure fails
            // the registration with nothing half-done.
            var journalEvent = JournalMapping.ToRegisteredEvent(
                name, attestation!, EngineKeyCredentials.Derive(engineKey), actor, _time.GetUtcNow());
            _journal.Append(journalEvent);
            Apply(journalEvent);
            return (_engines[name], engineKey, RosterError.None, null);
        }
    }

    /// <summary>Re-declare an engine's attestation (a correction or amendment; the history keeps every declaration).</summary>
    public (RosterEngineRecord? Engine, RosterError Error, string? Detail) DeclareAttestation(
        string name, EngineAttestation? attestation, string? actor)
    {
        if (ValidateAttestation(attestation) is { } invalid)
        {
            return (null, RosterError.InvalidRequest, invalid);
        }

        lock (_gate)
        {
            if (RequireActive(name) is { } refused)
            {
                return (null, refused.Error, refused.Detail);
            }

            var journalEvent = JournalMapping.ToAttestationEvent(
                name, attestation!, actor, _time.GetUtcNow());
            _journal.Append(journalEvent);
            Apply(journalEvent);
            return (_engines[name], RosterError.None, null);
        }
    }

    /// <summary>
    /// Rotate an engine's key: the old credential stops verifying, the fresh
    /// key is returned — shown once, like at registration.
    /// </summary>
    public (RosterEngineRecord? Engine, string? EngineKey, RosterError Error, string? Detail) RotateKey(
        string name, string? actor)
    {
        string engineKey = EngineKeyCredentials.GenerateKey();
        lock (_gate)
        {
            if (RequireActive(name) is { } refused)
            {
                return (null, null, refused.Error, refused.Detail);
            }

            var journalEvent = JournalMapping.ToRotatedEvent(
                name, EngineKeyCredentials.Derive(engineKey), actor, _time.GetUtcNow());
            _journal.Append(journalEvent);
            Apply(journalEvent);
            return (_engines[name], engineKey, RosterError.None, null);
        }
    }

    /// <summary>Deactivate an engine — terminal: its key stops authenticating and no further mutation applies.</summary>
    public (RosterEngineRecord? Engine, RosterError Error, string? Detail) Deactivate(string name, string? actor)
    {
        lock (_gate)
        {
            if (RequireActive(name) is { } refused)
            {
                return (null, refused.Error, refused.Detail);
            }

            var journalEvent = JournalMapping.ToDeactivatedEvent(name, actor, _time.GetUtcNow());
            _journal.Append(journalEvent);
            Apply(journalEvent);
            return (_engines[name], RosterError.None, null);
        }
    }

    /// <summary>Close the roster journal segment at host shutdown (the DI container disposes singletons).</summary>
    public void Dispose() => _journal.Dispose();

    /// <summary>The mutation precondition shared by everything but registration: the name exists and is active.</summary>
    private (RosterError Error, string Detail)? RequireActive(string name)
    {
        if (!_engines.TryGetValue(name, out var engine))
        {
            return (RosterError.NotRegistered, $"No engine named '{name}' is registered.");
        }

        if (!engine.Active)
        {
            return (
                RosterError.Deactivated,
                $"Engine '{name}' is deactivated; deactivation is terminal for a roster entry.");
        }

        return null;
    }

    /// <summary>
    /// Attestations are stored as declared — validation is presence, never
    /// content (forensics is an arbiter's right, not a code path).
    /// </summary>
    private static string? ValidateAttestation(EngineAttestation? attestation) => attestation switch
    {
        null => "attestation is required — registration records a provenance declaration.",
        { Authors: null } or { Authors.Count: 0 } => "attestation.authors must name at least one author.",
        { Authors: { } authors } when authors.Any(string.IsNullOrWhiteSpace) =>
            "attestation.authors must not contain blank entries.",
        _ when string.IsNullOrWhiteSpace(attestation.Origin) =>
            "attestation.origin must be non-empty.",
        { DerivedFrom: { } derived } when string.IsNullOrWhiteSpace(derived) =>
            "attestation.derivedFrom must be non-empty when present (omit it to declare an original work).",
        _ => null,
    };

    /// <summary>
    /// The one transition path — runtime mutations (after journaling) and the
    /// startup fold both land here. Returns a problem description instead of
    /// applying when the event cannot fold (possible only for a damaged
    /// journal; runtime callers pre-validate under the gate).
    /// </summary>
    private string? Apply(RosterJournalEvent journalEvent)
    {
        switch (journalEvent)
        {
            case EngineRegisteredEvent registered:
                if (_engines.ContainsKey(registered.Name))
                {
                    return $"an engine named '{registered.Name}' is already registered.";
                }

                var engine = new RosterEngineRecord
                {
                    Name = registered.Name,
                    Attestation = JournalMapping.ToAttestation(registered.Attestation),
                    Credential = JournalMapping.ToCredential(registered.Credential),
                    RegisteredAtUtc = registered.At,
                    RegisteredBy = registered.Actor,
                };
                _engines[registered.Name] = engine;
                _order.Add(engine);
                return null;

            case AttestationDeclaredEvent declared:
                if (RequireFoldable(declared.Name) is { } noDeclare)
                {
                    return noDeclare;
                }

                _engines[declared.Name].Attestation = JournalMapping.ToAttestation(declared.Attestation);
                return null;

            case CredentialRotatedEvent rotated:
                if (RequireFoldable(rotated.Name) is { } noRotate)
                {
                    return noRotate;
                }

                _engines[rotated.Name].Credential = JournalMapping.ToCredential(rotated.Credential);
                _engines[rotated.Name].KeyRotatedAtUtc = rotated.At;
                return null;

            case EngineDeactivatedEvent deactivated:
                if (RequireFoldable(deactivated.Name) is { } noDeactivate)
                {
                    return noDeactivate;
                }

                _engines[deactivated.Name].Active = false;
                _engines[deactivated.Name].DeactivatedAtUtc = deactivated.At;
                return null;

            default:
                return $"unhandled roster event type {journalEvent.GetType().Name}.";
        }
    }

    private string? RequireFoldable(string name) =>
        !_engines.TryGetValue(name, out var engine) ? $"no engine named '{name}' is registered."
        : !engine.Active ? $"engine '{name}' is deactivated."
        : null;
}
