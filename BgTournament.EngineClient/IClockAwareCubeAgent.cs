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
    /// <param name="state">The current game state. Treat as input only — do not mutate.</param>
    /// <param name="clock">Both pools as of query issuance. Never null: the flat regime is served through the plain overload.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask<CubeAction> ChooseOfferAsync(
        GameState state,
        ClockReading clock,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decide how to respond to a double offer, with the match clock in view.
    /// Mirrors <see cref="ICubeAgent.ChooseResponseAsync"/> exactly — the
    /// state is in the <em>responder's</em> frame with the pre-double cube,
    /// as documented there — with the clock inserted. Legal return values:
    /// <see cref="CubeAction.Take"/>, <see cref="CubeAction.Pass"/>.
    /// </summary>
    /// <param name="state">The current game state, in the responder's frame. Treat as input only — do not mutate.</param>
    /// <param name="clock">Both pools as of query issuance. Never null: the flat regime is served through the plain overload.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask<CubeAction> ChooseResponseAsync(
        GameState state,
        ClockReading clock,
        CancellationToken cancellationToken = default);
}
