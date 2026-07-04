using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// One recorded decision moment in a game replay, discriminated on the wire
/// by <c>"type"</c> (<c>play</c> / <c>cubeOffer</c> / <c>cubeResponse</c>) —
/// the same discriminated-union style the engine wire protocol uses. A
/// declined cube window is implicit: a play entry with no preceding cube
/// entry. The game's terminal event is not an entry; it is the game-level
/// outcome plus <see cref="GameReplay.FinalState"/>.
/// </summary>
/// <param name="Actor">The seat that made this decision.</param>
/// <param name="State">
/// The position the decision was made in (seat-One frame). A viewer steps
/// through positions: the outcome of this entry's action is the next entry's
/// <c>State</c> — or <see cref="GameReplay.FinalState"/> after the last entry.
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlayEntry), "play")]
[JsonDerivedType(typeof(CubeOfferEntry), "cubeOffer")]
[JsonDerivedType(typeof(CubeResponseEntry), "cubeResponse")]
public abstract record GameEntry(Seat Actor, GamePosition State);

/// <summary>
/// A play decision: the dice rolled and the moves chosen. A game's first play
/// entry is the opening roll — <c>Die1</c> is seat One's die, <c>Die2</c> seat
/// Two's, and the higher die's seat (this entry's <see cref="GameEntry.Actor"/>)
/// plays the pair. A dance (no legal move) is a play entry with empty
/// <paramref name="Moves"/>.
/// </summary>
/// <param name="Actor">The seat on roll.</param>
/// <param name="State">The position the play was chosen in (seat-One frame).</param>
/// <param name="Die1">First die (the opening roll's seat-One die).</param>
/// <param name="Die2">Second die (the opening roll's seat-Two die).</param>
/// <param name="Moves">The chosen checker movements, in the actor's own numbering (standard notation); empty for a dance.</param>
public sealed record PlayEntry(
    Seat Actor,
    GamePosition State,
    int Die1,
    int Die2,
    IReadOnlyList<PlayMove> Moves) : GameEntry(Actor, State);

/// <summary>
/// A double offered at the actor's pre-roll cube window. Offering is the
/// whole datum — <see cref="GameEntry.State"/> shows the pre-double cube.
/// </summary>
/// <param name="Actor">The seat offering the double.</param>
/// <param name="State">The position the double was offered from (seat-One frame, pre-double cube).</param>
public sealed record CubeOfferEntry(Seat Actor, GamePosition State) : GameEntry(Actor, State);

/// <summary>
/// The answer to the immediately preceding <see cref="CubeOfferEntry"/>.
/// <see cref="GameEntry.State"/> is the same pre-double position the offer
/// shows — nothing has moved between offer and answer.
/// </summary>
/// <param name="Actor">The seat answering the double (the offer's opponent).</param>
/// <param name="State">The position being decided (seat-One frame, pre-double cube).</param>
/// <param name="Action">Take or pass.</param>
public sealed record CubeResponseEntry(
    Seat Actor,
    GamePosition State,
    CubeResponseAction Action) : GameEntry(Actor, State);
