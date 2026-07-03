using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The dashboard listing endpoints over TestServer: <c>GET /matches</c> and
/// <c>GET /tournaments</c> return every record in creation order — the id
/// dictionaries are unordered, so the stable order is a served guarantee,
/// not an accident of enumeration.
/// </summary>
public class ListEndpointTests
{
    private static async Task<JsonElement> GetAsync(WebApplicationFactory<Program> factory, string path)
    {
        using var http = factory.CreateClient();
        var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement.Clone();
    }

    [Fact]
    public async Task GetMatches_NoMatchesYet_ReturnsEmptyArray()
    {
        using var factory = ServerHarness.NewFactory();

        var matches = await GetAsync(factory, "/matches");

        Assert.Equal(JsonValueKind.Array, matches.ValueKind);
        Assert.Empty(matches.EnumerateArray());
    }

    [Fact]
    public async Task GetTournaments_NoTournamentsYet_ReturnsEmptyArray()
    {
        using var factory = ServerHarness.NewFactory();

        var tournaments = await GetAsync(factory, "/tournaments");

        Assert.Equal(JsonValueKind.Array, tournaments.ValueKind);
        Assert.Empty(tournaments.EnumerateArray());
    }

    [Fact]
    public async Task GetMatches_ListsEveryMatch_InCreationOrder()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        // Sequential short matches, each awaited to its end so creation
        // order is unambiguous and the listing is a settled snapshot.
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var started = await ServerHarness.StartMatchAsync(
                factory, "Alpha", "Beta", matchLength: 1, seed: 100 + i);
            string matchId = started.GetProperty("matchId").GetString()!;
            await ServerHarness.WaitForMatchEndAsync(factory, matchId);
            ids.Add(matchId);
        }

        var matches = await GetAsync(factory, "/matches");

        Assert.Equal(ids, matches.EnumerateArray().Select(m => m.GetProperty("matchId").GetString()!).ToList());

        // Each listing row is the same projection GET /matches/{id} serves.
        foreach (var listed in matches.EnumerateArray())
        {
            var single = await ServerHarness.GetMatchAsync(factory, listed.GetProperty("matchId").GetString()!);
            Assert.Equal(single.GetRawText(), listed.GetRawText());
        }

        teardown.Cancel();
    }

    [Fact]
    public async Task GetTournaments_ListsEveryTournament_InCreationOrder()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        // Sequential tournaments (each claims its participants for its whole
        // duration, so the second cannot start until the first releases them).
        var ids = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var started = await ServerHarness.StartTournamentAsync(
                factory, ["Alpha", "Beta"], matchLength: 1, matchesPerPairing: 1, seed: 100 + i);
            string tournamentId = started.GetProperty("tournamentId").GetString()!;
            await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId);
            ids.Add(tournamentId);
        }

        var tournaments = await GetAsync(factory, "/tournaments");

        Assert.Equal(
            ids,
            tournaments.EnumerateArray().Select(t => t.GetProperty("tournamentId").GetString()!).ToList());

        // Each listing row is the same projection GET /tournaments/{id} serves —
        // standings and the per-match ledger included.
        foreach (var listed in tournaments.EnumerateArray())
        {
            var single = await ServerHarness.GetTournamentAsync(
                factory, listed.GetProperty("tournamentId").GetString()!);
            Assert.Equal(single.GetRawText(), listed.GetRawText());
        }

        teardown.Cancel();
    }
}
