using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;

namespace BgTournament.Protocol;

/// <summary>
/// The single home for wire ↔ substrate conversion. Both the server adapter
/// and the EngineClient SDK convert through these methods; the field
/// correspondences between the wire shapes and BgGame_Lib's types are encoded
/// nowhere else.
///
/// <para>Every conversion is <b>frame-preserving</b>. The substrate hands each
/// agent a <see cref="GameState"/> already in the queried player's own frame
/// (<see cref="GameState.OpponentView"/> is the substrate's only
/// re-expression), and the wire carries exactly that frame (PROTOCOL.md, "The
/// frame rule") — so no board, score, or cube-owner flip happens here, by
/// design. A flip in this class would double-apply the convention.</para>
/// </summary>
public static class WireMapping
{
    /// <summary>The wire board's length: the 26 slots of the substrate's board layout.</summary>
    private const int BoardSlots = 26;

    /// <summary>
    /// Express a game-and-match snapshot as the wire state carried by decision
    /// queries. Frame-preserving: the snapshot's on-roll-relative fields map
    /// to the wire's "your" fields one-for-one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static WireGameState ToWireState(this GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new WireGameState
        {
            Board = CountsOf(snapshot.Board),
            CubeValue = snapshot.CubeSize,
            CubeOwner = snapshot.CubeOwner switch
            {
                BgDataTypes_Lib.CubeOwner.OnRoll => WireCubeOwner.You,
                BgDataTypes_Lib.CubeOwner.Opponent => WireCubeOwner.Opponent,
                BgDataTypes_Lib.CubeOwner.Centered => WireCubeOwner.Centered,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(snapshot), snapshot.CubeOwner, "Unknown CubeOwner value."),
            },
            MatchLength = snapshot.Match.MatchLength,
            YourScore = snapshot.Match.OnRollScore,
            OpponentScore = snapshot.Match.OpponentScore,
            IsCrawford = snapshot.Match.IsCrawford,
        };
    }

    /// <summary>
    /// Reconstruct a detached <see cref="GameState"/> from a wire state, for
    /// handing to local <see cref="IPlayAgent"/>/<see cref="ICubeAgent"/>
    /// implementations. Frame-preserving: the wire's "your" fields become the
    /// state's on-roll-relative fields one-for-one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The board is not 26 elements or not a well-formed position (surfaced by
    /// <see cref="BoardState.FromMop"/>), or the match fields are inconsistent
    /// (surfaced by <see cref="MatchState.FromScores"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A field is out of range (e.g. cube value &lt; 1).</exception>
    public static GameState ToGameState(this WireGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Board.Count != BoardSlots)
        {
            throw new ArgumentException(
                $"Wire board must have {BoardSlots} elements, got {state.Board.Count}.", nameof(state));
        }

        var match = MatchState.FromScores(
            state.MatchLength, state.YourScore, state.OpponentScore, state.IsCrawford);
        var board = BoardState.FromMop(state.Board.ToArray());
        var owner = state.CubeOwner switch
        {
            WireCubeOwner.You => BgDataTypes_Lib.CubeOwner.OnRoll,
            WireCubeOwner.Opponent => BgDataTypes_Lib.CubeOwner.Opponent,
            WireCubeOwner.Centered => BgDataTypes_Lib.CubeOwner.Centered,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state), state.CubeOwner, "Unknown WireCubeOwner value."),
        };
        return GameState.FromPosition(match, board, state.CubeValue, owner);
    }

    /// <summary>
    /// Express a play as wire moves. The wire does not encode hits, so a
    /// hit-encoded destination (negative <see cref="Move.ToPt"/>) is emitted
    /// as the plain landing point.
    /// </summary>
    public static IReadOnlyList<WireMove> ToWireMoves(this Play play)
    {
        var moves = new WireMove[play.Count];
        for (int i = 0; i < play.Count; i++)
        {
            moves[i] = new WireMove { From = play[i].FrPt, To = Math.Abs(play[i].ToPt) };
        }

        return moves;
    }

    /// <summary>
    /// Read wire moves as the mark-free single-die hops they state, in the
    /// order sent — the input of <see cref="MoveGenerator.ResolvePlay"/>,
    /// which resolves them to the legal play they describe (PROTOCOL.md §6:
    /// each move is one legal single-die move, in any order). A hop cannot
    /// carry a hit mark, just as a wire move cannot: hits are the board's.
    /// Nothing here judges legality or counts the moves; that is the
    /// resolver's.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="moves"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A move is null, or its points are outside a hop's range — refused by
    /// <see cref="Hop(int, int)"/>, whose message names the point.
    /// </exception>
    public static IReadOnlyList<Hop> ToHops(this IReadOnlyList<WireMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var hops = new Hop[moves.Count];
        for (int i = 0; i < hops.Length; i++)
        {
            var move = moves[i]
                ?? throw new ArgumentException($"Move {i} is null.", nameof(moves));
            hops[i] = new Hop(move.From, move.To);
        }

        return hops;
    }

    /// <summary>The position's 26 counts, in the substrate's layout, as the wire board.</summary>
    private static int[] CountsOf(BoardPosition position)
    {
        var counts = new int[BoardSlots];
        position.CopyTo(counts);
        return counts;
    }
}
