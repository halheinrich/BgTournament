using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// How a hosted match concluded (or that it has not yet). This is the admin
/// API's status vocabulary <em>and</em> the server's own match state — one
/// enum, so an internal status change is visibly a contract change.
/// Serializes as the camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchStatus>))]
public enum MatchStatus
{
    /// <summary>Still being played.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Played to a natural end — a winner, or the games cap.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Ended by a forfeit: contract violation, timeout, or disconnect.</summary>
    [JsonStringEnumMemberName("forfeited")]
    Forfeited,

    /// <summary>Stopped by the server (shutdown), no side at fault.</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>An unexpected server-side error; see the detail and logs.</summary>
    [JsonStringEnumMemberName("faulted")]
    Faulted,
}
