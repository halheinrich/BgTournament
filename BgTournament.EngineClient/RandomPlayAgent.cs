using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;

namespace BgTournament.EngineClient;

/// <summary>
/// The reference play policy: a uniformly random choice among the legal plays
/// from <see cref="MoveGenerator.GeneratePlays"/>. Always legal, never
/// strong — the baseline opponent that proves a loop, a protocol, or a
/// stronger engine.
///
/// <para>The seed is required so tournament runs are reproducible by
/// construction; pass entropy if varied play is wanted. One instance is one
/// deterministic stream of choices — use a fresh instance per match when
/// replaying seeds.</para>
/// </summary>
public sealed class RandomPlayAgent : IPlayAgent
{
    private readonly Random _random;

    /// <summary>Create an agent whose choice sequence is determined by <paramref name="seed"/>.</summary>
    public RandomPlayAgent(int seed)
    {
        _random = new Random(seed);
    }

    /// <inheritdoc/>
    /// <remarks>Returns the empty play when no legal play exists.</remarks>
    public ValueTask<Play> ChoosePlayAsync(
        GameState state, int die1, int die2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = MoveGenerator.GeneratePlays(state.Board, die1, die2);
        var play = candidates.Count == 0 ? default : candidates[_random.Next(candidates.Count)];
        return new ValueTask<Play>(play);
    }
}
