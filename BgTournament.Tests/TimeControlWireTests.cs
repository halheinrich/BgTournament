using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.EngineClient;
using BgTournament.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
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
///
/// <para>The hosted-SDK region proves the clock crosses the last mile: an
/// <c>EngineClient</c>-hosted clock-aware agent observes the announced control
/// and the per-query pools, and a flat-regime match surfaces the absence
/// honestly (null announcement, no readings — never a fabricated value).</para>
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

    // ---- SDK clock surfacing: the hosted-agent side of the same wire ----

    private static readonly TimeSpan HostedDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A dual-role clock-aware recording agent: play delegates to
    /// <see cref="RandomPlayAgent"/>, the cube to <see cref="PassiveCubeAgent"/>,
    /// while every announcement and clock reading the SDK delivers — and every
    /// plain-overload call — is recorded for assertion.
    /// </summary>
    private sealed class ClockRecordingAgent : IClockAwarePlayAgent, IClockAwareCubeAgent
    {
        private readonly RandomPlayAgent _play = new(seed: 11);
        private readonly PassiveCubeAgent _cube = new();

        public List<MatchTimeControl?> Announcements { get; } = new();
        public List<ClockReading> PlayReadings { get; } = new();
        public List<ClockReading> OfferReadings { get; } = new();
        public List<ClockReading> ResponseReadings { get; } = new();
        public int PlainPlayCalls { get; private set; }
        public int PlainOfferCalls { get; private set; }
        public int PlainResponseCalls { get; private set; }

        public IEnumerable<ClockReading> AllReadings =>
            PlayReadings.Concat(OfferReadings).Concat(ResponseReadings);

        public void OnTimeControlAnnounced(MatchTimeControl? timeControl) =>
            Announcements.Add(timeControl);

        public ValueTask<Play> ChoosePlayAsync(
            GameState state, int die1, int die2, ClockReading clock, CancellationToken cancellationToken = default)
        {
            PlayReadings.Add(clock);
            return _play.ChoosePlayAsync(state, die1, die2, cancellationToken);
        }

        public ValueTask<Play> ChoosePlayAsync(
            GameState state, int die1, int die2, CancellationToken cancellationToken = default)
        {
            PlainPlayCalls++;
            return _play.ChoosePlayAsync(state, die1, die2, cancellationToken);
        }

        public ValueTask<CubeAction> ChooseOfferAsync(
            GameState state, ClockReading clock, CancellationToken cancellationToken = default)
        {
            OfferReadings.Add(clock);
            return _cube.ChooseOfferAsync(state, cancellationToken);
        }

        public ValueTask<CubeAction> ChooseOfferAsync(
            GameState state, CancellationToken cancellationToken = default)
        {
            PlainOfferCalls++;
            return _cube.ChooseOfferAsync(state, cancellationToken);
        }

        public ValueTask<CubeAction> ChooseResponseAsync(
            GameState state, ClockReading clock, CancellationToken cancellationToken = default)
        {
            ResponseReadings.Add(clock);
            return _cube.ChooseResponseAsync(state, cancellationToken);
        }

        public ValueTask<CubeAction> ChooseResponseAsync(
            GameState state, CancellationToken cancellationToken = default)
        {
            PlainResponseCalls++;
            return _cube.ChooseResponseAsync(state, cancellationToken);
        }
    }

    /// <summary>
    /// Connect a hosted EngineClient serving <paramref name="agent"/> in both
    /// roles (the SDK door, not raw wire) — connect awaited here so a failure
    /// surfaces in the test, then served in the background until teardown.
    /// </summary>
    private static async Task ConnectHostedAsync(
        WebApplicationFactory<Program> factory, string name, ClockRecordingAgent agent)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var client = new EngineClient.EngineClient(new EngineIdentity(name), agent, agent);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ServeAsync(socket);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                // Test teardown: server ended the session.
            }
        });
    }

    /// <summary>
    /// A hosted clock-aware agent observes the whole clocked surface through
    /// the SDK: the control announced once (the dual-role object hears it
    /// once, not per role), a full-pool first reading, cube offer and response
    /// readings, and the your/opponent frame mapping — the scripted opponent's
    /// slow thinking sinks the <em>opponent</em> pool below initial while the
    /// agent's own pool only banks credits. The plain overloads are never
    /// touched in a clocked match.
    /// </summary>
    [Fact]
    public async Task HostedClockAwareAgent_ObservesControlAndPools_ThroughTheSdk()
    {
        // Initial pool big enough that the scripted side never flags; the
        // 10 s scripted think per play out-earns the 3 s increment, so its
        // pool sinks visibly below initial.
        var control = new TimeControl(initialSeconds: 10_000, incrementSeconds: 3);
        var initial = TimeSpan.FromSeconds(10_000);

        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        var agent = new ClockRecordingAgent();
        await ConnectHostedAsync(factory, "Aware", agent);
        await using var scripted = await TestEngine.ConnectAsync(factory, "Scripted");
        await ServerHarness.WaitForEnginesAsync(factory, "Aware", "Scripted");

        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        var match = await ServerHarness.StartMatchAsync(
            factory, "Aware", "Scripted", matchLength: 3, seed, timeControl: control);
        string matchId = match.GetProperty("matchId").GetString()!;

        // Script the opponent: think a fixed 10 s on every play (advancing the
        // fake clock while its own query is the one in flight), double at the
        // first opportunity (driving one cube-response reading on the hosted
        // side), decline every later offer.
        bool doubled = false;
        while (true)
        {
            var message = await scripted.ReceiveAsync();
            Assert.NotNull(message);
            if (message is MatchEndedMessage)
            {
                break;
            }

            switch (message)
            {
                case MatchStartedMessage:
                    break;
                case PlayQueryMessage play:
                    time.Advance(TimeSpan.FromSeconds(10));
                    await scripted.ReplyWithFirstLegalPlayAsync(play);
                    break;
                case CubeOfferQueryMessage offer:
                    await scripted.SendAsync(new CubeOfferReplyMessage
                    {
                        RequestId = offer.RequestId,
                        Action = doubled ? CubeOfferAction.NoDouble : CubeOfferAction.Double,
                    });
                    doubled = true;
                    break;
                default:
                    Assert.Fail($"Unexpected message for the scripted side: {message.GetType().Name}.");
                    break;
            }
        }

        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId, HostedDeadline);
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        // The dual-role agent heard the announcement exactly once, with the control.
        var announced = Assert.Single(agent.Announcements);
        Assert.NotNull(announced);
        Assert.Equal(initial, announced!.Initial);
        Assert.Equal(TimeSpan.FromSeconds(3), announced.Increment);

        // Every decision of a clocked match went through the clock-carrying
        // overloads; the plain ones were never touched.
        Assert.Equal(0, agent.PlainPlayCalls);
        Assert.Equal(0, agent.PlainOfferCalls);
        Assert.Equal(0, agent.PlainResponseCalls);

        // The agent moved first (seed chosen for that): the match's first
        // reading shows both pools full, nothing yet debited or credited.
        Assert.NotEmpty(agent.PlayReadings);
        Assert.Equal(new ClockReading(initial, initial), agent.PlayReadings[0]);

        // This agent answers instantly on the fake clock, so its own pool only
        // banks credits — never below initial…
        Assert.All(agent.AllReadings, r => Assert.True(
            r.YourTimeRemaining >= initial,
            $"Own pool fell below initial: {r.YourTimeRemaining}."));

        // …while the scripted opponent's thinking sinks the opponent pool
        // below initial: the your/opponent frame mapping, pinned (a swap would
        // put the sunken pool on the wrong side).
        Assert.True(
            agent.PlayReadings[^1].OpponentTimeRemaining < initial,
            $"Opponent pool never sank below initial: {agent.PlayReadings[^1].OpponentTimeRemaining}.");

        // The cube path delivered readings too: the scripted double produced
        // exactly one response decision, and owning the cube afterwards
        // produced offer decisions.
        Assert.NotEmpty(agent.OfferReadings);
        Assert.Single(agent.ResponseReadings);
    }

    /// <summary>
    /// The flat regime through the SDK, honest by shape: the announcement
    /// still fires — null affirmatively says "unclocked" — and no reading is
    /// ever delivered (the plain overloads carry every decision); no sentinel
    /// stands in for pools that do not exist.
    /// </summary>
    [Fact]
    public async Task HostedClockAwareAgent_FlatRegime_NullAnnouncementAndNoReadings()
    {
        using var factory = ServerHarness.NewFactory();
        var agent = new ClockRecordingAgent();
        await ConnectHostedAsync(factory, "Aware", agent);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Plain", seed: 7, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "Aware", "Plain");

        var match = await ServerHarness.StartMatchAsync(
            factory, "Aware", "Plain", matchLength: 1, seed: 4242);
        string matchId = match.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId, HostedDeadline);
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        // Announced exactly once, affirmatively unclocked.
        var announced = Assert.Single(agent.Announcements);
        Assert.Null(announced);

        // No readings anywhere; every decision went through the plain overloads.
        Assert.Empty(agent.PlayReadings);
        Assert.Empty(agent.OfferReadings);
        Assert.Empty(agent.ResponseReadings);
        Assert.True(agent.PlainPlayCalls > 0, "The agent decided no plays at all.");
    }
}
