namespace BgTournament.Protocol;

/// <summary>
/// Engine → server. The first message on a new connection: protocol version
/// check plus engine identity. The server answers with
/// <see cref="WelcomeMessage"/> or <see cref="RejectedMessage"/> (then close).
/// </summary>
public sealed record HelloMessage : ProtocolMessage
{
    /// <summary>
    /// The wire protocol version the engine speaks. Must equal
    /// <see cref="WireProtocol.Version"/>; any mismatch is rejected.
    /// </summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>
    /// The engine's unique name — its registry identity, used to name it as a
    /// match participant. Rejected if already connected under the same name.
    /// </summary>
    public required string EngineName { get; init; }

    /// <summary>Optional engine version label, for records and diagnostics.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>Optional author attribution, for records and diagnostics.</summary>
    public string? Author { get; init; }
}
