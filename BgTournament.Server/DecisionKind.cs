namespace BgTournament.Server;

/// <summary>
/// Which decision query an engine is being asked — the server's one decision
/// vocabulary, shared by the clock (per-decision settlement evidence) and the
/// agent (query labels in failure messages), so the two never drift.
/// </summary>
internal enum DecisionKind
{
    /// <summary>A play query (checker movement for a rolled pair).</summary>
    Play,

    /// <summary>A cube-offer query (double or roll).</summary>
    CubeOffer,

    /// <summary>A cube-response query (take or pass).</summary>
    CubeResponse,
}

/// <summary>Display helpers for <see cref="DecisionKind"/>.</summary>
internal static class DecisionKindExtensions
{
    /// <summary>
    /// The query label used in human-readable failure messages ("play",
    /// "cube-offer", "cube-response") — the strings the failure taxonomy has
    /// always carried, now derived from the enum instead of passed beside it.
    /// </summary>
    public static string Label(this DecisionKind kind) => kind switch
    {
        DecisionKind.Play => "play",
        DecisionKind.CubeOffer => "cube-offer",
        DecisionKind.CubeResponse => "cube-response",
        _ => throw new InvalidOperationException($"Unhandled DecisionKind value: {kind}."),
    };
}
