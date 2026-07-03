namespace BgTournament.Protocol;

/// <summary>
/// Engine → server. The response decision for a
/// <see cref="CubeResponseQueryMessage"/>: take (play on at the doubled
/// stake, owning the cube) or pass (concede the game at the pre-double stake).
/// </summary>
public sealed record CubeResponseReplyMessage : ReplyMessage
{
    /// <summary>The chosen action.</summary>
    public required CubeResponseAction Action { get; init; }
}
