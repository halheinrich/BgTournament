namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. You are on roll and may legally double: decide before
/// rolling. Answer with <see cref="CubeOfferReplyMessage"/>. Only sent when
/// doubling is legal for you — never in Crawford games, cubeless (1-point)
/// matches, or when your opponent owns the cube.
/// </summary>
public sealed record CubeOfferQueryMessage : QueryMessage
{
    /// <summary>The position and match context, in your frame; you are on roll.</summary>
    public required WireGameState State { get; init; }
}
