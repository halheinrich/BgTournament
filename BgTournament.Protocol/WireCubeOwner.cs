namespace BgTournament.Protocol;

/// <summary>
/// Doubling-cube ownership as expressed on the wire, relative to the engine
/// receiving the state (serialized as <c>"you"</c> / <c>"opponent"</c> /
/// <c>"centered"</c>; see PROTOCOL.md).
/// </summary>
public enum WireCubeOwner
{
    /// <summary>You own the cube; only you may offer the next double.</summary>
    You,

    /// <summary>Your opponent owns the cube; only they may offer the next double.</summary>
    Opponent,

    /// <summary>The cube is centered; either player may offer the first double.</summary>
    Centered,
}
