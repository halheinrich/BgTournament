namespace BgTournament.Protocol;

/// <summary>
/// Engine → server. The offer decision for a <see cref="CubeOfferQueryMessage"/>:
/// double, or roll without doubling.
/// </summary>
public sealed record CubeOfferReplyMessage : ReplyMessage
{
    /// <summary>The chosen action.</summary>
    public required CubeOfferAction Action { get; init; }
}
