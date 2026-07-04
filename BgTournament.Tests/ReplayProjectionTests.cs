using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.Api;
using BgTournament.Server;
using Microsoft.Extensions.Logging.Abstractions;
using ApiCubeOwner = BgTournament.Api.CubeOwner;
using GameResultKind = BgTournament.Api.GameResultKind;

namespace BgTournament.Tests;

/// <summary>
/// The frame walk, pinned against the substrate: matches are played by the
/// real <see cref="MatchRunner"/> on scripted dice, so the expected seat
/// attributions are hand-known from the runner's documented sequencing —
/// never re-derived with the projection's own rules — and the expected flips
/// come from the substrate's own <see cref="BoardState.FlippedCopy"/>.
/// </summary>
public class ReplayProjectionTests
{
    /// <summary>Scripted rolls first, a seeded stream after — deterministic throughout.</summary>
    private sealed class ScriptedThenSeededDice(int seed, params (int Die1, int Die2)[] script) : IDiceSource
    {
        private readonly Queue<(int, int)> _script = new(script);
        private readonly SeededDiceSource _fallback = new(seed);

        public (int Die1, int Die2) Roll() =>
            _script.Count > 0 ? _script.Dequeue() : _fallback.Roll();
    }

    /// <summary>Always picks the first legal play — fully deterministic, no RNG.</summary>
    private sealed class FirstPlayAgent : IPlayAgent
    {
        public ValueTask<Play> ChoosePlayAsync(
            GameState state, int die1, int die2, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MoveGenerator.GeneratePlays(state.Board, die1, die2)[0]);
    }

