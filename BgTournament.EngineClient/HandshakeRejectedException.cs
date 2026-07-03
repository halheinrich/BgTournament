namespace BgTournament.EngineClient;

/// <summary>
/// The server explicitly rejected the handshake (version mismatch, name
/// already connected, malformed hello). Carries the server's stated reason.
/// </summary>
public sealed class HandshakeRejectedException : Exception
{
    /// <summary>The server's rejection reason, verbatim.</summary>
    public string Reason { get; }

    /// <summary>Create from the server's rejection reason.</summary>
    public HandshakeRejectedException(string reason)
        : base($"The server rejected the handshake: {reason}")
    {
        Reason = reason;
    }
}
