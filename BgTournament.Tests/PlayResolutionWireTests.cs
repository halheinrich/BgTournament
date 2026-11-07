using System.Net.Http.Json;
using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.Api;
using BgTournament.EngineClient;
using BgTournament.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Seat = BgTournament.Api.Seat;

namespace BgTournament.Tests;

/// <summary>
/// The server's Strict play resolution pinned at the wire, over a TestServer:
/// halheinrich/backgammon#304's two defects end to end, and the SDK's
/// conforming reply. PROTOCOL.md §6: each encoded move is one legal
/// single-die move, in any order and by any legal route. A reply by the route
/// the generator did not keep is accepted and recorded as the generator's
/// candidate — hitting on its final landing point included, which used to
/// forfeit — and a reply whose hops no die produces forfeits, where it used
/// to be accepted and recorded as written.
///
/// <para>The raw-wire engine plays the generator's first candidate every turn
/// but one: the first turn whose position offers the case under test, found
/// from the query itself. Seeded dice and a seeded opponent make the match,
/// and so that turn, deterministic; each test asserts the turn came.</para>
/// </summary>
public class PlayResolutionWireTests
{
    private const string Router = "Router";
    private const string Opponent = "Opponent";

    [Theory]
    [InlineData(false, 42, 11)] // the other route lands on an empty or own point
    [InlineData(true, 42, 11)]  // the other route hits on its final landing point
    public async Task OtherRouteReply_IsAccepted_AndRecordedAsTheCandidate(
        bool hitsOnLanding, int diceSeed, int opponentSeed)
    {
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await using var router = await TestEngine.ConnectAsync(factory, Router);
        await ServerHarness.RunWellBehavedClientAsync(factory, Opponent, opponentSeed, clientCts.Token);
        await ServerHarness.WaitForEnginesAsync(factory, Router, Opponent);

        var match = await ServerHarness.StartMatchAsync(factory, Router, Opponent, matchLength: 3, seed: diceSeed);
        string matchId = match.GetProperty("matchId").GetString()!;

        (PlayQueryMessage Query, Play Candidate, WireMove[] Reply)? sent = null;
        var ended = await PlayOutAsync(router, query =>
        {
            if (sent is not null || OtherRouteOf(query, hitsOnLanding) is not { } found)
            {
                return null;
            }

            sent = (query, found.Candidate, found.Reply);
            return found.Reply;
        });

        Assert.True(sent is not null, "the seeded match offered the case under test");
        Assert.Equal(MatchEndReason.MatchComplete, ended.Reason);

        // Recorded as the generator's candidate, not as the reply's route.
        var (query, candidate, reply) = sent.Value;
        var entry = await RecordedPlayAsync(factory, matchId, query);
        var recorded = entry.Moves.Select(m => new WireMove { From = m.From, To = m.To }).ToList();
        Assert.Equal(candidate.ToWireMoves(), recorded);
        Assert.False(reply.SequenceEqual(recorded), "the reply's route is not what was recorded");
        clientCts.Cancel();
    }

