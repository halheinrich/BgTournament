using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Pins the wire ↔ substrate mapping: field-for-field preservation in both
/// directions; the score's two kinds (needs for a match, running scores for
/// money) and the refusal of a state mixing them; and — the load-bearing pin —
/// that the mapping is frame-preserving: every state is in the on-roll
/// player's frame, a cube response included, with no re-expression anywhere
/// between the substrate and the wire (halheinrich/backgammon#533).
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

    /// <summary>A well-formed match state, in needs, to vary one field at a time.</summary>
    private static WireGameState MatchWire(int onRollNeeds = 7, int opponentNeeds = 7) => new()
    {
        Board = MidGameBoard(),
        CubeValue = 1,
        CubeOwner = WireCubeOwner.Centered,
        MatchLength = 7,
        OnRollNeeds = onRollNeeds,
        OpponentNeeds = opponentNeeds,
        IsCrawford = false,
    };

    /// <summary>A well-formed money state, in running scores, to vary one field at a time.</summary>
    private static WireGameState MoneyWire() => new()
    {
        Board = MidGameBoard(),
        CubeValue = 2,
        CubeOwner = WireCubeOwner.Opponent,
        MatchLength = 0,
        OnRollScore = 5,
        OpponentScore = 8,
        IsCrawford = false,
    };

    [Fact]
    public void ToWireState_MapsEveryField_AMatchScoreAsNeeds()
    {
        var state = MidGameState(cubeSize: 4, CubeOwner.OnRoll, onRollScore: 2, opponentScore: 3);

        var wire = state.Snapshot().ToWireState();

        Assert.Equal(MidGameBoard(), wire.Board);
        Assert.Equal(4, wire.CubeValue);
        Assert.Equal(WireCubeOwner.OnRoll, wire.CubeOwner);
        Assert.Equal(7, wire.MatchLength);
        Assert.Equal(5, wire.OnRollNeeds);
        Assert.Equal(4, wire.OpponentNeeds);
        Assert.Null(wire.OnRollScore);
        Assert.Null(wire.OpponentScore);
        Assert.False(wire.IsCrawford);
        Assert.Null(wire.Xgid);
    }

    [Fact]
    public void ToWireState_Money_CarriesRunningScores_NoNeeds()
    {
        var state = GameState.FromPosition(
            MatchState.FromScores(0, 5, 8, isCrawford: false),
            BoardState.FromMop(MidGameBoard()),
            cubeSize: 1,
            CubeOwner.Centered);

        var wire = state.Snapshot().ToWireState();

        Assert.Equal(0, wire.MatchLength);
        Assert.Equal(5, wire.OnRollScore);
        Assert.Equal(8, wire.OpponentScore);
        Assert.Null(wire.OnRollNeeds);
        Assert.Null(wire.OpponentNeeds);
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
        Assert.Equal((1, 4), (wire.OnRollNeeds, wire.OpponentNeeds));
    }

    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(7, 2, 2)]
    public void ToWireState_EquivalentScores_ArriveAsTheSameNeeds(int matchLength, int onRoll, int opponent)
    {
        // 0–0 in a 5-point match and 2–2 in a 7-point match: 5-away each.
        var state = GameState.FromPosition(
            MatchState.FromScores(matchLength, onRoll, opponent, isCrawford: false),
            BoardState.FromMop(MidGameBoard()),
            cubeSize: 1,
            CubeOwner.Centered);

        var wire = state.Snapshot().ToWireState();

        Assert.Equal((5, 5), (wire.OnRollNeeds, wire.OpponentNeeds));
    }

    [Fact]
    public void ToGameSnapshot_MatchState_RoundTrips_NeedsBecomeScores()
    {
        var original = MatchWire(onRollNeeds: 4, opponentNeeds: 5) with { CubeValue = 2, CubeOwner = WireCubeOwner.OnRoll };

        var snapshot = original.ToGameSnapshot();
        var wire = snapshot.ToWireState();

        Assert.Equal(new MatchSnapshot(7, OnRollScore: 3, OpponentScore: 2, IsCrawford: false), snapshot.Match);
        Assert.Equal(original.Board, wire.Board);
        Assert.Equal(original.CubeValue, wire.CubeValue);
        Assert.Equal(original.CubeOwner, wire.CubeOwner);
        Assert.Equal(original.MatchLength, wire.MatchLength);
        Assert.Equal((4, 5), (wire.OnRollNeeds, wire.OpponentNeeds));
        Assert.Null(wire.OnRollScore);
        Assert.Null(wire.OpponentScore);
        Assert.Equal(original.IsCrawford, wire.IsCrawford);
    }

    [Fact]
    public void ToGameSnapshot_MoneyState_RoundTrips_InScores()
    {
        var original = MoneyWire();

        var snapshot = original.ToGameSnapshot();
        var wire = snapshot.ToWireState();

        Assert.Equal(new MatchSnapshot(0, OnRollScore: 5, OpponentScore: 8, IsCrawford: false), snapshot.Match);
        Assert.Equal(original.Board, wire.Board);
        Assert.Equal(original.CubeValue, wire.CubeValue);
        Assert.Equal(original.CubeOwner, wire.CubeOwner);
        Assert.Equal((5, 8), (wire.OnRollScore, wire.OpponentScore));
        Assert.Null(wire.OnRollNeeds);
        Assert.Null(wire.OpponentNeeds);
    }

    [Fact]
    public void ToGameSnapshot_IsTheValidatedStatesSnapshot()
    {
        var wire = MatchWire(onRollNeeds: 4, opponentNeeds: 5) with { CubeValue = 2, CubeOwner = WireCubeOwner.OnRoll };

        var expected = GameState.FromPosition(
            MatchState.FromScores(7, 3, 2, isCrawford: false),
            BoardState.FromMop(MidGameBoard()),
            cubeSize: 2,
            CubeOwner.OnRoll).Snapshot();

        Assert.Equal(expected, wire.ToGameSnapshot());
    }

    /// <summary>
    /// The frame pass-through pin, inverted by Hal's ruling
    /// (halheinrich/backgammon#533). The runner hands a cube responder the
    /// offer's own snapshot — the offerer's (on-roll) frame, here a redouble
    /// with the offerer owning the cube — and the wire carries exactly that:
    /// the board unchanged, the offerer's needs in the on-roll fields, the
    /// pre-double cube "onRoll". If a flip ever creeps into the mapping or
    /// upstream of it, the board or labels here flip and this test fails.
    /// </summary>
    [Fact]
    public void ToWireState_OfAResponseQuery_IsTheOnRollFrame_PreDoubleCubeOnRolls()
    {
        var offer = MidGameState(cubeSize: 2, CubeOwner.OnRoll, onRollScore: 3, opponentScore: 2).Snapshot();

        var wire = offer.ToWireState();

        // The board is the offerer's, unchanged: the responder is the negative side.
        Assert.Equal(MidGameBoard(), wire.Board);

        // The doubler at 3 of 7 needs 4; the responder at 2 needs 5.
        Assert.Equal(4, wire.OnRollNeeds);
        Assert.Equal(5, wire.OpponentNeeds);

        // The offerer-owned (redouble) cube reads "onRoll", at its pre-double value.
        Assert.Equal(WireCubeOwner.OnRoll, wire.CubeOwner);
        Assert.Equal(2, wire.CubeValue);
    }

    [Fact]
    public void ToGameSnapshot_OfAFirstDoubleResponse_KeepsTheCubeCentered()
    {
        var wire = MatchWire(onRollNeeds: 4, opponentNeeds: 5);

        var snapshot = wire.ToGameSnapshot();

        Assert.Equal(CubeOwner.Centered, snapshot.CubeOwner);
        Assert.Equal(1, snapshot.CubeSize);
        Assert.Equal(BoardState.FromMop(MidGameBoard()).ToPosition(), snapshot.Board);
    }

    [Fact]
    public void ToWireMoves_StripsHitEncoding_AndKeepsBearOff()
    {
        var play = Play.Create(
            new Move(24, -21), // hit on 21, sign-encoded in the substrate
            new Move(6, 0));   // bear off

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
    public void ToHops_ReadsEachMoveAsItsHop_InTheOrderSent()
    {
        var moves = new[]
        {
            new WireMove { From = 25, To = 22 },
            new WireMove { From = 13, To = 10 },
            new WireMove { From = 6, To = 0 },
        };

        var hops = moves.ToHops();

        Assert.Equal([new Hop(25, 22), new Hop(13, 10), new Hop(6, 0)], hops);
    }

    [Fact]
    public void ToHops_Empty_IsNoHops()
    {
        Assert.Empty(Array.Empty<WireMove>().ToHops());
    }

    [Fact]
    public void ToHops_CountsNothing_FiveMovesAreFiveHops()
    {
        // How many moves a play has is the resolver's rule, not the mapping's:
        // five hops read fine here and resolve to no play there.
        var moves = Enumerable.Repeat(new WireMove { From = 6, To = 5 }, 5).ToArray();

        Assert.Equal(5, moves.ToHops().Count);
    }

    [Theory]
    [InlineData(0, 5)]   // from below range
    [InlineData(26, 5)]  // from above range
    [InlineData(6, -1)]  // to below range (a hit mark is not wire-legal)
    [InlineData(6, 25)]  // to above range
    public void ToHops_OutOfRangeMove_IsRefusedByTheHop(int from, int to)
    {
        var moves = new[] { new WireMove { From = from, To = to } };

        Assert.Throws<ArgumentOutOfRangeException>(() => moves.ToHops());
    }

    [Fact]
    public void ToHops_NullMove_Throws()
    {
        var moves = new WireMove[] { new() { From = 6, To = 5 }, null! };

        Assert.Throws<ArgumentException>(() => moves.ToHops());
    }

    [Fact]
    public void ToGameSnapshot_WrongBoardLength_Throws()
    {
        var state = MatchWire() with { Board = new int[25] };

        Assert.Throws<ArgumentException>(() => state.ToGameSnapshot());
    }

    [Fact]
    public void ToGameSnapshot_CubeValueBelowOne_Throws()
    {
        var state = MatchWire() with { CubeValue = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => state.ToGameSnapshot());
    }

    [Fact]
    public void ToGameSnapshot_InconsistentCrawford_IsRefusedByTheSubstrate()
    {
        // Crawford with neither side one-away: MatchState.FromScores's refusal.
        var state = MatchWire() with { IsCrawford = true };

        Assert.Throws<ArgumentException>(() => state.ToGameSnapshot());
    }

    [Theory]
    [InlineData(0, 7)]   // 0-away: a score at the length, a finished match
    [InlineData(7, 0)]
    [InlineData(8, 7)]   // more than the length: a negative score
    [InlineData(7, 8)]
    public void ToGameSnapshot_NeedsOutsideOneToLength_AreRefusedByTheSubstrate(int onRollNeeds, int opponentNeeds)
    {
        var state = MatchWire(onRollNeeds, opponentNeeds);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.ToGameSnapshot());
    }

    public static TheoryData<string, WireGameState> MixedKinds => new()
    {
        { "a match state with an on-roll score", MatchWire() with { OnRollScore = 0 } },
        { "a match state with an opponent score", MatchWire() with { OpponentScore = 0 } },
        { "a match state in scores, not needs", MatchWire() with { OnRollNeeds = null, OpponentNeeds = null, OnRollScore = 0, OpponentScore = 0 } },
        { "a match state missing its on-roll needs", MatchWire() with { OnRollNeeds = null } },
        { "a match state missing its opponent needs", MatchWire() with { OpponentNeeds = null } },
        { "a money state with on-roll needs", MoneyWire() with { OnRollNeeds = 1 } },
        { "a money state with opponent needs", MoneyWire() with { OpponentNeeds = 1 } },
        { "a money state in needs, not scores", MoneyWire() with { OnRollScore = null, OpponentScore = null, OnRollNeeds = 1, OpponentNeeds = 1 } },
        { "a money state missing its on-roll score", MoneyWire() with { OnRollScore = null } },
        { "a money state missing its opponent score", MoneyWire() with { OpponentScore = null } },
    };

    [Theory]
    [MemberData(nameof(MixedKinds))]
    public void ToGameSnapshot_AStateWhoseScoreFieldsAreNotItsKind_IsRefused(string label, WireGameState state)
    {
        _ = label;
        var ex = Assert.Throws<ArgumentException>(() => state.ToGameSnapshot());
        Assert.Equal("state", ex.ParamName);
    }
}
