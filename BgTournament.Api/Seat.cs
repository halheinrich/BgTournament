using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// Stable identity of one side of a match, independent of whose turn it is.
/// Seat One is always the match's <c>engineOne</c> (and seat Two its
/// <c>engineTwo</c>), so a viewer can anchor each engine to one side of the
/// board for a whole replay. Serializes as the camelCase strings pinned on
/// each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Seat>))]
public enum Seat
{
    /// <summary>The first seat — occupied by the match's <c>engineOne</c>.</summary>
    [JsonStringEnumMemberName("seatOne")]
    One = 1,

    /// <summary>The second seat — occupied by the match's <c>engineTwo</c>.</summary>
    [JsonStringEnumMemberName("seatTwo")]
    Two = 2,
}
