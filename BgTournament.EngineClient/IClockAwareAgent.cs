namespace BgTournament.EngineClient;

/// <summary>
/// Opt-in clock awareness for an <see cref="EngineClient"/>-hosted agent: the
/// match-start time-control announcement. Implement it through the role
/// interfaces — <see cref="IClockAwarePlayAgent"/> /
/// <see cref="IClockAwareCubeAgent"/>, which add the per-decision clock
/// delivery — and the client detects the implementation by type; the
/// constructor is unchanged, and plain agents are served exactly as before.
/// </summary>
public interface IClockAwareAgent
{
    /// <summary>
    /// Called once per match, at match start and before any decision query —
    /// clocked or not. A non-null control announces the Fischer clock
    /// governing the whole match (PROTOCOL.md §10); every decision then
    /// carries a <see cref="ClockReading"/> to budget against. Null
    /// affirmatively says the match is unclocked: the flat regime's
    /// per-decision timeout is server-side configuration that never rides the
    /// wire, so the actual limit is unknowable here — answer promptly. No
    /// <see cref="ClockReading"/> is delivered in that regime, and no
    /// sentinel value ever stands in for a pool that does not exist.
    ///
    /// <para>An object implementing both role interfaces hears each match's
    /// announcement exactly once. An exception thrown here propagates out of
    /// the serve loop like any agent exception and drops the connection —
    /// which the server treats as a forfeit.</para>
    /// </summary>
    /// <param name="timeControl">The announced control, or null for an unclocked match.</param>
    void OnTimeControlAnnounced(MatchTimeControl? timeControl);
}
