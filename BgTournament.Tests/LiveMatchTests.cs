using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.Api;
using BgTournament.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace BgTournament.Tests;

/// <summary>
/// The live cache/broadcast core, driven directly through its
/// <see cref="IMatchObserver"/> surface so the observer sequence is exact and
/// deterministic — no match timing. Pins the join-in-progress snapshot
/// (including late joins), per-move increments, multi-subscriber fan-out, the
/// single terminal path, forfeit-time retention of completed games, and the
/// loud fault behavior when a projection throws.
/// </summary>
public class LiveMatchTests
{
    private static LiveMatch NewLiveMatch() => new("match-under-test", NullLogger.Instance);

    private static GameSnapshot NewSnapshot(int matchLength = 3, bool crawford = false) =>
        GameState.NewGame(MatchState.FromScores(matchLength, 0, 0, crawford)).Snapshot();

    private static Play FirstPlay(int die1, int die2) =>
        MoveGenerator.GeneratePlays(BoardState.Standard(), die1, die2)[0];

    private static PlayTranscriptEntry PlayEntry(MatchSeat seat, int die1, int die2) =>
        new(NewSnapshot(), seat, die1, die2, FirstPlay(die1, die2));

    /// <summary>The frame-free game-start context the substrate hands the observer.</summary>
    private static GameStartContext Started(
        int gameNumber, int seatOne = 0, int seatTwo = 0, bool crawford = false) =>
        new(gameNumber, seatOne, seatTwo, crawford);

    /// <summary>A minimal completed game won by <paramref name="winner"/> for <paramref name="points"/>.</summary>
    private static GameRecord CompletedGame(MatchSeat winner, int points)
    {
        var snapshot = NewSnapshot();
        var result = new GameResult(BgGame_Lib.GameResultKind.WinSingle, OnRollWon: true, CubeSize: points);
        var transcript = new Transcript();
        transcript.Append(new PlayTranscriptEntry(snapshot, winner, 5, 2, FirstPlay(5, 2)));
        transcript.Append(new GameEndedTranscriptEntry(snapshot, winner, result));
        return new GameRecord(winner, result, transcript);
    }

