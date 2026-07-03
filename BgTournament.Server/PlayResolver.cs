using BgDataTypes_Lib;
using BgMoveGen;

namespace BgTournament.Server;

/// <summary>
/// Resolves a wire-derived (hit-less) play to the canonical, hit-encoded
/// candidate from <see cref="MoveGenerator.GeneratePlays"/> — the sole
/// sanctioned consumer of <c>WireMapping.ToUnresolvedPlay</c>'s output.
///
/// <para>This step is load-bearing, not cosmetic: candidate matching compares
/// hit-insensitive keys, so an unresolved play can <em>validate</em> as legal
/// while <em>applying</em> without its hits (no blot to the bar — a corrupted
/// board). The canonical candidate is what gets applied and what the
/// transcript records.</para>
/// </summary>
internal static class PlayResolver
{
    /// <summary>
    /// The canonical legal play matching <paramref name="unresolved"/> for
    /// this board and dice, or null when no legal candidate matches.
    /// </summary>
    public static Play? Resolve(BoardState board, int die1, int die2, Play unresolved)
    {
        ArgumentNullException.ThrowIfNull(board);
        foreach (var candidate in MoveGenerator.GeneratePlays(board, die1, die2))
        {
            if (candidate.Equals(unresolved))
            {
                return candidate;
            }
        }

        return null;
    }
}
