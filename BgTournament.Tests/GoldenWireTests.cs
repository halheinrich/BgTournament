using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Pins the exact wire text of every message type. The JSON is the engine
/// contract (PROTOCOL.md); these tests exist so no refactor — in this repo or
/// in a substrate the DTOs touch — can change a byte of it silently. Each case
/// asserts both directions: our serializer produces exactly the golden text,
/// and the golden text survives a deserialize → reserialize round trip
/// unchanged.
/// </summary>
public class GoldenWireTests
{
    private static void AssertGolden(ProtocolMessage message, string golden)
    {
        Assert.Equal(golden, WireProtocol.Serialize(message));
        Assert.Equal(golden, WireProtocol.Serialize(WireProtocol.Deserialize(golden)));
    }

    /// <summary>The standard opening position, on-roll player's frame.</summary>
    private static WireGameState OpeningState(int matchLength, int yourScore, int opponentScore) => new()
    {
        Board = new[] { 0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0 },
        CubeValue = 1,
        CubeOwner = WireCubeOwner.Centered,
        MatchLength = matchLength,
        YourScore = yourScore,
        OpponentScore = opponentScore,
        IsCrawford = false,
    };

    [Fact]
    public void Hello_AllFields()
    {
        var message = new HelloMessage
        {
            ProtocolVersion = 1,
            EngineName = "MyBot",
            EngineVersion = "2.1",
            Author = "Jane Doe",
        };
        AssertGolden(
            message,
            """{"type":"hello","protocolVersion":1,"engineName":"MyBot","engineVersion":"2.1","author":"Jane Doe"}""");
    }

    [Fact]
    public void Hello_OptionalFieldsAbsent_NotNull()
    {
        var message = new HelloMessage { ProtocolVersion = 1, EngineName = "MyBot" };
        AssertGolden(
            message,
            """{"type":"hello","protocolVersion":1,"engineName":"MyBot"}""");
    }

    [Fact]
    public void Welcome()
    {
        AssertGolden(
            new WelcomeMessage { ProtocolVersion = 1 },
            """{"type":"welcome","protocolVersion":1}""");
    }

    [Fact]
    public void Rejected()
    {
        AssertGolden(
            new RejectedMessage { Reason = "protocol version 2 not supported; this server speaks 1" },
            """{"type":"rejected","reason":"protocol version 2 not supported; this server speaks 1"}""");
    }

    [Fact]
    public void PlayQuery_OpeningPosition()
    {
        var message = new PlayQueryMessage
        {
            RequestId = "q-17",
            State = OpeningState(matchLength: 7, yourScore: 0, opponentScore: 0),
            Die1 = 3,
            Die2 = 1,
        };
        AssertGolden(
            message,
            """{"type":"playQuery","requestId":"q-17","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":0,"opponentScore":0,"isCrawford":false},"die1":3,"die2":1}""");
    }

    /// <summary>Answers the 3-1 opening playQuery golden: 8/5 6/5, the point-making play.</summary>
    [Fact]
    public void PlayReply_TwoMoves()
    {
        var message = new PlayReplyMessage
        {
            RequestId = "q-17",
            Moves = new[]
            {
                new WireMove { From = 8, To = 5 },
                new WireMove { From = 6, To = 5 },
            },
        };
        AssertGolden(
            message,
            """{"type":"playReply","requestId":"q-17","moves":[{"from":8,"to":5},{"from":6,"to":5}]}""");
    }

    [Fact]
    public void PlayReply_NoLegalPlay_EmptyMoves()
    {
        var message = new PlayReplyMessage { RequestId = "q-18", Moves = Array.Empty<WireMove>() };
        AssertGolden(
            message,
            """{"type":"playReply","requestId":"q-18","moves":[]}""");
    }

    [Fact]
    public void CubeOfferQuery()
    {
        var message = new CubeOfferQueryMessage
        {
            RequestId = "q-42",
            State = OpeningState(matchLength: 7, yourScore: 3, opponentScore: 2),
        };
        AssertGolden(
            message,
            """{"type":"cubeOfferQuery","requestId":"q-42","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":3,"opponentScore":2,"isCrawford":false}}""");
    }

