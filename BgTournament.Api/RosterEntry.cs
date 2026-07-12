namespace BgTournament.Api;

/// <summary>
/// One roster entry's current state, as the roster endpoints project it.
/// Deliberately credential-free: the engine key exists in plaintext only in
/// the moment of issuance (<see cref="EngineKeyGrant"/>), and no summary ever
/// carries hash material. The full registration <em>history</em> (who did
/// what, when) is arbitration evidence in the server's roster journal, not an
/// API surface.
/// </summary>
/// <param name="Name">The engine's roster identity.</param>
/// <param name="Attestation">The current provenance declaration (the latest one declared).</param>
/// <param name="Active">Whether the engine may connect under a Registered policy; false once deactivated.</param>
/// <param name="RegisteredAtUtc">When the engine was registered (UTC).</param>
/// <param name="RegisteredBy">The admin actor who registered it; null when the admin surface served anonymously.</param>
/// <param name="KeyRotatedAtUtc">When the engine key was last rotated (UTC); null if never.</param>
/// <param name="DeactivatedAtUtc">When the engine was deactivated (UTC); null while active.</param>
public sealed record RosterEntry(
    string Name,
    EngineAttestation Attestation,
    bool Active,
    DateTimeOffset RegisteredAtUtc,
    string? RegisteredBy,
    DateTimeOffset? KeyRotatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc);
