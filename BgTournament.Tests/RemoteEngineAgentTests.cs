using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.Protocol;
using BgTournament.Server;
using Microsoft.Extensions.Time.Testing;
using DecisionKind = BgTournament.Server.DecisionKind;
using TimeControl = BgTournament.Api.TimeControl;

namespace BgTournament.Tests;

/// <summary>
/// Behavioral pins for the adapter over a scripted channel: the forfeit
/// taxonomy (violation wrapping with the decision-appropriate kind and seat,
/// timeout, disconnect pass-through, external cancellation untranslated), the
/// server's play resolution (each reply resolved Strictly by
/// <see cref="MoveGenerator.ResolvePlay"/>: the generator's candidate is
/// returned, never the engine's spelling, and a reply describing no play is
/// the engine's violation, raised here), and the two timing regimes (flat
/// timeout vs. Fischer clock: stamped pools, flag fall, increment credit).
/// The resolver's own semantics are BgMoveGen's to pin; these pin that the
/// server goes through it and what leaves the agent.
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

    /// <summary>
    /// halheinrich/backgammon#304's board: your lone checker on 13 with points
    /// 10 and 11 empty, so a 3-2 reaches 8 by either route and the generator
    /// keeps one; with <paramref name="blotOnEight"/>, an opposing blot there
    /// is hit on the final landing point.
    /// </summary>
    private static GameState LoneCheckerState(bool blotOnEight)
    {
        var mop = new int[26];
        mop[13] = 1;
        mop[23] = -2;
        mop[24] = -2;
        if (blotOnEight)
        {
            mop[8] = -1;
        }

        return StateOver(mop);
    }

    /// <summary>
    /// Your lone checker on 13 reaches 8 with a 3-2 by two routes: via 10
    /// (hitting the opponent blot there) or via 11 (clean) — two legal
    /// candidates, told apart by the route alone.
    /// </summary>
    private static GameState BlotEnRouteState() => StateOver(new[]
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, -2, -2, 0, 0, 0, -2, 0,
    });

    /// <summary>On the bar behind a closed board (dice 3-1 cannot enter).</summary>
    private static GameState ClosedOutState() => StateOver(new[]
    {
        0, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -3, 0, 0, 0, 0, 0, 0, -2, -2, -2, -2, -2, -2, 1,
    });

    private static GameState StateOver(int[] mop) => GameState.FromPosition(
        MatchState.NewMatch(7), BoardState.FromMop(mop), cubeSize: 1, CubeOwner.Centered);

    private static PlayReplyMessage PlayReply(params (int From, int To)[] moves) => new()
    {
        RequestId = "q-1",
        Moves = moves.Select(m => new WireMove { From = m.From, To = m.To }).ToArray(),
    };

    /// <summary>
    /// Asserts that <paramref name="resolved"/> is the generator's own
    /// candidate for the play <paramref name="expected"/> — the element of
    /// <see cref="MoveGenerator.GeneratePlays"/>' list, in its encoding and
    /// with its hit marks — so the pin holds whatever encoding the generator
    /// chooses, and fails if the engine's spelling leaves the agent.
    /// </summary>
    private static void AssertIsTheCandidate(GameState state, int die1, int die2, Play expected, Play resolved)
    {
        var candidates = MoveGenerator.GeneratePlays(state.Board, die1, die2);
        int index = state.Board.IndexOfSamePlay(expected, candidates);
        Assert.True(index >= 0, $"{expected.ToNotation()} is a candidate of {die1}-{die2}.");
        Assert.True(
            resolved.IsSameEncoding(candidates[index]),
            $"resolved {resolved.ToNotation()}, expected the candidate {candidates[index].ToNotation()} in its encoding.");
    }

    private static async Task<AgentContractViolationException> AssertIllegalPlayAsync(
        GameState state, int die1, int die2, PlayReplyMessage reply, MatchSeat seat = MatchSeat.One)
    {
        var agent = new RemoteEngineAgent(Replying(reply), seat, TestTimeout);

        var violation = await Assert.ThrowsAsync<AgentContractViolationException>(
            async () => await agent.ChoosePlayAsync(state, die1, die2));

        Assert.Equal(AgentContractViolationKind.IllegalPlay, violation.Kind);
        Assert.Equal(seat, violation.Seat);
        return violation;
    }

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

    // ---- Play resolution: every reply through MoveGenerator.ResolvePlay ----

    [Fact]
    public async Task ChoosePlay_SignFreeHitReply_ResolvesToTheCandidateWithTheHit()
    {
        var state = HittableState();
        var agent = new RemoteEngineAgent(Replying(PlayReply((24, 18), (13, 10))), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(state, 6, 3);

        // MatchRunner applies and records this hit: the board's, not the wire's.
        AssertIsTheCandidate(state, 6, 3, [new(24, -18), new(13, 10)], play);
    }

    [Fact]
    public async Task ChoosePlay_OtherRouteToTheSameLanding_ResolvesToTheGeneratorsCandidate()
    {
        // halheinrich/backgammon#304, without the blot: the generator keeps
        // one route to 8; the reply names the other. It resolves, and what
        // leaves the agent is the generator's candidate, not the reply's route.
        var state = LoneCheckerState(blotOnEight: false);
        var agent = new RemoteEngineAgent(Replying(PlayReply((13, 10), (10, 8))), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(state, 3, 2);

        var candidates = MoveGenerator.GeneratePlays(state.Board, 3, 2);
        Assert.True(play.IsSameEncoding(Assert.Single(candidates)));
        Assert.False(play.IsSameEncoding([new(13, 10), new(10, 8)]), "the engine's spelling must not leave the agent");
    }

    [Fact]
    public async Task ChoosePlay_OtherRouteHittingOnItsFinalLanding_ResolvesToTheCandidateWithTheHit()
    {
        // halheinrich/backgammon#304 itself: this legal reply used to forfeit,
        // because the unmarked landing on the blot reached the runner as sent.
        var state = LoneCheckerState(blotOnEight: true);
        var agent = new RemoteEngineAgent(Replying(PlayReply((13, 10), (10, 8))), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(state, 3, 2);

        AssertIsTheCandidate(state, 3, 2, [new(13, 10), new(10, -8)], play);
    }

    [Theory]
    [InlineData(10)] // via 10: hits the blot en route
    [InlineData(11)] // via 11: clean
    public async Task ChoosePlay_RouteDecidesAHitEnRoute(int via)
    {
        // Two candidates that differ only in the hit en route: the route the
        // engine names is what tells them apart, in either move order.
        var state = BlotEnRouteState();
        int hitMark = via == 10 ? -1 : 1;
        Play expected = [new(13, hitMark * via), new(via, 8)];

        foreach (var reply in new[] { PlayReply((13, via), (via, 8)), PlayReply((via, 8), (13, via)) })
        {
            var agent = new RemoteEngineAgent(Replying(reply), MatchSeat.One, TestTimeout);

            var play = await agent.ChoosePlayAsync(state, 3, 2);

            AssertIsTheCandidate(state, 3, 2, expected, play);
        }
    }

    [Fact]
    public async Task ChoosePlay_EmptyReplyOnADance_ResolvesToTheEmptyCandidate()
    {
        var agent = new RemoteEngineAgent(Replying(PlayReply()), MatchSeat.One, TestTimeout);

        var play = await agent.ChoosePlayAsync(ClosedOutState(), 3, 1);

        Assert.Equal(0, play.Count);
    }

    [Fact]
    public async Task ChoosePlay_HopsNoDieProduces_IsAnIllegalPlayViolation()
    {
        // halheinrich/backgammon#304's other half: 13/9 9/6 reaches the one
        // candidate's position with a 6-1, but no die of the roll makes those
        // hops. It used to be accepted and recorded as written.
        var violation = await AssertIllegalPlayAsync(
            LoneCheckerState(blotOnEight: false), 6, 1, PlayReply((13, 9), (9, 6)));

        Assert.Contains("no legal play", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosePlay_MergedHop_IsAnIllegalPlayViolation()
    {
        // PROTOCOL.md §6: each move is one legal single-die move, so the
        // merged 13/8 is no play of a 3-2, though it reaches the candidate's position.
        await AssertIllegalPlayAsync(LoneCheckerState(blotOnEight: false), 3, 2, PlayReply((13, 8)));
    }

    [Fact]
    public async Task ChoosePlay_IllegalReply_IsAnIllegalPlayViolation_WithSeat()
    {
        // No checker of yours on 3: never legal, whatever the dice. Raised
        // where the reply is resolved; nothing reaches the runner.
        await AssertIllegalPlayAsync(HittableState(), 6, 3, PlayReply((3, 2)), MatchSeat.Two);
    }

    [Fact]
    public async Task ChoosePlay_OutOfRangeMoves_IsAnIllegalPlayViolation_WithSeat()
    {
        var violation = await AssertIllegalPlayAsync(
            HittableState(), 6, 3, PlayReply((26, 2)), MatchSeat.Two);

        Assert.IsType<ArgumentOutOfRangeException>(violation.InnerException);
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

    [Fact]
    public async Task FlatRegime_OmitsTheClockFields()
    {
        QueryMessage? captured = null;
        var channel = new ScriptedChannel
        {
            Handler = (query, _) =>
            {
                captured = query;
                return Task.FromResult<ReplyMessage>(
                    new CubeOfferReplyMessage { RequestId = "q-1", Action = CubeOfferAction.NoDouble });
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TestTimeout);

        await agent.ChooseOfferAsync(HittableState());

        Assert.NotNull(captured);
        Assert.Null(captured!.YourTimeRemainingSeconds);
        Assert.Null(captured.OpponentTimeRemainingSeconds);
    }

    /// <summary>
    /// The stamped pools are seat-oriented per the frame rule: the queried
    /// engine's own pool is "your", the other seat's is "opponent" — pinned
    /// with unequal pools so a swap cannot pass.
    /// </summary>
    [Fact]
    public async Task ClockedQuery_StampsBothPools_InTheQueriedSeatsFrame()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(120, 8), time);

        // Give seat One an answered decision first, so the pools differ.
        using (var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            decision.MarkAnswered();
        }

        QueryMessage? captured = null;
        var channel = new ScriptedChannel
        {
            Handler = (query, _) =>
            {
                captured = query;
                return Task.FromResult<ReplyMessage>(
                    new CubeOfferReplyMessage { RequestId = "q-1", Action = CubeOfferAction.NoDouble });
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.Two, TestTimeout, clock: clock);

        await agent.ChooseOfferAsync(HittableState());

        Assert.NotNull(captured);
        Assert.Equal(120, captured!.YourTimeRemainingSeconds);
        Assert.Equal(128, captured.OpponentTimeRemainingSeconds);
    }

    [Fact]
    public async Task ClockedAnswer_CreditsTheIncrement()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(120, 8), time);
        var agent = new RemoteEngineAgent(
            Replying(new CubeOfferReplyMessage { RequestId = "q-1", Action = CubeOfferAction.NoDouble }),
            MatchSeat.One,
            TestTimeout,
            clock: clock);

        await agent.ChooseOfferAsync(HittableState());

        Assert.Equal(TimeSpan.FromSeconds(128), clock.Remaining(MatchSeat.One));
    }

    [Fact]
    public async Task PoolExhaustedMidDecision_IsAFlagFall_AttributedAndUncredited()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(60, 5), time);
        var queried = new TaskCompletionSource();
        var channel = new ScriptedChannel
        {
            Handler = async (_, ct) =>
            {
                queried.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TestTimeout, clock: clock);

        var choosing = agent.ChoosePlayAsync(HittableState(), 6, 3).AsTask();
        await queried.Task; // the flag source is armed once the query is in flight
        time.Advance(TimeSpan.FromSeconds(61));

        var flagFall = await Assert.ThrowsAsync<EngineFlagFallException>(async () => await choosing);
        Assert.Equal("scripted", flagFall.EngineName);
        Assert.Contains("60s + 5s/decision", flagFall.Message);
        Assert.Equal(TimeSpan.Zero, clock.Remaining(MatchSeat.One)); // no increment for a flagged decision
    }

    [Fact]
    public async Task Clocked_ExternalCancellation_PropagatesUntranslated()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(600, 0), time);
        var channel = new ScriptedChannel
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var agent = new RemoteEngineAgent(channel, MatchSeat.One, TestTimeout, clock: clock);
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await agent.ChoosePlayAsync(HittableState(), 6, 3, external.Token));
    }
}
