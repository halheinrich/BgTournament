using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Protocol;
using BgTournament.Server;
using BgTournament.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using CubeResponseAction = BgTournament.Protocol.CubeResponseAction;

namespace BgTournament.Tests;

/// <summary>
/// The audit surface end to end: <c>GET /matches/{matchId}/audit</c> serves a
/// terminal match's arbitration timeline from its durable journal — refusal
/// semantics mirroring replay (404 unknown, 409 while running), the
/// journal-settled drain gate (proven deterministically against a parked
/// writer, never by racing the real one), the replay join (game number +
/// entry index), the per-decision clock evidence including the uncredited
/// flag-fall trail, the fair-dice verification packet (served commitment
/// byte-equals the wire's, terminal reveals the key), the evidence events'
/// disk-to-timeline projection, and the damage-policy surfaces. Plus the
/// late-reply evidence hook itself, pinned at the connection.
/// </summary>
public class AuditEndpointTests
{
    private static readonly int[] OpeningBoard =
        [0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0];

    private const string EscrowKeyHex =
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private const string RawAt = "2026-07-05T12:00:00+00:00";

    private const string RawState =
        """{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeSize":1,"cubeOwner":"centered","matchLength":1,"onRollScore":0,"opponentScore":0,"isCrawford":false}""";

