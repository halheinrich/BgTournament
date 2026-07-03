namespace BgTournament.Server;

/// <summary>
/// An engine exceeded the per-decision timeout. One of the three forfeit
/// causes (with contract violations and disconnects); translated to a forfeit
/// by <see cref="MatchService"/>, attributed via <see cref="EngineName"/>.
/// </summary>
internal sealed class EngineTimeoutException : Exception
{
    /// <summary>The engine that failed to answer in time.</summary>
    public string EngineName { get; }

    public EngineTimeoutException(string engineName, string decision, TimeSpan timeout)
        : base($"Engine '{engineName}' did not answer a {decision} query within {timeout.TotalSeconds:0.##} s.")
    {
        EngineName = engineName;
    }
}

/// <summary>
/// An engine's connection ended while the server needed it — mid-query, or
/// flagged proactively while its opponent was deciding. One of the three
/// forfeit causes; attributed via <see cref="EngineName"/>.
/// </summary>
internal sealed class EngineDisconnectedException : Exception
{
    /// <summary>The engine whose connection ended.</summary>
    public string EngineName { get; }

    public EngineDisconnectedException(string engineName, string context)
        : base($"Engine '{engineName}' disconnected: {context}")
    {
        EngineName = engineName;
    }
}

/// <summary>
/// An engine sent traffic that breaks the protocol: a malformed frame, a
/// reply of the wrong type, or a reply correlating to no outstanding query.
/// Raised inside the connection layer and wrapped by
/// <see cref="RemoteEngineAgent"/> into the substrate's typed
/// AgentContractViolationException with the decision-appropriate kind.
/// </summary>
internal sealed class EngineProtocolViolationException : Exception
{
    public EngineProtocolViolationException(string message)
        : base(message)
    {
    }

    public EngineProtocolViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
