namespace BgTournament.Protocol;

/// <summary>
/// The two legal answers to a <see cref="CubeOfferQueryMessage"/> (serialized
/// as <c>"noDouble"</c> / <c>"double"</c>). A deliberately narrow enum: a
/// take/pass value in an offer reply fails deserialization rather than
/// reaching game logic.
/// </summary>
public enum CubeOfferAction
{
    /// <summary>Do not double; proceed to roll.</summary>
    NoDouble,

    /// <summary>Offer the doubling cube to the opponent.</summary>
    Double,
}
