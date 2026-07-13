using BgDataTypes_Lib;
using BgGame_Lib;

namespace BgTournament.EngineClient;

/// <summary>
/// A play agent that budgets its thinking against the match clock. When the
/// hosted play agent implements this interface, <see cref="EngineClient"/>
/// answers every play query in a clocked match through the clock-carrying
/// overload; in the flat regime (no time control) it calls the plain
/// <see cref="IPlayAgent.ChoosePlayAsync"/> instead — the regime is told by
/// shape (which method runs, and the announcement's null), never by a
/// sentinel value.
/// </summary>
public interface IClockAwarePlayAgent : IPlayAgent, IClockAwareAgent
{
    /// <summary>
    /// Choose a Play with the match clock in view. Mirrors
    /// <see cref="IPlayAgent.ChoosePlayAsync"/> exactly, with the clock
    /// inserted.
    /// </summary>
    /// <param name="state">The current game state. Treat as input only — do not mutate.</param>
    /// <param name="die1">First die (1–6).</param>
    /// <param name="die2">Second die (1–6).</param>
    /// <param name="clock">Both pools as of query issuance. Never null: the flat regime is served through the plain overload.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask<Play> ChoosePlayAsync(
        GameState state,
        int die1,
        int die2,
        ClockReading clock,
        CancellationToken cancellationToken = default);
}
