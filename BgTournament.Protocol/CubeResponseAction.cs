namespace BgTournament.Protocol;

/// <summary>
/// The two legal answers to a <see cref="CubeResponseQueryMessage"/>
/// (serialized as <c>"take"</c> / <c>"pass"</c>). A deliberately narrow enum:
/// an offer-side value in a response reply fails deserialization rather than
/// reaching game logic.
/// </summary>
public enum CubeResponseAction
{
    /// <summary>Accept the double; play on at the raised stake.</summary>
    Take,

    /// <summary>Decline the double; concede the game at the pre-double stake.</summary>
    Pass,
}
