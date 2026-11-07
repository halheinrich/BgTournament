namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. Your opponent has offered a double: take or pass. Answer
/// with <see cref="CubeResponseReplyMessage"/>.
///
/// <para>The state is in the on-roll player's frame like every query, and
/// here your opponent is the player on roll (they double before rolling): you
/// are the negative side, your score the opponent's fields. The cube shown is
/// the pre-double cube: <see cref="WireCubeOwner.Centered"/> for a first double
/// or <see cref="WireCubeOwner.OnRoll"/> for a redouble, at its pre-double
/// value. If you take, the cube becomes yours at twice that value. See
/// PROTOCOL.md ("The frame rule") for a worked example.</para>
/// </summary>
public sealed record CubeResponseQueryMessage : QueryMessage
{
    /// <summary>The position and match context, in the on-roll player's frame — your opponent's: you are the negative side.</summary>
    public required WireGameState State { get; init; }
}
