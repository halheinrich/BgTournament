namespace BgTournament.Server;

/// <summary>
/// Who may connect on the engine wire (<c>Tournament:EnginePolicy</c>).
/// Whichever the policy, a <em>presented</em> <c>engineKey</c> is always
/// validated — an unrecognized key is rejected, never silently ignored — so
/// the policy governs only whether a keyless hello is allowed.
/// </summary>
internal enum EnginePolicy
{
    /// <summary>
    /// Any engine may connect (the default — dev/smoke ergonomics, and the
    /// only mode a rosterless server can meaningfully run).
    /// </summary>
    Open,

    /// <summary>
    /// Only roster-registered engines with a valid <c>engineKey</c> may
    /// connect; everything else gets a named rejection.
    /// </summary>
    Registered,
}
