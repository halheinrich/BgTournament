using BgDataTypes_Lib;
using BgGame_Lib;

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
            Board = snapshot.Board,
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
    /// The board is not 26 elements, or the match fields are inconsistent
    /// (surfaced by <see cref="MatchState.FromScores"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A field is out of range (e.g. cube value &lt; 1).</exception>
    public static GameState ToGameState(this WireGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Board.Count != 26)
        {
            throw new ArgumentException(
                $"Wire board must have 26 elements, got {state.Board.Count}.", nameof(state));
        }

        var match = MatchState.FromScores(
            state.MatchLength, state.YourScore, state.OpponentScore, state.IsCrawford);
        var board = BoardState.FromMop(state.Board);
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
    /// Build a <see cref="Play"/> from wire moves, exactly as sent — without
    /// hit encoding, because the wire carries none. The result is
    /// <b>unresolved</b>: suitable for legality matching against generated
    /// candidates (which compare on hit-insensitive keys), but not for
    /// applying to a board — applying it would skip any hits. The server
    /// resolves it to the matching canonical candidate before play continues.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="moves"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// More than 4 moves, or a move is out of range (<c>from</c> must be 1–25,
    /// <c>to</c> must be 0–24).
    /// </exception>
    public static Play ToUnresolvedPlay(this IReadOnlyList<WireMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count > 4)
        {
            throw new ArgumentException(
                $"A play has at most 4 moves, got {moves.Count}.", nameof(moves));
        }

        var play = new Play();
        foreach (var move in moves)
        {
            if (move.From is < 1 or > 25)
            {
                throw new ArgumentException(
                    $"Move source must be 1–25 (25 = bar), got {move.From}.", nameof(moves));
            }

            if (move.To is < 0 or > 24)
            {
                throw new ArgumentException(
                    $"Move destination must be 0–24 (0 = bear off), got {move.To}.", nameof(moves));
            }

            play.Add(new Move(move.From, move.To));
        }

        return play;
    }
}
