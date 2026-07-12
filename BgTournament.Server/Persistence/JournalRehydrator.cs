using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Core;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The startup fold: reads every journal in the store and rebuilds the match
/// and tournament records before any endpoint serves. The match fold replays
/// the journaled entries through a fresh <see cref="LiveMatch"/> via its own
/// <c>IMatchObserver</c> callbacks — the exact reverse of the write path, so
/// retained games, the trailing partial transcript, replay, and the
/// <c>.MAT</c> export come back identical with no second fold implementation.
///
/// <para><b>Damage policy.</b> A journal that cannot be trusted end-to-end is
/// folded to its trusted prefix, never poisoned — the parse policy itself
/// (torn tail dropped with a warning; mid-file corruption cuts the read with
/// a detail) lives in <see cref="JournalReader"/>, shared with the audit read
/// surface. A journal without a (trusted) terminal event folds to
/// <c>Interrupted</c> — evidence intact, including the escrowed fair-mode
/// dice key, with a null end time (the true one died with the server). A
/// file whose header is unreadable or carries an unknown schema version is
/// skipped whole, loudly — there is no identity to build a record from.</para>
///
/// <para>Restart ordering: records re-sequence by header timestamp, with an
/// ordinal-id tiebreak so equal stamps (test clocks) stay deterministic.</para>
/// </summary>
internal sealed class JournalRehydrator
{
    private readonly IJournalStore _store;
    private readonly MatchService _matches;
    private readonly TournamentService _tournaments;
    private readonly RosterService _roster;
    private readonly ILogger<JournalRehydrator> _logger;

    public JournalRehydrator(
        IJournalStore store,
        MatchService matches,
        TournamentService tournaments,
        RosterService roster,
        ILogger<JournalRehydrator> logger)
    {
        _store = store;
        _matches = matches;
        _tournaments = tournaments;
        _roster = roster;
        _logger = logger;
    }

    /// <summary>Fold every stored journal into the services' records. Call once, before serving.</summary>
    public async Task RehydrateAsync()
    {
        var matchRecords = await RehydrateKindAsync(
            JournalKind.Match, JournalCodec.DeserializeMatchEvent, ReadMatchHeader, FoldMatch);
        _matches.Restore(matchRecords);

        var tournamentRecords = await RehydrateKindAsync(
            JournalKind.Tournament, JournalCodec.DeserializeTournamentEvent, ReadTournamentHeader,
            FoldTournament);
        _tournaments.Restore(tournamentRecords);

        _roster.Restore(await ReadRosterEventsAsync());

        if (matchRecords.Count > 0 || tournamentRecords.Count > 0)
        {
            _logger.LogInformation(
                "Rehydrated {MatchCount} match record(s) and {TournamentCount} tournament record(s) "
                    + "from the journal store.",
                matchRecords.Count, tournamentRecords.Count);
        }
    }

    /// <summary>
    /// Read the roster's full history: every segment's trusted events (the
    /// standard damage policy per file — a corrupt segment folds its trusted
    /// prefix, loudly, and later segments still fold; roster events carry
    /// whole state, so a dropped suffix leaves stale-but-honest entries the
    /// admin repairs by rotation), ordered by segment header time with an
    /// ordinal-id tiebreak. Segment headers validate the version and are not
    /// themselves folded.
    /// </summary>
    private async Task<IReadOnlyList<RosterJournalEvent>> ReadRosterEventsAsync()
    {
        var segments = new List<(string Id, DateTimeOffset At, IReadOnlyList<RosterJournalEvent> Events)>();
        foreach (string id in _store.ListJournalIds(JournalKind.Roster))
        {
            var journal = await JournalReader.ReadTrustedEventsAsync(
                _store, JournalKind.Roster, id, JournalCodec.DeserializeRosterEvent, _logger);
            if (journal is not { } trusted || trusted.Events.Count == 0)
            {
                continue;
            }

            if (trusted.Events[0] is not RosterStartedEvent header)
            {
                _logger.LogError(
                    "The roster segment '{Id}' does not begin with a started header; skipping it.", id);
                continue;
            }

            if (!JournalCodec.IsRosterSupported(header.SchemaVersion))
            {
                _logger.LogError(
                    "The roster segment '{Id}' carries schema version {Version}, which this server "
                        + "does not know (it folds 1 through {Known}); skipping it.",
                    id, header.SchemaVersion, JournalCodec.RosterSchemaVersion);
                continue;
            }

            segments.Add((id, header.At, trusted.Events.Skip(1).ToArray()));
        }

        segments.Sort((a, b) => a.At != b.At
            ? a.At.CompareTo(b.At)
            : string.CompareOrdinal(a.Id, b.Id));
        return segments.SelectMany(segment => segment.Events).ToArray();
    }

