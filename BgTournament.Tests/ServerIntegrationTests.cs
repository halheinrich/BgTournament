using System.Net.Http.Json;
using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Wire-level behavior of the server over an in-proc TestServer: the
/// handshake gate, and the forfeit taxonomy end to end — illegal play,
/// malformed frame, timeout, disconnect mid-query, and disconnect while the
/// other engine is thinking — each with the right seat attributed.
/// </summary>
public class ServerIntegrationTests
{
    [Fact]
    public async Task Handshake_VersionMismatch_IsRejectedNamingBothVersions()
    {
        using var factory = ServerHarness.NewFactory();
        await using var engine = await TestEngine.ConnectAsync(
            factory, "TimeTraveler", protocolVersion: 99, expectWelcome: false);

        var rejected = await engine.ExpectAsync<RejectedMessage>();

        Assert.Contains("99", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains(WireProtocol.Version.ToString(), rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_DuplicateName_IsRejected()
    {
        using var factory = ServerHarness.NewFactory();
        await using var first = await TestEngine.ConnectAsync(factory, "Highlander");
        await using var second = await TestEngine.ConnectAsync(factory, "Highlander", expectWelcome: false);

        var rejected = await second.ExpectAsync<RejectedMessage>();

        Assert.Contains("Highlander", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_FirstMessageNotHello_IsRejected()
    {
        using var factory = ServerHarness.NewFactory();
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var channel = new ProtocolSocket(socket);

        await channel.SendAsync(
            new PlayReplyMessage { RequestId = "q-0", Moves = Array.Empty<WireMove>() },
            CancellationToken.None);

        var rejected = Assert.IsType<RejectedMessage>(await channel.ReceiveAsync(CancellationToken.None));
        Assert.Contains("hello", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnginesEndpoint_ListsIdentityAndIdleState()
    {
        using var factory = ServerHarness.NewFactory();
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var channel = new ProtocolSocket(socket);
        await channel.SendAsync(
            new HelloMessage
            {
                ProtocolVersion = WireProtocol.Version,
                EngineName = "Ident",
                EngineVersion = "3.1",
                Author = "Jane",
            },
            CancellationToken.None);
        Assert.IsType<WelcomeMessage>(await channel.ReceiveAsync(CancellationToken.None));

        await ServerHarness.WaitForEnginesAsync(factory, "Ident");
        using var http = factory.CreateClient();
        var engines = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/engines");

        var ident = engines.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Ident");
        Assert.Equal("3.1", ident.GetProperty("version").GetString());
        Assert.Equal("Jane", ident.GetProperty("author").GetString());
        Assert.False(ident.GetProperty("inMatch").GetBoolean());
    }

    [Fact]
    public async Task IllegalPlayReply_ForfeitsTheOffendingSeat_AndInformsIt()
    {
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, clientCts.Token);
        await using var bad = await TestEngine.ConnectAsync(factory, "Bad");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Bad");

        var match = await ServerHarness.StartMatchAsync(factory, "Bad", "Good", matchLength: 3, seed: 42);
        string matchId = match.GetProperty("matchId").GetString()!;

        // First play query to Bad: a play from point 3 — where it has no
        // checker in its first turn — is illegal for any dice.
        var query = await bad.ExpectPlayQueryAsync();
        await bad.SendAsync(new PlayReplyMessage
        {
            RequestId = query.RequestId,
            Moves = new[] { new WireMove { From = 3, To = 2 } },
        });

        var ended = await bad.ExpectAsync<MatchEndedMessage>(skipMatchStarted: true);
        Assert.Equal(MatchEndReason.Forfeit, ended.Reason);
        Assert.Equal(ForfeitSide.You, ended.ForfeitedBy);
        Assert.False(ended.YouWon);

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Bad", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Good", record.GetProperty("winner").GetString());
        Assert.False(string.IsNullOrEmpty(record.GetProperty("detail").GetString()));
        clientCts.Cancel();
    }

    [Fact]
    public async Task MalformedFrameReply_ForfeitsTheOffendingSeat()
    {
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, clientCts.Token);
        await using var bad = await TestEngine.ConnectAsync(factory, "Garbler");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Garbler");

        var match = await ServerHarness.StartMatchAsync(factory, "Garbler", "Good", matchLength: 3, seed: 42);
        string matchId = match.GetProperty("matchId").GetString()!;

        _ = await bad.ExpectPlayQueryAsync();
        await bad.SendRawAsync("this is not json {{");

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Garbler", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Good", record.GetProperty("winner").GetString());
        Assert.Contains("protocol", record.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        clientCts.Cancel();
    }

    [Fact]
    public async Task DecisionTimeout_ForfeitsTheSilentSeat_AndInformsIt()
    {
        using var factory = ServerHarness.NewFactory(decisionTimeoutSeconds: 0.5);
        using var clientCts = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, clientCts.Token);
        await using var silent = await TestEngine.ConnectAsync(factory, "Sloth");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Sloth");

        var match = await ServerHarness.StartMatchAsync(factory, "Sloth", "Good", matchLength: 3, seed: 42);
        string matchId = match.GetProperty("matchId").GetString()!;

        _ = await silent.ExpectPlayQueryAsync();
        // ... and say nothing.

        var ended = await silent.ExpectAsync<MatchEndedMessage>(skipMatchStarted: true);
        Assert.Equal(MatchEndReason.Forfeit, ended.Reason);
        Assert.Equal(ForfeitSide.You, ended.ForfeitedBy);

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Sloth", record.GetProperty("forfeitedBy").GetString());
        Assert.Contains("did not answer", record.GetProperty("detail").GetString(), StringComparison.Ordinal);
        clientCts.Cancel();
    }

    [Fact]
    public async Task DisconnectMidQuery_ForfeitsTheVanishedSeat()
    {
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Good", seed: 11, clientCts.Token);
        await using var quitter = await TestEngine.ConnectAsync(factory, "Rager");
        await ServerHarness.WaitForEnginesAsync(factory, "Good", "Rager");

        var match = await ServerHarness.StartMatchAsync(factory, "Rager", "Good", matchLength: 3, seed: 42);
        string matchId = match.GetProperty("matchId").GetString()!;

        _ = await quitter.ExpectPlayQueryAsync();
        quitter.Abort();

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Rager", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Good", record.GetProperty("winner").GetString());
        clientCts.Cancel();
    }

    /// <summary>
    /// The proactive case: the disconnecting engine is NOT the one being
    /// queried. Seat One is mid-think on the first play query when seat Two
    /// vanishes; the forfeit must land on seat Two, and seat One — still
    /// holding an unanswered query — is told it won.
    /// </summary>
    [Fact]
    public async Task DisconnectWhileOpponentIsThinking_ForfeitsTheVanishedSeat_NotTheThinker()
    {
        int seed = ServerHarness.SeedWhereSeatOneMovesFirst();
        using var factory = ServerHarness.NewFactory();
        await using var thinker = await TestEngine.ConnectAsync(factory, "Thinker");
        await using var quitter = await TestEngine.ConnectAsync(factory, "Quitter");
        await ServerHarness.WaitForEnginesAsync(factory, "Thinker", "Quitter");

        var match = await ServerHarness.StartMatchAsync(factory, "Thinker", "Quitter", matchLength: 3, seed: seed);
        string matchId = match.GetProperty("matchId").GetString()!;

        // Thinker (seat One, first mover for this seed) receives its query
        // and goes quiet — legitimately deciding.
        _ = await thinker.ExpectPlayQueryAsync();

        // Quitter vanishes while its opponent is thinking.
        await quitter.ExpectAsync<MatchStartedMessage>();
        quitter.Abort();

        // The thinker is told the match ended in its favor.
        var ended = await thinker.ExpectAsync<MatchEndedMessage>();
        Assert.Equal(MatchEndReason.Forfeit, ended.Reason);
        Assert.Equal(ForfeitSide.Opponent, ended.ForfeitedBy);
        Assert.True(ended.YouWon);

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Quitter", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("Thinker", record.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task StartMatch_UnknownEngine_Is404_AndBusyEngineIs409()
    {
        using var factory = ServerHarness.NewFactory();
        await using var lonely = await TestEngine.ConnectAsync(factory, "Lonely");
        await ServerHarness.WaitForEnginesAsync(factory, "Lonely");
        using var http = factory.CreateClient();

        var unknown = await http.PostAsJsonAsync(
            "/matches", new { engineOne = "Lonely", engineTwo = "Ghost", matchLength = 3 });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);

        var same = await http.PostAsJsonAsync(
            "/matches", new { engineOne = "Lonely", engineTwo = "Lonely", matchLength = 3 });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, same.StatusCode);

        var money = await http.PostAsJsonAsync(
            "/matches", new { engineOne = "Lonely", engineTwo = "Ghost", matchLength = 0 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, money.StatusCode);
    }
}
