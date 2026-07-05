using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace BgTournament.Tests;

/// <summary>
/// Time controls end to end over the real wire: the matchStarted announcement,
/// both pools riding every query, the Fischer debit/credit arithmetic visible
/// across consecutive queries, flag fall forfeiting a match (and folding as an
/// ordinary win inside a tournament), the flat regime staying clock-free, and
/// request validation. Deterministic throughout — the server's clock runs on a
/// <see cref="FakeTimeProvider"/>, so "thinking time" is advanced explicitly,
/// never slept.
/// </summary>
public class TimeControlWireTests
{
    private static readonly TimeControl Control = new(initialSeconds: 60, incrementSeconds: 5);

    /// <summary>The next decision query of any type, skipping matchStarted.</summary>
    private static async Task<QueryMessage> NextQueryAsync(TestEngine engine)
    {
        while (true)
        {
            var message = await engine.ReceiveAsync();
            Assert.NotNull(message);
            if (message is QueryMessage query)
            {
                return query;
            }

            Assert.IsType<MatchStartedMessage>(message); // anything else is off-script
        }
    }

    [Fact]
    public async Task FlagFall_ForfeitsTheMatch_OverTheWire()
    {
        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        var match = await ServerHarness.StartMatchAsync(
            factory, "Alpha", "Beta", matchLength: 7, seed, timeControl: Control);
        string matchId = match.GetProperty("matchId").GetString()!;

        // Both seats learn the control up front, on matchStarted.
        foreach (var engine in new[] { alpha, beta })
        {
            var started = await engine.ExpectAsync<MatchStartedMessage>();
            Assert.NotNull(started.TimeControl);
            Assert.Equal(60, started.TimeControl!.InitialSeconds);
            Assert.Equal(5, started.TimeControl.IncrementSeconds);
        }

        // Seat One is queried first (seed chosen for that) with full pools.
        var query = await NextQueryAsync(alpha);
        Assert.Equal(60, query.YourTimeRemainingSeconds);
        Assert.Equal(60, query.OpponentTimeRemainingSeconds);

        // Alpha never answers; its pool runs dry. The flag is the server's
        // measurement — no engine cooperation involved.
        time.Advance(TimeSpan.FromSeconds(61));

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Alpha", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Beta", record.GetProperty("winner").GetString());
        Assert.Contains("flag fall", record.GetProperty("detail").GetString());

