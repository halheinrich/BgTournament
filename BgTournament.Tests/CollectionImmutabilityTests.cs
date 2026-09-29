using System.Text.Json;
using BgTournament.Api;
using BgTournament.Protocol;
using CubeOwner = BgTournament.Api.CubeOwner;
using Seat = BgTournament.Api.Seat;

namespace BgTournament.Tests;

/// <summary>
/// The collection rider of halheinrich/backgammon#273: no public member hands
/// out a live mutable collection or array behind a read-only interface. Every
/// list a wire message or an admin Api record holds is an immutable copy of
/// the one it was given — at construction, through <c>with</c>, and out of the
/// deserializer — so neither a later change to the caller's array nor a cast
/// back to a writable list reaches the value. A null stays null: the copy does
/// not validate. The JSON is unchanged; the goldens pin that.
/// </summary>
public class CollectionImmutabilityTests
{
    private static readonly int[] OpeningBoard =
        [0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0];

    private static GamePosition Position() => new(OpeningBoard.ToArray(), 1, CubeOwner.Centered);

    private static GameEntry Entry() => new CubeOfferEntry(Seat.One, Position());

    private static GameReplay Replay(GameEntry[] entries) =>
        new(1, Seat.One, GameResultKind.Single, 1, 1, 0, 0, false, entries, Position());

    /// <summary>
    /// Builds the value from <paramref name="source"/>, then changes the
    /// source and tries to write through the held list: neither reaches it.
    /// </summary>
    private static void AssertHeldAsAnImmutableCopy<T>(T[] source, T replacement, Func<T[], IReadOnlyList<T>> held)
    {
        var expected = source.ToArray();
        var list = held(source);

        source[0] = replacement;

        Assert.Equal(expected, list);
        Assert.Throws<NotSupportedException>(() => ((IList<T>)list)[0] = replacement);
    }

    [Fact]
    public void PlayReplyMessage_Moves() => AssertHeldAsAnImmutableCopy(
        [new WireMove { From = 8, To = 5 }], new WireMove { From = 6, To = 5 },
        moves => new PlayReplyMessage { RequestId = "q-1", Moves = moves }.Moves);

    [Fact]
    public void WireGameState_Board() => AssertHeldAsAnImmutableCopy(
        OpeningBoard.ToArray(), 7,
        board => new WireGameState
        {
            Board = board, CubeValue = 1, CubeOwner = WireCubeOwner.Centered,
            MatchLength = 7, YourScore = 0, OpponentScore = 0, IsCrawford = false,
        }.Board);

    [Fact]
    public void EngineAttestation_Authors() => AssertHeldAsAnImmutableCopy(
        ["Jane Doe"], "Someone Else", authors => new EngineAttestation(authors, "Original work.").Authors);

    [Fact]
    public void PlayEntry_Moves() => AssertHeldAsAnImmutableCopy(
        [new PlayMove(8, 5)], new PlayMove(6, 5),
        moves => new PlayEntry(Seat.One, Position(), 3, 1, moves).Moves);

    [Fact]
    public void GamePosition_Board() => AssertHeldAsAnImmutableCopy(
        OpeningBoard.ToArray(), 7, board => new GamePosition(board, 1, CubeOwner.Centered).Board);

    [Fact]
    public void GameReplay_Entries() => AssertHeldAsAnImmutableCopy(
        [Entry()], Entry(), entries => Replay(entries).Entries);

    [Fact]
    public void LiveSnapshotEvent_Entries() => AssertHeldAsAnImmutableCopy(
        [Entry()], Entry(), entries => new LiveSnapshotEvent(1, 0, 0, false, entries).Entries);

    [Fact]
    public void MatchAuditResponse_Events() => AssertHeldAsAnImmutableCopy<AuditEvent>(
        [new AuditStartedEvent(null)], new AuditStartedEvent(DateTimeOffset.UnixEpoch),
        events => new MatchAuditResponse("m-1", "A", "B", MatchStatus.Completed, null, events).Events);

    [Fact]
    public void MatchGamesResponse_Games() => AssertHeldAsAnImmutableCopy(
        [Replay([Entry()])], Replay([]),
        games => new MatchGamesResponse("m-1", "A", "B", 1, MatchStatus.Completed, games).Games);

    [Fact]
    public void StartTournamentRequest_Participants() => AssertHeldAsAnImmutableCopy(
        ["A", "B"], "C", participants => new StartTournamentRequest(participants, 3, 1).Participants!);

    [Theory]
    [InlineData("participants")]
    [InlineData("standings")]
    [InlineData("matches")]
    public void TournamentSummary_Lists(string list)
    {
        TournamentSummary Summary(string[] participants, StandingEntry[] standings, TournamentMatchEntry[] matches) =>
            new("t-1", participants, 3, 1, 42, null, TournamentStatus.Running, null, null,
                standings, matches, DateTimeOffset.UnixEpoch, null);
        string[] participants = ["A", "B"];
        StandingEntry[] standings = [new(1, "A", 0, 0, 0)];
        TournamentMatchEntry[] matches = [new(0, "A", "B", 7, null, null, null)];

        switch (list)
        {
            case "participants":
                AssertHeldAsAnImmutableCopy(participants, "C", p => Summary(p, standings, matches).Participants);
                break;
            case "standings":
                AssertHeldAsAnImmutableCopy(
                    standings, new StandingEntry(2, "B", 0, 0, 0), s => Summary(participants, s, matches).Standings);
                break;
            default:
                AssertHeldAsAnImmutableCopy(
                    matches, new TournamentMatchEntry(1, "B", "A", 8, null, null, null),
                    m => Summary(participants, standings, m).Matches);
                break;
        }
    }

    [Fact]
    public void With_CopiesToo()
    {
        AssertHeldAsAnImmutableCopy(OpeningBoard.ToArray(), 7, board => (Position() with { Board = board }).Board);
    }

    [Fact]
    public void Deserialized_WireMessage_HoldsAnImmutableList()
    {
        var reply = Assert.IsType<PlayReplyMessage>(WireProtocol.Deserialize(
            """{"type":"playReply","requestId":"q-1","moves":[{"from":8,"to":5},{"from":6,"to":5}]}"""));

        Assert.Throws<NotSupportedException>(() => ((IList<WireMove>)reply.Moves)[0] = new WireMove { From = 1, To = 0 });
    }

    [Fact]
    public void Deserialized_ApiRecord_HoldsAnImmutableList()
    {
        var position = JsonSerializer.Deserialize<GamePosition>(
            """{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}""",
            JsonSerializerOptions.Web);

        Assert.NotNull(position);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)position.Board)[0] = 7);
    }

    [Fact]
    public void Null_StaysNull_TheCopyDoesNotValidate()
    {
        Assert.Null(new StartTournamentRequest(null, 3, 1).Participants);
        Assert.Null(new EngineAttestation(null!, "Original work.").Authors);
        Assert.Null(new PlayReplyMessage { RequestId = "q-1", Moves = null! }.Moves);
    }
}
