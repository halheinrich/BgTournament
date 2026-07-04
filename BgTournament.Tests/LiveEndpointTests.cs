using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgTournament.Api;
using BgTournament.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The live SSE feed (<c>GET /matches/{matchId}/live</c>) end to end over the
/// real in-proc server. Determinism comes from raw-wire engines: the match
/// parks at its first decision query until the test drives replies, and the
/// subscription is registered by the time the response headers arrive — so a
/// subscriber that joins before driving catches the whole stream from the top.
/// </summary>
public class LiveEndpointTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WebJson);

    [Fact]
    public async Task LiveEndpoint_UnknownMatch_Is404()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var response = await http.GetAsync("/matches/no-such-match/live");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_FromStart_StreamsSnapshotThenIncrementsThenTerminal_AgreeingWithReplay()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 1, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;

        using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Join first: once the headers are back the subscription is registered,
        // so nothing is missed. The match is still parked at its first query.
        using var response = await http.GetAsync(
            $"/matches/{matchId}/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Now let the match play out.
        var driveAlpha = DriveToEndAsync(alpha);
        var driveBeta = DriveToEndAsync(beta);

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var events = await CollectAsync(stream, deadline.Token);
        await Task.WhenAll(driveAlpha, driveBeta);

        // Ordering: snapshot first, exactly one terminal, and it is last.
        Assert.IsType<LiveSnapshotEvent>(events[0]);
        Assert.Equal(MatchStatus.Completed, Assert.IsType<LiveTerminalEvent>(events[^1]).Match.Status);
        Assert.All(events.SkipLast(1), e => Assert.IsNotType<LiveTerminalEvent>(e));

        // Payload/transcript agreement: the finalized game pushed on the feed
        // and the entries streamed live equal exactly what the settled-replay
        // endpoint serves for the same match.
        var replay = await http.GetFromJsonAsync<MatchGamesResponse>(
            $"/matches/{matchId}/games", WebJson, deadline.Token);
        Assert.NotNull(replay);

        var gameEnded = Assert.Single(events.OfType<LiveGameEndedEvent>());
        Assert.Equal(Json(replay.Games[0]), Json(gameEnded.Game));

        var streamedEntries = events.OfType<LiveEntryEvent>().Select(e => e.Entry).ToList();
        Assert.Equal(
            replay.Games[0].Entries.Select(Json<GameEntry>),
            streamedEntries.Select(Json));
    }

    [Fact]
    public async Task LiveEndpoint_Forfeit_TerminalCarriesForfeited()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();

        // Bad is seat One and moves first (so it is the one queried); Good
        // auto-plays. Bad vanishes on its first query — a forfeit to Good.
        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, teardown.Token);
        await using var bad = await TestEngine.ConnectAsync(factory, "Bad");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Bad");

        var started = await ServerHarness.StartMatchAsync(factory, "Bad", "Good", matchLength: 1, seed: seed);
        string matchId = started.GetProperty("matchId").GetString()!;

        using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(
            $"/matches/{matchId}/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        // Bad disconnects mid-query — the established forfeit lever.
        _ = await bad.ExpectPlayQueryAsync();
        bad.Abort();

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var events = await CollectAsync(stream, deadline.Token);

        var terminal = Assert.IsType<LiveTerminalEvent>(events[^1]);
        Assert.Equal(MatchStatus.Forfeited, terminal.Match.Status);
        Assert.Equal("Bad", terminal.Match.ForfeitedBy);
        Assert.Equal("Good", terminal.Match.Winner);
        teardown.Cancel();
    }

    [Fact]
    public async Task LiveEndpoint_AlreadyCompleted_Join_GetsSnapshotThenTerminal_ThenCloses()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 1, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;
        await ServerHarness.WaitForMatchEndAsync(factory, matchId, deadlineOverride: TimeSpan.FromSeconds(30));

        // Subscribing to a finished match takes the same one code path:
        // snapshot, terminal, close — never a hang.
        using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(
            $"/matches/{matchId}/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var events = await CollectAsync(stream, deadline.Token);

        Assert.IsType<LiveSnapshotEvent>(events[0]);
        Assert.Equal(MatchStatus.Completed, Assert.IsType<LiveTerminalEvent>(events[^1]).Match.Status);
        teardown.Cancel();
    }

    [Fact]
    public async Task LiveEndpoint_ForfeitAfterAGame_ServesThatGameAsPartialReplay()
    {
        using var factory = ServerHarness.NewFactory();
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        // Long enough that game 1 (cube declined, so ≤ 3 points) never ends the match.
        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 5, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;

        using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(
            $"/matches/{matchId}/live", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        // Beta forfeits on its first query after game 1 completes — signaled off
        // the live feed's own gameEnded event, so the boundary is deterministic.
        using var betaForfeit = new CancellationTokenSource();
        var driveAlpha = DriveToEndAsync(alpha);
        var driveBeta = DriveToEndAsync(beta, betaForfeit.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var events = new List<LiveMatchEvent>();
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            var liveEvent = JsonSerializer.Deserialize<LiveMatchEvent>(payload, WebJson)!;
            events.Add(liveEvent);
            if (liveEvent is LiveGameEndedEvent && events.OfType<LiveGameEndedEvent>().Count() == 1)
            {
                betaForfeit.Cancel();
            }

            if (liveEvent is LiveTerminalEvent)
            {
                break;
            }
        }

        await Task.WhenAll(driveAlpha, driveBeta);

        Assert.Equal(MatchStatus.Forfeited, Assert.IsType<LiveTerminalEvent>(events[^1]).Match.Status);

        // The partial replay serves exactly the games that finished before the
        // forfeit — one game here — matching the gameEnded events pushed live.
        var replay = await http.GetFromJsonAsync<MatchGamesResponse>(
            $"/matches/{matchId}/games", WebJson, deadline.Token);
        Assert.NotNull(replay);
        Assert.Equal(MatchStatus.Forfeited, replay.Status);
        Assert.NotEmpty(replay.Games);
        Assert.Equal(
            events.OfType<LiveGameEndedEvent>().Select(e => Json(e.Game)),
            replay.Games.Select(Json));
    }

    /// <summary>Read the SSE body, deserializing each event, until the terminal event.</summary>
    private static async Task<List<LiveMatchEvent>> CollectAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var events = new List<LiveMatchEvent>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            var liveEvent = JsonSerializer.Deserialize<LiveMatchEvent>(payload, WebJson)!;
            events.Add(liveEvent);
            if (liveEvent is LiveTerminalEvent)
            {
                break;
            }
        }

        return events;
    }

    /// <summary>
    /// Auto-play a raw-wire engine to match end: first legal play, decline
    /// cubes, take doubles. If <paramref name="abort"/> fires, the engine
    /// disconnects on its next query — the forfeit lever, timed by the caller.
    /// </summary>
    private static async Task DriveToEndAsync(TestEngine engine, CancellationToken abort = default)
    {
        try
        {
            while (await engine.ReceiveAsync() is { } message)
            {
                if (abort.IsCancellationRequested)
                {
                    engine.Abort();
                    return;
                }

                switch (message)
                {
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
                    case CubeResponseQueryMessage responseQuery:
                        await engine.SendAsync(new CubeResponseReplyMessage
                        {
                            RequestId = responseQuery.RequestId,
                            Action = BgTournament.Protocol.CubeResponseAction.Take,
                        });
                        break;
                    case MatchEndedMessage:
                        return;
                }
            }
        }
        catch (Exception) when (abort.IsCancellationRequested)
        {
            engine.Abort();
        }
    }
}