        // The innocent side is told, wire-style: a forfeit by the opponent.
        var ended = await beta.ExpectAsync<MatchEndedMessage>();
        Assert.Equal(MatchEndReason.Forfeit, ended.Reason);
        Assert.Equal(ForfeitSide.Opponent, ended.ForfeitedBy);
    }

    /// <summary>
    /// The Fischer arithmetic, pinned through the wire across four consecutive
    /// decisions: debit is the server-measured decision time, the increment is
    /// credited per answered decision (cube decisions included), and each query
    /// shows both pools as of issuance.
    /// </summary>
    [Fact]
    public async Task ClockArithmetic_IsVisibleAcrossConsecutiveQueries()
    {
        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        await ServerHarness.StartMatchAsync(
            factory, "Alpha", "Beta", matchLength: 7, seed, timeControl: Control);

        // Turn 1 — Alpha's opening play (no cube offer before the opening).
        // Answered instantly: 60 − 0 + 5 = 65 banked.
        var alphaPlay = Assert.IsType<PlayQueryMessage>(await NextQueryAsync(alpha));
        Assert.Equal(60, alphaPlay.YourTimeRemainingSeconds);
        Assert.Equal(60, alphaPlay.OpponentTimeRemainingSeconds);
        await alpha.ReplyWithFirstLegalPlayAsync(alphaPlay);

        // Turn 2 — Beta's cube offer: Beta still at 60, Alpha's credit visible.
        var betaOffer = Assert.IsType<CubeOfferQueryMessage>(await NextQueryAsync(beta));
        Assert.Equal(60, betaOffer.YourTimeRemainingSeconds);
        Assert.Equal(65, betaOffer.OpponentTimeRemainingSeconds);

        // Beta thinks 10 s on the cube: 60 − 10 + 5 = 55.
        time.Advance(TimeSpan.FromSeconds(10));
        await beta.SendAsync(new CubeOfferReplyMessage
        {
            RequestId = betaOffer.RequestId,
            Action = CubeOfferAction.NoDouble,
        });

        // Beta's play query shows its own debit; Alpha unchanged.
        var betaPlay = Assert.IsType<PlayQueryMessage>(await NextQueryAsync(beta));
        Assert.Equal(55, betaPlay.YourTimeRemainingSeconds);
        Assert.Equal(65, betaPlay.OpponentTimeRemainingSeconds);
        await beta.ReplyWithFirstLegalPlayAsync(betaPlay);

        // Turn 3 — Alpha's cube offer sees both updated pools, from its frame.
        var alphaOffer = Assert.IsType<CubeOfferQueryMessage>(await NextQueryAsync(alpha));
        Assert.Equal(65, alphaOffer.YourTimeRemainingSeconds);
        Assert.Equal(60, alphaOffer.OpponentTimeRemainingSeconds); // 55 + 5 after Beta's instant play
    }

    [Fact]
    public async Task NoTimeControl_FlatRegime_CarriesNoClockFields()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 7, seed);

        var started = await alpha.ExpectAsync<MatchStartedMessage>();
        Assert.Null(started.TimeControl);

        var query = await NextQueryAsync(alpha);
        Assert.Null(query.YourTimeRemainingSeconds);
        Assert.Null(query.OpponentTimeRemainingSeconds);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(60, -1)]
    public async Task InvalidTimeControl_IsRejectedAsABadRequest(
        double initialSeconds, double incrementSeconds)
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync("/matches", new
        {
            engineOne = "Alpha",
            engineTwo = "Beta",
            matchLength = 7,
            seed = 1,
            timeControl = new { initialSeconds, incrementSeconds },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A tournament-level control governs every scheduled match, and a flag
    /// fall folds like any forfeit: a win for the non-offender.
    /// </summary>
    [Fact]
    public async Task TournamentTimeControl_FlagFall_FoldsAsAWin()
    {
        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var tournament = await ServerHarness.StartTournamentAsync(
            factory, ["Alpha", "Beta"], matchLength: 1, matchesPerPairing: 1, seed: 7,
            timeControl: Control);
        string tournamentId = tournament.GetProperty("tournamentId").GetString()!;

        // Whoever wins the opening is queried first (1-pointers are cubeless,
        // so the first query is a play query); derive the first mover from the
        // scheduled seed via the substrate's own dice source.
        var scheduled = tournament.GetProperty("matches")[0];
        var (firstMover, other) =
            SeatOneMovesFirst(scheduled.GetProperty("seed").GetInt32())
                ? (scheduled.GetProperty("seatOne").GetString(), scheduled.GetProperty("seatTwo").GetString())
                : (scheduled.GetProperty("seatTwo").GetString(), scheduled.GetProperty("seatOne").GetString());
        var flagged = firstMover == "Alpha" ? alpha : beta;

        var query = await flagged.ExpectPlayQueryAsync();
        Assert.Equal(60, query.YourTimeRemainingSeconds);

        time.Advance(TimeSpan.FromSeconds(61));

        var record = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId);
        Assert.Equal("completed", record.GetProperty("status").GetString());
        Assert.Equal(other, record.GetProperty("winner").GetString());
    }

    /// <summary>Mirrors <see cref="ServerHarness.SeedWhereSeatOneMovesFirst"/> for a given seed.</summary>
    private static bool SeatOneMovesFirst(int seed)
    {
        var dice = new SeededDiceSource(seed);
        (int die1, int die2) = dice.Roll();
        while (die1 == die2)
        {
            (die1, die2) = dice.Roll();
        }

        return die1 > die2;
    }
}