    /// <summary>Delegates both cube decisions to lambdas.</summary>
    private sealed class DelegateCubeAgent(
        Func<GameState, CubeAction> offer,
        Func<GameState, CubeAction> response) : ICubeAgent
    {
        public ValueTask<CubeAction> ChooseOfferAsync(
            GameState state, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(offer(state));

        public ValueTask<CubeAction> ChooseResponseAsync(
            GameState state, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(response(state));
    }

    private static DelegateCubeAgent NeverDoubles() =>
        new(_ => CubeAction.NoDouble, _ => CubeAction.Take);

    private static MatchParticipant Participant(ICubeAgent cube) =>
        new(new FirstPlayAgent(), cube);

    private static async Task<(MatchResult Result, MatchGamesResponse Response)> RunAndProjectAsync(
        int matchLength, IDiceSource dice, MatchParticipant seatOne, MatchParticipant seatTwo)
    {
        var runner = new MatchRunner(dice);
        var result = await runner.RunMatchAsync(seatOne, seatTwo, matchLength);
        return (result, Project(result, matchLength));
    }

    private static MatchGamesResponse Project(MatchResult result, int matchLength)
    {
        var record = new MatchRecord
        {
            MatchId = "match-under-test",
            EngineOne = "Alpha",
            EngineTwo = "Beta",
            MatchLength = matchLength,
            Seed = 0,
            Sequence = 1,
            Status = MatchStatus.Completed,
            Result = result,
            Live = new LiveMatch("match-under-test", NullLogger.Instance),
        };
        return record.ToGamesResponse();
    }

    /// <summary>Fold the projected games back into seat-keyed match scores.</summary>
    private static (int SeatOne, int SeatTwo) FoldScores(MatchGamesResponse response)
    {
        int one = 0, two = 0;
        foreach (var game in response.Games)
        {
            if (game.Winner == Seat.One)
            {
                one += game.Points;
            }
            else
            {
                two += game.Points;
            }
        }

        return (one, two);
    }

    private static void AssertReplayInvariants(MatchGamesResponse response, MatchResult result)
    {
        // The projected outcomes must reproduce the substrate's own
        // seat-keyed scores — game winners come from GameRecord, so this
        // cross-checks the projection against attribution it did not derive.
        Assert.Equal((result.SeatOneScore, result.SeatTwoScore), FoldScores(response));

        int expectedSeatOne = 0, expectedSeatTwo = 0;
        foreach (var game in response.Games)
        {
            // Entering scores chain game over game.
            Assert.Equal(expectedSeatOne, game.SeatOneScore);
            Assert.Equal(expectedSeatTwo, game.SeatTwoScore);
            if (game.Winner == Seat.One)
            {
                expectedSeatOne += game.Points;
            }
            else
            {
                expectedSeatTwo += game.Points;
            }

            Assert.NotEmpty(game.Entries);
            Assert.IsType<PlayEntry>(game.Entries[0]);

            // Play entries strictly alternate seats within a game.
            Seat? previous = null;
            foreach (var play in game.Entries.OfType<PlayEntry>())
            {
                if (previous is { } seat)
                {
                    Assert.NotEqual(seat, play.Actor);
                }

                previous = play.Actor;
            }

            foreach (var entry in game.Entries)
            {
                AssertPositionInvariants(entry.State);
            }

            AssertPositionInvariants(game.FinalState);
        }
    }

    private static void AssertPositionInvariants(GamePosition position)
    {
        Assert.Equal(26, position.Board.Count);
        Assert.True(position.Board.Where(p => p > 0).Sum() <= 15, "Seat One has at most 15 checkers on the board.");
        Assert.True(position.Board.Where(p => p < 0).Sum() >= -15, "Seat Two has at most 15 checkers on the board.");
        Assert.True(position.CubeValue >= 1);
    }

    [Fact]
    public async Task OpeningRollWinner_IsTheFirstActor_AndTheWholeReplayHolds()
    {
        // Opening (2, 5): Die2 is seat Two's die and it is higher, so the
        // runner seats seat Two on roll first — the hand-known expectation.
        var dice = new ScriptedThenSeededDice(seed: 1234, (2, 5));
        var (result, response) = await RunAndProjectAsync(
            matchLength: 1, dice, Participant(NeverDoubles()), Participant(NeverDoubles()));

        Assert.Equal("match-under-test", response.MatchId);
        Assert.Equal("Alpha", response.EngineOne);
        Assert.Equal("Beta", response.EngineTwo);
        Assert.Equal(1, response.MatchLength);

        var game = Assert.Single(response.Games);
        Assert.Equal(1, game.GameNumber);
        Assert.Equal(0, game.SeatOneScore);
        Assert.Equal(0, game.SeatTwoScore);

        var openingEntry = Assert.IsType<PlayEntry>(game.Entries[0]);
        Assert.Equal(Seat.Two, openingEntry.Actor);
        Assert.Equal(2, openingEntry.Die1);
        Assert.Equal(5, openingEntry.Die2);
        Assert.NotEmpty(openingEntry.Moves);

        // A 1-point match has no live cube: play entries only.
        Assert.All(game.Entries, entry => Assert.IsType<PlayEntry>(entry));

        AssertReplayInvariants(response, result);
    }

    [Fact]
    public async Task SeatTwoFramePositions_AreFlipped_SeatOneFramesServedVerbatim()
    {
        var dice = new ScriptedThenSeededDice(seed: 1234, (2, 5));
        var (result, response) = await RunAndProjectAsync(
            matchLength: 1, dice, Participant(NeverDoubles()), Participant(NeverDoubles()));

        var transcript = result.Games[0].Transcript.Entries;
        var entries = response.Games[0].Entries;

        // Entry 0 was recorded in seat Two's frame (it won the opening):
        // the served board must be the substrate's own flip of the recorded
        // one. Entry 1 is seat One's frame: served verbatim.
        var flipped = BoardState.FromMop(transcript[0].State.Board).FlippedCopy();
        Assert.Equal(flipped.Points, entries[0].State.Board);
        Assert.Equal(transcript[1].State.Board, entries[1].State.Board);

        // And the flip is frame-only — cube facts carry through.
        Assert.Equal(transcript[0].State.CubeSize, entries[0].State.CubeValue);
        Assert.Equal(ApiCubeOwner.Centered, entries[0].State.CubeOwner);
    }

    [Fact]
    public async Task CubePass_AttributesOfferAndResponse_EndsTheGame_AndTheNextGameRealigns()
    {
        // Game 1, fully scripted: opening (5, 2) seats seat One first; seat
        // Two plays (3, 1); seat One's next-turn window opens, it doubles,
        // seat Two passes — game over, 1 point to seat One, no more rolls.
        // Game 2 opens (1, 6): seat Two first — pins per-game realignment.
        // Game 2 is the Crawford game (leader at matchLength - 1), so the
        // doubler is never consulted and play runs seeded to the end.
        var dice = new ScriptedThenSeededDice(seed: 42, (5, 2), (3, 1), (1, 6));
        var seatOne = Participant(new DelegateCubeAgent(_ => CubeAction.Double, _ => CubeAction.Take));
        var seatTwo = Participant(new DelegateCubeAgent(_ => CubeAction.NoDouble, _ => CubeAction.Pass));
        var (result, response) = await RunAndProjectAsync(matchLength: 2, dice, seatOne, seatTwo);

        var gameOne = response.Games[0];
        Assert.Equal(4, gameOne.Entries.Count);
        var opening = Assert.IsType<PlayEntry>(gameOne.Entries[0]);
        Assert.Equal(Seat.One, opening.Actor);
        Assert.Equal(Seat.Two, Assert.IsType<PlayEntry>(gameOne.Entries[1]).Actor);

        var offer = Assert.IsType<CubeOfferEntry>(gameOne.Entries[2]);
        Assert.Equal(Seat.One, offer.Actor);
        Assert.Equal(1, offer.State.CubeValue);                       // pre-double
        Assert.Equal(ApiCubeOwner.Centered, offer.State.CubeOwner);

        var pass = Assert.IsType<CubeResponseEntry>(gameOne.Entries[3]);
        Assert.Equal(Seat.Two, pass.Actor);
        Assert.Equal(CubeResponseAction.Pass, pass.Action);

        // Offer and response are the same position — nothing moved between.
        Assert.Equal(offer.State.Board, pass.State.Board);
        Assert.Equal(offer.State.CubeValue, pass.State.CubeValue);
        Assert.Equal(offer.State.CubeOwner, pass.State.CubeOwner);

        Assert.Equal(Seat.One, gameOne.Winner);
        Assert.Equal(GameResultKind.Single, gameOne.ResultKind);
        Assert.Equal(1, gameOne.CubeValue);                           // scored at the pre-double stake
        Assert.Equal(1, gameOne.Points);
        Assert.False(gameOne.IsCrawford);

        var gameTwo = response.Games[1];
        Assert.Equal(2, gameTwo.GameNumber);
        Assert.True(gameTwo.IsCrawford);
        Assert.Equal(1, gameTwo.SeatOneScore);
        Assert.Equal(0, gameTwo.SeatTwoScore);
        var gameTwoOpening = Assert.IsType<PlayEntry>(gameTwo.Entries[0]);
        Assert.Equal(Seat.Two, gameTwoOpening.Actor);                 // (1, 6) — realigned, not carried over
        Assert.All(gameTwo.Entries, entry => Assert.IsType<PlayEntry>(entry));  // Crawford: no cube traffic

        AssertReplayInvariants(response, result);
    }

    [Fact]
    public async Task CubeTake_RaisesTheCube_AndSubsequentPositionsShowTheSeatKeyedOwner()
    {
        // As above through the offer, but seat Two takes: the cube goes to 2
        // owned by seat Two, and seat One (the offerer) rolls on.
        var dice = new ScriptedThenSeededDice(seed: 7, (5, 2), (3, 1));
        var seatOne = Participant(new DelegateCubeAgent(
            state => state.CubeSize == 1 ? CubeAction.Double : CubeAction.NoDouble,
            _ => CubeAction.Take));
        var seatTwo = Participant(new DelegateCubeAgent(_ => CubeAction.NoDouble, _ => CubeAction.Take));
        var (result, response) = await RunAndProjectAsync(matchLength: 2, dice, seatOne, seatTwo);

        // The doubled cube decides the whole match: 2 points ≥ matchLength.
        var game = Assert.Single(response.Games);

        var take = Assert.IsType<CubeResponseEntry>(game.Entries[3]);
        Assert.Equal(Seat.Two, take.Actor);
        Assert.Equal(CubeResponseAction.Take, take.Action);

        var afterTake = Assert.IsType<PlayEntry>(game.Entries[4]);
        Assert.Equal(Seat.One, afterTake.Actor);                      // the offerer rolls after a take
        Assert.Equal(2, afterTake.State.CubeValue);
        Assert.Equal(ApiCubeOwner.SeatTwo, afterTake.State.CubeOwner);

        Assert.Equal(2, game.CubeValue);
        Assert.Equal(game.Points, game.CubeValue * (int)ToMultiplier(game.ResultKind));

        AssertReplayInvariants(response, result);
    }

    private static int ToMultiplier(GameResultKind kind) => kind switch
    {
        GameResultKind.Single => 1,
        GameResultKind.Gammon => 2,
        _ => 3,
    };

    [Fact]
    public void Dance_ProjectsAsAPlayEntryWithEmptyMoves_AndGameEndBecomesFinalState()
    {
        // Synthetic single game: the projection only reads entry kinds, the
        // opening dice, and each snapshot — cross-entry board continuity is
        // not its concern, so identical snapshots keep the fixture small.
        var match = MatchState.FromScores(matchLength: 3, onRollScore: 0, opponentScore: 0, isCrawford: false);
        var snapshot = GameState.NewGame(match).Snapshot();
        var openingPlay = MoveGenerator.GeneratePlays(BoardState.Standard(), 5, 2)[0];

        var transcript = new Transcript();
        transcript.Append(new PlayTranscriptEntry(snapshot, MatchSeat.One, 5, 2, openingPlay));  // seat One opens
        transcript.Append(new PlayTranscriptEntry(snapshot, MatchSeat.Two, 6, 6, default));      // seat Two dances
        transcript.Append(new GameEndedTranscriptEntry(
            snapshot, MatchSeat.One, new GameResult(BgGame_Lib.GameResultKind.WinSingle, OnRollWon: true, CubeSize: 1)));

        var result = new MatchResult(
            Winner: null, SeatOneScore: 1, SeatTwoScore: 0,
            [new GameRecord(MatchSeat.One, new GameResult(BgGame_Lib.GameResultKind.WinSingle, true, 1), transcript)]);

        var response = Project(result, matchLength: 3);
        var game = Assert.Single(response.Games);

        // The game-end transcript entry is not an entry in the contract.
        Assert.Equal(2, game.Entries.Count);

        var openingEntry = Assert.IsType<PlayEntry>(game.Entries[0]);
        Assert.Equal(Seat.One, openingEntry.Actor);
        Assert.NotEmpty(openingEntry.Moves);

        var dance = Assert.IsType<PlayEntry>(game.Entries[1]);
        Assert.Equal(Seat.Two, dance.Actor);
        Assert.Equal(6, dance.Die1);
        Assert.Equal(6, dance.Die2);
        Assert.Empty(dance.Moves);

        Assert.Equal(Seat.One, game.Winner);
        Assert.Equal(26, game.FinalState.Board.Count);
    }
}
