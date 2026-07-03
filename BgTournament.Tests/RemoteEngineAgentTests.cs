using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Protocol;
using BgTournament.Server;

namespace BgTournament.Tests;

/// <summary>
/// Behavioral pins for the adapter over a scripted channel: the forfeit
/// taxonomy (violation wrapping with the decision-appropriate kind and seat,
/// timeout, disconnect pass-through, external cancellation untranslated), the
/// sign-free hit canonicalization on the live path, and the
/// leave-illegality-to-the-runner rule.
/// </summary>
public class RemoteEngineAgentTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static GameState HittableState() => GameState.FromPosition(
        MatchState.NewMatch(7),
        BoardState.FromMop(new[]
        {
            0, 0, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -2, -1, -5, 0, 0, 0, 0, 2, 0,
        }),
        cubeSize: 1,
        CubeOwner.Centered);

    private sealed class ScriptedChannel : IEngineChannel
    {
        public string EngineName => "scripted";

        public required Func<QueryMessage, CancellationToken, Task<ReplyMessage>> Handler { get; init; }

        public async Task<TReply> QueryAsync<TReply>(
            Func<string, QueryMessage> queryFactory, CancellationToken cancellationToken)
            where TReply : ReplyMessage
        {
            var reply = await Handler(queryFactory("q-1"), cancellationToken);
            return (TReply)reply;
        }
    }

    private static ScriptedChannel Replying(ReplyMessage reply) => new()
    {
        Handler = (query, _) => Task.FromResult(reply),
    };

    [Fact]
    public async Task ChoosePlay_SignFreeHitReply_IsCanonicalizedWithTheHit()
    {
        var reply = new PlayReplyMessage
        {
            RequestId = "q-1",
            Moves = new[] { new WireMove { From = 24, To = 18 }, new WireMove { From = 13, To = 10 } },
        };
        var agent = new RemoteEngineAgent(Replying(reply), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(HittableState(), 6, 3);

        var moves = Enumerable.Range(0, play.Count).Select(i => play[i]).ToList();
        Assert.Contains(new Move(24, -18), moves); // MatchRunner applies and records this hit
        Assert.Contains(new Move(13, 10), moves);
    }

    [Fact]
    public async Task ChoosePlay_IllegalReply_PassesThroughForTheRunnerToJudge()
    {
        var reply = new PlayReplyMessage
        {
            RequestId = "q-1",
            Moves = new[] { new WireMove { From = 3, To = 2 } },
        };
        var agent = new RemoteEngineAgent(Replying(reply), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(HittableState(), 6, 3);

        // Not policed here: the runner is the single legality authority.
        Assert.Equal(1, play.Count);
        Assert.Equal(new Move(3, 2), play[0]);
    }

    [Fact]
    public async Task ChoosePlay_OutOfRangeMoves_IsAnIllegalPlayViolation_WithSeat()
    {
        var reply = new PlayReplyMessage
        {
            RequestId = "q-1",
            Moves = new[] { new WireMove { From = 26, To = 2 } },
        };
        var agent = new RemoteEngineAgent(Replying(reply), MatchSeat.Two, TestTimeout);

        var violation = await Assert.ThrowsAsync<AgentContractViolationException>(
            async () => await agent.ChoosePlayAsync(HittableState(), 6, 3));

        Assert.Equal(AgentContractViolationKind.IllegalPlay, violation.Kind);
        Assert.Equal(MatchSeat.Two, violation.Seat);
        Assert.IsType<ArgumentException>(violation.InnerException);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("offer")]
    [InlineData("response")]
    public async Task ProtocolViolation_WrapsWithTheDecisionAppropriateKind(string decision)
    {
        var channel = new ScriptedChannel
        {
            Handler = (_, _) => throw new EngineProtocolViolationException("the engine sent a malformed frame."),
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.Two, TestTimeout);
        var state = HittableState();

        var violation = await Assert.ThrowsAsync<AgentContractViolationException>(async () => _ = decision switch
        {
            "play" => (object)await agent.ChoosePlayAsync(state, 6, 3),
            "offer" => await agent.ChooseOfferAsync(state),
            _ => await agent.ChooseResponseAsync(state),
        });

        var expectedKind = decision switch
        {
            "play" => AgentContractViolationKind.IllegalPlay,
            "offer" => AgentContractViolationKind.IllegalCubeOffer,
            _ => AgentContractViolationKind.IllegalCubeResponse,
        };
        Assert.Equal(expectedKind, violation.Kind);
        Assert.Equal(MatchSeat.Two, violation.Seat);
        Assert.IsType<EngineProtocolViolationException>(violation.InnerException);
    }

    [Fact]
    public async Task NoReplyWithinTimeout_IsAnEngineTimeout()
    {
        var channel = new ScriptedChannel
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TimeSpan.FromMilliseconds(50));

        var timeout = await Assert.ThrowsAsync<EngineTimeoutException>(
            async () => await agent.ChoosePlayAsync(HittableState(), 6, 3));

        Assert.Equal("scripted", timeout.EngineName);
    }

    [Fact]
    public async Task ExternalCancellation_PropagatesUntranslated()
    {
        var channel = new ScriptedChannel
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TestTimeout);
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await agent.ChoosePlayAsync(HittableState(), 6, 3, external.Token));
    }

    [Fact]
    public async Task Disconnect_PassesThroughUnwrapped()
    {
        var channel = new ScriptedChannel
        {
            Handler = (_, _) => throw new EngineDisconnectedException("scripted", "the connection dropped."),
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TestTimeout);

        await Assert.ThrowsAsync<EngineDisconnectedException>(
            async () => await agent.ChooseOfferAsync(HittableState()));
    }

    [Theory]
    [InlineData(CubeOfferAction.NoDouble, CubeAction.NoDouble)]
    [InlineData(CubeOfferAction.Double, CubeAction.Double)]
    public async Task CubeOffer_MapsNarrowWireActions(CubeOfferAction wire, CubeAction expected)
    {
        var agent = new RemoteEngineAgent(
            Replying(new CubeOfferReplyMessage { RequestId = "q-1", Action = wire }),
            MatchSeat.One,
            TestTimeout);

        Assert.Equal(expected, await agent.ChooseOfferAsync(HittableState()));
    }

    [Theory]
    [InlineData(CubeResponseAction.Take, CubeAction.Take)]
    [InlineData(CubeResponseAction.Pass, CubeAction.Pass)]
    public async Task CubeResponse_MapsNarrowWireActions(CubeResponseAction wire, CubeAction expected)
    {
        var agent = new RemoteEngineAgent(
            Replying(new CubeResponseReplyMessage { RequestId = "q-1", Action = wire }),
            MatchSeat.One,
            TestTimeout);

        Assert.Equal(expected, await agent.ChooseResponseAsync(HittableState()));
    }
}
