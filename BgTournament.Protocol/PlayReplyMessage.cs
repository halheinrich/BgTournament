namespace BgTournament.Protocol;

/// <summary>
/// Engine → server. The chosen play for a <see cref="PlayQueryMessage"/>: the
/// checker movements for the whole turn (up to 4 for doubles), order
/// insignificant, hits implicit. Empty means no legal play exists. The play
/// must be legal for the queried position and dice — an illegal or
/// unresolvable play forfeits the match (see PROTOCOL.md).
/// </summary>
public sealed record PlayReplyMessage : ReplyMessage
{
    /// <summary>The checker movements, 0–4 entries.</summary>
    public required IReadOnlyList<WireMove> Moves { get; init; }
}
