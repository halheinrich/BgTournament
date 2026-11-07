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
/// agent a <see cref="GameSnapshot"/> in the on-roll player's frame, and the
/// wire carries exactly that frame, whichever side the queried engine is
/// (PROTOCOL.md, "The frame rule") — so no board, score, or cube-owner flip
/// happens here, by design, and none happens upstream.</para>
///
/// <para>The score is the one field that changes shape: a match state carries
/// what each side still needs (the snapshot's derived away counts), a money
/// state the running scores. Reading a state back converts the needs to scores
/// and refuses a state carrying the other kind's fields; every other refusal
/// is the substrate's own validation, reached by the path a
/// <see cref="GameState"/> is built on.</para>
/// </summary>
public static class WireMapping
{
    /// <summary>The wire board's length: the 26 slots of the substrate's board layout.</summary>
    private const int BoardSlots = 26;

    /// <summary>
    /// Express a game-and-match snapshot as the wire state carried by decision
    /// queries. Frame-preserving: the snapshot's on-roll-relative fields map
    /// to the wire's on-roll and opponent fields one-for-one. A match
    /// snapshot's score goes out as its away counts
    /// (<see cref="MatchSnapshot.OnRollNeeds"/>,
    /// <see cref="MatchSnapshot.OpponentNeeds"/>), a money snapshot's as its
    /// running scores.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static WireGameState ToWireState(this GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var match = snapshot.Match;
        bool isMoney = IsMoney(match.MatchLength);
        return new WireGameState
        {
            Board = CountsOf(snapshot.Board),
            CubeValue = snapshot.CubeSize,
            CubeOwner = snapshot.CubeOwner switch
            {
                BgDataTypes_Lib.CubeOwner.OnRoll => WireCubeOwner.OnRoll,
                BgDataTypes_Lib.CubeOwner.Opponent => WireCubeOwner.Opponent,
                BgDataTypes_Lib.CubeOwner.Centered => WireCubeOwner.Centered,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(snapshot), snapshot.CubeOwner, "Unknown CubeOwner value."),
            },
            MatchLength = match.MatchLength,
            OnRollNeeds = match.OnRollNeeds,
            OpponentNeeds = match.OpponentNeeds,
            OnRollScore = isMoney ? match.OnRollScore : null,
            OpponentScore = isMoney ? match.OpponentScore : null,
            IsCrawford = match.IsCrawford,
        };
    }

    /// <summary>
    /// Reconstruct the <see cref="GameSnapshot"/> a wire state describes, for
    /// handing to local <see cref="IPlayAgent"/>/<see cref="ICubeAgent"/>
    /// implementations. Frame-preserving: the wire's on-roll fields become the
    /// snapshot's on-roll-relative fields one-for-one; a match state's needs
    /// become scores (length − needs).
    ///
    /// <para>Validated by the path a live state is: the state is built as a
    /// <see cref="GameState"/> (<see cref="MatchState.FromScores"/>,
    /// <see cref="BoardState.FromMop"/>, <see cref="GameState.FromPosition"/>)
    /// and its <see cref="GameState.Snapshot"/> is returned, so a malformed
    /// state never reaches an agent. The one refusal of this method's own is a
    /// state whose score fields are not its kind's.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The board is not 26 elements or not a well-formed position (surfaced by
    /// <see cref="BoardState.FromMop"/>); the score fields are not of the
    /// state's kind — a match state (length &gt; 0) carries both needs and no
    /// score, a money state (length 0) both scores and no needs; or the match
    /// fields are inconsistent (surfaced by <see cref="MatchState.FromScores"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A field is out of range: a cube value below 1, or a needs value outside
    /// 1..length, surfaced by <see cref="MatchState.FromScores"/> as the score
    /// it converts to.
    /// </exception>
    public static GameSnapshot ToGameSnapshot(this WireGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Board.Count != BoardSlots)
        {
            throw new ArgumentException(
                $"Wire board must have {BoardSlots} elements, got {state.Board.Count}.", nameof(state));
        }

        var (onRollScore, opponentScore) = ScoresOf(state);
        var match = MatchState.FromScores(state.MatchLength, onRollScore, opponentScore, state.IsCrawford);
        var board = BoardState.FromMop(state.Board.ToArray());
        var owner = state.CubeOwner switch
        {
            WireCubeOwner.OnRoll => BgDataTypes_Lib.CubeOwner.OnRoll,
            WireCubeOwner.Opponent => BgDataTypes_Lib.CubeOwner.Opponent,
            WireCubeOwner.Centered => BgDataTypes_Lib.CubeOwner.Centered,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state), state.CubeOwner, "Unknown WireCubeOwner value."),
        };
        return GameState.FromPosition(match, board, state.CubeValue, owner).Snapshot();
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

    /// <summary>
    /// A length of 0 is a money state; any other is a match state (a negative
    /// one is refused by <see cref="MatchState.FromScores"/>).
    /// </summary>
    private static bool IsMoney(int matchLength) => matchLength == 0;

    /// <summary>
    /// The scores a wire state describes, refusing a state whose score fields
    /// are not its kind's. A match state's needs convert to scores here; their
    /// range is <see cref="MatchState.FromScores"/>'s to refuse (0-away is a
    /// score at the length, a finished match; more than the length is a
    /// negative score).
    /// </summary>
    private static (int OnRoll, int Opponent) ScoresOf(WireGameState state)
    {
        if (IsMoney(state.MatchLength))
        {
            if (state.OnRollNeeds is not null || state.OpponentNeeds is not null)
            {
                throw new ArgumentException(
                    "A money state (matchLength 0) carries onRollScore and opponentScore, never onRollNeeds or opponentNeeds.",
                    nameof(state));
            }

            if (state.OnRollScore is not int onRollScore || state.OpponentScore is not int opponentScore)
            {
                throw new ArgumentException(
                    "A money state (matchLength 0) must carry both onRollScore and opponentScore.", nameof(state));
            }

            return (onRollScore, opponentScore);
        }

        if (state.OnRollScore is not null || state.OpponentScore is not null)
        {
            throw new ArgumentException(
                "A match state (matchLength > 0) carries onRollNeeds and opponentNeeds, never onRollScore or opponentScore.",
                nameof(state));
        }

        if (state.OnRollNeeds is not int onRollNeeds || state.OpponentNeeds is not int opponentNeeds)
        {
            throw new ArgumentException(
                "A match state (matchLength > 0) must carry both onRollNeeds and opponentNeeds.", nameof(state));
        }

        return (state.MatchLength - onRollNeeds, state.MatchLength - opponentNeeds);
    }

    /// <summary>The position's 26 counts, in the substrate's layout, as the wire board.</summary>
    private static int[] CountsOf(BoardPosition position)
    {
        var counts = new int[BoardSlots];
        position.CopyTo(counts);
        return counts;
    }
}
