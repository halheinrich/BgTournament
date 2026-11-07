using BgDataTypes_Lib;
using BgGame_Lib;

namespace BgTournament.EngineClient;

/// <summary>
/// A cube agent that budgets its thinking against the match clock. When the
/// hosted cube agent implements this interface, <see cref="EngineClient"/>
/// answers every cube query in a clocked match through the clock-carrying
/// overloads; in the flat regime (no time control) it calls the plain
/// <see cref="ICubeAgent"/> methods instead — the regime is told by shape
/// (which method runs, and the announcement's null), never by a sentinel
/// value.
/// </summary>
public interface IClockAwareCubeAgent : ICubeAgent, IClockAwareAgent
{
    /// <summary>
    /// Decide whether to offer a double at the start of the on-roll player's
    /// turn, with the match clock in view. Mirrors
    /// <see cref="ICubeAgent.ChooseOfferAsync"/> exactly, with the clock
    /// inserted. Legal return values: <see cref="CubeAction.NoDouble"/>,
    /// <see cref="CubeAction.Double"/>.
    /// </summary>
    /// <param name="state">The decision state, in the on-roll player's frame (the agent's own).</param>
    /// <param name="clock">Both pools as of query issuance. Never null: the flat regime is served through the plain overload.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask<CubeAction> ChooseOfferAsync(
        GameSnapshot state,
        ClockReading clock,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decide how to respond to a double offer, with the match clock in view.
    /// Mirrors <see cref="ICubeAgent.ChooseResponseAsync"/> exactly — the
    /// offer's state, in the on-roll (offerer's) frame with the pre-double
    /// cube, the agent the negative side, as documented there — with the clock
    /// inserted. The clock stays the agent's own: its pool and its opponent's.
    /// Legal return values:
    /// <see cref="CubeAction.Take"/>, <see cref="CubeAction.Pass"/>.
    /// </summary>
    /// <param name="state">The offer's decision state, in the on-roll (offerer's) frame; the agent is the negative side.</param>
    /// <param name="clock">Both pools as of query issuance. Never null: the flat regime is served through the plain overload.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask<CubeAction> ChooseResponseAsync(
        GameSnapshot state,
        ClockReading clock,
        CancellationToken cancellationToken = default);
}
