namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. Your opponent has offered a double: take or pass. Answer
/// with <see cref="CubeResponseReplyMessage"/>.
///
/// <para>The state is in <em>your</em> frame like every query, but here your
/// opponent is the player on roll (they double before rolling), and the cube
/// shown is the pre-double cube: <see cref="WireCubeOwner.Centered"/> for a
/// first double or <see cref="WireCubeOwner.Opponent"/> for a redouble, at its
/// pre-double value. If you take, the cube becomes yours at twice that value.
/// See PROTOCOL.md ("The frame rule") for a worked example.</para>
/// </summary>
public sealed record CubeResponseQueryMessage : QueryMessage
{
    /// <summary>The position and match context, in your frame; your opponent is on roll.</summary>
    public required WireGameState State { get; init; }
}
