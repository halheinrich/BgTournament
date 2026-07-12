using System.Net.Http.Json;
using System.Net.WebSockets;
using BgTournament.Api;
using BgTournament.EngineClient;
using BgTournament.Protocol;
using BgTournament.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The wire's registration gate end to end (PROTOCOL.md §3.1): a presented
/// <c>engineKey</c> is always validated — under Open and Registered alike —
/// and only the Registered policy refuses keyless hellos. The key resolves
/// the roster identity, the claimed name must match it, deactivation and
/// rotation kill old keys immediately, and every rejection is a named
/// <c>rejected</c> message journaled through the existing handshake funnel.
/// </summary>
public class WireEnforcementTests
{
    private static readonly Dictionary<string, string> Registered = new()
    {
        ["Tournament:EnginePolicy"] = "Registered",
    };

    private static async Task<string> RegisterAsync(WebApplicationFactory<Program> factory, string name)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/roster",
            new { name, attestation = new { authors = new[] { "Jane Doe" }, origin = "Original work." } });
        Assert.True(response.IsSuccessStatusCode, $"POST /roster failed: {response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<EngineKeyGrant>())!.EngineKey;
    }

    private static async Task<string> ExpectRejectionAsync(
        WebApplicationFactory<Program> factory, string name, string? engineKey)
    {
        await using var engine = await TestEngine.ConnectAsync(
            factory, name, expectWelcome: false, engineKey: engineKey);
        var rejected = await engine.ExpectAsync<RejectedMessage>();
        return rejected.Reason;
    }

    // ---- Open policy: keyless is today's wire; a presented key still validates ----

    [Fact]
    public async Task Open_KeylessHello_Welcomed()
    {
        using var factory = ServerHarness.NewFactory();
        await using var engine = await TestEngine.ConnectAsync(factory, "Alpha");
        // Welcome asserted inside ConnectAsync — the pre-registration wire, unchanged.
    }

    [Fact]
    public async Task Open_KeyAgainstEmptyRoster_RejectedLoudly()
    {
        using var factory = ServerHarness.NewFactory();
        string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: new string('a', 64));
        Assert.Contains("no engine roster", reason);
    }

    [Fact]
    public async Task Open_UnknownKey_RejectedLoudly()
    {
        using var factory = ServerHarness.NewFactory();
        await RegisterAsync(factory, "Alpha");

        string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: new string('a', 64));
        Assert.Contains("not recognized", reason);
    }

    /// <summary>An Open server accepts a valid key too — registration is usable before enforcement is switched on.</summary>
    [Fact]
    public async Task Open_ValidKey_Welcomed()
    {
        using var factory = ServerHarness.NewFactory();
        string key = await RegisterAsync(factory, "Alpha");
        await using var engine = await TestEngine.ConnectAsync(factory, "Alpha", engineKey: key);
    }

    // ---- Registered policy ----

    [Fact]
    public async Task Registered_KeylessHello_Rejected_AndJournaled()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-enforce-").FullName;
        try
        {
            using (var factory = ServerHarness.NewFactory(
                dataDirectory: dataDirectory, settings: Registered))
            {
                string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: null);
                Assert.Contains("registered engines only", reason);
            }   // dispose closes the server-journal segment

            // The same reason, verbatim, in the handshake-rejection evidence —
            // the existing funnel needed no new event type.
            string segment = Assert.Single(
                Directory.GetFiles(Path.Combine(dataDirectory, "server"), "*.jsonl"));
            var rejected = (await File.ReadAllLinesAsync(segment))
                .Select(JournalCodec.DeserializeServerEvent)
                .OfType<HandshakeRejectedEvent>()
                .Single();
            Assert.Contains("registered engines only", rejected.Reason);
            Assert.Equal("Alpha", rejected.EngineName);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The full round-trip through the SDK: register over the admin surface,
    /// hand the show-once key to an EngineClient, and the engine is admitted
    /// by an enforcing server and listed as connected.
    /// </summary>
    [Fact]
    public async Task Registered_ValidKey_RoundTripThroughSdk()
    {
        using var factory = ServerHarness.NewFactory(settings: Registered);
        string key = await RegisterAsync(factory, "Alpha");

        using var cts = new CancellationTokenSource(ServerHarness.ReceiveDeadline);
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), cts.Token);
        var client = new EngineClient.EngineClient(
            new EngineIdentity("Alpha"), new RandomPlayAgent(seed: 1), new PassiveCubeAgent(),
            engineKey: key);
        var serving = Task.Run(
            async () =>
            {
                try
                {
                    await client.ServeAsync(socket, cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                    // Test teardown.
                }
            },
            CancellationToken.None);

        await ServerHarness.WaitForEnginesAsync(factory, "Alpha");
        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Registered_NameMismatch_Rejected()
    {
        using var factory = ServerHarness.NewFactory(settings: Registered);
        string alphaKey = await RegisterAsync(factory, "Alpha");

        // A valid key claiming a different name: the key authenticates the
        // name, it does not rename the engine — and the rejection does not
        // echo whose key it is.
        string reason = await ExpectRejectionAsync(factory, "Beta", engineKey: alphaKey);
        Assert.Contains("does not match", reason);
        Assert.DoesNotContain("Alpha", reason);
    }

    [Fact]
    public async Task Registered_DeactivatedKey_Rejected()
    {
        using var factory = ServerHarness.NewFactory(settings: Registered);
        string key = await RegisterAsync(factory, "Alpha");
        using (var http = factory.CreateClient())
        {
            Assert.True((await http.PostAsync("/roster/Alpha/deactivate", null)).IsSuccessStatusCode);
        }

        string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: key);
        Assert.Contains("deactivated", reason);
    }

    /// <summary>Rotation kills the old key immediately; the fresh one is admitted.</summary>
    [Fact]
    public async Task Registered_RotatedKey_OldRefusedNewAdmitted()
    {
        using var factory = ServerHarness.NewFactory(settings: Registered);
        string oldKey = await RegisterAsync(factory, "Alpha");

        string newKey;
        using (var http = factory.CreateClient())
        {
            var response = await http.PostAsync("/roster/Alpha/rotate", null);
            Assert.True(response.IsSuccessStatusCode);
            newKey = (await response.Content.ReadFromJsonAsync<EngineKeyGrant>())!.EngineKey;
        }

        string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: oldKey);
        Assert.Contains("not recognized", reason);

        await using var engine = await TestEngine.ConnectAsync(factory, "Alpha", engineKey: newKey);
    }

    /// <summary>
    /// Registered with an empty roster is a valid (warned-about) boot, not a
    /// startup failure: the natural bootstrap is boot-enforcing → register →
    /// connect, so the server serves and rejects until the roster fills.
    /// </summary>
    [Fact]
    public async Task Registered_EmptyRoster_BootsAndRejects()
    {
        using var factory = ServerHarness.NewFactory(settings: Registered);

        string reason = await ExpectRejectionAsync(factory, "Alpha", engineKey: null);
        Assert.Contains("registered engines only", reason);

        // …and registering repairs it at runtime, no restart.
        string key = await RegisterAsync(factory, "Alpha");
        await using var engine = await TestEngine.ConnectAsync(factory, "Alpha", engineKey: key);
    }

    /// <summary>A policy typo must fail the boot, never silently serve open.</summary>
    [Fact]
    public void InvalidPolicyValue_FailsStartup()
    {
        using var factory = ServerHarness.NewFactory(settings: new Dictionary<string, string>
        {
            ["Tournament:EnginePolicy"] = "Anything",
        });

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }
}
