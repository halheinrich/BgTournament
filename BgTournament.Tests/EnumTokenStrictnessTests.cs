using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgTournament.Api;
using BgTournament.Server.Persistence;

namespace BgTournament.Tests;

/// <summary>
/// Pins the string-token-exact contract of every attribute-registered enum
/// (halheinrich/backgammon#164): the readers of the admin Api and the durable
/// journal accept the pinned member names and reject numeric ordinals, so no
/// payload can silently re-couple to member declaration numbering. The wire's
/// equivalent pin lives with <see cref="ProtocolRoundTripTests"/> (its
/// options-form converter was already strict). Which bytes the names are is
/// pinned by <see cref="ApiGoldenTests"/> and <see cref="JournalGoldenTests"/>;
/// this file pins which token kinds the readers admit.
/// </summary>
public class EnumTokenStrictnessTests
{
    /// <summary>
    /// A probe enum with the DEFAULT converter registration — the measurement
    /// that motivates <see cref="StrictJsonStringEnumConverter{TEnum}"/>: the
    /// base converter accepts integer ordinals on read, defined or not.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DefaultConverterProbe>))]
    private enum DefaultConverterProbe
    {
        Alpha,
        Beta,
    }

    /// <summary>
    /// The measured STJ default this repo tightens away from. If this pin ever
    /// breaks, STJ changed its default and the strict converter's rationale
    /// (and the halheinrich/backgammon#164 sweep recipe) needs re-measuring.
    /// </summary>
    [Fact]
    public void DefaultConverter_AcceptsOrdinals_TheHazardBeingClosed()
    {
        Assert.Equal(DefaultConverterProbe.Beta, JsonSerializer.Deserialize<DefaultConverterProbe>("1"));
        Assert.Equal((DefaultConverterProbe)99, JsonSerializer.Deserialize<DefaultConverterProbe>("99"));
    }

    /// <summary>
    /// The reader is the inverse of its writer: the pinned name deserializes to
    /// its member, while the member's own ordinal — and an undefined one — are
    /// rejected with the <see cref="JsonException"/> every serialization
    /// boundary here funnels malformed payloads into.
    /// </summary>
    private static void AssertStringTokenExact<TEnum>(string pinnedToken, TEnum member)
        where TEnum : struct, Enum
    {
        Assert.Equal(member, JsonSerializer.Deserialize<TEnum>($"\"{pinnedToken}\""));

        string ordinal = Convert.ToInt32(member, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TEnum>(ordinal));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TEnum>("99"));
    }

    [Fact]
    public void ApiSeat_IsStringTokenExact() =>
        AssertStringTokenExact("seatOne", Seat.One);

    [Fact]
    public void ApiCubeOwner_IsStringTokenExact() =>
        AssertStringTokenExact("centered", CubeOwner.Centered);

    [Fact]
    public void ApiCubeResponseAction_IsStringTokenExact() =>
        AssertStringTokenExact("take", CubeResponseAction.Take);

    [Fact]
    public void ApiDecisionKind_IsStringTokenExact() =>
        AssertStringTokenExact("play", DecisionKind.Play);

    [Fact]
    public void ApiForfeitCause_IsStringTokenExact() =>
        AssertStringTokenExact("timeout", ForfeitCause.Timeout);

    [Fact]
    public void ApiGameResultKind_IsStringTokenExact() =>
        AssertStringTokenExact("gammon", GameResultKind.Gammon);

    [Fact]
    public void ApiMatchStatus_IsStringTokenExact() =>
        AssertStringTokenExact("running", MatchStatus.Running);

    [Fact]
    public void ApiTournamentStatus_IsStringTokenExact() =>
        AssertStringTokenExact("completed", TournamentStatus.Completed);

    [Fact]
    public void JournalSeat_IsStringTokenExact() =>
        AssertStringTokenExact("one", JournalSeat.One);

    [Fact]
    public void JournalCubeOwner_IsStringTokenExact() =>
        AssertStringTokenExact("onRoll", JournalCubeOwner.OnRoll);

    [Fact]
    public void JournalCubeAction_IsStringTokenExact() =>
        AssertStringTokenExact("double", JournalCubeAction.Double);

    [Fact]
    public void JournalDecisionKind_IsStringTokenExact() =>
        AssertStringTokenExact("cubeOffer", JournalDecisionKind.CubeOffer);

    [Fact]
    public void JournalResultKind_IsStringTokenExact() =>
        AssertStringTokenExact("single", JournalResultKind.Single);

    [Fact]
    public void JournalMatchOutcome_IsStringTokenExact() =>
        AssertStringTokenExact("forfeited", JournalMatchOutcome.Forfeited);

    [Fact]
    public void JournalForfeitCause_IsStringTokenExact() =>
        AssertStringTokenExact("contractViolation", JournalForfeitCause.ContractViolation);

    [Fact]
    public void JournalTournamentOutcome_IsStringTokenExact() =>
        AssertStringTokenExact("aborted", JournalTournamentOutcome.Aborted);

    /// <summary>
    /// The rejection through the real durable reader: an ordinal enum token in
    /// a journal line fails as any malformed line does — the
    /// <see cref="JsonException"/> the rehydrator's corruption policy keys on —
    /// instead of decoding to whatever member holds that number today.
    /// </summary>
    [Fact]
    public void JournalCodec_OrdinalEnumToken_ThrowsJsonException()
    {
        var line =
            """{"type":"cube","at":"2026-07-05T12:00:00+00:00","onRollSeat":"one","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeSize":1,"cubeOwner":"centered","matchLength":5,"onRollScore":2,"opponentScore":3,"isCrawford":false},"action":1}""";
        Assert.Throws<JsonException>(() => JournalCodec.DeserializeMatchEvent(line));
    }

    /// <summary>
    /// The write side of the same strictness: with integer fallback disabled an
    /// undefined value cannot be serialized at all, where the default converter
    /// would have written its number.
    /// </summary>
    [Fact]
    public void StrictConverter_UndefinedValue_CannotBeWritten() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((Seat)99));
}