    /// <summary>
    /// One kind's full pass: parse every journal, order by header timestamp
    /// (ordinal-id tiebreak), and fold each with its restart sequence number.
    /// </summary>
    private async Task<IReadOnlyList<TRecord>> RehydrateKindAsync<TEvent, THeader, TRecord>(
        JournalKind kind,
        Func<string, TEvent> deserialize,
        Func<string, IReadOnlyList<TEvent>, (THeader Header, DateTimeOffset At)?> readHeader,
        Func<THeader, IReadOnlyList<TEvent>, string?, long, TRecord?> fold)
        where TEvent : class
        where THeader : class
        where TRecord : class
    {
        var parsed = new List<(string Id, THeader Header, DateTimeOffset At,
            IReadOnlyList<TEvent> Events, string? CorruptionDetail)>();

        foreach (string id in _store.ListJournalIds(kind))
        {
            var journal = await JournalReader.ReadTrustedEventsAsync(_store, kind, id, deserialize, _logger);
            if (journal is not { } trusted)
            {
                continue;
            }

            if (readHeader(id, trusted.Events) is not { } header)
            {
                continue;
            }

            parsed.Add((id, header.Header, header.At, trusted.Events, trusted.CorruptionDetail));
        }

        // Creation order across restarts: header timestamp, then ordinal id —
        // deterministic even when a test clock stamps every journal identically.
        parsed.Sort((a, b) => a.At != b.At
            ? a.At.CompareTo(b.At)
            : string.CompareOrdinal(a.Id, b.Id));

        var records = new List<TRecord>(parsed.Count);
        foreach (var journal in parsed)
        {
            try
            {
                if (fold(journal.Header, journal.Events, journal.CorruptionDetail,
                        records.Count + 1) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(
                    ex, "Could not fold the {Kind} journal '{Id}'; skipping it.", kind, journal.Id);
            }
        }

        return records;
    }

    private (MatchCreatedEvent Header, DateTimeOffset At)? ReadMatchHeader(
        string id, IReadOnlyList<MatchJournalEvent> events) =>
        ReadHeader<MatchJournalEvent, MatchCreatedEvent>(
            JournalKind.Match, id, events, header => header.SchemaVersion);

    private (TournamentCreatedEvent Header, DateTimeOffset At)? ReadTournamentHeader(
        string id, IReadOnlyList<TournamentJournalEvent> events) =>
        ReadHeader<TournamentJournalEvent, TournamentCreatedEvent>(
            JournalKind.Tournament, id, events, header => header.SchemaVersion);

    /// <summary>
    /// Validate the header contract: line 1 is the created event, carrying a
    /// schema version this server knows. Anything else has no identity to
    /// build a record from — skip the file, loudly.
    /// </summary>
    private (THeader Header, DateTimeOffset At)? ReadHeader<TEvent, THeader>(
        JournalKind kind, string id, IReadOnlyList<TEvent> events, Func<THeader, int> schemaVersion)
        where TEvent : class
        where THeader : TEvent
    {
        if (events.Count == 0)
        {
            _logger.LogWarning(
                "The {Kind} journal '{Id}' has no readable events; skipping it.", kind, id);
            return null;
        }

        if (events[0] is not THeader header)
        {
            _logger.LogError(
                "The {Kind} journal '{Id}' does not begin with a created header; skipping it.",
                kind, id);
            return null;
        }

        if (!JournalCodec.IsSupported(schemaVersion(header)))
        {
            _logger.LogError(
                "The {Kind} journal '{Id}' carries schema version {Version}, which this server does "
                    + "not know (it folds {Min} through {Known}); skipping it.",
                kind, id, schemaVersion(header), JournalCodec.MinSchemaVersion, JournalCodec.SchemaVersion);
            return null;
        }

        return (header, HeaderAt(header));
    }

    private static DateTimeOffset HeaderAt<THeader>(THeader header) => header switch
    {
        MatchCreatedEvent match => match.At,
        TournamentCreatedEvent tournament => tournament.At,
        _ => throw new InvalidOperationException($"Unhandled header type: {header!.GetType().Name}."),
    };

    /// <summary>
    /// Fold one match journal's trusted events into a terminal record —
    /// entries replayed through a fresh <see cref="LiveMatch"/>, games
    /// rebuilt from the stamped facts on each game-ended entry.
    /// </summary>
    private MatchRecord FoldMatch(
        MatchCreatedEvent header, IReadOnlyList<MatchJournalEvent> events, string? corruptionDetail,
        long sequence)
    {
        var live = new LiveMatch(header.MatchId, _logger);
        var record = new MatchRecord
        {
            MatchId = header.MatchId,
            EngineOne = header.EngineOne,
            EngineTwo = header.EngineTwo,
            MatchLength = header.MatchLength,
            MaxGames = header.MaxGames,
            Seed = header.Seed,
            DiceKey = header.DiceKey is null ? null : DiceKey.FromHex(header.DiceKey),
            TimeControl = JournalMapping.ToTimeControl(header.TimeControl),
            CreatedBy = header.CreatedBy,
            Sequence = sequence,
            StartedAtUtc = header.At,
            Live = live,
            Journal = null,   // the journal on disk is this record's complete history
        };

        var games = new List<GameRecord>();
        Transcript? currentGame = null;
        MatchTerminalEvent? terminal = null;

        foreach (var journalEvent in events.Skip(1))
        {
            switch (journalEvent)
            {
                case MatchStartedEvent:
                    break;

                case MatchGameStartedEvent gameStarted:
                    live.OnGameStarted(JournalMapping.ToGameStartContext(gameStarted));
                    currentGame = new Transcript();
                    break;

                case MatchPlayEvent or MatchCubeEvent:
                    var entry = JournalMapping.ToTranscriptEntry(journalEvent);
                    live.OnEntryRecorded(entry);
                    currentGame?.Append(entry);
                    break;

                case MatchGameEndedEvent gameEnded:
                    // The game's record is derived exactly as the substrate
                    // derives it: winner and result read off the terminating
                    // entry's stamped facts, transcript = the accumulated
                    // entries. (LiveMatch deliberately ignores the terminating
                    // entry in OnEntryRecorded, so it is not forwarded.)
                    var end = (GameEndedTranscriptEntry)JournalMapping.ToTranscriptEntry(gameEnded);
                    if (currentGame is not null)
                    {
                        currentGame.Append(end);
                        var game = new GameRecord(end.Winner, end.Result, currentGame);
                        games.Add(game);
                        live.OnGameEnded(games.Count, game);
                        currentGame = null;
                    }

                    break;

                case MatchClockEvent or MatchLateReplyEvent:
                    // Evidence-only (schema v2): clock settlements and late-
                    // reply discards feed the audit surface, not the record —
                    // nothing to fold, and the live feed has no vocabulary
                    // for them.
                    break;

                case MatchTerminalEvent terminalEvent:
                    terminal = terminalEvent;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled match journal event: {journalEvent.GetType().Name}.");
            }
        }

        if (terminal is not null && corruptionDetail is null)
        {
            record.Status = JournalMapping.ToMatchStatus(terminal.Status);
            record.Winner = terminal.Winner;
            record.SeatOneScore = terminal.SeatOneScore;
            record.SeatTwoScore = terminal.SeatTwoScore;
            record.ForfeitedBy = terminal.ForfeitedBy;
            record.ForfeitCause =
                terminal.ForfeitCause is { } cause ? JournalMapping.ToForfeitCause(cause) : null;
            record.Detail = terminal.Detail;
            record.EndedAtUtc = terminal.At;

            if (record.Status == MatchStatus.Completed)
            {
                MatchSeat? winnerSeat = terminal.Winner is null
                    ? null
                    : terminal.Winner == record.EngineOne ? MatchSeat.One : MatchSeat.Two;
                record.Result = new MatchResult(
                    winnerSeat, terminal.SeatOneScore ?? 0, terminal.SeatTwoScore ?? 0, games);
            }
        }
        else
        {
            // No trusted terminal event: the server died under this match.
            // All evidence is retained — the completed games, the trailing
            // partial transcript, and (fair mode) the escrowed dice key, so
            // the partial roll stream stays verifiable. EndedAtUtc stays
            // null: the true end time died with the server.
            record.Status = MatchStatus.Interrupted;
            record.Detail = corruptionDetail
                ?? "The server was interrupted while this match was running; the record was "
                    + "reconstructed from its journal.";
        }

        live.MarkTerminal(record.ToSummary());
        return record;
    }

    /// <summary>
    /// Fold one tournament journal's trusted events into a terminal record.
    /// The schedule is re-derived by the <see cref="Tournament"/> constructor
    /// — participants + format + seed are the durable facts; the seed
    /// derivation is a pinned reproducibility (and now durable-format)
    /// contract.
    /// </summary>
    private TournamentRecord FoldTournament(
        TournamentCreatedEvent header, IReadOnlyList<TournamentJournalEvent> events,
        string? corruptionDetail, long sequence)
    {
        var tournament = new Tournament(
            header.Participants,
            new TournamentFormat(header.MatchLength, header.MatchesPerPairing),
            header.Seed);
        var record = new TournamentRecord(
            header.TournamentId, tournament, sequence, header.FairDice,
            JournalMapping.ToTimeControl(header.TimeControl), header.At,
            journal: null, header.CreatedBy);

        TournamentTerminalEvent? terminal = null;
        foreach (var journalEvent in events.Skip(1))
        {
            switch (journalEvent)
            {
                case TournamentMatchStartedEvent matchStarted:
                    if (matchStarted.MatchIndex >= 0
                        && matchStarted.MatchIndex < record.MatchIds.Length)
                    {
                        record.MatchIds[matchStarted.MatchIndex] = matchStarted.MatchId;
                    }

                    break;

                case TournamentResultEvent result:
                    tournament.RecordResult(result.MatchIndex, result.Winner);
                    break;

                case TournamentTerminalEvent terminalEvent:
                    terminal = terminalEvent;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled tournament journal event: {journalEvent.GetType().Name}.");
            }
        }

        if (terminal is not null && corruptionDetail is null)
        {
            record.Status = JournalMapping.ToTournamentStatus(terminal.Status);
            record.Detail = terminal.Detail;
            record.EndedAtUtc = terminal.At;
        }
        else
        {
            record.Status = TournamentStatus.Interrupted;
            record.Detail = corruptionDetail
                ?? "The server was interrupted while this tournament was running; the record was "
                    + "reconstructed from its journal.";
        }

        return record;
    }
}
