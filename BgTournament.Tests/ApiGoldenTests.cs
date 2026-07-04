using System.Text.Json;
using BgTournament.Api;

namespace BgTournament.Tests;

/// <summary>
/// Pins the exact JSON of every admin-API shape, the same discipline
/// <see cref="GoldenWireTests"/> applies to the wire: BgTournament.Api is the
/// contract BgArena_Blazor consumes, so no refactor may change a byte of it
/// silently. Serialization uses ASP.NET Core's Web defaults — the contracts
/// are self-describing (per-member string-enum pins), so the host needs no
/// converter configuration and neither does a consumer.
/// </summary>
public class ApiGoldenTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static void AssertGolden<T>(T value, string golden)
    {
        Assert.Equal(golden, JsonSerializer.Serialize(value, WebJson));
        Assert.Equal(golden, JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(golden, WebJson), WebJson));
    }

    [Theory]
    [InlineData(MatchStatus.Running, "running")]
    [InlineData(MatchStatus.Completed, "completed")]
    [InlineData(MatchStatus.Forfeited, "forfeited")]
    [InlineData(MatchStatus.Aborted, "aborted")]
    [InlineData(MatchStatus.Faulted, "faulted")]
    public void MatchStatus_EveryMember(MatchStatus status, string wire) =>
        AssertGolden(status, $"\"{wire}\"");

    [Theory]
    [InlineData(TournamentStatus.Running, "running")]
    [InlineData(TournamentStatus.Completed, "completed")]
    [InlineData(TournamentStatus.Aborted, "aborted")]
    [InlineData(TournamentStatus.Faulted, "faulted")]
    public void TournamentStatus_EveryMember(TournamentStatus status, string wire) =>
        AssertGolden(status, $"\"{wire}\"");

    /// <summary>
    /// Detail strings often quote engine names; the Web-default encoder
    /// escapes the apostrophe, and that escaping is part of the pinned bytes.
    /// </summary>
    [Fact]
    public void ErrorResponse_Golden_ApostrophesEscape() =>
        AssertGolden(
            new ErrorResponse("No engine named 'Ghost' is connected."),
            """{"error":"No engine named \u0027Ghost\u0027 is connected."}""");

    [Fact]
    public void EngineSummary_AllFields() =>
        AssertGolden(
            new EngineSummary("MyBot", "2.1", "Jane Doe", InMatch: true),
            """{"name":"MyBot","version":"2.1","author":"Jane Doe","inMatch":true}""");

    [Fact]
    public void EngineSummary_OptionalFieldsNull() =>
        AssertGolden(
            new EngineSummary("MyBot", Version: null, Author: null, InMatch: false),
            """{"name":"MyBot","version":null,"author":null,"inMatch":false}""");

    [Fact]
    public void StartMatchRequest_Golden() =>
        AssertGolden(
            new StartMatchRequest("Alpha", "Beta", MatchLength: 7, Seed: 42, MaxGames: 50),
            """{"engineOne":"Alpha","engineTwo":"Beta","matchLength":7,"seed":42,"maxGames":50}""");

    [Fact]
    public void StartMatchRequest_OptionalFieldsNull() =>
        AssertGolden(
            new StartMatchRequest("Alpha", "Beta", MatchLength: 7),
            """{"engineOne":"Alpha","engineTwo":"Beta","matchLength":7,"seed":null,"maxGames":null}""");

    [Fact]
    public void MatchSummary_Running() =>
        AssertGolden(
            new MatchSummary(
                "match-1", "Alpha", "Beta", MatchLength: 7, MaxGames: null, Seed: 42,
                MatchStatus.Running, Winner: null, SeatOneScore: null, SeatTwoScore: null,
                ForfeitedBy: null, Detail: null),
            """{"matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":7,"maxGames":null,"seed":42,"status":"running","winner":null,"seatOneScore":null,"seatTwoScore":null,"forfeitedBy":null,"detail":null}""");

    [Fact]
    public void MatchSummary_Completed() =>
        AssertGolden(
            new MatchSummary(
                "match-1", "Alpha", "Beta", MatchLength: 3, MaxGames: null, Seed: 42,
                MatchStatus.Completed, Winner: "Alpha", SeatOneScore: 3, SeatTwoScore: 1,
                ForfeitedBy: null, Detail: null),
            """{"matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":3,"maxGames":null,"seed":42,"status":"completed","winner":"Alpha","seatOneScore":3,"seatTwoScore":1,"forfeitedBy":null,"detail":null}""");

    [Fact]
    public void MatchSummary_Forfeited() =>
        AssertGolden(
            new MatchSummary(
                "match-1", "Alpha", "Beta", MatchLength: 3, MaxGames: null, Seed: 42,
                MatchStatus.Forfeited, Winner: "Beta", SeatOneScore: null, SeatTwoScore: null,
                ForfeitedBy: "Alpha", Detail: "Engine 'Alpha' disconnected mid-match."),
            """{"matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":3,"maxGames":null,"seed":42,"status":"forfeited","winner":"Beta","seatOneScore":null,"seatTwoScore":null,"forfeitedBy":"Alpha","detail":"Engine \u0027Alpha\u0027 disconnected mid-match."}""");

    [Fact]
    public void StartTournamentRequest_Golden() =>
        AssertGolden(
            new StartTournamentRequest(["Alpha", "Beta"], MatchLength: 3, MatchesPerPairing: 2, Seed: 7),
            """{"participants":["Alpha","Beta"],"matchLength":3,"matchesPerPairing":2,"seed":7}""");

    [Fact]
    public void StandingEntry_Golden() =>
        AssertGolden(
            new StandingEntry(Rank: 1, "Alpha", Wins: 3, Losses: 1, SonnebornBerger: 2),
            """{"rank":1,"participant":"Alpha","wins":3,"losses":1,"sonnebornBerger":2}""");

    [Fact]
    public void TournamentMatchEntry_Unreached() =>
        AssertGolden(
            new TournamentMatchEntry(
                Index: 0, "Alpha", "Beta", Seed: 1234, MatchId: null, Status: null, Winner: null),
            """{"index":0,"seatOne":"Alpha","seatTwo":"Beta","seed":1234,"matchId":null,"status":null,"winner":null}""");

    [Fact]
    public void TournamentMatchEntry_Decided() =>
        AssertGolden(
            new TournamentMatchEntry(
                Index: 1, "Beta", "Alpha", Seed: 99, MatchId: "match-2",
                Status: MatchStatus.Completed, Winner: "Beta"),
            """{"index":1,"seatOne":"Beta","seatTwo":"Alpha","seed":99,"matchId":"match-2","status":"completed","winner":"Beta"}""");

    [Fact]
    public void TournamentSummary_Golden() =>
        AssertGolden(
            new TournamentSummary(
                "tournament-1", ["Alpha", "Beta"], MatchLength: 1, MatchesPerPairing: 1, Seed: 7,
                TournamentStatus.Completed, Winner: "Alpha", Detail: null,
                Standings:
                [
                    new StandingEntry(1, "Alpha", 1, 0, 0),
                    new StandingEntry(2, "Beta", 0, 1, 0),
                ],
                Matches:
                [
                    new TournamentMatchEntry(0, "Alpha", "Beta", 1234, "match-1", MatchStatus.Completed, "Alpha"),
                ]),
            """{"tournamentId":"tournament-1","participants":["Alpha","Beta"],"matchLength":1,"matchesPerPairing":1,"seed":7,"status":"completed","winner":"Alpha","detail":null,"standings":[{"rank":1,"participant":"Alpha","wins":1,"losses":0,"sonnebornBerger":0},{"rank":2,"participant":"Beta","wins":0,"losses":1,"sonnebornBerger":0}],"matches":[{"index":0,"seatOne":"Alpha","seatTwo":"Beta","seed":1234,"matchId":"match-1","status":"completed","winner":"Alpha"}]}""");

    [Theory]
    [InlineData(Seat.One, "seatOne")]
    [InlineData(Seat.Two, "seatTwo")]
    public void Seat_EveryMember(Seat seat, string wire) =>
        AssertGolden(seat, $"\"{wire}\"");

    [Theory]
    [InlineData(CubeOwner.Centered, "centered")]
    [InlineData(CubeOwner.SeatOne, "seatOne")]
    [InlineData(CubeOwner.SeatTwo, "seatTwo")]
    public void CubeOwner_EveryMember(CubeOwner owner, string wire) =>
        AssertGolden(owner, $"\"{wire}\"");

    [Theory]
    [InlineData(GameResultKind.Single, "single")]
    [InlineData(GameResultKind.Gammon, "gammon")]
    [InlineData(GameResultKind.Backgammon, "backgammon")]
    public void GameResultKind_EveryMember(GameResultKind kind, string wire) =>
        AssertGolden(kind, $"\"{wire}\"");

    [Theory]
    [InlineData(CubeResponseAction.Take, "take")]
    [InlineData(CubeResponseAction.Pass, "pass")]
    public void CubeResponseAction_EveryMember(CubeResponseAction action, string wire) =>
        AssertGolden(action, $"\"{wire}\"");

    /// <summary>The standard opening position in seat One's frame.</summary>
    private static GamePosition OpeningPosition(int cubeValue = 1, CubeOwner cubeOwner = CubeOwner.Centered) => new(
        new[] { 0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0 },
        cubeValue,
        cubeOwner);

    /// <summary>
    /// The whole replay shape in one pin: all three entry kinds under their
    /// "type" discriminators, the seat-keyed cube owner, and the game-level
    /// outcome + finalState. Inherited members (actor, state) serialize after
    /// a derived record's own — pinned here so it never drifts silently.
    /// </summary>
    [Fact]
    public void MatchGamesResponse_Golden() =>
        AssertGolden(
            new MatchGamesResponse(
                "match-1", "Alpha", "Beta", MatchLength: 3, MatchStatus.Completed,
                Games:
                [
                    new GameReplay(
                        GameNumber: 1, Seat.Two, GameResultKind.Single, CubeValue: 2, Points: 2,
                        SeatOneScore: 0, SeatTwoScore: 0, IsCrawford: false,
                        Entries:
                        [
                            new PlayEntry(
                                Seat.One, OpeningPosition(), Die1: 3, Die2: 1,
                                Moves: [new PlayMove(8, 5), new PlayMove(6, 5)]),
                            new CubeOfferEntry(Seat.Two, OpeningPosition()),
                            new CubeResponseEntry(Seat.One, OpeningPosition(), CubeResponseAction.Take),
                        ],
                        FinalState: OpeningPosition(cubeValue: 2, cubeOwner: CubeOwner.SeatOne)),
                ]),
            """{"matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":3,"status":"completed","games":[{"gameNumber":1,"winner":"seatTwo","resultKind":"single","cubeValue":2,"points":2,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false,"entries":[{"type":"play","die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}],"actor":"seatOne","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}},{"type":"cubeOffer","actor":"seatTwo","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}},{"type":"cubeResponse","action":"take","actor":"seatOne","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}}],"finalState":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":2,"cubeOwner":"seatOne"}}]}""");

    /// <summary>
    /// One completed game, replay-ready — reused as the payload of the live
    /// feed's <c>gameEnded</c> event, so its bytes are pinned once here.
    /// </summary>
    private static GameReplay CompletedGame() =>
        new(
            GameNumber: 1, Seat.Two, GameResultKind.Single, CubeValue: 2, Points: 2,
            SeatOneScore: 0, SeatTwoScore: 0, IsCrawford: false,
            Entries:
            [
                new PlayEntry(
                    Seat.One, OpeningPosition(), Die1: 3, Die2: 1,
                    Moves: [new PlayMove(8, 5), new PlayMove(6, 5)]),
                new CubeOfferEntry(Seat.Two, OpeningPosition()),
                new CubeResponseEntry(Seat.One, OpeningPosition(), CubeResponseAction.Take),
            ],
            FinalState: OpeningPosition(cubeValue: 2, cubeOwner: CubeOwner.SeatOne));

    private const string CompletedGameJson =
        """{"gameNumber":1,"winner":"seatTwo","resultKind":"single","cubeValue":2,"points":2,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false,"entries":[{"type":"play","die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}],"actor":"seatOne","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}},{"type":"cubeOffer","actor":"seatTwo","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}},{"type":"cubeResponse","action":"take","actor":"seatOne","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}}],"finalState":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":2,"cubeOwner":"seatOne"}}""";

    /// <summary>
    /// The live-feed envelope, every event serialized through the polymorphic
    /// base so the <c>"type"</c> discriminator is emitted — the same
    /// self-describing union the replay entries use. Payloads reuse the settled
    /// replay/summary contracts (pinned above), so these pins guard only the
    /// envelope's own bytes.
    /// </summary>
    [Fact]
    public void LiveSnapshotEvent_Golden() =>
        AssertGolden<LiveMatchEvent>(
            new LiveSnapshotEvent(
                GameNumber: 1, SeatOneScore: 0, SeatTwoScore: 0,
                Entries:
                [
                    new PlayEntry(
                        Seat.One, OpeningPosition(), Die1: 3, Die2: 1,
                        Moves: [new PlayMove(8, 5), new PlayMove(6, 5)]),
                ]),
            """{"type":"snapshot","gameNumber":1,"seatOneScore":0,"seatTwoScore":0,"entries":[{"type":"play","die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}],"actor":"seatOne","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}}]}""");

    [Fact]
    public void LiveGameStartedEvent_Golden() =>
        AssertGolden<LiveMatchEvent>(
            new LiveGameStartedEvent(GameNumber: 2, SeatOneScore: 1, SeatTwoScore: 0),
            """{"type":"gameStarted","gameNumber":2,"seatOneScore":1,"seatTwoScore":0}""");

    [Fact]
    public void LiveEntryEvent_Golden() =>
        AssertGolden<LiveMatchEvent>(
            new LiveEntryEvent(new CubeOfferEntry(Seat.Two, OpeningPosition())),
            """{"type":"entry","entry":{"type":"cubeOffer","actor":"seatTwo","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered"}}}""");

    [Fact]
    public void LiveGameEndedEvent_Golden() =>
        AssertGolden<LiveMatchEvent>(
            new LiveGameEndedEvent(CompletedGame()),
            $$"""{"type":"gameEnded","game":{{CompletedGameJson}}}""");

    [Fact]
    public void LiveTerminalEvent_Golden() =>
        AssertGolden<LiveMatchEvent>(
            new LiveTerminalEvent(new MatchSummary(
                "match-1", "Alpha", "Beta", MatchLength: 3, MaxGames: null, Seed: 42,
                MatchStatus.Completed, Winner: "Alpha", SeatOneScore: 3, SeatTwoScore: 1,
                ForfeitedBy: null, Detail: null)),
            """{"type":"terminal","match":{"matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":3,"maxGames":null,"seed":42,"status":"completed","winner":"Alpha","seatOneScore":3,"seatTwoScore":1,"forfeitedBy":null,"detail":null}}""");
}