    [Fact]
    public void CubeOfferReply_Double()
    {
        AssertGolden(
            new CubeOfferReplyMessage { RequestId = "q-42", Action = CubeOfferAction.Double },
            """{"type":"cubeOfferReply","requestId":"q-42","action":"double"}""");
    }

    /// <summary>
    /// The redouble frame pin: at response-query time the cube is still the
    /// pre-double cube, so the responder sees value 2 owned by "opponent" (the
    /// redoubling offerer) — never "you". The in-proc twin of this pin lives in
    /// BgGame_Lib's responder-frame tests; this is the wire-text half.
    /// </summary>
    [Fact]
    public void CubeResponseQuery_Redouble_ShowsPreDoubleCube_OpponentOwned()
    {
        var message = new CubeResponseQueryMessage
        {
            RequestId = "q-43",
            State = new WireGameState
            {
                Board = new[] { 0, 2, 2, 2, 2, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -2, -2, -3, -2, -2, -2, 0 },
                CubeValue = 2,
                CubeOwner = WireCubeOwner.Opponent,
                MatchLength = 7,
                YourScore = 2,
                OpponentScore = 3,
                IsCrawford = false,
            },
        };
        AssertGolden(
            message,
            """{"type":"cubeResponseQuery","requestId":"q-43","state":{"board":[0,2,2,2,2,2,3,0,0,0,0,0,0,0,0,0,0,0,0,-2,-2,-3,-2,-2,-2,0],"cubeValue":2,"cubeOwner":"opponent","matchLength":7,"yourScore":2,"opponentScore":3,"isCrawford":false}}""");
    }

    [Fact]
    public void CubeResponseReply_Take()
    {
        AssertGolden(
            new CubeResponseReplyMessage { RequestId = "q-43", Action = CubeResponseAction.Take },
            """{"type":"cubeResponseReply","requestId":"q-43","action":"take"}""");
    }

    [Fact]
    public void MatchStarted_MatchPlay_NoGamesCap()
    {
        AssertGolden(
            new MatchStartedMessage { MatchId = "m-1", Opponent = "RandomBot", MatchLength = 7 },
            """{"type":"matchStarted","matchId":"m-1","opponent":"RandomBot","matchLength":7}""");
    }

    [Fact]
    public void MatchStarted_MoneySession_WithGamesCap()
    {
        AssertGolden(
            new MatchStartedMessage { MatchId = "m-2", Opponent = "RandomBot", MatchLength = 0, MaxGames = 50 },
            """{"type":"matchStarted","matchId":"m-2","opponent":"RandomBot","matchLength":0,"maxGames":50}""");
    }

    [Fact]
    public void MatchEnded_MatchComplete()
    {
        var message = new MatchEndedMessage
        {
            MatchId = "m-1",
            Reason = MatchEndReason.MatchComplete,
            YourPoints = 7,
            OpponentPoints = 4,
            YouWon = true,
        };
        AssertGolden(
            message,
            """{"type":"matchEnded","matchId":"m-1","reason":"matchComplete","yourPoints":7,"opponentPoints":4,"youWon":true}""");
    }

    [Fact]
    public void MatchEnded_Forfeit_WithDetail()
    {
        var message = new MatchEndedMessage
        {
            MatchId = "m-1",
            Reason = MatchEndReason.Forfeit,
            YourPoints = 3,
            OpponentPoints = 7,
            YouWon = false,
            ForfeitedBy = ForfeitSide.You,
            Detail = "reply to playQuery was not a legal play for the queried dice",
        };
        AssertGolden(
            message,
            """{"type":"matchEnded","matchId":"m-1","reason":"forfeit","yourPoints":3,"opponentPoints":7,"youWon":false,"forfeitedBy":"you","detail":"reply to playQuery was not a legal play for the queried dice"}""");
    }

    [Fact]
    public void MatchEnded_MoneySession_NoWinner()
    {
        var message = new MatchEndedMessage
        {
            MatchId = "m-2",
            Reason = MatchEndReason.GamesCapReached,
            YourPoints = 11,
            OpponentPoints = 9,
        };
        AssertGolden(
            message,
            """{"type":"matchEnded","matchId":"m-2","reason":"gamesCapReached","yourPoints":11,"opponentPoints":9}""");
    }
}
