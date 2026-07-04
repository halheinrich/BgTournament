using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// The two answers to an offered double — deliberately narrow, mirroring the
/// wire protocol's per-query enums: a replay can never carry an
/// out-of-context cube action. Serializes as the camelCase strings pinned on
/// each member.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CubeResponseAction>))]
public enum CubeResponseAction
{
    /// <summary>The double was accepted; play continued at the raised stake.</summary>
    [JsonStringEnumMemberName("take")]
    Take,

    /// <summary>The double was declined; the game ended at the pre-double stake.</summary>
    [JsonStringEnumMemberName("pass")]
    Pass,
}
