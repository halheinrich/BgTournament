using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// One event on a match's audit timeline (<c>GET /matches/{matchId}/audit</c>),
/// discriminated on the wire by <c>"type"</c> — the arbitration read of the
/// match record: timestamps, attribution, per-decision clock evidence,
/// discipline events, and the terminal outcome with its structured cause.
///
/// <para><b>Arbitration altitude.</b> Audit events deliberately carry no
/// board positions and no checker moves — those are the replay surface's job
/// (<c>GET /matches/{matchId}/games</c>). Decision events join to replay by
/// game number plus <c>entryIndex</c> (the 0-based index into that game's
/// <see cref="GameReplay.Entries"/>).</para>
///
/// <para><b>Clock correlation.</b> A <see cref="AuditClockEvent"/> precedes
/// the decision event it timed. A <c>cubeOffer</c> clock event with no cube
/// event before the next clock event was a declined double window (replay
/// deliberately elides those, so its think time is visible only here); a
/// clock event followed by no decision event at all timed the decision that
/// ended the match; and a dance is a <c>play</c> event with no clock event —
/// the forced empty play is recorded without querying the engine.</para>
///
/// <para><b>Verification packet.</b> For a fair-dice match the response is
/// self-contained: <see cref="AuditCreatedEvent"/> carries the commitment and
/// algorithm the engines saw on <c>matchStarted</c>, the play events carry
/// the rolls, and <see cref="AuditTerminalEvent"/> carries the revealed key —
/// commitment, stream, and key check against each other without running
/// anything.</para>
/// </summary>
/// <param name="At">
/// When the event occurred (UTC, server-measured). Null only on the terminal
/// event of an <see cref="MatchStatus.Interrupted"/> match, whose true end
/// time died with the server.
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AuditCreatedEvent), "created")]
[JsonDerivedType(typeof(AuditStartedEvent), "started")]
[JsonDerivedType(typeof(AuditGameStartedEvent), "gameStarted")]
[JsonDerivedType(typeof(AuditPlayEvent), "play")]
[JsonDerivedType(typeof(AuditCubeOfferEvent), "cubeOffer")]
[JsonDerivedType(typeof(AuditCubeResponseEvent), "cubeResponse")]
[JsonDerivedType(typeof(AuditGameEndedEvent), "gameEnded")]
[JsonDerivedType(typeof(AuditClockEvent), "clock")]
[JsonDerivedType(typeof(AuditLateReplyEvent), "lateReply")]
[JsonDerivedType(typeof(AuditTerminalEvent), "terminal")]
public abstract record AuditEvent([property: JsonPropertyOrder(-1)] DateTimeOffset? At);

/// <summary>The match's creation: its configuration as durably recorded.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="MatchLength">Match length in points; 0 is a money session.</param>
/// <param name="MaxGames">The games cap, when one was set.</param>
/// <param name="Seed">The recorded dice seed (drives the dice only when <paramref name="DiceAlgorithm"/> is null — see <see cref="MatchSummary.Seed"/>).</param>
/// <param name="DiceAlgorithm">Fair mode: the verifiable-dice algorithm id; null for explicit-seed matches.</param>
/// <param name="DiceCommitment">
/// Fair mode: the pre-match commitment (hex) — the same value published to
/// both engines on <c>matchStarted</c>, derived from the escrowed key at
/// projection time (never stored, so it cannot drift from the key the
/// terminal event reveals). Null for explicit-seed matches.
/// </param>
/// <param name="TimeControl">The Fischer time control, or null for the flat regime.</param>
public sealed record AuditCreatedEvent(
    DateTimeOffset? At,
    int MatchLength,
    int? MaxGames,
    int Seed,
    string? DiceAlgorithm,
    string? DiceCommitment,
    TimeControl? TimeControl) : AuditEvent(At);

/// <summary>
/// The wire lifecycle began: the server was about to send <c>matchStarted</c>
/// to both engines. Absent for a tournament forfeit-without-play, which never
/// touched the wire.
/// </summary>
/// <param name="At">Event time (UTC).</param>
public sealed record AuditStartedEvent(DateTimeOffset? At) : AuditEvent(At);

/// <summary>A game began.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">1-based game number within the match.</param>
/// <param name="SeatOneScore">Seat One's score entering the game.</param>
/// <param name="SeatTwoScore">Seat Two's score entering the game.</param>
/// <param name="IsCrawford">Whether this is the Crawford game.</param>
public sealed record AuditGameStartedEvent(
    DateTimeOffset? At,
    int GameNumber,
    int SeatOneScore,
    int SeatTwoScore,
    bool IsCrawford) : AuditEvent(At);

/// <summary>
/// A play decision was recorded. The rolled dice are the audit-relevant facts
/// (roll evidence for the fair-dice packet); the chosen moves live on the
/// replay surface.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">The game this decision belongs to.</param>
/// <param name="EntryIndex">0-based index into that game's <see cref="GameReplay.Entries"/> — the replay join.</param>
/// <param name="Actor">The seat on roll.</param>
/// <param name="Die1">First die (a game's first play entry is the opening roll — seat One's die).</param>
/// <param name="Die2">Second die (the opening roll's seat-Two die).</param>
public sealed record AuditPlayEvent(
    DateTimeOffset? At,
    int GameNumber,
    int EntryIndex,
    Seat Actor,
    int Die1,
    int Die2) : AuditEvent(At);

