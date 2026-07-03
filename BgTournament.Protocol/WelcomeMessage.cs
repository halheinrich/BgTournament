namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. Successful handshake: the engine is registered and may be
/// named as a match participant. Confirms the protocol version in effect.
/// </summary>
public sealed record WelcomeMessage : ProtocolMessage
{
    /// <summary>The wire protocol version in effect for this connection.</summary>
    public required int ProtocolVersion { get; init; }
}
