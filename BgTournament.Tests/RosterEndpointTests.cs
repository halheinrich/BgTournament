using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BgTournament.Api;
using BgTournament.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The roster's admin surface end to end: registration records the
/// attestation and issues a show-once engine key (the salted hash is what
/// lands on disk — never a plaintext key byte), rotation and deactivation
/// enforce the entry lifecycle, the acting admin stamps every roster event,
/// and the whole roster folds back identically across restarts from its
/// per-boot journal segments.
/// </summary>
public class RosterEndpointTests
{
    private static readonly EngineAttestation Attestation = new(
        ["Jane Doe"], "Original neural-net engine.", DerivedFrom: null);

    private static object AttestationBody => new
    {
        authors = new[] { "Jane Doe" },
        origin = "Original neural-net engine.",
    };

    private static async Task<EngineKeyGrant> RegisterAsync(
        HttpClient http, string name, object? attestation = null)
    {
        var response = await http.PostAsJsonAsync(
            "/roster", new { name, attestation = attestation ?? AttestationBody });
        Assert.True(response.IsSuccessStatusCode, $"POST /roster failed: {response.StatusCode}");
        var grant = await response.Content.ReadFromJsonAsync<EngineKeyGrant>();
        Assert.NotNull(grant);
        return grant;
    }

    [Fact]
    public async Task Register_IssuesShowOnceKey_AndLists()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var grant = await RegisterAsync(http, "MyBot");