/// <summary>A double was offered at the actor's pre-roll cube window.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">The game this decision belongs to.</param>
/// <param name="EntryIndex">0-based index into that game's <see cref="GameReplay.Entries"/> — the replay join.</param>
/// <param name="Actor">The seat offering the double.</param>
public sealed record AuditCubeOfferEvent(
    DateTimeOffset? At,
    int GameNumber,
    int EntryIndex,
    Seat Actor) : AuditEvent(At);

/// <summary>The answer to the immediately preceding cube offer.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">The game this decision belongs to.</param>
/// <param name="EntryIndex">0-based index into that game's <see cref="GameReplay.Entries"/> — the replay join.</param>
/// <param name="Actor">The seat answering the double (the offer's opponent).</param>
/// <param name="Action">Take or pass.</param>
public sealed record AuditCubeResponseEvent(
    DateTimeOffset? At,
    int GameNumber,
    int EntryIndex,
    Seat Actor,
    CubeResponseAction Action) : AuditEvent(At);

/// <summary>A game ended.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="GameNumber">The finished game's number.</param>
/// <param name="Winner">The winning seat.</param>
/// <param name="ResultKind">Win kind.</param>
/// <param name="CubeValue">The cube value the game ended at.</param>
public sealed record AuditGameEndedEvent(
    DateTimeOffset? At,
    int GameNumber,
    Seat Winner,
    GameResultKind ResultKind,
    int CubeValue) : AuditEvent(At);

/// <summary>
/// One settled clocked decision — the per-decision clock evidence (clocked
/// matches only; the flat regime measures no per-decision timing). Precedes
/// the decision event it timed; see the correlation rule on
/// <see cref="AuditEvent"/>.
/// </summary>
/// <param name="At">Event time (UTC), stamped at settlement.</param>
/// <param name="GameNumber">The game the decision belongs to.</param>
/// <param name="Seat">The seat whose pool the decision ran on.</param>
/// <param name="Decision">Which decision query was timed.</param>
/// <param name="ThinkSeconds">The wall time the server measured around the query (latency deliberately on the player's clock).</param>
/// <param name="IncrementCredited">Whether the increment was credited — true iff the engine answered; a flag fall, violation, or disconnect debits without credit.</param>
/// <param name="RemainingBeforeSeconds">The seat's pool entering the decision.</param>
/// <param name="RemainingAfterSeconds">The seat's pool after settlement (debit, zero floor, then any credit).</param>
public sealed record AuditClockEvent(
    DateTimeOffset? At,
    int GameNumber,
    Seat Seat,
    DecisionKind Decision,
    double ThinkSeconds,
    bool IncrementCredited,
    double RemainingBeforeSeconds,
    double RemainingAfterSeconds) : AuditEvent(At);

/// <summary>
/// A late reply to an abandoned query was discarded — evidence that the
/// engine did answer (a timed-out or flagged decision, typically), just too
/// late to count.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Seat">The seat whose engine sent the late reply.</param>
/// <param name="RequestId">The abandoned query's request id (correlates with engine-side logs).</param>
public sealed record AuditLateReplyEvent(
    DateTimeOffset? At,
    Seat Seat,
    string RequestId) : AuditEvent(At);

/// <summary>
/// The match's terminal outcome — always the timeline's last event, projected
/// from the record itself (the host truth the journal mirrors), so it is
/// present for every terminal status including
/// <see cref="MatchStatus.Interrupted"/>, whose journal never got a terminal
/// line.
/// </summary>
/// <param name="At">Event time (UTC); null on an Interrupted match — the true end time died with the server.</param>
/// <param name="Status">The terminal status.</param>
/// <param name="Winner">Winning engine's name; null when there is no winner.</param>
/// <param name="SeatOneScore">Seat One's final points; null unless the match completed naturally.</param>
/// <param name="SeatTwoScore">Seat Two's final points; null unless the match completed naturally.</param>
/// <param name="ForfeitedBy">Forfeiting engine's name; null unless forfeited.</param>
/// <param name="ForfeitCause">The structured forfeit cause; null unless forfeited.</param>
/// <param name="Detail">Human-readable outcome detail.</param>
/// <param name="DiceAlgorithm">Fair mode: the verifiable-dice algorithm id; null for explicit-seed matches.</param>
/// <param name="DiceKey">
/// Fair mode: the revealed dice key (hex) — the reveal-at-end mirror, served
/// only for terminal matches (a running match's audit is refused), including
/// Interrupted ones, where it is exactly what an arbiter needs to verify the
/// partial roll stream against <see cref="AuditCreatedEvent.DiceCommitment"/>.
/// Null for explicit-seed matches.
/// </param>
public sealed record AuditTerminalEvent(
    DateTimeOffset? At,
    MatchStatus Status,
    string? Winner,
    int? SeatOneScore,
    int? SeatTwoScore,
    string? ForfeitedBy,
    ForfeitCause? ForfeitCause,
    string? Detail,
    string? DiceAlgorithm,
    string? DiceKey) : AuditEvent(At);
