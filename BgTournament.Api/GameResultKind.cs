using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// How decisively a game was won. The game's <c>points</c> already carries
/// the settled award (cube × multiplier) — this is the descriptive half.
/// Serializes as the camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GameResultKind>))]
public enum GameResultKind
{
    /// <summary>The loser had borne off at least one checker (or passed a double).</summary>
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>The loser had borne off nothing.</summary>
    [JsonStringEnumMemberName("gammon")]
    Gammon,

    /// <summary>The loser had borne off nothing and still had a checker in the winner's home or on the bar.</summary>
    [JsonStringEnumMemberName("backgammon")]
    Backgammon,
}
