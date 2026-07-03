using System.Text.Json;
using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Object-level round-trip coverage plus the strictness and tolerance edges of
/// <see cref="WireProtocol.Deserialize"/>: everything malformed surfaces as
/// <see cref="JsonException"/> (the single failure type adapters translate to
/// protocol violations), while the documented tolerances — unknown fields
/// ignored, discriminator accepted out of order — hold for third-party
/// friendliness.
/// </summary>
public class ProtocolRoundTripTests
{
    [Fact]
    public void Version_IsOne()
    {
        Assert.Equal(1, WireProtocol.Version);
    }

    [Fact]
    public void PlayQuery_RoundTrips_AllFields()
    {
        var original = new PlayQueryMessage
        {
            RequestId = "abc",
            State = new WireGameState
            {
                Board = new[] { 0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0 },
                CubeValue = 4,
                CubeOwner = WireCubeOwner.You,
                MatchLength = 0,
                YourScore = 2,
                OpponentScore = 5,
                IsCrawford = false,
                Xgid = "XGID=-b----E-C---eE---c-e----B-:1:-1:1:31:2:5:0:0:10",
            },
            Die1 = 3,
            Die2 = 1,
        };

        var restored = Assert.IsType<PlayQueryMessage>(WireProtocol.Deserialize(WireProtocol.Serialize(original)));

        Assert.Equal(original.RequestId, restored.RequestId);
        Assert.Equal(original.State.Board, restored.State.Board);
        Assert.Equal(original.State.CubeValue, restored.State.CubeValue);
        Assert.Equal(original.State.CubeOwner, restored.State.CubeOwner);
        Assert.Equal(original.State.MatchLength, restored.State.MatchLength);
        Assert.Equal(original.State.YourScore, restored.State.YourScore);
        Assert.Equal(original.State.OpponentScore, restored.State.OpponentScore);
        Assert.Equal(original.State.IsCrawford, restored.State.IsCrawford);
        Assert.Equal(original.State.Xgid, restored.State.Xgid);
        Assert.Equal(original.Die1, restored.Die1);
        Assert.Equal(original.Die2, restored.Die2);
    }

    [Fact]
    public void PlayReply_RoundTrips_FourMoveDouble()
    {
        var original = new PlayReplyMessage
        {
            RequestId = "r1",
            Moves = new[]
            {
                new WireMove { From = 13, To = 8 },
                new WireMove { From = 13, To = 8 },
                new WireMove { From = 8, To = 3 },
                new WireMove { From = 8, To = 3 },
            },
        };

        var restored = Assert.IsType<PlayReplyMessage>(WireProtocol.Deserialize(WireProtocol.Serialize(original)));

        Assert.Equal(4, restored.Moves.Count);
        Assert.Equal(original.Moves, restored.Moves);
    }

    [Fact]
    public void EveryMessageType_RoundTrips_ToItsOwnType()
    {
        var state = new WireGameState
        {
            Board = new int[26],
            CubeValue = 1,
            CubeOwner = WireCubeOwner.Centered,
            MatchLength = 5,
            YourScore = 0,
            OpponentScore = 0,
            IsCrawford = false,
        };
        var messages = new ProtocolMessage[]
        {
            new HelloMessage { ProtocolVersion = 1, EngineName = "e" },
            new WelcomeMessage { ProtocolVersion = 1 },
            new RejectedMessage { Reason = "r" },
            new PlayQueryMessage { RequestId = "1", State = state, Die1 = 6, Die2 = 6 },
            new PlayReplyMessage { RequestId = "1", Moves = Array.Empty<WireMove>() },
            new CubeOfferQueryMessage { RequestId = "2", State = state },
            new CubeOfferReplyMessage { RequestId = "2", Action = CubeOfferAction.NoDouble },
            new CubeResponseQueryMessage { RequestId = "3", State = state },
            new CubeResponseReplyMessage { RequestId = "3", Action = CubeResponseAction.Pass },
            new MatchStartedMessage { MatchId = "m", Opponent = "o", MatchLength = 5 },
            new MatchEndedMessage { MatchId = "m", Reason = MatchEndReason.MatchComplete, YourPoints = 5, OpponentPoints = 1, YouWon = true },
        };

        foreach (var message in messages)
        {
            var restored = WireProtocol.Deserialize(WireProtocol.Serialize(message));
            Assert.Equal(message.GetType(), restored.GetType());
        }
    }

    [Fact]
    public void OptionalFields_AbsentOnWire_DeserializeToNull()
    {
        var restored = Assert.IsType<MatchEndedMessage>(WireProtocol.Deserialize(
            """{"type":"matchEnded","matchId":"m","reason":"gamesCapReached","yourPoints":1,"opponentPoints":2}"""));

        Assert.Null(restored.YouWon);
        Assert.Null(restored.ForfeitedBy);
        Assert.Null(restored.Detail);
    }

    [Fact]
    public void Deserialize_TypeDiscriminatorNotFirst_IsAccepted()
    {
        // Third-party JSON emitters do not all control key order; the contract
        // accepts "type" anywhere in the object.
        var restored = Assert.IsType<CubeOfferReplyMessage>(WireProtocol.Deserialize(
            """{"requestId":"q","action":"double","type":"cubeOfferReply"}"""));

        Assert.Equal(CubeOfferAction.Double, restored.Action);
    }

    [Fact]
    public void Deserialize_UnknownExtraField_IsIgnored()
    {
        var restored = Assert.IsType<WelcomeMessage>(WireProtocol.Deserialize(
            """{"type":"welcome","protocolVersion":1,"futureField":"ignored"}"""));

        Assert.Equal(1, restored.ProtocolVersion);
    }

    [Fact]
    public void Deserialize_GarbageText_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize("this is not json {{"));
    }

    [Fact]
    public void Deserialize_JsonNull_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize("null"));
    }

    [Fact]
    public void Deserialize_UnknownType_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize("""{"type":"quack","requestId":"1"}"""));
    }

    [Fact]
    public void Deserialize_MissingType_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize("""{"requestId":"1","action":"double"}"""));
    }

    [Fact]
    public void Deserialize_MissingRequiredField_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize("""{"type":"hello","protocolVersion":1}"""));
    }

    [Fact]
    public void Deserialize_IntegerEnumValue_ThrowsJsonException()
    {
        // The wire is strict: enum values are camelCase strings only.
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize(
            """{"type":"cubeOfferReply","requestId":"1","action":1}"""));
    }

    [Fact]
    public void Deserialize_OfferActionInResponseReply_ThrowsJsonException()
    {
        // The narrow per-query enums make an out-of-contract action value fail
        // at the deserialization boundary, not in game logic.
        Assert.Throws<JsonException>(() => WireProtocol.Deserialize(
            """{"type":"cubeResponseReply","requestId":"1","action":"double"}"""));
    }

    [Fact]
    public void Serialize_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WireProtocol.Serialize(null!));
    }

    [Fact]
    public void Deserialize_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WireProtocol.Deserialize(null!));
    }
}
