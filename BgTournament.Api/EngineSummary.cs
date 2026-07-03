namespace BgTournament.Api;

/// <summary>A connected engine, as listed by <c>GET /engines</c>.</summary>
/// <param name="Name">The engine's unique connected name.</param>
/// <param name="Version">Engine version string from the handshake, when given.</param>
/// <param name="Author">Engine author from the handshake, when given.</param>
/// <param name="InMatch">True while the engine is claimed by a match or tournament.</param>
public sealed record EngineSummary(string Name, string? Version, string? Author, bool InMatch);
