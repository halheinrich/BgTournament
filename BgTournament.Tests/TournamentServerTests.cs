using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.EngineClient;
using BgTournament.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The tournament admin surface and orchestration policies over TestServer:
/// request validation, tournament-wide engine claims (a participant is busy
/// for the whole tournament), and forfeit folding — a mid-match forfeit and
/// an unplayed forfeit against a no-longer-connected participant both count
/// as ordinary losses in the standings.
/// </summary>
public class TournamentServerTests
{
    private static readonly TimeSpan TournamentDeadline = TimeSpan.FromSeconds(30);

    /// <summary>A play agent that dies on its first decision — the client drops the connection.</summary>
    private sealed class ThrowingPlayAgent : IPlayAgent
    {
        public ValueTask<Play> ChoosePlayAsync(
            GameState state, int die1, int die2, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This engine crashes on purpose.");
    }

    private static async Task RunCrashingClientAsync(WebApplicationFactory<Program> factory, string name)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var client = new EngineClient.EngineClient(
            new EngineIdentity(name), new ThrowingPlayAgent(), new PassiveCubeAgent());
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ServeAsync(socket);
            }
            catch (Exception)
            {
                // ServeAsync leaves socket ownership with the caller: drop
                // the connection the way a crashed engine process would.
                socket.Abort();
            }
        });
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        WebApplicationFactory<Program> factory, string path, object request)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(path, request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task PostTournament_RejectsBadRequests_WithTheRightStatusCodes()
    {
        using var factory = ServerHarness.NewFactory();

        // Domain validation (400) — checked before connectivity, so none of
        // these need a connected engine.
        (HttpStatusCode status, JsonElement _) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "Solo" },
            matchLength = 1,
            matchesPerPairing = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, _) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "Twin", "Twin" },
            matchLength = 1,
            matchesPerPairing = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, _) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "A", "B" },
            matchLength = 0,
            matchesPerPairing = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, _) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "A", "B" },
            matchLength = 1,
            matchesPerPairing = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, _) = await PostAsync(factory, "/tournaments", new { matchLength = 1, matchesPerPairing = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // Valid shape, but nobody by those names is connected (404).
        (status, _) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "GhostOne", "GhostTwo" },
            matchLength = 1,
            matchesPerPairing = 1,
        });
        Assert.Equal(HttpStatusCode.NotFound, status);

        // Unknown tournament id (404).
        using var http = factory.CreateClient();
        var lookup = await http.GetAsync("/tournaments/nope");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task Tournament_ClaimsItsParticipantsForItsWholeDuration()
    {
        // A raw-wire engine that never answers keeps the first tournament
        // match pending on the (shortened) decision timeout — a deterministic
        // window to probe the claims, which are taken synchronously before
        // POST /tournaments returns.
        using var factory = ServerHarness.NewFactory(decisionTimeoutSeconds: 2);
        await using var staller = await TestEngine.ConnectAsync(factory, "Staller");
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomOne", seed: 31, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomTwo", seed: 32, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Bystander", seed: 33, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "Staller", "RandomOne", "RandomTwo", "Bystander");

        var tournament = await ServerHarness.StartTournamentAsync(
            factory, ["Staller", "RandomOne", "RandomTwo"], matchLength: 1, matchesPerPairing: 1, seed: 555);
        string tournamentId = tournament.GetProperty("tournamentId").GetString()!;

        // A tournament participant cannot be poached — by a match or by
        // another tournament — even while idle between its matches.
        (HttpStatusCode status, JsonElement body) = await PostAsync(factory, "/matches", new
        {
            engineOne = "RandomTwo",
            engineTwo = "Bystander",
            matchLength = 1,
        });
        Assert.Equal(HttpStatusCode.Conflict, status);

        (status, body) = await PostAsync(factory, "/tournaments", new
        {
            participants = new[] { "RandomTwo", "Bystander" },
            matchLength = 1,
            matchesPerPairing = 1,
        });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("already in a match", body.GetProperty("error").GetString());

        // The tournament itself still runs to completion: the staller times
        // out (mid-match forfeit, connection closed), its remaining match is
        // forfeited without play, and the real match decides the winner.
        var ended = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId, TournamentDeadline);
        await AssertOneBadParticipantOutcomeAsync(factory, ended, badParticipant: "Staller");
    }

    [Fact]
    public async Task Tournament_FoldsForfeits_MidMatchDisconnectAndUnplayed()
    {
        using var factory = ServerHarness.NewFactory();
        await RunCrashingClientAsync(factory, "Crasher");
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomOne", seed: 41, CancellationToken.None);
        await ServerHarness.RunWellBehavedClientAsync(factory, "RandomTwo", seed: 42, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "Crasher", "RandomOne", "RandomTwo");

        var tournament = await ServerHarness.StartTournamentAsync(
            factory, ["Crasher", "RandomOne", "RandomTwo"], matchLength: 1, matchesPerPairing: 1, seed: 888);
        string tournamentId = tournament.GetProperty("tournamentId").GetString()!;

        var ended = await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId, TournamentDeadline);
        await AssertOneBadParticipantOutcomeAsync(factory, ended, badParticipant: "Crasher");
    }

    /// <summary>
    /// The shared shape of a 3-engine, K=1 tournament whose first-listed
    /// participant forfeits everything: schedule (Bad,R1),(Bad,R2),(R1,R2) →
    /// two forfeits attributed to the bad participant and folded as ordinary
    /// losses (the second without the match ever being playable), one real
    /// match, a completed tournament, and a winner with a clean 2–0.
    /// </summary>
    private static async Task AssertOneBadParticipantOutcomeAsync(
        WebApplicationFactory<Program> factory, JsonElement ended, string badParticipant)
    {
        Assert.Equal("completed", ended.GetProperty("status").GetString());

        var matches = ended.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(3, matches.Count);
        Assert.Equal("forfeited", matches[0].GetProperty("status").GetString());
        Assert.Equal("forfeited", matches[1].GetProperty("status").GetString());
        Assert.Equal("completed", matches[2].GetProperty("status").GetString());

        var standings = ended.GetProperty("standings").EnumerateArray().ToList();
        Assert.Equal(3, standings.Count);

        string winner = ended.GetProperty("winner").GetString()!;
        Assert.Equal(winner, standings[0].GetProperty("participant").GetString());
        Assert.NotEqual(badParticipant, winner);
        Assert.Equal(2, standings[0].GetProperty("wins").GetInt32());
        Assert.Equal(0, standings[0].GetProperty("losses").GetInt32());

        var bottom = standings[2];
        Assert.Equal(badParticipant, bottom.GetProperty("participant").GetString());
        Assert.Equal(0, bottom.GetProperty("wins").GetInt32());
        Assert.Equal(2, bottom.GetProperty("losses").GetInt32());

        // Both forfeits land on the bad participant, and each is a
        // retrievable match record like any other.
        foreach (int forfeitedIndex in new[] { 0, 1 })
        {
            string matchId = matches[forfeitedIndex].GetProperty("matchId").GetString()!;
            var match = await ServerHarness.GetMatchAsync(factory, matchId);
            Assert.Equal(badParticipant, match.GetProperty("forfeitedBy").GetString());
            Assert.NotEqual(badParticipant, match.GetProperty("winner").GetString());
        }
    }
}
