namespace BgTournament.Server;

/// <summary>
/// Server-configured timing, bound from the <c>Tournament</c> configuration
/// section. Real time controls (clocks) are a later protocol version; these
/// two timeouts are the only timing rules in v1 (PROTOCOL.md §9).
/// </summary>
internal sealed class TournamentOptions
{
    /// <summary>Seconds an engine has to answer one decision query.</summary>
    public double DecisionTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds a new connection has to complete the hello handshake.</summary>
    public double HandshakeTimeoutSeconds { get; set; } = 10;

    /// <summary>The per-decision timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan DecisionTimeout => TimeSpan.FromSeconds(DecisionTimeoutSeconds);

    /// <summary>The handshake timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(HandshakeTimeoutSeconds);
}
