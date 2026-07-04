using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgTournament.Api;

namespace BgTournament.Tests;

/// <summary>
/// The replay endpoint over TestServer: <c>GET /matches/{matchId}/games</c>
/// serves a completed match's per-game transcripts — and refuses, with the
/// right statuses, everything that has none. The happy path deserializes
/// through the BgTournament.Api contracts with default options, exactly the
/// way the Blazor consumer will.
/// </summary>
public class ReplayEndpointTests
{
    [Fact]
    public async Task GamesEndpoint_UnknownMatch_Is404()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var response = await http.GetAsync("/matches/no-such-match/games");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GamesEndpoint_RunningMatch_Is409()
    {
        using var factory = ServerHarness.NewFactory();
        await using var one = await TestEngine.ConnectAsync(factory, "Thinker");
        await using var two = await TestEngine.ConnectAsync(factory, "Waiter");

        // Neither engine ever answers, so the match stays running.
        var started = await ServerHarness.StartMatchAsync(factory, "Thinker", "Waiter", matchLength: 1, seed: 1);
        string matchId = started.GetProperty("matchId").GetString()!;
        Assert.Equal("running", started.GetProperty("status").GetString());

        using var http = factory.CreateClient();
        var response = await http.GetAsync($"/matches/{matchId}/games");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("still running", error!.Error);
    }

    [Fact]
    public async Task GamesEndpoint_ForfeitedBeforeAnyGame_Is200_WithAnEmptyPartialAndForfeitedStatus()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, teardown.Token);
        await using var bad = await TestEngine.ConnectAsync(factory, "Bad");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Bad");

        var started = await ServerHarness.StartMatchAsync(factory, "Bad", "Good", matchLength: 1, seed: 1);
        string matchId = started.GetProperty("matchId").GetString()!;

        // Disconnect mid-query — the established forfeit lever: Bad vanishes on
        // its first decision, before any game finishes.
        _ = await bad.ExpectPlayQueryAsync();
        bad.Abort();

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());

        // A terminal match serves its retained games — here none finished, so
        // an empty list, tagged forfeited so the partiality is self-describing.
        using var http = factory.CreateClient();
        var replay = await http.GetFromJsonAsync<MatchGamesResponse>($"/matches/{matchId}/games");

        Assert.NotNull(replay);
        Assert.Equal(MatchStatus.Forfeited, replay.Status);
        Assert.Empty(replay.Games);
        teardown.Cancel();
    }

    [Fact]
    public async Task GamesEndpoint_CompletedMatch_ServesTheFullReplay()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 3, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(
            factory, matchId, deadlineOverride: TimeSpan.FromSeconds(30));
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        // Deserialize through the contracts assembly with default options —
        // the consumer's exact path, polymorphic entries included.
        using var http = factory.CreateClient();
        var replay = await http.GetFromJsonAsync<MatchGamesResponse>($"/matches/{matchId}/games");

        Assert.NotNull(replay);
        Assert.Equal(matchId, replay.MatchId);
        Assert.Equal("Alpha", replay.EngineOne);
        Assert.Equal("Beta", replay.EngineTwo);
        Assert.Equal(3, replay.MatchLength);
        Assert.NotEmpty(replay.Games);
        Assert.Equal(
            Enumerable.Range(1, replay.Games.Count),
            replay.Games.Select(game => game.GameNumber));

        // The replayed outcomes must fold back into exactly the completed
        // match's recorded scores — attribution round-trips end to end.
        int seatOnePoints = replay.Games.Where(g => g.Winner == Seat.One).Sum(g => g.Points);
        int seatTwoPoints = replay.Games.Where(g => g.Winner == Seat.Two).Sum(g => g.Points);
        Assert.Equal(summary.GetProperty("seatOneScore").GetInt32(), seatOnePoints);
        Assert.Equal(summary.GetProperty("seatTwoScore").GetInt32(), seatTwoPoints);

        int enteringOne = 0, enteringTwo = 0;
        foreach (var game in replay.Games)
        {
            Assert.Equal(enteringOne, game.SeatOneScore);
            Assert.Equal(enteringTwo, game.SeatTwoScore);
            if (game.Winner == Seat.One)
            {
                enteringOne += game.Points;
            }
            else
            {
                enteringTwo += game.Points;
            }

            Assert.NotEmpty(game.Entries);
            var opening = Assert.IsType<PlayEntry>(game.Entries[0]);
            Assert.Equal(opening.Die1 > opening.Die2 ? Seat.One : Seat.Two, opening.Actor);

            foreach (var entry in game.Entries)
            {
                Assert.Equal(26, entry.State.Board.Count);
                Assert.InRange(entry.State.Board.Where(p => p > 0).Sum(), 0, 15);
                Assert.InRange(entry.State.Board.Where(p => p < 0).Sum(), -15, 0);
            }

            Assert.Equal(26, game.FinalState.Board.Count);
        }

        teardown.Cancel();
    }
}
