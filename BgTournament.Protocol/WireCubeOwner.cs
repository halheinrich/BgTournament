namespace BgTournament.Protocol;

/// <summary>
/// Doubling-cube ownership as expressed on the wire, relative to the player on
/// roll — never to the engine receiving the state (serialized as
/// <c>"onRoll"</c> / <c>"opponent"</c> / <c>"centered"</c>; see PROTOCOL.md).
/// </summary>
public enum WireCubeOwner
{
    /// <summary>The player on roll owns the cube; only they may offer the next double.</summary>
    OnRoll,

    /// <summary>The opponent of the player on roll owns the cube; only they may offer the next double.</summary>
    Opponent,

    /// <summary>The cube is centered; either player may offer the first double.</summary>
    Centered,
}
