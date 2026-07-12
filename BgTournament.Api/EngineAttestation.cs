namespace BgTournament.Api;

/// <summary>
/// An engine's provenance declaration — the originality attestation recorded
/// at registration (the WCCC-model posture: originality is <em>declared</em>,
/// and the declaration is what a tournament arbiter holds the entrant to;
/// verification stays an arbiter's right, not a code path). Stored as
/// declared: the server validates presence, never content.
/// </summary>
/// <param name="Authors">The people (or teams) who wrote the engine; at least one entry.</param>
/// <param name="Origin">
/// A free-form statement of the engine's origin — its own code, training
/// pipeline, published techniques used, and the like.
/// </param>
/// <param name="DerivedFrom">
/// The prior work this engine derives from (a fork, a fine-tune, a ported
/// engine), when any; null declares an original work.
/// </param>
public sealed record EngineAttestation(
    IReadOnlyList<string> Authors,
    string Origin,
    string? DerivedFrom = null);