    private static async Task<LiveMatchEvent> NextAsync(LiveSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await subscription.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Assert the stream is completed and drained — a subscriber never left hanging.</summary>
    private static async Task AssertClosedAsync(LiveSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.False(await subscription.Reader.WaitToReadAsync(timeout.Token));
    }

    [Fact]
    public async Task Snapshot_IsFirst_AndReflectsEntriesRecordedBeforeTheJoin()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));
        live.OnEntryRecorded(PlayEntry(MatchSeat.One, 6, 3));
        live.OnEntryRecorded(PlayEntry(MatchSeat.Two, 4, 2));

        // A late joiner's first event is a snapshot carrying the moves already
        // made — the join-in-progress correctness pin.
        var subscription = live.Subscribe();
        var snapshot = Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));

        Assert.Equal(1, snapshot.GameNumber);
        Assert.Equal(0, snapshot.SeatOneScore);
        Assert.Equal(0, snapshot.SeatTwoScore);
        Assert.Equal(2, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, entry => Assert.IsType<PlayEntry>(entry));
    }

    [Fact]
    public async Task Subscribe_ThenRecord_DeliversTheEmptySnapshotThenTheIncrement()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));

        var subscription = live.Subscribe();
        var snapshot = Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));
        Assert.Empty(snapshot.Entries);

        live.OnEntryRecorded(PlayEntry(MatchSeat.One, 3, 1));
        var increment = Assert.IsType<LiveEntryEvent>(await NextAsync(subscription));
        Assert.IsType<PlayEntry>(increment.Entry);
    }

    [Fact]
    public async Task EverySubscriber_ReceivesEveryIncrement()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));

        var first = live.Subscribe();
        var second = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(first));
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(second));

        live.OnEntryRecorded(PlayEntry(MatchSeat.One, 3, 1));

        Assert.IsType<LiveEntryEvent>(await NextAsync(first));
        Assert.IsType<LiveEntryEvent>(await NextAsync(second));
    }

    [Fact]
    public async Task GameEnded_EmitsTheCompletedGame_AndTheNextGameStartsAtTheUpdatedScore()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));
        live.OnEntryRecorded(PlayEntry(MatchSeat.One, 5, 2));

        var subscription = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));

        live.OnGameEnded(1, CompletedGame(MatchSeat.One, points: 1));
        var ended = Assert.IsType<LiveGameEndedEvent>(await NextAsync(subscription));
        Assert.Equal(1, ended.Game.GameNumber);
        Assert.Equal(Seat.One, ended.Game.Winner);

        // The substrate carries the entering scores: the next game starts 1–0.
        live.OnGameStarted(Started(2, seatOne: 1, seatTwo: 0));
        var started = Assert.IsType<LiveGameStartedEvent>(await NextAsync(subscription));
        Assert.Equal(2, started.GameNumber);
        Assert.Equal(1, started.SeatOneScore);
        Assert.Equal(0, started.SeatTwoScore);
        Assert.False(started.IsCrawford);
    }

    [Fact]
    public async Task Crawford_FlagRidesGameStartedEvent_AndTheLateJoinSnapshot()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));                              // pre-Crawford
        var subscription = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));

        // The Crawford game starts at 2–0; the flag rides the live event...
        live.OnGameStarted(Started(2, seatOne: 2, seatTwo: 0, crawford: true));
        var started = Assert.IsType<LiveGameStartedEvent>(await NextAsync(subscription));
        Assert.True(started.IsCrawford);

        // ...and a late joiner's snapshot reports the current (Crawford) game.
        var lateJoin = live.Subscribe();
        var snapshot = Assert.IsType<LiveSnapshotEvent>(await NextAsync(lateJoin));
        Assert.True(snapshot.IsCrawford);
        Assert.Equal(2, snapshot.GameNumber);

        // The post-Crawford game clears the flag back off the wire.
        live.OnGameStarted(Started(3, seatOne: 2, seatTwo: 1, crawford: false));
        var post = Assert.IsType<LiveGameStartedEvent>(await NextAsync(subscription));
        Assert.False(post.IsCrawford);
    }

    [Fact]
    public async Task MarkTerminal_EmitsTheTerminalEvent_ThenClosesTheStream()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));
        var subscription = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));

        var summary = new MatchSummary(
            "match-under-test", "Alpha", "Beta", MatchLength: 1, MaxGames: null, Seed: 7,
            TimeControl: null,
            MatchStatus.Completed, Winner: "Alpha", SeatOneScore: 1, SeatTwoScore: 0,
            ForfeitedBy: null, Detail: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch, EndedAtUtc: DateTimeOffset.UnixEpoch);
        live.MarkTerminal(summary);

        var terminal = Assert.IsType<LiveTerminalEvent>(await NextAsync(subscription));
        Assert.Equal(MatchStatus.Completed, terminal.Match.Status);
        await AssertClosedAsync(subscription);
    }

    [Fact]
    public async Task AlreadyTerminal_Join_GetsSnapshotThenTerminalThenClose_OneCodePath()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));
        live.OnGameEnded(1, CompletedGame(MatchSeat.Two, points: 1));
        var summary = new MatchSummary(
            "match-under-test", "Alpha", "Beta", MatchLength: 1, MaxGames: null, Seed: 7,
            TimeControl: null,
            MatchStatus.Completed, Winner: "Beta", SeatOneScore: 0, SeatTwoScore: 1,
            ForfeitedBy: null, Detail: null,
            StartedAtUtc: DateTimeOffset.UnixEpoch, EndedAtUtc: DateTimeOffset.UnixEpoch);
        live.MarkTerminal(summary);

        // Joining after the match is over follows the same snapshot→terminal→close path.
        var subscription = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));
        var terminal = Assert.IsType<LiveTerminalEvent>(await NextAsync(subscription));
        Assert.Equal("Beta", terminal.Match.Winner);
        await AssertClosedAsync(subscription);
    }

    [Fact]
    public async Task ForfeitMidGame_RetainsFinishedGames_AndSnapshotShowsTheInterruptedGame()
    {
        var live = NewLiveMatch();

        // Game 1 completes; game 2 is interrupted mid-play (no OnGameEnded).
        live.OnGameStarted(Started(1));
        live.OnGameEnded(1, CompletedGame(MatchSeat.One, points: 1));
        live.OnGameStarted(Started(2, seatOne: 1, seatTwo: 0));
        live.OnEntryRecorded(PlayEntry(MatchSeat.Two, 6, 4));

        // The one finished game is retained for partial replay.
        Assert.Single(live.CompletedGames);

        // A joiner sees the interrupted game's partial entries.
        var subscription = live.Subscribe();
        var snapshot = Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));
        Assert.Equal(2, snapshot.GameNumber);
        Assert.Equal(1, snapshot.SeatOneScore);
        Assert.Single(snapshot.Entries);

        // The host emits the terminal (forfeit) — the stream ends cleanly.
        var summary = new MatchSummary(
            "match-under-test", "Alpha", "Beta", MatchLength: 3, MaxGames: null, Seed: 7,
            TimeControl: null,
            MatchStatus.Forfeited, Winner: "Alpha", SeatOneScore: null, SeatTwoScore: null,
            ForfeitedBy: "Beta", Detail: "Engine 'Beta' disconnected mid-match.",
            StartedAtUtc: DateTimeOffset.UnixEpoch, EndedAtUtc: DateTimeOffset.UnixEpoch);
        live.MarkTerminal(summary);
        Assert.IsType<LiveTerminalEvent>(await NextAsync(subscription));
        await AssertClosedAsync(subscription);
    }

    /// <summary>A transcript entry the projection cannot handle — forces a callback fault.</summary>
    private sealed record BogusEntry(GameSnapshot State, MatchSeat OnRollSeat)
        : TranscriptEntry(State, OnRollSeat);

    [Fact]
    public async Task ProjectionFault_FailsLoudly_ClosingSubscriberStreamsWithNoTerminal()
    {
        var live = NewLiveMatch();
        live.OnGameStarted(Started(1));
        var subscription = live.Subscribe();
        Assert.IsType<LiveSnapshotEvent>(await NextAsync(subscription));

        // A projection bug must never leave the feed quietly stale: the callback
        // is contained (the match would play on) and the stream is closed — a
        // loud EOF with no terminal event, distinct from a clean finish.
        live.OnEntryRecorded(new BogusEntry(NewSnapshot(), MatchSeat.One));
        await AssertClosedAsync(subscription);
    }
}
