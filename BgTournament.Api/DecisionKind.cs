using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// Which decision query a clocked audit event timed. Serializes as the
/// camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DecisionKind>))]
public enum DecisionKind
{
    /// <summary>A play query (checker movement for a rolled pair).</summary>
    [JsonStringEnumMemberName("play")]
    Play,

    /// <summary>A cube-offer query (double or roll).</summary>
    [JsonStringEnumMemberName("cubeOffer")]
    CubeOffer,

    /// <summary>A cube-response query (take or pass).</summary>
    [JsonStringEnumMemberName("cubeResponse")]
    CubeResponse,
}
