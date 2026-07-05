namespace BgTournament.Server;

/// <summary>
/// The structured forfeit taxonomy — which of the forfeit paths ended the
/// match. The catch blocks in <see cref="MatchService.RunHostedMatchAsync"/>
/// (and the tournament's forfeit-without-play branch) are the only producers;
/// the journal records it so the durable audit record carries structure, not
/// just the human-readable detail string.
/// </summary>
internal enum ForfeitCause
{
    /// <summary>A malformed, illegal, or out-of-contract reply.</summary>
    ContractViolation,

    /// <summary>The flat per-decision timeout elapsed.</summary>
    Timeout,

    /// <summary>The Fischer clock pool emptied mid-decision — the flag fell.</summary>
    FlagFall,

    /// <summary>The engine's connection ended mid-match.</summary>
    Disconnect,

    /// <summary>A tournament participant was not connected when its match came up (forfeit without play).</summary>
    NeverConnected,
}
