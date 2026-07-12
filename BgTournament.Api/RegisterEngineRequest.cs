namespace BgTournament.Api;

/// <summary>
/// <c>POST /roster</c> — register an engine on the server's roster.
/// Registration is an administrative act (the request rides the
/// authenticated admin surface): the acting admin records the engine's
/// identity and attestation, and the server issues its engine key — returned
/// exactly once in the <see cref="EngineKeyGrant"/> response.
/// </summary>
/// <param name="Name">
/// The engine's roster identity — the name it must claim in its wire hello,
/// and the name matches attribute to. Unique on the roster (ordinal).
/// </param>
/// <param name="Attestation">The provenance declaration, stored as declared.</param>
public sealed record RegisterEngineRequest(string Name, EngineAttestation Attestation);