    private static async Task<JsonElement> GetAuditAsync(
        WebApplicationFactory<Program> factory, string matchId)
    {
        using var http = factory.CreateClient();
        return await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}/audit");
    }

    private static JsonElement[] EventsOf(JsonElement audit) =>
        audit.GetProperty("events").EnumerateArray().ToArray();

    private static JsonElement[] OfType(JsonElement[] events, string type) =>
        events.Where(e => e.GetProperty("type").GetString() == type).ToArray();

    [Fact]
    public async Task UnknownMatch_NotFound()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();
        var response = await http.GetAsync("/matches/no-such-match/audit");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RunningMatch_Conflict_PointsAtTheLiveFeed()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 1, seed: 7);
        string matchId = started.GetProperty("matchId").GetString()!;

        // Neither engine answers, so the match is deterministically running.
        using var http = factory.CreateClient();
        var response = await http.GetAsync($"/matches/{matchId}/audit");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains($"/matches/{matchId}/live", error.GetProperty("error").GetString());
    }

    /// <summary>
    /// A clocked match's full timeline: header first, terminal last, one clock
    /// event per decision query (a play query always yields a play entry, so
    /// those counts match exactly), the documented clock-precedes-its-entry
    /// ordering, exact Fischer arithmetic on a fake clock, and the
    /// game-number + entry-index join onto the replay surface.
    /// </summary>
    [Fact]
    public async Task ClockedMatch_ServesTheFullTimeline_JoinedToReplay()
    {
        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        using var clients = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(
            factory, "Alpha", "Beta", matchLength: 1, seed: 7,
            timeControl: new TimeControl(120, 8));
        string matchId = started.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId);

        // No journal-file polling: the endpoint's own settled gate is the
        // synchronization — an immediate read sees the complete timeline.
        var audit = await GetAuditAsync(factory, matchId);
        using var http = factory.CreateClient();
        var games = await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}/games");
        clients.Cancel();

        Assert.Equal("completed", audit.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("integrity").ValueKind);

        var events = EventsOf(audit);
        Assert.Equal("created", events[0].GetProperty("type").GetString());
        Assert.Equal(120, events[0].GetProperty("timeControl").GetProperty("initialSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("diceAlgorithm").ValueKind); // seeded mode
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("diceCommitment").ValueKind);
        Assert.Equal("started", events[1].GetProperty("type").GetString());

        var terminal = events[^1];
        Assert.Equal("terminal", terminal.GetProperty("type").GetString());
        Assert.Equal("completed", terminal.GetProperty("status").GetString());
        Assert.Equal(
            summary.GetProperty("winner").GetString(), terminal.GetProperty("winner").GetString());
        Assert.NotEqual(JsonValueKind.Null, terminal.GetProperty("at").ValueKind);

        // Clock evidence: every decision answered (nothing flagged), and with
        // the fake clock never advanced the arithmetic is exact — think 0,
        // increment 8 credited on each settlement.
        var clockEvents = OfType(events, "clock");
        Assert.NotEmpty(clockEvents);
        Assert.All(clockEvents, clock =>
        {
            Assert.True(clock.GetProperty("incrementCredited").GetBoolean());
            Assert.Equal(0, clock.GetProperty("thinkSeconds").GetDouble());
            Assert.Equal(
                clock.GetProperty("remainingBeforeSeconds").GetDouble() + 8,
                clock.GetProperty("remainingAfterSeconds").GetDouble());
            Assert.True(clock.GetProperty("gameNumber").GetInt32() >= 1);
        });

        // The replay join: gameNumber selects the game, entryIndex the entry,
        // and the shared facts (kind, actor, dice) agree across surfaces.
        var plays = OfType(events, "play");
        Assert.NotEmpty(plays);
        var gameArray = games.GetProperty("games").EnumerateArray().ToArray();
        JsonElement ReplayEntryOf(JsonElement play)
        {
            var game = gameArray[play.GetProperty("gameNumber").GetInt32() - 1];
            Assert.Equal(play.GetProperty("gameNumber").GetInt32(), game.GetProperty("gameNumber").GetInt32());
            return game.GetProperty("entries")[play.GetProperty("entryIndex").GetInt32()];
        }

        int replayPlayCount = gameArray.Sum(game => game.GetProperty("entries")
            .EnumerateArray().Count(entry => entry.GetProperty("type").GetString() == "play"));
        Assert.Equal(replayPlayCount, plays.Length);
        Assert.All(plays, play =>
        {
            var entry = ReplayEntryOf(play);
            Assert.Equal("play", entry.GetProperty("type").GetString());
            Assert.Equal(play.GetProperty("actor").GetString(), entry.GetProperty("actor").GetString());
            Assert.Equal(play.GetProperty("die1").GetInt32(), entry.GetProperty("die1").GetInt32());
            Assert.Equal(play.GetProperty("die2").GetInt32(), entry.GetProperty("die2").GetInt32());
        });

        // One clock event per play *query*: a dance is recorded as a play
        // entry without any query (the runner never asks the engine), so
        // queried plays — the non-dances, read off the replay join — match
        // the play-decision clock events exactly, and each queried play is
        // immediately preceded by its own clock event (the settle-then-record
        // ordering).
        bool IsDance(JsonElement play) =>
            ReplayEntryOf(play).GetProperty("moves").GetArrayLength() == 0;
        Assert.Equal(
            plays.Count(play => !IsDance(play)),
            clockEvents.Count(c => c.GetProperty("decision").GetString() == "play"));
        for (int i = 0; i < events.Length; i++)
        {
            if (events[i].GetProperty("type").GetString() != "play" || IsDance(events[i]))
            {
                continue;
            }

            var preceding = events[i - 1];
            Assert.Equal("clock", preceding.GetProperty("type").GetString());
            Assert.Equal("play", preceding.GetProperty("decision").GetString());
            Assert.Equal(
                events[i].GetProperty("actor").GetString(), preceding.GetProperty("seat").GetString());
        }
    }

    /// <summary>
    /// Plays one side of a fair match to its natural end over the raw wire
    /// (first legal play, no double, always take), capturing the fair-dice
    /// lifecycle: the commitment received on <c>matchStarted</c> and the key
    /// revealed on <c>matchEnded</c>.
    /// </summary>
    private static async Task<(string Commitment, string RevealedKey)> PlayFairSideAsync(TestEngine engine)
    {
        string? commitment = null;
        while (true)
        {
            var message = await engine.ReceiveAsync();
            Assert.NotNull(message);
            switch (message)
            {
                case MatchStartedMessage started:
                    commitment = started.DiceCommitment;
                    break;
                case PlayQueryMessage play:
                    await engine.ReplyWithFirstLegalPlayAsync(play);
                    break;
                case CubeOfferQueryMessage offer:
                    await engine.SendAsync(new CubeOfferReplyMessage
                    {
                        RequestId = offer.RequestId,
                        Action = CubeOfferAction.NoDouble,
                    });
                    break;
                case CubeResponseQueryMessage response:
                    await engine.SendAsync(new CubeResponseReplyMessage
                    {
                        RequestId = response.RequestId,
                        Action = CubeResponseAction.Take,
                    });
                    break;
                case MatchEndedMessage ended:
                    Assert.NotNull(commitment);
                    Assert.NotNull(ended.DiceKey);
                    return (commitment!, ended.DiceKey!);
                default:
                    Assert.Fail($"Off-script message: {message!.GetType().Name}.");
                    break;
            }
        }
    }

    /// <summary>
    /// The verification packet, closed end to end: the audit's created event
    /// serves byte-for-byte the commitment the engines received on
    /// <c>matchStarted</c> (the no-drift pin — both derive from the one
    /// escrowed key through the one derivation path), the terminal event
    /// reveals the same key the wire revealed, and the key re-derives the
    /// commitment under the pinned public context.
    /// </summary>
    [Fact]
    public async Task FairMatch_AuditIsASelfContainedVerificationPacket()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartFairMatchAsync(factory, "Alpha", "Beta", matchLength: 1);
        string matchId = started.GetProperty("matchId").GetString()!;

        var sideA = PlayFairSideAsync(alpha);
        var sideB = PlayFairSideAsync(beta);
        (string commitment, string revealedKey) = await sideA;
        var other = await sideB;
        Assert.Equal(commitment, other.Commitment);
        Assert.Equal(revealedKey, other.RevealedKey);

        // matchEnded is sent only after the record went terminal, so the
        // audit gate is already open.
        var audit = await GetAuditAsync(factory, matchId);
        var events = EventsOf(audit);

        var created = events[0];
        Assert.Equal(VerifiableDice.AlgorithmId, created.GetProperty("diceAlgorithm").GetString());
        Assert.Equal(commitment, created.GetProperty("diceCommitment").GetString());

        var terminal = events[^1];
        Assert.Equal(revealedKey, terminal.GetProperty("diceKey").GetString());
        Assert.Equal(VerifiableDice.AlgorithmId, terminal.GetProperty("diceAlgorithm").GetString());

        // Commitment ↔ key close under the pinned public derivation.
        Assert.Equal(
            commitment,
            DiceKey.FromHex(revealedKey).Commit(VerifiableDice.ContextFor(matchId)).ToHex());
    }

    /// <summary>
    /// A flag fall leaves exactly the evidence an arbiter needs: the terminal
    /// event carries the structured cause, and the timeline's last clock
    /// event is the fatal decision — debited to zero, uncredited, followed by
    /// no decision entry (the engine never answered).
    /// </summary>
    [Fact]
    public async Task FlagFall_LeavesTheUncreditedClockTrail()
    {
        var time = new FakeTimeProvider();
        using var factory = ServerHarness.NewFactory(timeProvider: time);
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        var match = await ServerHarness.StartMatchAsync(
            factory, "Alpha", "Beta", matchLength: 7, seed,
            timeControl: new TimeControl(60, 5));
        string matchId = match.GetProperty("matchId").GetString()!;

        // Alpha (seat One, queried first) never answers; its pool runs dry.
        await alpha.ExpectPlayQueryAsync();
        time.Advance(TimeSpan.FromSeconds(61));
        await ServerHarness.WaitForMatchEndAsync(factory, matchId);

        var audit = await GetAuditAsync(factory, matchId);
        var events = EventsOf(audit);

        var terminal = events[^1];
        Assert.Equal("forfeited", terminal.GetProperty("status").GetString());
        Assert.Equal("flagFall", terminal.GetProperty("forfeitCause").GetString());
        Assert.Equal("Alpha", terminal.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Beta", terminal.GetProperty("winner").GetString());

        // The one decision that happened never produced an entry — the
        // timeline is header, started, gameStarted, the fatal clock event,
        // then terminal.
        Assert.Empty(OfType(events, "play"));
        var clock = Assert.Single(OfType(events, "clock"));
        Assert.Equal("seatOne", clock.GetProperty("seat").GetString());
        Assert.Equal("play", clock.GetProperty("decision").GetString());
        Assert.False(clock.GetProperty("incrementCredited").GetBoolean());
        Assert.Equal(61, clock.GetProperty("thinkSeconds").GetDouble());
        Assert.Equal(60, clock.GetProperty("remainingBeforeSeconds").GetDouble());
        Assert.Equal(0, clock.GetProperty("remainingAfterSeconds").GetDouble());
    }

    /// <summary>
    /// The drain gate, proven deterministic-red: every match-journal write is
    /// parked behind a test-held gate, so the file is provably incomplete
    /// while the record is already terminal. The audit read must hold at the
    /// settled gate rather than serve the short file, and must serve the
    /// complete timeline once the writer drains. Without the endpoint's
    /// <c>JournalSettled</c> await this fails reliably (the response would
    /// carry no decision events at all) — not merely sometimes.
    /// </summary>
    [Fact]
    public async Task Audit_WaitsForTheJournalToSettle()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-audit-gate-").FullName;
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Persistence:DataDirectory", dataDirectory);
                builder.ConfigureServices(services => services.AddSingleton<IJournalStore>(
                    provider => new GatedJournalStore(
                        new FileJournalStore(
                            provider.GetRequiredService<IOptions<PersistenceOptions>>(),
                            provider.GetRequiredService<IHostEnvironment>()),
                        gate.Task)));
            });

            using var clients = new CancellationTokenSource();
            await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
            await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
            await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

            var started = await ServerHarness.StartMatchAsync(
                factory, "Alpha", "Beta", matchLength: 1, seed: 7);
            string matchId = started.GetProperty("matchId").GetString()!;

            // The record turns terminal while the journal pump is still parked
            // on its very first write — the widest possible drain window.
            await ServerHarness.WaitForMatchEndAsync(factory, matchId);

            using var http = factory.CreateClient();
            var pending = http.GetAsync($"/matches/{matchId}/audit");
            var first = await Task.WhenAny(pending, Task.Delay(300));
            Assert.NotSame(pending, first); // held at the gate, not serving the short file

            gate.SetResult();
            var response = await pending;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var audit = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            clients.Cancel();

            var events = EventsOf(audit);
            Assert.Equal("created", events[0].GetProperty("type").GetString());
            Assert.Equal("terminal", events[^1].GetProperty("type").GetString());
            Assert.NotEmpty(OfType(events, "play")); // the drained file, complete
        }
        finally
        {
            // Release before cleanup so the parked pump can drain and close
            // its file; brief retries absorb the close racing the delete
            // (without them a red assertion above would be masked by an
            // IOException from cleanup).
            gate.TrySetResult();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(dataDirectory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 40)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    /// <summary>
    /// The Interrupted arbitration case, from a hand-written fair-mode journal
    /// with no terminal line: the timeline still closes with a record-derived
    /// terminal event — <c>at</c> honestly null, the escrowed key revealed
    /// (exactly what verifying the partial roll stream needs) — and the
    /// evidence events project from disk, commitment re-derived from the key.
    /// </summary>
    [Fact]
    public async Task InterruptedFairJournal_RevealsTheKey_AndProjectsEvidence()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-audit-int-").FullName;
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "fairint",
                $$"""{"type":"created","schemaVersion":2,"at":"{{RawAt}}","matchId":"fairint","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7,"diceAlgorithm":"hmac-sha256-dice-v1","diceKey":"{{EscrowKeyHex}}"}""",
                $$"""{"type":"started","at":"{{RawAt}}"}""",
                $$"""{"type":"gameStarted","at":"{{RawAt}}","gameNumber":1,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false}""",
                $$"""{"type":"clock","at":"{{RawAt}}","seat":"one","decision":"play","thinkSeconds":2.5,"incrementCredited":true,"remainingBeforeSeconds":60,"remainingAfterSeconds":62.5}""",
                $$"""{"type":"play","at":"{{RawAt}}","onRollSeat":"one","state":{{RawState}},"die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}]}""",
                $$"""{"type":"lateReply","at":"{{RawAt}}","seat":"two","requestId":"q-4"}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            var audit = await GetAuditAsync(factory, "fairint");

            Assert.Equal("interrupted", audit.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, audit.GetProperty("integrity").ValueKind);
            var events = EventsOf(audit);

            // The derived commitment on created — never stored, re-derived
            // from the escrowed key through the one public derivation.
            Assert.Equal(
                DiceKey.FromHex(EscrowKeyHex).Commit(VerifiableDice.ContextFor("fairint")).ToHex(),
                events[0].GetProperty("diceCommitment").GetString());

            var clock = Assert.Single(OfType(events, "clock"));
            Assert.Equal("seatOne", clock.GetProperty("seat").GetString());
            Assert.Equal(2.5, clock.GetProperty("thinkSeconds").GetDouble());
            Assert.Equal(1, clock.GetProperty("gameNumber").GetInt32());

            var lateReply = Assert.Single(OfType(events, "lateReply"));
            Assert.Equal("seatTwo", lateReply.GetProperty("seat").GetString());
            Assert.Equal("q-4", lateReply.GetProperty("requestId").GetString());

            var terminal = events[^1];
            Assert.Equal("terminal", terminal.GetProperty("type").GetString());
            Assert.Equal("interrupted", terminal.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, terminal.GetProperty("at").ValueKind);
            Assert.Equal(EscrowKeyHex, terminal.GetProperty("diceKey").GetString());
            Assert.Equal(JsonValueKind.Null, terminal.GetProperty("forfeitCause").ValueKind);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Mid-file corruption: the audit serves only the trusted prefix, names
    /// the damage in <c>integrity</c>, and still closes with the
    /// record-derived terminal — the terminal line beyond the corruption is
    /// not trusted, so the record (and the timeline) say Interrupted.
    /// </summary>
    [Fact]
    public async Task CorruptJournal_ServesTrustedPrefix_WithIntegrityNote()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-audit-corrupt-").FullName;
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "corrupt1",
                $$"""{"type":"created","schemaVersion":2,"at":"{{RawAt}}","matchId":"corrupt1","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7}""",
                $$"""{"type":"started","at":"{{RawAt}}"}""",
                "this is not a journal event",
                $$"""{"type":"gameStarted","at":"{{RawAt}}","gameNumber":1,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false}""",
                $$"""{"type":"terminal","at":"{{RawAt}}","status":"completed","winner":"Alpha","seatOneScore":1,"seatTwoScore":0}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            var audit = await GetAuditAsync(factory, "corrupt1");

            Assert.Equal("interrupted", audit.GetProperty("status").GetString());
            Assert.Contains("corrupt at line 3", audit.GetProperty("integrity").GetString());

            // Trusted prefix (created, started) plus the record-derived close.
            var events = EventsOf(audit);
            Assert.Equal(3, events.Length);
            Assert.Equal("created", events[0].GetProperty("type").GetString());
            Assert.Equal("started", events[1].GetProperty("type").GetString());
            Assert.Equal("terminal", events[2].GetProperty("type").GetString());
            Assert.Equal("interrupted", events[2].GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The evidence hook at its source, pinned deterministically: a query is
    /// abandoned (caller cancellation), the engine's reply lands late, and
    /// <see cref="EngineConnection.LateReplyDiscarded"/> reports exactly the
    /// abandoned request id — the previously invisible benign race of
    /// PROTOCOL.md §8, now observable.
    /// </summary>
    [Fact]
    public async Task LateReplyDiscard_RaisesTheEvidenceHook()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha");

        var registry = factory.Services.GetRequiredService<EngineRegistry>();
        Assert.True(registry.TryGet("Alpha", out var session));

        var discarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connection.LateReplyDiscarded += requestId => discarded.TrySetResult(requestId);

        using var abandon = new CancellationTokenSource();
        var query = session.Connection.QueryAsync<PlayReplyMessage>(
            id => new PlayQueryMessage
            {
                RequestId = id,
                State = new WireGameState
                {
                    Board = OpeningBoard,
                    CubeValue = 1,
                    CubeOwner = WireCubeOwner.Centered,
                    MatchLength = 1,
                    YourScore = 0,
                    OpponentScore = 0,
                    IsCrawford = false,
                },
                Die1 = 3,
                Die2 = 1,
            },
            abandon.Token);

        var received = await alpha.ExpectAsync<PlayQueryMessage>();
        abandon.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);

        // The reply arrives after abandonment: discarded once, and reported.
        await alpha.SendAsync(new PlayReplyMessage { RequestId = received.RequestId, Moves = [] });
        string reportedId = await discarded.Task.WaitAsync(ServerHarness.ReceiveDeadline);
        Assert.Equal(received.RequestId, reportedId);

        // Tolerated exactly once: a second copy of the same late reply
        // correlates to nothing and is a protocol violation — the connection
        // closes (the discard-once semantics, pinned directly).
        await alpha.SendAsync(new PlayReplyMessage { RequestId = received.RequestId, Moves = [] });
        await session.Connection.Closed.WaitAsync(ServerHarness.ReceiveDeadline);
    }

    /// <summary>
    /// Wraps the real store but parks every match-journal write behind a
    /// test-held gate — the deterministic lever for the drain-gate proof.
    /// Other kinds (the server journal) pass through untouched.
    /// </summary>
    private sealed class GatedJournalStore(IJournalStore inner, Task gate) : IJournalStore
    {
        public Stream CreateJournal(JournalKind kind, string id) =>
            kind == JournalKind.Match
                ? new GatedStream(inner.CreateJournal(kind, id), gate)
                : inner.CreateJournal(kind, id);

        public IReadOnlyList<string> ListJournalIds(JournalKind kind) => inner.ListJournalIds(kind);

        public Stream OpenJournal(JournalKind kind, string id) => inner.OpenJournal(kind, id);
    }

    private sealed class GatedStream(Stream inner, Task gate) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        // Only the async paths are gated: the JournalWriter pump writes and
        // flushes exclusively asynchronously, and the sync members must not
        // block (xUnit1031) — they only run on the post-release dispose path.
        public override void Flush() => inner.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate;
            await inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override async Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate;
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate;
            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
