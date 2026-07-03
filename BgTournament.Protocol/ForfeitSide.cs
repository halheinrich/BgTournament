namespace BgTournament.Protocol;

/// <summary>
/// Which side forfeited, relative to the engine receiving the message
/// (serialized as <c>"you"</c> / <c>"opponent"</c>).
/// </summary>
public enum ForfeitSide
{
    /// <summary>The receiving engine forfeited.</summary>
    You,

    /// <summary>The receiving engine's opponent forfeited.</summary>
    Opponent,
}
