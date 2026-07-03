namespace BgTournament.Protocol;

/// <summary>
/// A single checker movement inside a <see cref="PlayReplyMessage"/>, in the
/// replying engine's frame. Hits are not encoded — the server derives them
/// when it resolves the reply against its own legal-play set (see PROTOCOL.md
/// "Move encoding").
/// </summary>
public sealed record WireMove
{
    /// <summary>Source point: 1–24, or 25 to enter from your bar.</summary>
    public required int From { get; init; }

    /// <summary>Destination point: 1–24, or 0 to bear the checker off.</summary>
    public required int To { get; init; }
}
