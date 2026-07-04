using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// Who holds the doubling cube in a <see cref="GamePosition"/> — seat-keyed
/// (absolute), like everything else in a replay position. Serializes as the
/// camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CubeOwner>))]
public enum CubeOwner
{
    /// <summary>Nobody owns the cube yet; either side may double.</summary>
    [JsonStringEnumMemberName("centered")]
    Centered,

    /// <summary>Seat One owns the cube.</summary>
    [JsonStringEnumMemberName("seatOne")]
    SeatOne,

    /// <summary>Seat Two owns the cube.</summary>
    [JsonStringEnumMemberName("seatTwo")]
    SeatTwo,
}
