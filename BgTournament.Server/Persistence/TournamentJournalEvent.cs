using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

/// <summary>
/// Base type for every event in a tournament journal — one JSON object per
/// JSONL line, <c>"type"</c>-discriminated, written in orchestration order.
/// Serialize and deserialize exclusively through <see cref="JournalCodec"/>.
///
/// <para>Deliberately thin: the schedule is <em>not</em> journaled — it is
/// re-derived from participants + format + seed by the <c>Tournament</c>
/// constructor, whose seed derivation is a pinned reproducibility contract
/// (and, with this file format, a durable-format contract). The journal is
/// creation, the per-match linkage, each result fold, and the terminal
/// outcome; a journal without a terminal event folds to
/// <c>TournamentStatus.Interrupted</c>.</para>
/// </summary>
/// <param name="At">When the event occurred (UTC, via the DI <see cref="TimeProvider"/> — never ambient time).</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TournamentCreatedEvent), "created")]
[JsonDerivedType(typeof(TournamentMatchStartedEvent), "matchStarted")]
[JsonDerivedType(typeof(TournamentResultEvent), "result")]
[JsonDerivedType(typeof(TournamentTerminalEvent), "terminal")]
internal abstract record TournamentJournalEvent([property: JsonPropertyOrder(-1)] DateTimeOffset At);

/// <summary>The journal header (always line 1): the tournament's identity and configuration.</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="SchemaVersion">The journal schema version governing the whole file; readers reject an unknown version before folding anything.</param>
/// <param name="TournamentId">Server-assigned tournament id (also the journal's file name).</param>
/// <param name="Participants">Participants in seeding order.</param>
/// <param name="MatchLength">Match length in points for every scheduled match.</param>
/// <param name="MatchesPerPairing">How many times each pair meets.</param>
/// <param name="Seed">The tournament seed — schedule structure in every mode, dice only in explicit-seed mode.</param>
/// <param name="FairDice">Whether the tournament's matches run on fair-mode (committed, verifiable) dice.</param>
/// <param name="TimeControl">The Fischer time control every scheduled match runs under, or null for the flat regime.</param>
/// <param name="CreatedBy">
/// The authenticated admin actor (API-key name) whose request created the
/// tournament — the tier-C admin-action stamp. Null when the request was
/// anonymous (admin surface serving openly) or the file predates actor
/// identity — both honestly "no authenticated actor", which is why this field
/// is additive under the existing schema version rather than a bump.
/// </param>
internal sealed record TournamentCreatedEvent(
    DateTimeOffset At,
    [property: JsonPropertyOrder(-2)] int SchemaVersion,
    string TournamentId,
    IReadOnlyList<string> Participants,
    int MatchLength,
    int MatchesPerPairing,
    int Seed,
    bool FairDice,
    JournalTimeControl? TimeControl,
    string? CreatedBy) : TournamentJournalEvent(At);

/// <summary>
/// A scheduled match was created and (unless forfeited without play) is about
/// to run — the linkage that lets a rehydrated ledger name the match record,
/// including the one in flight when the server died.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="MatchIndex">The schedule index.</param>
/// <param name="MatchId">The hosted match's record id (its own journal's file name).</param>
internal sealed record TournamentMatchStartedEvent(
    DateTimeOffset At,
    int MatchIndex,
    string MatchId) : TournamentJournalEvent(At);

/// <summary>One result fold: the scheduled match produced a winner (a forfeit folds as an ordinary win).</summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="MatchIndex">The schedule index the result folds into.</param>
/// <param name="Winner">The winning participant's name.</param>
internal sealed record TournamentResultEvent(
    DateTimeOffset At,
    int MatchIndex,
    string Winner) : TournamentJournalEvent(At);

/// <summary>
/// The tournament's terminal outcome — always the journal's last event. Its
/// absence <em>is</em> the interrupted state at rehydration.
/// </summary>
/// <param name="At">Event time (UTC).</param>
/// <param name="Status">The terminal status.</param>
/// <param name="Detail">Human-readable outcome detail (abort or fault reason); null on completion.</param>
internal sealed record TournamentTerminalEvent(
    DateTimeOffset At,
    JournalTournamentOutcome Status,
    string? Detail) : TournamentJournalEvent(At);
