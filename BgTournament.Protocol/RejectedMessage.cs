namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. Handshake rejection — version mismatch, name already
/// connected, or malformed hello. The server closes the connection after
/// sending it.
/// </summary>
public sealed record RejectedMessage : ProtocolMessage
{
    /// <summary>Human-readable rejection reason.</summary>
    public required string Reason { get; init; }
}
