using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// How a hosted tournament concluded (or that it has not yet). This is the
/// admin API's status vocabulary <em>and</em> the server's own tournament
/// state — one enum, so an internal status change is visibly a contract
/// change. Serializes as the camelCase strings pinned on each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TournamentStatus>))]
public enum TournamentStatus
{
    /// <summary>Matches are still being played.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Every scheduled match has a result and the winner is declared.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Stopped by the server (shutdown), no side at fault.</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>A match ended without a winner to fold, or an unexpected server error; see the detail and logs.</summary>
    [JsonStringEnumMemberName("faulted")]
    Faulted,

    /// <summary>
    /// The server died (crash, kill) while the tournament was running; the
    /// record was reconstructed from its journal at the next start. Terminal,
    /// and only ever produced by rehydration — a live tournament never
    /// carries it.
    /// </summary>
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,
}
