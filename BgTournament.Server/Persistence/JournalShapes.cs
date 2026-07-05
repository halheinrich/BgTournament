using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

// The journal's own vocabulary, deliberately separate from the Api and
// substrate enums it mirrors: a journal file is a durable format, and an Api
// or substrate rename must never silently rewrite bytes already on disk.
// JournalMapping is the only place the correspondences live; JournalGoldenTests
// pins every string.

/// <summary>A match seat, journal-pinned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalSeat>))]
internal enum JournalSeat
{
    /// <summary>Seat One (the record's <c>engineOne</c>).</summary>
    [JsonStringEnumMemberName("one")]
    One,

    /// <summary>Seat Two (the record's <c>engineTwo</c>).</summary>
    [JsonStringEnumMemberName("two")]
    Two,
}

/// <summary>
/// A snapshot's cube owner, journal-pinned. Perspective-relative like the
/// substrate value it records: interpret via the entry's on-roll seat.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalCubeOwner>))]
internal enum JournalCubeOwner
{
    /// <summary>Nobody owns the cube yet.</summary>
    [JsonStringEnumMemberName("centered")]
    Centered,

    /// <summary>The on-roll (frame) player owns the cube.</summary>
    [JsonStringEnumMemberName("onRoll")]
    OnRoll,

    /// <summary>The opponent of the frame player owns the cube.</summary>
    [JsonStringEnumMemberName("opponent")]
    Opponent,
}

/// <summary>A cube decision, journal-pinned (offer side and response side).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalCubeAction>))]
internal enum JournalCubeAction
{
    /// <summary>The on-roll player declined to double.</summary>
    [JsonStringEnumMemberName("noDouble")]
    NoDouble,

    /// <summary>The on-roll player offered the cube.</summary>
    [JsonStringEnumMemberName("double")]
    Double,

    /// <summary>The responder took the offered cube.</summary>
    [JsonStringEnumMemberName("take")]
    Take,

    /// <summary>The responder passed, ending the game.</summary>
    [JsonStringEnumMemberName("pass")]
    Pass,
}

/// <summary>A finished game's win kind, journal-pinned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalResultKind>))]
internal enum JournalResultKind
{
    /// <summary>A single win.</summary>
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>A gammon.</summary>
    [JsonStringEnumMemberName("gammon")]
    Gammon,

    /// <summary>A backgammon.</summary>
    [JsonStringEnumMemberName("backgammon")]
    Backgammon,
}

/// <summary>
/// How a journaled match concluded, journal-pinned. Deliberately terminal-only:
/// a running match is a journal with no terminal event, and an interrupted one
/// is exactly that at rehydration time — neither is ever written.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalMatchOutcome>))]
internal enum JournalMatchOutcome
{
    /// <summary>Played to a natural end.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Ended by a forfeit; see the forfeit cause.</summary>
    [JsonStringEnumMemberName("forfeited")]
    Forfeited,

    /// <summary>Stopped deliberately by the server (shutdown).</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>An unexpected server-side error.</summary>
    [JsonStringEnumMemberName("faulted")]
    Faulted,
}

/// <summary>Why a match was forfeited, journal-pinned — structured for the arbitration record, not string parsing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalForfeitCause>))]
internal enum JournalForfeitCause
{
    /// <summary>A malformed, illegal, or out-of-contract reply.</summary>
    [JsonStringEnumMemberName("contractViolation")]
    ContractViolation,

    /// <summary>The flat per-decision timeout elapsed.</summary>
    [JsonStringEnumMemberName("timeout")]
    Timeout,

    /// <summary>The Fischer clock pool emptied mid-decision.</summary>
    [JsonStringEnumMemberName("flagFall")]
    FlagFall,

    /// <summary>The engine's connection ended mid-match.</summary>
    [JsonStringEnumMemberName("disconnect")]
    Disconnect,

    /// <summary>A tournament participant was not connected when its match came up (forfeit without play).</summary>
    [JsonStringEnumMemberName("neverConnected")]
    NeverConnected,
}

/// <summary>How a journaled tournament concluded, journal-pinned (terminal-only, like <see cref="JournalMatchOutcome"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalTournamentOutcome>))]
internal enum JournalTournamentOutcome
{
    /// <summary>Every scheduled match folded and the winner is declared.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Stopped deliberately by the server (shutdown, or an aborted match).</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>A match ended without a winner to fold, or an unexpected server error.</summary>
    [JsonStringEnumMemberName("faulted")]
    Faulted,
}

/// <summary>
/// One checker move, full substrate fidelity: <paramref name="To"/> keeps the
/// sign encoding (negative = hit, 0 = bear-off) the wire deliberately strips —
/// the journal must rebuild the exact <c>Play</c>, hits included.
/// </summary>
/// <param name="From">Source point (1–24; 25 = bar entry), mover's numbering.</param>
/// <param name="To">Destination, sign-encoded: positive = plain move, negative = hit on |To|, 0 = bear-off.</param>
internal sealed record JournalMove(int From, int To);

/// <summary>
/// A recorded game snapshot at full substrate fidelity, in the entry's native
/// (on-roll) frame — the audit record, not a viewer projection. Interpret every
/// perspective-relative field via the carrying entry's <c>onRollSeat</c>.
/// </summary>
/// <param name="Board">26-element Mop board in the frame player's perspective.</param>
/// <param name="CubeSize">Current cube value.</param>
/// <param name="CubeOwner">Cube owner, perspective-relative.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="OnRollScore">The frame player's score entering the game.</param>
/// <param name="OpponentScore">The opponent's score entering the game.</param>
/// <param name="IsCrawford">Whether this is the Crawford game.</param>
internal sealed record JournalGameState(
    IReadOnlyList<int> Board,
    int CubeSize,
    JournalCubeOwner CubeOwner,
    int MatchLength,
    int OnRollScore,
    int OpponentScore,
    bool IsCrawford);

/// <summary>A finished game's result as the substrate recorded it (perspective-relative winner flag).</summary>
/// <param name="Kind">Win kind.</param>
/// <param name="OnRollWon">Whether the frame (on-roll) player won.</param>
/// <param name="CubeSize">The cube value the game ended at.</param>
internal sealed record JournalGameResult(JournalResultKind Kind, bool OnRollWon, int CubeSize);

/// <summary>The Fischer time control a match or tournament ran under.</summary>
/// <param name="InitialSeconds">Each seat's initial pool, in seconds.</param>
/// <param name="IncrementSeconds">The per-answered-decision credit, in seconds.</param>
internal sealed record JournalTimeControl(double InitialSeconds, double IncrementSeconds);
