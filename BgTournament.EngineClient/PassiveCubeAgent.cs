using BgDataTypes_Lib;
using BgGame_Lib;

namespace BgTournament.EngineClient;

/// <summary>
/// The reference cube policy: never doubles, always takes. Stateless and
/// deliberately trivial — it keeps the cube out of the way so a baseline
/// match exercises the play loop, while still accepting any double offered.
/// </summary>
public sealed class PassiveCubeAgent : ICubeAgent
{
    /// <inheritdoc/>
    /// <remarks>Always <see cref="CubeAction.NoDouble"/>.</remarks>
    public ValueTask<CubeAction> ChooseOfferAsync(
        GameState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CubeAction>(CubeAction.NoDouble);
    }

    /// <inheritdoc/>
    /// <remarks>Always <see cref="CubeAction.Take"/>.</remarks>
    public ValueTask<CubeAction> ChooseResponseAsync(
        GameState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CubeAction>(CubeAction.Take);
    }
}
