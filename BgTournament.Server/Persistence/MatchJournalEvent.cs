using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

/// <summary>
/// Base type for every event in a match journal — one JSON object per JSONL
/// line, carrying a <c>"type"</c> discriminator, written in match order so the
/// file is an ordered audit record (the future arbitration log reads this
/// vocabulary). Serialize and deserialize exclusively through
/// <see cref="JournalCodec"/>.
///
/// <para>The first line of every journal is a <see cref="MatchCreatedEvent"/>
/// carrying the schema version; a terminal outcome is a trailing
/// <see cref="MatchTerminalEvent"/>. A journal <em>without</em> a terminal
/// event is a match the server died under — rehydration folds it to
/// <c>MatchStatus.Interrupted</c>.</para>
/// </summary>
/// <param name="At">When the event occurred (UTC, via the DI <see cref="TimeProvider"/> — never ambient time).</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MatchCreatedEvent), "created")]
[JsonDerivedType(typeof(MatchStartedEvent), "started")]
[JsonDerivedType(typeof(MatchGameStartedEvent), "gameStarted")]
[JsonDerivedType(typeof(MatchPlayEvent), "play")]
[JsonDerivedType(typeof(MatchCubeEvent), "cube")]
[JsonDerivedType(typeof(MatchGameEndedEvent), "gameEnded")]
[JsonDerivedType(typeof(MatchTerminalEvent), "terminal")]
internal abstract record MatchJournalEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At);

/// <summary>
/// The journal header (always line 1): the match's identity and configuration.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="SchemaVersion">The journal schema version governing the whole file; readers reject an unknown version before folding anything.</param>
/// <param name="MatchId">Server-assigned match id (also the journal's file name).</param>
/// <param name="EngineOne">Engine occupying seat One.</param>
/// <param name="EngineTwo">Engine occupying seat Two.</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="MaxGames">The games cap, when one was set.</param>
/// <param name="Seed">The recorded dice seed (drives the dice only in explicit-seed mode).</param>
/// <param name="DiceAlgorithm">Fair mode: the verifiable-dice algorithm id; null for explicit-seed matches.</param>
/// <param name="DiceKey">
/// Fair mode: the secret key driving the match's verifiable dice, escrowed at
/// creation — <em>before</em> terminal — so an interrupted match's partial
/// rolls stay verifiable (the journal lives in the server's own trust domain,
/// exactly like the in-memory key it records). The commitment is deliberately
/// <em>not</em> stored: it is derived from the key and match id, the settled
/// no-drift rule. Null for explicit-seed matches.
/// </param>
/// <param name="TimeControl">The Fischer time control, or null for the flat regime.</param>
internal sealed record MatchCreatedEvent(
    DateTimeOffset At,
    [property: JsonPropertyOrder(-2)] int SchemaVersion,
    string MatchId,
    string EngineOne,
    string EngineTwo,
    int MatchLength,
    int? MaxGames,
    int Seed,
    string? DiceAlgorithm,
    string? DiceKey,
    JournalTimeControl? TimeControl) : MatchJournalEvent(At);

/// <summary>
/// The wire lifecycle began: the server is about to send <c>matchStarted</c>
/// to both engines. Absent for a tournament forfeit-without-play, whose
/// journal is just <c>created</c> + <c>terminal</c>.
/// </summary>
/// <param name="At">Event time (UTC).</param>
internal sealed record MatchStartedEvent(DateTimeOffset At) : MatchJournalEvent(At);

/// <summary>A game began — the substrate's frame-free start context, verbatim.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">1-based game number within the match.</param>
/// <param name="SeatOneScore">Seat One's score entering the game.</param>
/// <param name="SeatTwoScore">Seat Two's score entering the game.</param>
/// <param name="IsCrawford">Whether this is the Crawford game.</param>
internal sealed record MatchGameStartedEvent(
    DateTimeOffset At,
    int GameNumber,
    int SeatOneScore,
    int SeatTwoScore,
    bool IsCrawford) : MatchJournalEvent(At);

/// <summary>
/// A play decision — the substrate transcript entry at full fidelity: dice,
/// the chosen play with hit encoding intact, and the decision-time state in
/// the mover's frame.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="OnRollSeat">The mover — the seat whose frame <paramref name="State"/> is in.</param>
/// <param name="State">The state the decision was made in.</param>
/// <param name="Die1">First die.</param>
/// <param name="Die2">Second die.</param>
/// <param name="Moves">The chosen play's moves in play order; empty for a dance.</param>
internal sealed record MatchPlayEvent(
    DateTimeOffset At,
    JournalSeat OnRollSeat,
    JournalGameState State,
    int Die1,
    int Die2,
    IReadOnlyList<JournalMove> Moves) : MatchJournalEvent(At);

/// <summary>
/// A cube decision — offer side or response side. As in the substrate, the
/// state stays in the offerer's frame across the response, and the acting
/// seat stays derived from the action (the subtype-owned attribution rule is
/// not duplicated into the format).
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="OnRollSeat">The offerer — the seat whose frame <paramref name="State"/> is in.</param>
/// <param name="State">The live state at the decision.</param>
/// <param name="Action">The cube action taken.</param>
internal sealed record MatchCubeEvent(
    DateTimeOffset At,
    JournalSeat OnRollSeat,
    JournalGameState State,
    JournalCubeAction Action) : MatchJournalEvent(At);

/// <summary>
/// A game's terminating transcript entry — bear-off completion or cube pass.
/// The folded per-game record is deliberately not journaled: winner and
/// transcript are derivable from the entries (single source of truth).
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="OnRollSeat">The seat whose frame <paramref name="State"/> is in (wherever the final transition left the live frame).</param>
/// <param name="State">The terminal position.</param>
/// <param name="Result">The substrate result: kind, perspective-relative winner flag, cube value.</param>
internal sealed record MatchGameEndedEvent(
    DateTimeOffset At,
    JournalSeat OnRollSeat,
    JournalGameState State,
    JournalGameResult Result) : MatchJournalEvent(At);

/// <summary>
/// The match's terminal outcome, folded by the host after the run ended —
/// always the journal's last event. Its absence <em>is</em> the interrupted
/// state: rehydration folds a terminal-less journal to
/// <c>MatchStatus.Interrupted</c>.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Status">The terminal status.</param>
/// <param name="Winner">Winning engine's name; null when there is no winner.</param>
/// <param name="SeatOneScore">Seat One's final points; null unless the match completed naturally.</param>
/// <param name="SeatTwoScore">Seat Two's final points; null unless the match completed naturally.</param>
/// <param name="ForfeitedBy">Forfeiting engine's name; null unless forfeited.</param>
/// <param name="ForfeitCause">The structured forfeit cause; null unless forfeited.</param>
/// <param name="Detail">Human-readable outcome detail.</param>
internal sealed record MatchTerminalEvent(
    DateTimeOffset At,
    JournalMatchOutcome Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    JournalForfeitCause? ForfeitCause,
    string? Detail) : MatchJournalEvent(At);