        // The issued key is a 64-hex string; the entry is active, and (open
        // admin surface) registeredBy is honestly null.
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), grant.EngineKey);
        Assert.Equal("MyBot", grant.Entry.Name);
        Assert.True(grant.Entry.Active);
        Assert.Null(grant.Entry.RegisteredBy);
        Assert.Equal(Attestation.Authors, grant.Entry.Attestation.Authors);
        Assert.Equal(Attestation.Origin, grant.Entry.Attestation.Origin);
        Assert.Null(grant.Entry.Attestation.DerivedFrom);

        var listed = await http.GetFromJsonAsync<IReadOnlyList<RosterEntry>>("/roster");
        Assert.Equal("MyBot", Assert.Single(listed!).Name);

        var fetched = await http.GetFromJsonAsync<RosterEntry>("/roster/MyBot");
        Assert.Equal("MyBot", fetched!.Name);
        Assert.Equal(grant.Entry.RegisteredAtUtc, fetched.RegisteredAtUtc);
    }

    [Fact]
    public async Task Register_DuplicateName_Conflict()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();
        await RegisterAsync(http, "MyBot");

        var response = await http.PostAsJsonAsync(
            "/roster", new { name = "MyBot", attestation = AttestationBody });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("already registered", error!.Error);
    }

    [Theory]
    [InlineData("""{"attestation":{"authors":["Jane"],"origin":"x"}}""")]              // no name
    [InlineData("""{"name":"  ","attestation":{"authors":["Jane"],"origin":"x"}}""")]  // blank name
    [InlineData("""{"name":"MyBot"}""")]                                               // no attestation
    [InlineData("""{"name":"MyBot","attestation":{"authors":[],"origin":"x"}}""")]     // no authors
    [InlineData("""{"name":"MyBot","attestation":{"authors":[" "],"origin":"x"}}""")]  // blank author
    [InlineData("""{"name":"MyBot","attestation":{"authors":["Jane"],"origin":""}}""")]     // blank origin
    [InlineData("""{"name":"MyBot","attestation":{"authors":["Jane"],"origin":"x","derivedFrom":" "}}""")]
    public async Task Register_InvalidRequest_BadRequest(string body)
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var response = await http.PostAsync(
            "/roster", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The tier-C stamp plus the credential posture, on the raw bytes: the
    /// acting admin's name is journaled on the roster event, and the issued
    /// key never appears in the file — only its salted hash under the pinned
    /// scheme id.
    /// </summary>
    [Fact]
    public async Task Register_ActorStamped_NoPlaintextKeyOnDisk()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-roster-").FullName;
        try
        {
            string issuedKey;
            using (var factory = ServerHarness.NewFactory(
                dataDirectory: dataDirectory,
                settings: new Dictionary<string, string> { ["Admin:ApiKeys:director"] = "director-secret" }))
            {
                using var http = factory.CreateClient();
                http.DefaultRequestHeaders.Add(AdminApiKey.HeaderName, "director-secret");

                var grant = await RegisterAsync(http, "MyBot");
                issuedKey = grant.EngineKey;
                Assert.Equal("director", grant.Entry.RegisteredBy);
            }   // disposing the factory closes the roster segment

            string segment = Assert.Single(
                Directory.GetFiles(Path.Combine(dataDirectory, "roster"), "*.jsonl"));
            string raw = await File.ReadAllTextAsync(segment);
            Assert.DoesNotContain(issuedKey, raw);
            Assert.Contains("sha256-salted-v1", raw);

            var events = (await File.ReadAllLinesAsync(segment))
                .Select(JournalCodec.DeserializeRosterEvent)
                .ToArray();
            Assert.Equal(JournalCodec.RosterSchemaVersion,
                Assert.IsType<RosterStartedEvent>(events[0]).SchemaVersion);
            var registered = Assert.IsType<EngineRegisteredEvent>(events[1]);
            Assert.Equal("MyBot", registered.Name);
            Assert.Equal("director", registered.Actor);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Rotate_IssuesFreshKey()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();
        var original = await RegisterAsync(http, "MyBot");

        var response = await http.PostAsync("/roster/MyBot/rotate", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var rotated = await response.Content.ReadFromJsonAsync<EngineKeyGrant>();

        Assert.NotEqual(original.EngineKey, rotated!.EngineKey);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), rotated.EngineKey);
        Assert.NotNull(rotated.Entry.KeyRotatedAtUtc);
        Assert.True(rotated.Entry.Active);
    }

    [Fact]
    public async Task Deactivate_IsTerminal()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();
        await RegisterAsync(http, "MyBot");

        var response = await http.PostAsync("/roster/MyBot/deactivate", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var entry = await response.Content.ReadFromJsonAsync<RosterEntry>();
        Assert.False(entry!.Active);
        Assert.NotNull(entry.DeactivatedAtUtc);

        // Every further mutation is refused: deactivation ends the entry's
        // lifecycle (the name stays reserved, the history stays served).
        Assert.Equal(
            HttpStatusCode.Conflict, (await http.PostAsync("/roster/MyBot/rotate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict, (await http.PostAsync("/roster/MyBot/deactivate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync("/roster/MyBot/attestation", AttestationBody)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync("/roster", new { name = "MyBot", attestation = AttestationBody }))
                .StatusCode);

        // The deactivated entry still lists — history is evidence.
        var listed = await http.GetFromJsonAsync<IReadOnlyList<RosterEntry>>("/roster");
        Assert.False(Assert.Single(listed!).Active);
    }

    [Fact]
    public async Task Attestation_Redeclared()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();
        await RegisterAsync(http, "MyBot");

        var response = await http.PostAsJsonAsync(
            "/roster/MyBot/attestation",
            new { authors = new[] { "Jane Doe" }, origin = "Corrected: derived.", derivedFrom = "gnubg" });
        Assert.True(response.IsSuccessStatusCode);
        var entry = await response.Content.ReadFromJsonAsync<RosterEntry>();
        Assert.Equal("Corrected: derived.", entry!.Attestation.Origin);
        Assert.Equal("gnubg", entry.Attestation.DerivedFrom);
    }

    [Fact]
    public async Task UnknownName_NotFound()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/roster/Ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/roster/Ghost/rotate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await http.PostAsync("/roster/Ghost/deactivate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/roster/Ghost/attestation", AttestationBody)).StatusCode);
    }

    /// <summary>
    /// The roster is durable across restarts as the fold of its per-boot
    /// segments: each mutating session appends its own segment, and a later
    /// boot folds them all in order — registration order, rotation state, and
    /// deactivation all survive.
    /// </summary>
    [Fact]
    public async Task Rehydration_FoldsAcrossSegments()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-roster-").FullName;
        try
        {
            using (var first = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = first.CreateClient();
                await RegisterAsync(http, "Alpha");
            }

            using (var second = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = second.CreateClient();

                // The first boot's registration folded back.
                var listed = await http.GetFromJsonAsync<IReadOnlyList<RosterEntry>>("/roster");
                Assert.Equal("Alpha", Assert.Single(listed!).Name);

                // Mutate in a second segment: a new engine and a rotation.
                await RegisterAsync(http, "Beta");
                Assert.True((await http.PostAsync("/roster/Alpha/rotate", null)).IsSuccessStatusCode);
                Assert.True((await http.PostAsync("/roster/Beta/deactivate", null)).IsSuccessStatusCode);
            }

            Assert.Equal(2, Directory.GetFiles(Path.Combine(dataDirectory, "roster"), "*.jsonl").Length);

            using (var third = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = third.CreateClient();
                var listed = await http.GetFromJsonAsync<IReadOnlyList<RosterEntry>>("/roster");
                Assert.Equal(2, listed!.Count);
                Assert.Equal("Alpha", listed[0].Name);       // registration order, stable
                Assert.NotNull(listed[0].KeyRotatedAtUtc);   // the rotation folded
                Assert.True(listed[0].Active);
                Assert.Equal("Beta", listed[1].Name);
                Assert.False(listed[1].Active);              // the deactivation folded
            }
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A read-only boot writes no roster segment: segments are created
    /// lazily on the first mutation, so idle boots never accrete files.
    /// </summary>
    [Fact]
    public async Task ReadOnlyBoot_WritesNoSegment()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-roster-").FullName;
        try
        {
            using (var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = factory.CreateClient();
                Assert.Empty((await http.GetFromJsonAsync<IReadOnlyList<RosterEntry>>("/roster"))!);
            }

            Assert.False(Directory.Exists(Path.Combine(dataDirectory, "roster")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The roster endpoints sit behind the session-1 admin identity gate
    /// automatically — the whole-surface middleware needed no new code.
    /// </summary>
    [Fact]
    public async Task RosterEndpoints_BehindAdminGate()
    {
        using var factory = ServerHarness.NewFactory(
            settings: new Dictionary<string, string> { ["Admin:ApiKeys:director"] = "director-secret" });
        using var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/roster")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/roster", new { name = "MyBot", attestation = AttestationBody }))
                .StatusCode);
    }
}
