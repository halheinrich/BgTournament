using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// The structured forfeit taxonomy — which of the server's forfeit paths
/// ended a match. Carried by the audit surface's terminal event so an arbiter
/// reads a cause, not a prose string (<c>detail</c> stays the human-readable
/// companion). Serializes as the camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ForfeitCause>))]
public enum ForfeitCause
{
    /// <summary>A malformed, illegal, or out-of-contract reply.</summary>
    [JsonStringEnumMemberName("contractViolation")]
    ContractViolation,

    /// <summary>The flat per-decision timeout elapsed.</summary>
    [JsonStringEnumMemberName("timeout")]
    Timeout,

    /// <summary>The Fischer clock pool emptied mid-decision — the flag fell.</summary>
    [JsonStringEnumMemberName("flagFall")]
    FlagFall,

    /// <summary>The engine's connection ended mid-match.</summary>
    [JsonStringEnumMemberName("disconnect")]
    Disconnect,

    /// <summary>A tournament participant was not connected when its match came up (forfeit without play).</summary>
    [JsonStringEnumMemberName("neverConnected")]
    NeverConnected,
}
