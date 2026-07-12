using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgTournament.Api;
using BgTournament.Server;
using BgTournament.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BgTournament.Tests;

/// <summary>
/// The admin surface's identity gate end to end: one rule for the whole
/// surface (every HTTP endpoint; the engine wire is exempt — its gate is the
/// handshake). Keys configured ⇒ every request must present a valid
/// <c>X-Api-Key</c>, and the key's <em>name</em> stamps the durable
/// <c>created</c> header of the actions it authorizes. No keys ⇒ anonymous
/// service (today's behavior, the dev/smoke default) — but a presented key is
/// still validated, so a client/server key mismatch fails loudly instead of
/// silently working unattributed. Refusals are journaled as server-journal
/// <c>adminRejected</c> evidence, and a broken key configuration fails the
/// boot.
/// </summary>
public class AdminAuthTests
{
    private static readonly Dictionary<string, string> DirectorKey = new()
    {
        ["Admin:ApiKeys:director"] = "director-secret",
    };

    private static HttpClient KeyedClient(
        WebApplicationFactory<Program> factory, string key = "director-secret")
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(AdminApiKey.HeaderName, key);
        return http;
    }

    private static async Task AssertRefusedAsync(
        HttpResponseMessage response, string reasonFragment)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Contains(reasonFragment, body.Error);
    }

    private static async Task<MatchCreatedEvent> ReadMatchHeaderAsync(string dataDirectory, string matchId)
    {
        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(dataDirectory, "matches", matchId + ".jsonl"));
        return Assert.IsType<MatchCreatedEvent>(JournalCodec.DeserializeMatchEvent(lines[0]));
    }

    // ---- enforcing mode: keys configured ----

    [Fact]
    public async Task Enforcing_MissingKey_RefusedWithErrorBody()
    {
        using var factory = ServerHarness.NewFactory(settings: DirectorKey);
        using var http = factory.CreateClient();

        using var response = await http.GetAsync("/matches");
        await AssertRefusedAsync(response, "requires an admin API key");
    }

    [Fact]
    public async Task Enforcing_UnknownKey_Refused()
    {
        using var factory = ServerHarness.NewFactory(settings: DirectorKey);
        using var http = KeyedClient(factory, key: "not-the-secret");

        using var response = await http.PostAsJsonAsync(
            "/matches", new { engineOne = "A", engineTwo = "B", matchLength = 1 });
        await AssertRefusedAsync(response, "not recognized");
    }

    [Fact]
    public async Task Enforcing_ValidKey_Served()
    {
        using var factory = ServerHarness.NewFactory(settings: DirectorKey);
        using var http = KeyedClient(factory);

        var matches = await http.GetFromJsonAsync<JsonElement>("/matches");
        Assert.Empty(matches.EnumerateArray());
        var engines = await http.GetFromJsonAsync<JsonElement>("/engines");
        Assert.Empty(engines.EnumerateArray());
    }

    /// <summary>
    /// The engine wire is not the admin surface: an engine registers through
    /// the handshake gate with no API key, keys configured or not.
    /// </summary>
    [Fact]
    public async Task Enforcing_EngineWire_Unaffected()
    {
        using var factory = ServerHarness.NewFactory(settings: DirectorKey);
        await using var engine = await TestEngine.ConnectAsync(factory, "Alpha");

        using var http = KeyedClient(factory);
        var engines = await http.GetFromJsonAsync<JsonElement>("/engines");
        Assert.Equal("Alpha", engines.EnumerateArray().Single().GetProperty("name").GetString());
    }

    /// <summary>
    /// The tier-C stamp: a match created by an authenticated request journals
    /// the key's name — never the key — as <c>createdBy</c> on its header.
    /// </summary>
    [Fact]
    public async Task Enforcing_CreatedMatch_JournalsActor()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-adminauth-").FullName;
        try
        {
            string matchId;
            using var cts = new CancellationTokenSource(ServerHarness.ReceiveDeadline);
            using (var factory = ServerHarness.NewFactory(
                dataDirectory: dataDirectory, settings: DirectorKey))
            {
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 1, cts.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 2, cts.Token);

                using var http = KeyedClient(factory);
                await WaitForEnginesAsync(http, "Alpha", "Beta");
                var created = await http.PostAsJsonAsync(
                    "/matches", new { engineOne = "Alpha", engineTwo = "Beta", matchLength = 1, seed = 5 });
                Assert.True(created.IsSuccessStatusCode);
                matchId = (await created.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("matchId").GetString()!;

                await WaitForMatchEndAsync(http, matchId);
            }   // disposing the factory drains and closes the journals

            var header = await ReadMatchHeaderAsync(dataDirectory, matchId);
            Assert.Equal("director", header.CreatedBy);
            Assert.DoesNotContain(
                "director-secret",
                await File.ReadAllTextAsync(
                    Path.Combine(dataDirectory, "matches", matchId + ".jsonl")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A tournament's header carries the actor; its scheduled matches carry
    /// none of their own — accountability lives on the tournament record,
    /// reachable through the <c>matchStarted</c> linkage.
    /// </summary>
    [Fact]
    public async Task Enforcing_Tournament_JournalsActor_ScheduledMatchesCarryNone()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-adminauth-").FullName;
        try
        {
            string tournamentId;
            string matchId;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using (var factory = ServerHarness.NewFactory(
                dataDirectory: dataDirectory, settings: DirectorKey))
            {
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 1, cts.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 2, cts.Token);

                using var http = KeyedClient(factory);
                await WaitForEnginesAsync(http, "Alpha", "Beta");
                var created = await http.PostAsJsonAsync(
                    "/tournaments",
                    new
                    {
                        participants = new[] { "Alpha", "Beta" },
                        matchLength = 1,
                        matchesPerPairing = 1,
                        seed = 7,
                    });
                Assert.True(created.IsSuccessStatusCode);
                tournamentId = (await created.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("tournamentId").GetString()!;

                var tournament = await WaitForTournamentEndAsync(http, tournamentId);
                matchId = tournament.GetProperty("matches").EnumerateArray()
                    .Single().GetProperty("matchId").GetString()!;
            }   // disposing the factory drains and closes the journals

            string[] tournamentLines = await File.ReadAllLinesAsync(
                Path.Combine(dataDirectory, "tournaments", tournamentId + ".jsonl"));
            var header = Assert.IsType<TournamentCreatedEvent>(
                JournalCodec.DeserializeTournamentEvent(tournamentLines[0]));
            Assert.Equal("director", header.CreatedBy);

            var matchHeader = await ReadMatchHeaderAsync(dataDirectory, matchId);
            Assert.Null(matchHeader.CreatedBy);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A refusal is journaled as server-journal evidence with the exact
    /// response reason — the handshake-rejection funnel, one gate over.
    /// </summary>
    [Fact]
    public async Task Enforcing_Refusal_JournalsAdminRejectedEvidence()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-adminauth-").FullName;
        try
        {
            using (var factory = ServerHarness.NewFactory(
                dataDirectory: dataDirectory, settings: DirectorKey))
            {
                using var http = factory.CreateClient();
                using var response = await http.PostAsJsonAsync(
                    "/matches", new { engineOne = "A", engineTwo = "B", matchLength = 1 });
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }   // disposing the factory closes the server-journal segment

            string segment = Directory.GetFiles(Path.Combine(dataDirectory, "server"), "*.jsonl").Single();
            var events = (await File.ReadAllLinesAsync(segment))
                .Select(JournalCodec.DeserializeServerEvent)
                .ToArray();
            var rejected = Assert.Single(events.OfType<AdminRejectedEvent>());
            Assert.Contains("requires an admin API key", rejected.Reason);
            Assert.Equal("POST", rejected.Method);
            Assert.Equal("/matches", rejected.Path);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    // ---- open mode: no keys configured ----

    /// <summary>
    /// Anonymous service is the unconfigured default (the dev/smoke path,
    /// and BgArena's until its adaptation lands): requests succeed, and the
    /// durable header honestly carries no actor.
    /// </summary>
    [Fact]
    public async Task Open_AnonymousCreation_JournalsNoActor()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-adminauth-").FullName;
        try
        {
            string matchId;
            using var cts = new CancellationTokenSource(ServerHarness.ReceiveDeadline);
            using (var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 1, cts.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 2, cts.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

                var match = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", 1, seed: 5);
                matchId = match.GetProperty("matchId").GetString()!;
                await ServerHarness.WaitForMatchEndAsync(factory, matchId);
            }   // disposing the factory drains and closes the journals

            var header = await ReadMatchHeaderAsync(dataDirectory, matchId);
            Assert.Null(header.CreatedBy);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A key presented to a server that knows none is a configuration
    /// mismatch, refused loudly — never silently served anonymously.
    /// </summary>
    [Fact]
    public async Task Open_PresentedKey_RefusedLoudly()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = KeyedClient(factory);

        using var response = await http.GetAsync("/matches");
        await AssertRefusedAsync(response, "no admin API keys configured");
    }

    // ---- configuration validation: a broken key set fails the boot ----
    //
    // The named reasons are pinned at the unit level (AdminApiKeys constructs
    // and throws deterministically); the boot-level tests pin only that the
    // host refuses to start — how WebApplicationFactory wraps and surfaces a
    // startup exception is host plumbing, not our contract to assert on.

    [Fact]
    public void DuplicateKeyValues_NamedRejection()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new AdminApiKeys(
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                ApiKeys = new Dictionary<string, string>
                {
                    ["alice"] = "same-secret",
                    ["bob"] = "same-secret",
                },
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminApiKeys>.Instance));
        Assert.Contains("share one key value", ex.Message);
    }

    [Fact]
    public void BlankKeyValue_NamedRejection()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new AdminApiKeys(
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                ApiKeys = new Dictionary<string, string> { ["alice"] = " " },
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminApiKeys>.Instance));
        Assert.Contains("blank key value", ex.Message);
    }

    [Fact]
    public void DuplicateKeyValues_FailStartup()
    {
        using var factory = ServerHarness.NewFactory(settings: new Dictionary<string, string>
        {
            ["Admin:ApiKeys:alice"] = "same-secret",
            ["Admin:ApiKeys:bob"] = "same-secret",
        });

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    [Fact]
    public void BlankKeyValue_FailsStartup()
    {
        using var factory = ServerHarness.NewFactory(settings: new Dictionary<string, string>
        {
            ["Admin:ApiKeys:alice"] = " ",
        });

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    // ---- rehydration: the actor survives a restart ----

    /// <summary>
    /// The stamped actor folds back onto the record at startup — full
    /// fidelity both ways, so any future surface reading it cannot silently
    /// differ across restarts.
    /// </summary>
    [Fact]
    public async Task Rehydration_RestoresCreatedBy()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-adminauth-").FullName;
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "m1",
                """{"type":"created","schemaVersion":2,"at":"2026-07-05T12:00:00+00:00","matchId":"m1","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7,"createdBy":"director"}""",
                """{"type":"terminal","at":"2026-07-05T12:05:00+00:00","status":"completed","winner":"Alpha","seatOneScore":1,"seatTwoScore":0}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = factory.CreateClient();
            await http.GetAsync("/engines"); // force the host (and rehydration) to run

            var matches = factory.Services.GetRequiredService<MatchService>();
            Assert.True(matches.TryGetRecord("m1", out var record));
            Assert.Equal("director", record.CreatedBy);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>Wait for engine registrations through an already-keyed client.</summary>
    private static async Task WaitForEnginesAsync(HttpClient http, params string[] names)
    {
        var deadline = DateTime.UtcNow + ServerHarness.ReceiveDeadline;
        while (DateTime.UtcNow < deadline)
        {
            var engines = await http.GetFromJsonAsync<JsonElement>("/engines");
            var connected = engines.EnumerateArray()
                .Select(engine => engine.GetProperty("name").GetString())
                .ToHashSet();
            if (names.All(connected.Contains))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Engines [{string.Join(", ", names)}] did not all register within {ServerHarness.ReceiveDeadline}.");
    }

    /// <summary>Poll one match to its end through an already-keyed client.</summary>
    private static async Task WaitForMatchEndAsync(HttpClient http, string matchId)
    {
        var deadline = DateTime.UtcNow + ServerHarness.ReceiveDeadline;
        while (DateTime.UtcNow < deadline)
        {
            var match = await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}");
            if (match.GetProperty("status").GetString() != "running")
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Match {matchId} still running after {ServerHarness.ReceiveDeadline}.");
    }

    /// <summary>Poll one tournament to its end through an already-keyed client.</summary>
    private static async Task<JsonElement> WaitForTournamentEndAsync(HttpClient http, string tournamentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tournament = await http.GetFromJsonAsync<JsonElement>($"/tournaments/{tournamentId}");
            if (tournament.GetProperty("status").GetString() != "running")
            {
                return tournament;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Tournament {tournamentId} still running after 60 s.");
    }
}
