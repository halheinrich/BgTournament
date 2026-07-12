using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BgGame_Lib;
using BgInference;
using BgMoveGen;
using BgTournament.Api;
using BgTournament.EngineClient;
using BgTournament.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BgTournament.Tests;

/// <summary>Shared plumbing for wire-level server tests over TestServer.</summary>
internal static class ServerHarness
{
    public static readonly TimeSpan ReceiveDeadline = TimeSpan.FromSeconds(10);

    /// <summary>The parity fixtures, consumed in place from the sibling checkout (never copied).</summary>
    public static string ParityModelPath { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "BgRLEngine", "BgRLEngine", "parity", "model.onnx"));

    public static WebApplicationFactory<Program> NewFactory(
        double? decisionTimeoutSeconds = null, TimeProvider? timeProvider = null,
        string? dataDirectory = null, IReadOnlyDictionary<string, string>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Every factory journals into an isolated directory in the system
            // temp area — never the repo checkout, never shared between tests.
            // Rehydration tests pass the same directory to a second factory to
            // restart "the server" over the same durable store.
            builder.UseSetting(
                "Persistence:DataDirectory",
                dataDirectory ?? Directory.CreateTempSubdirectory("bgtournament-test-").FullName);

            if (decisionTimeoutSeconds is { } seconds)
            {
                builder.UseSetting(
                    "Tournament:DecisionTimeoutSeconds", seconds.ToString(CultureInfo.InvariantCulture));
            }

            // Arbitrary extra configuration (e.g. Admin:ApiKeys:<name> entries).
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (timeProvider is not null)
            {
                // Replace the server's timestamp source (clock logic and journal
                // timestamps read time only through it), making both
                // test-controlled.
                builder.ConfigureServices(services => services.AddSingleton(timeProvider));
            }
        });

    /// <summary>
    /// Connect a real EngineClient (random play, passive cube) — the connect
    /// is awaited here so a failure surfaces in the test, not in a swallowed
    /// background task — then serve in the background until teardown.
    /// </summary>
    public static async Task RunWellBehavedClientAsync(
        WebApplicationFactory<Program> factory, string name, int seed, CancellationToken cancellationToken)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), cancellationToken);
        var client = new EngineClient.EngineClient(
            new EngineIdentity(name), new RandomPlayAgent(seed), new PassiveCubeAgent());
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await client.ServeAsync(socket, cancellationToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                    // Test teardown: server or token ended the session.
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Connect a real EngineClient (random play, passive cube) that verifies its
    /// fair-mode dice on match end, delivering each report to
    /// <paramref name="onVerified"/>. Connect awaited here; served in the
    /// background until teardown.
    /// </summary>
    public static async Task RunVerifyingClientAsync(
        WebApplicationFactory<Program> factory,
        string name,
        int seed,
        Action<DiceVerificationReport> onVerified,
        CancellationToken cancellationToken)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), cancellationToken);
        var client = new EngineClient.EngineClient(
            new EngineIdentity(name), new RandomPlayAgent(seed), new PassiveCubeAgent(),
            logger: null, onDiceVerified: onVerified);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await client.ServeAsync(socket, cancellationToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                    // Test teardown: server or token ended the session.
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Connect a real EngineClient over BgInference's agents (the dogfooding
    /// door: exactly the way a third-party engine enters) — connect awaited
    /// here, then served in the background until teardown.
    /// </summary>
    public static async Task RunBgInferenceClientAsync(
        WebApplicationFactory<Program> factory, string name, OnnxEvaluator evaluator)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var client = new EngineClient.EngineClient(
            new EngineIdentity(name, version: "parity-model"),
            new OnePlyPlayAgent(evaluator),
            new ThresholdCubeAgent(evaluator));
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ServeAsync(socket);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                // Test teardown.
            }
        });
    }

    public static async Task WaitForEnginesAsync(WebApplicationFactory<Program> factory, params string[] names)
    {
        using var http = factory.CreateClient();
        var deadline = DateTime.UtcNow + ReceiveDeadline;
        while (DateTime.UtcNow < deadline)
        {
            var engines = await http.GetFromJsonAsync<JsonElement>("/engines");
            var connected = engines.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToHashSet();
            if (names.All(connected.Contains))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Engines [{string.Join(", ", names)}] did not all register within {ReceiveDeadline}.");
    }

    public static async Task<JsonElement> StartMatchAsync(
        WebApplicationFactory<Program> factory,
        string engineOne,
        string engineTwo,
        int matchLength,
        int seed,
        int? maxGames = null,
        TimeControl? timeControl = null)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/matches",
            new { engineOne, engineTwo, matchLength, seed, maxGames, timeControl });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"POST /matches failed: {response.StatusCode} {body}");
        return body;
    }

    /// <summary>Start a match with no seed — selecting fair (verifiable) dice mode.</summary>
    public static async Task<JsonElement> StartFairMatchAsync(
        WebApplicationFactory<Program> factory,
        string engineOne,
        string engineTwo,
        int matchLength,
        int? maxGames = null)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/matches",
            new { engineOne, engineTwo, matchLength, maxGames });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"POST /matches (fair) failed: {response.StatusCode} {body}");
        return body;
    }

    public static async Task<JsonElement> GetMatchAsync(WebApplicationFactory<Program> factory, string matchId)
    {
        using var http = factory.CreateClient();
        return await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}");
    }

    /// <summary>Poll the match record until it leaves <c>running</c>.</summary>
    public static async Task<JsonElement> WaitForMatchEndAsync(
        WebApplicationFactory<Program> factory, string matchId, TimeSpan? deadlineOverride = null)
    {
        var deadline = DateTime.UtcNow + (deadlineOverride ?? ReceiveDeadline);
        while (DateTime.UtcNow < deadline)
        {
            var match = await GetMatchAsync(factory, matchId);
            if (match.GetProperty("status").GetString() != "running")
            {
                return match;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Match {matchId} still running after {ReceiveDeadline}.");
    }

    public static async Task<JsonElement> StartTournamentAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyList<string> participants,
        int matchLength,
        int matchesPerPairing,
        int seed,
        TimeControl? timeControl = null)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/tournaments",
            new { participants, matchLength, matchesPerPairing, seed, timeControl });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"POST /tournaments failed: {response.StatusCode} {body}");
        return body;
    }

    /// <summary>Start a tournament with no seed — selecting fair (verifiable) dice for every match.</summary>
    public static async Task<JsonElement> StartFairTournamentAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyList<string> participants,
        int matchLength,
        int matchesPerPairing)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/tournaments",
            new { participants, matchLength, matchesPerPairing });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"POST /tournaments (fair) failed: {response.StatusCode} {body}");
        return body;
    }

    public static async Task<JsonElement> GetTournamentAsync(
        WebApplicationFactory<Program> factory, string tournamentId)
    {
        using var http = factory.CreateClient();
        return await http.GetFromJsonAsync<JsonElement>($"/tournaments/{tournamentId}");
    }

    /// <summary>Poll the tournament record until it leaves <c>running</c>.</summary>
    public static async Task<JsonElement> WaitForTournamentEndAsync(
        WebApplicationFactory<Program> factory, string tournamentId, TimeSpan? deadlineOverride = null)
    {
        var deadline = DateTime.UtcNow + (deadlineOverride ?? ReceiveDeadline);
        while (DateTime.UtcNow < deadline)
        {
            var tournament = await GetTournamentAsync(factory, tournamentId);
            if (tournament.GetProperty("status").GetString() != "running")
            {
                return tournament;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Tournament {tournamentId} still running after {deadlineOverride ?? ReceiveDeadline}.");
    }

    /// <summary>
    /// Hand-write one raw journal file into a data directory — the lever for
    /// durable-format tests (old-version tolerance, damage policies, evidence
    /// projection) that must control the exact bytes on disk.
    /// </summary>
    public static async Task WriteJournalAsync(
        string dataDirectory, string kind, string id, params string[] lines)
    {
        string directory = Path.Combine(dataDirectory, kind);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, id + ".jsonl"), string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// The first seed in 1.. whose opening roll (after re-rolled ties) gives
    /// seat One the higher die — i.e. seat One is queried first. Computed
    /// from the substrate's own SeededDiceSource, not re-derived.
    /// </summary>
    public static int SeedWhereSeatOneMovesFirst()
    {
        for (int seed = 1; seed < 1000; seed++)
        {
            var dice = new SeededDiceSource(seed);
            (int die1, int die2) = dice.Roll();
            while (die1 == die2)
            {
                (die1, die2) = dice.Roll();
            }

            if (die1 > die2)
            {
                return seed;
            }
        }

        throw new InvalidOperationException("No qualifying seed below 1000 — implausible.");
    }
}

/// <summary>
/// A scriptable raw-wire engine: the tests speak the protocol directly (and
/// can deliberately break it) instead of going through the SDK.
/// </summary>
internal sealed class TestEngine : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ProtocolSocket _channel;

    private TestEngine(WebSocket socket, string name)
    {
        _socket = socket;
        _channel = new ProtocolSocket(socket);
        Name = name;
    }

    public string Name { get; }

    public static async Task<TestEngine> ConnectAsync(
        WebApplicationFactory<Program> factory,
        string name,
        int protocolVersion = WireProtocol.Version,
        bool expectWelcome = true)
    {
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var engine = new TestEngine(socket, name);
        await engine.SendAsync(new HelloMessage { ProtocolVersion = protocolVersion, EngineName = name });
        if (expectWelcome)
        {
            await engine.ExpectAsync<WelcomeMessage>();
        }

        return engine;
    }

    public Task SendAsync(ProtocolMessage message) =>
        _channel.SendAsync(message, CancellationToken.None);

    /// <summary>Send raw text — the deliberately-malformed-frame lever.</summary>
    public Task SendRawAsync(string text) =>
        _socket.SendAsync(
            Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    public async Task<ProtocolMessage?> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(ServerHarness.ReceiveDeadline);
        return await _channel.ReceiveAsync(timeout.Token);
    }

    /// <summary>Receive, optionally skipping matchStarted, and assert the message type.</summary>
    public async Task<T> ExpectAsync<T>(bool skipMatchStarted = false)
        where T : ProtocolMessage
    {
        while (true)
        {
            var message = await ReceiveAsync();
            Assert.NotNull(message);
            if (skipMatchStarted && message is MatchStartedMessage)
            {
                continue;
            }

            return Assert.IsType<T>(message);
        }
    }

    /// <summary>
    /// The next decision query, skipping matchStarted; cube-offer queries are
    /// answered noDouble along the way when <paramref name="declineCubeOffers"/>.
    /// </summary>
    public async Task<PlayQueryMessage> ExpectPlayQueryAsync(bool declineCubeOffers = true)
    {
        while (true)
        {
            var message = await ReceiveAsync();
            Assert.NotNull(message);
            switch (message)
            {
                case PlayQueryMessage play:
                    return play;
                case CubeOfferQueryMessage offer when declineCubeOffers:
                    await SendAsync(new CubeOfferReplyMessage
                    {
                        RequestId = offer.RequestId,
                        Action = CubeOfferAction.NoDouble,
                    });
                    break;
                case MatchStartedMessage:
                    break;
                default:
                    Assert.Fail($"Expected a play query, got {message.GetType().Name}.");
                    break;
            }
        }
    }

    /// <summary>Answer a play query with the first legal play (computed from the wire state).</summary>
    public async Task ReplyWithFirstLegalPlayAsync(PlayQueryMessage query)
    {
        var state = query.State.ToGameState();
        var candidates = MoveGenerator.GeneratePlays(state.Board, query.Die1, query.Die2);
        var play = candidates.Count == 0 ? default : candidates[0];
        await SendAsync(new PlayReplyMessage { RequestId = query.RequestId, Moves = play.ToWireMoves() });
    }

    /// <summary>Tear the connection down abruptly (the disconnect lever).</summary>
    public void Abort() => _socket.Abort();

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
