namespace BgTournament.Server;

/// <summary>
/// Server-configured timing, bound from the <c>Tournament</c> configuration
/// section: the flat per-decision timeout (the default timing regime) and the
/// handshake timeout. A match started with a Fischer time control
/// <em>replaces</em> the per-decision timeout with its clock pool for that
/// match (PROTOCOL.md §10); the handshake timeout applies regardless.
/// </summary>
internal sealed class TournamentOptions
{
    /// <summary>
    /// Seconds an engine has to answer one decision query — the flat regime.
    /// Not applied to time-control matches, where the remaining pool is the
    /// only per-decision limit.
    /// </summary>
    public double DecisionTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds a new connection has to complete the hello handshake.</summary>
    public double HandshakeTimeoutSeconds { get; set; } = 10;

    /// <summary>The per-decision timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan DecisionTimeout => TimeSpan.FromSeconds(DecisionTimeoutSeconds);

    /// <summary>The handshake timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(HandshakeTimeoutSeconds);
}