    [Theory]
    [InlineData(false)] // split into hops no die of the roll makes
    [InlineData(true)]  // merged into one hop
    public async Task ReplyWhoseMovesAreNotSingleDieMoves_Forfeits(bool merged)
    {
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await using var router = await TestEngine.ConnectAsync(factory, Router);
        await ServerHarness.RunWellBehavedClientAsync(factory, Opponent, seed: 11, clientCts.Token);
        await ServerHarness.WaitForEnginesAsync(factory, Router, Opponent);

        var match = await ServerHarness.StartMatchAsync(factory, Router, Opponent, matchLength: 3, seed: 42);
        string matchId = match.GetProperty("matchId").GetString()!;

        bool sent = false;
        var ended = await PlayOutAsync(router, query =>
        {
            if (sent || NonConformingSpellingOf(query, merged) is not { } reply)
            {
                return null;
            }

            sent = true;
            return reply;
        });

        Assert.True(sent, "the seeded match offered the case under test");
        Assert.Equal(MatchEndReason.Forfeit, ended.Reason);
        Assert.Equal(ForfeitSide.You, ended.ForfeitedBy);

        var record = await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal(Router, record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("contractViolation", record.GetProperty("forfeitCause").GetString());
        Assert.Contains("no legal play", record.GetProperty("detail").GetString(), StringComparison.Ordinal);
        clientCts.Cancel();
    }

    [Fact]
    public async Task SdkClient_AgentReturningMergedSpellings_RepliesConformingly()
    {
        // A local agent may return a legal play in any encoding — here the
        // merged spelling (13/8 for 13/11 11/8), which the wire refuses. The
        // SDK sends the generator's candidate it is the same play as, so the
        // match completes with no forfeit.
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        var merging = new MergingPlayAgent(seed: 5);
        await ServerHarness.RunClientAsync(factory, "Merger", merging, new PassiveCubeAgent(), clientCts.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, Opponent, seed: 11, clientCts.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Merger", Opponent);

        var match = await ServerHarness.StartMatchAsync(factory, "Merger", Opponent, matchLength: 3, seed: 42);
        var record = await ServerHarness.WaitForMatchEndAsync(
            factory, match.GetProperty("matchId").GetString()!, TimeSpan.FromSeconds(30));

        Assert.Equal("completed", record.GetProperty("status").GetString());
        Assert.True(merging.MergedCount > 0, "the agent returned merged spellings");
        clientCts.Cancel();
    }

    [Fact]
    public async Task SdkClient_AgentReturningNoLegalPlay_IsSentAsWritten_AndForfeitsAsAViolation()
    {
        // Not the SDK's to police: an illegal play goes out as the agent wrote
        // it, and the server's forfeit names the contract violation rather
        // than a dropped connection.
        using var factory = ServerHarness.NewFactory();
        using var clientCts = new CancellationTokenSource();
        await ServerHarness.RunClientAsync(
            factory, "Stray", new FixedPlayAgent([new Move(3, 2)]), new PassiveCubeAgent(), clientCts.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, Opponent, seed: 11, clientCts.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Stray", Opponent);

        var match = await ServerHarness.StartMatchAsync(factory, "Stray", Opponent, matchLength: 3, seed: 42);
        var record = await ServerHarness.WaitForMatchEndAsync(factory, match.GetProperty("matchId").GetString()!);

        Assert.Equal("forfeited", record.GetProperty("status").GetString());
        Assert.Equal("Stray", record.GetProperty("forfeitedBy").GetString());
        Assert.Equal("contractViolation", record.GetProperty("forfeitCause").GetString());
        clientCts.Cancel();
    }

    /// <summary>
    /// Serve the raw-wire engine's queries until its match ends: each play
    /// query is answered with <paramref name="reply"/>'s moves, or with the
    /// generator's first candidate when it returns null; cube offers are
    /// declined.
    /// </summary>
    private static async Task<MatchEndedMessage> PlayOutAsync(
        TestEngine engine, Func<PlayQueryMessage, WireMove[]?> reply)
    {
        while (true)
        {
            var message = await engine.ReceiveAsync();
            switch (message)
            {
                case MatchStartedMessage:
                    break;
                case CubeOfferQueryMessage offer:
                    await engine.SendAsync(new CubeOfferReplyMessage
                    {
                        RequestId = offer.RequestId,
                        Action = CubeOfferAction.NoDouble,
                    });
                    break;
                case PlayQueryMessage query:
                    if (reply(query) is { } moves)
                    {
                        await engine.SendAsync(new PlayReplyMessage { RequestId = query.RequestId, Moves = moves });
                    }
                    else
                    {
                        await engine.ReplyWithFirstLegalPlayAsync(query);
                    }

                    break;
                case MatchEndedMessage ended:
                    return ended;
                default:
                    Assert.Fail($"Unexpected message: {message?.GetType().Name ?? "connection closed"}.");
                    break;
            }
        }
    }

    /// <summary>
    /// A candidate that moves one checker twice with a non-double, hitting
    /// nothing en route, whose other route is open too: the reply names the
    /// other route, which the generator does not keep (it lists one play per
    /// position). Null when the query offers no such candidate with the asked
    /// landing.
    /// </summary>
    private static (Play Candidate, WireMove[] Reply)? OtherRouteOf(PlayQueryMessage query, bool hitsOnLanding)
    {
        var board = new BoardState(query.State.ToGameSnapshot().Board);
        foreach (var (candidate, from, to, firstDie) in ChainsOf(query, board))
        {
            int via = from - (query.Die1 + query.Die2 - firstDie);
            if (board.Points[via] >= 0 && (candidate[1].ToPt < 0) == hitsOnLanding)
            {
                return (candidate, [new() { From = from, To = via }, new() { From = via, To = to }]);
            }
        }

        return null;
    }

    /// <summary>
    /// A spelling of a one-checker chain whose moves are not single-die moves:
    /// merged into one hop, or split at a point no die of the roll reaches.
    /// </summary>
    private static WireMove[]? NonConformingSpellingOf(PlayQueryMessage query, bool merged)
    {
        var board = new BoardState(query.State.ToGameSnapshot().Board);
        foreach (var (_, from, to, _) in ChainsOf(query, board))
        {
            if (merged)
            {
                return [new() { From = from, To = to }];
            }

            for (int step = 1; step < from - to; step++)
            {
                if (step != query.Die1 && step != query.Die2 && board.Points[from - step] >= 0)
                {
                    return [new() { From = from, To = from - step }, new() { From = from - step, To = to }];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The generator's candidates for a non-double that move one checker
    /// twice onto the board, hitting nothing en route: each with the chain's
    /// source, its landing point and the die the candidate plays first.
    /// </summary>
    private static IEnumerable<(Play Candidate, int From, int To, int FirstDie)> ChainsOf(
        PlayQueryMessage query, BoardState board)
    {
        if (query.Die1 == query.Die2)
        {
            yield break;
        }

        foreach (var candidate in MoveGenerator.GeneratePlays(board, query.Die1, query.Die2))
        {
            if (candidate.Count == 2 && candidate[0].ToPt > 0 && candidate[0].ToPt == candidate[1].FrPt
                && candidate[1].ToPt != 0)
            {
                yield return (candidate, candidate[0].FrPt, Math.Abs(candidate[1].ToPt),
                    candidate[0].FrPt - candidate[0].ToPt);
            }
        }
    }

    /// <summary>
    /// The replay entry recording the raw-wire engine's play for
    /// <paramref name="query"/>: seat One's (it is engine one, so its frame
    /// is the replay's), with the query's dice and position.
    /// </summary>
    private static async Task<PlayEntry> RecordedPlayAsync(
        WebApplicationFactory<Program> factory,
        string matchId,
        PlayQueryMessage query)
    {
        using var http = factory.CreateClient();
        var replay = await http.GetFromJsonAsync<MatchGamesResponse>($"/matches/{matchId}/games");
        Assert.NotNull(replay);
        return Assert.Single(
            replay.Games.SelectMany(game => game.Entries).OfType<PlayEntry>(),
            entry => entry.Actor == Seat.One
                && entry.Die1 == query.Die1 && entry.Die2 == query.Die2
                && entry.State.Board.SequenceEqual(query.State.Board));
    }

    /// <summary>
    /// Plays a random legal play, returned merged into one move when it moves
    /// one checker twice with nothing hit en route — the same play in an
    /// encoding the wire does not accept.
    /// </summary>
    private sealed class MergingPlayAgent(int seed) : IPlayAgent
    {
        private readonly RandomPlayAgent _inner = new(seed);
        private int _merged;

        public int MergedCount => Volatile.Read(ref _merged);

        public async ValueTask<Play> ChoosePlayAsync(
            GameSnapshot state, int die1, int die2, CancellationToken cancellationToken = default)
        {
            var play = await _inner.ChoosePlayAsync(state, die1, die2, cancellationToken);
            if (play.Count == 2 && play[0].ToPt > 0 && play[0].ToPt == play[1].FrPt)
            {
                Interlocked.Increment(ref _merged);
                return [new Move(play[0].FrPt, play[1].ToPt)];
            }

            return play;
        }
    }

    /// <summary>Always returns the same play, legal or not.</summary>
    private sealed class FixedPlayAgent(Play play) : IPlayAgent
    {
        public ValueTask<Play> ChoosePlayAsync(
            GameSnapshot state, int die1, int die2, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(play);
    }
}
