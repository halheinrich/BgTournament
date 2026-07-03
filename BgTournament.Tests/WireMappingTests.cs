using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Pins the wire ↔ substrate mapping: field-for-field preservation in both
/// directions, and — the load-bearing pin — that the mapping is
/// frame-preserving, with <see cref="GameState.OpponentView"/> as the only
/// re-expression anywhere between the substrate and the wire.
/// </summary>
public class WireMappingTests
{
    /// <summary>An asymmetric mid-game board (13 checkers a side, 2 borne off each).</summary>
    private static int[] MidGameBoard() => new[]
    {
        0, 2, 2, 2, 2, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -2, -2, -3, -2, -2, -2, 0,
    };

    private static GameState MidGameState(int cubeSize, CubeOwner owner, int onRollScore, int opponentScore) =>
        GameState.FromPosition(
            MatchState.FromScores(7, onRollScore, opponentScore, isCrawford: false),
            BoardState.FromMop(MidGameBoard()),
            cubeSize,
            owner);

    [Fact]
    public void ToWireState_MapsEveryField()
    {
        var state = MidGameState(cubeSize: 4, CubeOwner.OnRoll, onRollScore: 2, opponentScore: 3);

        var wire = state.Snapshot().ToWireState();

        Assert.Equal(MidGameBoard(), wire.Board);
        Assert.Equal(4, wire.CubeValue);
        Assert.Equal(WireCubeOwner.You, wire.CubeOwner);
        Assert.Equal(7, wire.MatchLength);
        Assert.Equal(2, wire.YourScore);
        Assert.Equal(3, wire.OpponentScore);
        Assert.False(wire.IsCrawford);
        Assert.Null(wire.Xgid);
    }

    [Fact]
    public void ToWireState_Crawford_PassesThrough()
    {
        var state = GameState.FromPosition(
            MatchState.FromScores(7, 6, 3, isCrawford: true),
            BoardState.FromMop(MidGameBoard()),
            cubeSize: 1,
            CubeOwner.Centered);

        var wire = state.Snapshot().ToWireState();

        Assert.True(wire.IsCrawford);
        Assert.Equal(WireCubeOwner.Centered, wire.CubeOwner);
    }

    [Fact]
    public void ToGameState_RoundTrips_ThroughSnapshot()
    {
        var original = new WireGameState
        {
            Board = MidGameBoard(),
            CubeValue = 2,
            CubeOwner = WireCubeOwner.Opponent,
            MatchLength = 0,
            YourScore = 5,
            OpponentScore = 8,
            IsCrawford = false,
        };

        var wire = original.ToGameState().Snapshot().ToWireState();

        Assert.Equal(original.Board, wire.Board);
        Assert.Equal(original.CubeValue, wire.CubeValue);
        Assert.Equal(original.CubeOwner, wire.CubeOwner);
        Assert.Equal(original.MatchLength, wire.MatchLength);
        Assert.Equal(original.YourScore, wire.YourScore);
        Assert.Equal(original.OpponentScore, wire.OpponentScore);
        Assert.Equal(original.IsCrawford, wire.IsCrawford);
    }

    /// <summary>
    /// The frame pass-through pin. An offerer-frame state (offerer owns the
    /// cube — a redouble) re-expressed by the substrate's OpponentView and
    /// then mapped must show the wire responder exactly what PROTOCOL.md
    /// promises: its own scores in "your" fields, the mirrored board, and the
    /// pre-double cube owned by "opponent" — with no flip performed by the
    /// mapping itself. If a flip ever creeps into WireMapping, the board or
    /// labels here double-flip and this test fails.
    /// </summary>
    [Fact]
    public void ToWireState_OfOpponentView_IsTheResponderFrame_NoDoubleFlip()
    {
        var offererFrame = MidGameState(cubeSize: 2, CubeOwner.OnRoll, onRollScore: 3, opponentScore: 2);

        var wire = offererFrame.OpponentView().Snapshot().ToWireState();

        // Scores swap into the responder's "your" fields.
        Assert.Equal(2, wire.YourScore);
        Assert.Equal(3, wire.OpponentScore);

        // The offerer-owned (redouble) cube reads "opponent" to the responder.
        Assert.Equal(WireCubeOwner.Opponent, wire.CubeOwner);
        Assert.Equal(2, wire.CubeValue);

        // The board is the mirror: wire[i] == -original[25 - i].
        var original = MidGameBoard();
        for (int i = 0; i < 26; i++)
        {
            Assert.Equal(-original[25 - i], wire.Board[i]);
        }
    }

    [Fact]
    public void ToWireMoves_StripsHitEncoding_AndKeepsBearOff()
    {
        var play = new Play();
        play.Add(new Move(24, -21)); // hit on 21, sign-encoded in the substrate
        play.Add(new Move(6, 0));    // bear off

        var moves = play.ToWireMoves();

        Assert.Equal(2, moves.Count);
        Assert.Equal(new WireMove { From = 24, To = 21 }, moves[0]);
        Assert.Equal(new WireMove { From = 6, To = 0 }, moves[1]);
    }

    [Fact]
    public void ToWireMoves_EmptyPlay_EmptyList()
    {
        Assert.Empty(default(Play).ToWireMoves());
    }

    [Fact]
    public void ToUnresolvedPlay_PreservesMovesVerbatim()
    {
        var moves = new[]
        {
            new WireMove { From = 25, To = 22 },
            new WireMove { From = 13, To = 10 },
        };

        var play = moves.ToUnresolvedPlay();

        Assert.Equal(2, play.Count);
        Assert.Equal(new Move(25, 22), play[0]);
        Assert.Equal(new Move(13, 10), play[1]);
    }

    [Fact]
    public void ToUnresolvedPlay_Empty_IsTheEmptyPlay()
    {
        Assert.Equal(0, Array.Empty<WireMove>().ToUnresolvedPlay().Count);
    }

    [Fact]
    public void ToUnresolvedPlay_FiveMoves_Throws()
    {
        var moves = Enumerable.Repeat(new WireMove { From = 6, To = 5 }, 5).ToArray();

        Assert.Throws<ArgumentException>(() => moves.ToUnresolvedPlay());
    }

    [Theory]
    [InlineData(0, 5)]   // from below range
    [InlineData(26, 5)]  // from above range
    [InlineData(6, -1)]  // to below range (hit encoding is not wire-legal)
    [InlineData(6, 25)]  // to above range
    public void ToUnresolvedPlay_OutOfRangeMove_Throws(int from, int to)
    {
        var moves = new[] { new WireMove { From = from, To = to } };

        Assert.Throws<ArgumentException>(() => moves.ToUnresolvedPlay());
    }

    [Fact]
    public void ToGameState_WrongBoardLength_Throws()
    {
        var state = new WireGameState
        {
            Board = new int[25],
            CubeValue = 1,
            CubeOwner = WireCubeOwner.Centered,
            MatchLength = 7,
            YourScore = 0,
            OpponentScore = 0,
            IsCrawford = false,
        };

        Assert.Throws<ArgumentException>(() => state.ToGameState());
    }

    [Fact]
    public void ToGameState_CubeValueBelowOne_Throws()
    {
        var state = new WireGameState
        {
            Board = MidGameBoard(),
            CubeValue = 0,
            CubeOwner = WireCubeOwner.Centered,
            MatchLength = 7,
            YourScore = 0,
            OpponentScore = 0,
            IsCrawford = false,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => state.ToGameState());
    }
}
