using System.Collections.Concurrent;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Protocol;
using BgTournament.Server.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BgTournament.Server;

/// <summary>One hosted match: identity, configuration (seed always recorded — auditability), and outcome.</summary>
internal sealed class MatchRecord
{
    public required string MatchId { get; init; }

    public required string EngineOne { get; init; }

    public required string EngineTwo { get; init; }

    public required int MatchLength { get; init; }

    public int? MaxGames { get; init; }

    /// <summary>
    /// The dice seed, always recorded. In explicit-seed (dev/repro) mode it
    /// drives the match's <see cref="SeededDiceSource"/>. In fair mode
    /// (<see cref="DiceKey"/> present) the committed key drives the dice instead,
    /// and this remains a recorded per-match datum — not the reproduction key
    /// (the revealed <see cref="DiceKey"/> is). Never null, so the admin summary
    /// shape is unchanged whichever mode a match runs in.
    /// </summary>
    public required int Seed { get; init; }

    /// <summary>
    /// Fair mode: the secret 256-bit key driving this match's verifiable dice,
    /// committed before the first roll and revealed at match end. Null for
    /// explicit-seed matches, which run the (uncommitted) <see cref="SeededDiceSource"/>.
    /// Kept secret until <see cref="MatchEndedMessage"/> — never projected onto the
    /// admin surface.
    /// </summary>
    public DiceKey? DiceKey { get; init; }

    /// <summary>
    /// The pre-match commitment published on <see cref="MatchStartedMessage"/>,
    /// bound to this match id — or null in explicit-seed mode. Derived (not
    /// stored) so the key stays the single source and the commitment cannot drift
    /// from it.
    /// </summary>
    public DiceCommitment? Commitment => DiceKey?.Commit(VerifiableDice.ContextFor(MatchId));

    /// <summary>
    /// The Fischer time control this match runs under, or null for the flat
    /// per-decision-timeout regime. Announced on
    /// <see cref="MatchStartedMessage"/>; the live clock state itself is match
    /// runtime (a <see cref="MatchClock"/> scoped to the run), not record state.
    /// </summary>
    public TimeControl? TimeControl { get; init; }

    /// <summary>
    /// Monotonic creation order, for stable listings — a concurrent
    /// dictionary has none of its own. Server-internal; never serialized.
    /// Rehydrated records are re-sequenced by journal creation time, so the
    /// order survives restarts.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// When the server created and began hosting the match (UTC, via the DI
    /// <see cref="TimeProvider"/>).
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// When the match reached its terminal status (UTC); null while running —
    /// and null on an <see cref="MatchStatus.Interrupted"/> record, whose
    /// true end time died with the server.
    /// </summary>
    public DateTimeOffset? EndedAtUtc { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.Running;

    /// <summary>Winning engine's name; null while running or when there is no winner.</summary>
    public string? Winner { get; set; }

    public int? SeatOneScore { get; set; }

    public int? SeatTwoScore { get; set; }

    /// <summary>Forfeiting engine's name; null unless <see cref="Status"/> is Forfeited.</summary>
    public string? ForfeitedBy { get; set; }

    /// <summary>
    /// The structured forfeit taxonomy; null unless <see cref="Status"/> is
    /// Forfeited. Journaled so the durable audit record carries structure —
    /// <see cref="Detail"/> stays the human-readable surface.
    /// </summary>
    public ForfeitCause? ForfeitCause { get; set; }

    /// <summary>Human-readable outcome detail (forfeit cause, abort reason).</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// The substrate's full result — per-game records and transcripts —
    /// retained in memory for completed matches. Absent on forfeit: the
    /// runner's throw discards partial state. The games that <em>did</em>
    /// finish before an interruption are retained on <see cref="Live"/>.
    /// Export formats are a later arc.
    /// </summary>
    public MatchResult? Result { get; set; }

    /// <summary>
    /// The live per-move cache and SSE broadcast hub, written from the runner
    /// flow and read by <c>GET /matches/{matchId}/live</c>. Every hosted match
    /// has one (assigned at creation); it also retains the completed games that
    /// feed terminal-match replay.
    /// </summary>
    public required LiveMatch Live { get; init; }

    /// <summary>
    /// The durable write-through journal riding the same observer flow as
    /// <see cref="Live"/>. Null only on a rehydrated record, whose journal is
    /// already complete on disk (rehydrated records are terminal and never
    /// write another event).
    /// </summary>
    public MatchJournal? Journal { get; init; }

    private readonly TaskCompletionSource _journalSettled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the journal on disk is this record's settled history —
    /// the audit read gate. A match turns terminal (status, live terminal
    /// event) <em>before</em> its journal finishes draining, so an audit read
    /// in that window would see an incomplete file; it awaits this instead.
    /// Already complete when <see cref="Journal"/> is null (a rehydrated
    /// record's file is its history by definition); otherwise completed by
    /// <see cref="MarkJournalSettled"/> — unconditionally, on every finalize
    /// path, because a gate left pending would turn a millisecond race into a
    /// hung request.
    /// </summary>
    public Task JournalSettled => Journal is null ? Task.CompletedTask : _journalSettled.Task;

    /// <summary>The journal has drained (or will never drain further); open the audit read gate.</summary>
    public void MarkJournalSettled() => _journalSettled.TrySetResult();

    /// <summary>
    /// Mark this match forfeited by the engine in <paramref name="seat"/>:
    /// the opponent wins the match outright.
    /// </summary>
    public void RecordForfeit(int seat, ForfeitCause cause, string detail)
    {
        Status = MatchStatus.Forfeited;
        ForfeitedBy = seat == 1 ? EngineOne : EngineTwo;
        Winner = seat == 1 ? EngineTwo : EngineOne;
        ForfeitCause = cause;
        Detail = detail;
    }
}

/// <summary>Why a match could not be started.</summary>
internal enum StartMatchError
{
    /// <summary>No error — started.</summary>
    None,

    /// <summary>A named engine is not connected.</summary>
    UnknownEngine,

    /// <summary>The two participants must be distinct connected engines.</summary>
    SameEngine,

    /// <summary>A named engine is already in a match.</summary>
    EngineBusy,

    /// <summary>The match configuration is invalid.</summary>
    InvalidConfiguration,
}

/// <summary>
/// Starts and referees matches between connected engines. The substrate's
/// <see cref="MatchRunner"/> plays the match; this class owns what the
/// substrate deliberately does not: seat ↔ engine attribution, the forfeit
/// policy (v1: forfeit the match), proactive disconnect handling, lifecycle
/// notifications, and record retention. <see cref="TournamentService"/>
/// reuses the hosting core (<see cref="CreateHostedMatch"/> +
/// <see cref="RunHostedMatchAsync"/>) under its own tournament-wide engine
/// claims.
/// </summary>
internal sealed class MatchService
{
    private readonly EngineRegistry _registry;
    private readonly TournamentOptions _options;
    private readonly TimeProvider _time;
    private readonly IJournalStore _store;
    private readonly ILogger<MatchService> _logger;
    private readonly CancellationToken _serverStopping;
    private readonly ConcurrentDictionary<string, MatchRecord> _records = new();
    private long _sequenceSource;

    public MatchService(
        EngineRegistry registry,
        IOptions<TournamentOptions> options,
        TimeProvider time,
        IJournalStore store,
        ILogger<MatchService> logger,
        IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _options = options.Value;
        _time = time;
        _store = store;
        _logger = logger;
        _serverStopping = lifetime.ApplicationStopping;
    }

    /// <summary>Fetch a match record by id.</summary>
    public bool TryGetRecord(string matchId, out MatchRecord record) =>
        _records.TryGetValue(matchId, out record!);

    /// <summary>
    /// Adopt rehydrated (terminal, journal-complete) records at startup,
    /// before the endpoints serve. The records arrive already sequenced by
    /// journal creation time; new matches sequence after them.
    /// </summary>
    public void Restore(IReadOnlyList<MatchRecord> records)
    {
        foreach (var record in records)
        {
            _records[record.MatchId] = record;
            _sequenceSource = Math.Max(_sequenceSource, record.Sequence);
        }
    }

    /// <summary>All match records in creation order — the stable listing.</summary>
    public IReadOnlyList<MatchRecord> ListRecords() =>
        _records.Values.OrderBy(record => record.Sequence).ToArray();

    /// <summary>
    /// Validate, claim both engines, and start the match in the background.
    /// Returns the running record, or the reason it could not start.
    /// </summary>
    public (MatchRecord? Record, StartMatchError Error, string? ErrorDetail) StartMatch(
        string engineOne, string engineTwo, int matchLength, int? seed, int? maxGames,
        TimeControl? timeControl = null)
    {
        if (matchLength < 0)
        {
            return (null, StartMatchError.InvalidConfiguration, "matchLength must be ≥ 1, or 0 for a money session.");
        }

        if (matchLength == 0 && maxGames is null)
        {
            return (null, StartMatchError.InvalidConfiguration, "A money session (matchLength 0) requires maxGames.");
        }

        if (maxGames is < 1)
        {
            return (null, StartMatchError.InvalidConfiguration, "maxGames must be ≥ 1 when provided.");
        }

        if (string.Equals(engineOne, engineTwo, StringComparison.Ordinal))
        {
            return (null, StartMatchError.SameEngine, "The two participants must be distinct engines.");
        }

        if (!_registry.TryGet(engineOne, out var sessionOne))
        {
            return (null, StartMatchError.UnknownEngine, $"No engine named '{engineOne}' is connected.");
        }

        if (!_registry.TryGet(engineTwo, out var sessionTwo))
        {
            return (null, StartMatchError.UnknownEngine, $"No engine named '{engineTwo}' is connected.");
        }

        if (!sessionOne.TryEnterMatch())
        {
            return (null, StartMatchError.EngineBusy, $"Engine '{engineOne}' is already in a match.");
        }

        if (!sessionTwo.TryEnterMatch())
        {
            sessionOne.ExitMatch();
            return (null, StartMatchError.EngineBusy, $"Engine '{engineTwo}' is already in a match.");
        }

        // An explicit seed selects the reproducible, uncommitted SeededDiceSource
        // (dev/repro). Omitting it selects fair mode: a freshly generated key
        // drives verifiable dice, committed to the match before roll one and
        // revealed at match end. A seed is still recorded either way (admin datum).
        DiceKey? diceKey = seed is null ? DiceKey.Generate() : null;
        var record = CreateHostedMatch(
            sessionOne.Name, sessionTwo.Name, matchLength, seed ?? Random.Shared.Next(), maxGames, diceKey,
            timeControl);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunHostedMatchAsync(record, sessionOne, sessionTwo);
                }
                finally
                {
                    sessionOne.ExitMatch();
                    sessionTwo.ExitMatch();
                }
            },
            CancellationToken.None);
        return (record, StartMatchError.None, null);
    }

    /// <summary>
    /// Create and retain a Running record for a match this service will host.
    /// The caller owns the engines' busy claims. Pass <paramref name="diceKey"/>
    /// to run the match on committed, verifiable fair-mode dice; omit it for the
    /// explicit-seed <see cref="SeededDiceSource"/>. Pass
    /// <paramref name="timeControl"/> to run the match on Fischer clocks; omit
    /// it for the flat per-decision timeout.
    /// </summary>
    public MatchRecord CreateHostedMatch(
        string engineOne, string engineTwo, int matchLength, int seed, int? maxGames = null,
        DiceKey? diceKey = null, TimeControl? timeControl = null)
    {
        var matchId = Guid.NewGuid().ToString("N");
        var record = new MatchRecord
        {
            MatchId = matchId,
            EngineOne = engineOne,
            EngineTwo = engineTwo,
            MatchLength = matchLength,
            MaxGames = maxGames,
            Seed = seed,
            DiceKey = diceKey,
            TimeControl = timeControl,
            Sequence = Interlocked.Increment(ref _sequenceSource),
            StartedAtUtc = _time.GetUtcNow(),
            Live = new LiveMatch(matchId, _logger),
            Journal = MatchJournal.Create(_store, matchId, _time, _logger),
        };

        // The journal header is the record's durable identity — written first,
        // before anything can happen to the match.
        record.Journal!.RecordCreated(record);
        _records[record.MatchId] = record;
        return record;
    }

    /// <summary>
    /// Fold the terminal outcome of a match that never ran (a tournament
    /// forfeit-without-play) into its live feed and journal — the counterpart
    /// of <see cref="RunHostedMatchAsync"/>'s finally for the no-play path.
    /// The caller has already recorded the forfeit on the record.
    /// </summary>
    public async Task FinalizeUnplayedMatchAsync(MatchRecord record)
    {
        record.EndedAtUtc = _time.GetUtcNow();
        record.Live.MarkTerminal(record.ToSummary());
        try
        {
            if (record.Journal is { } journal)
            {
                journal.RecordTerminal(record);
                await journal.CompleteAsync();
            }
        }
        finally
        {
            // Unconditionally: a gate left pending would hang the audit read,
            // which is strictly worse than the short file it protects against.
            record.MarkJournalSettled();
        }
    }

    /// <summary>
    /// Referee one already-claimed match to its end, folding the outcome
    /// (result, forfeit, abort, fault) into the record and sending the wire
    /// lifecycle notifications. Deliberately does not touch the engines' busy
    /// flags — the caller that claimed them releases them, which lets a
    /// tournament hold its participants across many matches.
    /// </summary>
    public async Task RunHostedMatchAsync(
        MatchRecord record, EngineSession sessionOne, EngineSession sessionTwo)
    {
        using var matchCts = CancellationTokenSource.CreateLinkedTokenSource(_serverStopping);
        bool matchDone = false;

        // Proactive disconnect watch: the first connection to end mid-match
        // is recorded and cancels the run — even if the disconnecting engine
        // was not the one being queried (its opponent may be mid-think).
        int disconnectedSeat = 0;
        void WatchClosed(EngineSession session, int seat) =>
            _ = session.Connection.Closed.ContinueWith(
                _ =>
                {
                    if (!Volatile.Read(ref matchDone)
                        && Interlocked.CompareExchange(ref disconnectedSeat, seat, 0) == 0)
                    {
                        matchCts.Cancel();
                    }
                },
                TaskScheduler.Default);

        WatchClosed(sessionOne, 1);
        WatchClosed(sessionTwo, 2);

        // Late-reply discards are per-connection evidence; attribute them to
        // seats for this match's journal while the match runs. The handlers
        // obey the observer discipline (map + non-blocking enqueue, contained
        // by the journal); one racing past match end lands in a completed
        // writer and is dropped.
        Action<string> lateReplyOne = requestId =>
            record.Journal?.RecordLateReply(MatchSeat.One, requestId);
        Action<string> lateReplyTwo = requestId =>
            record.Journal?.RecordLateReply(MatchSeat.Two, requestId);
        sessionOne.Connection.LateReplyDiscarded += lateReplyOne;
        sessionTwo.Connection.LateReplyDiscarded += lateReplyTwo;

        try
        {
            record.Journal?.RecordStarted();
            await NotifyStartedAsync(record, sessionOne, opponent: sessionTwo.Name, matchCts.Token);
            await NotifyStartedAsync(record, sessionTwo, opponent: sessionOne.Name, matchCts.Token);

            // Fair mode drives the runner with the committed key's verifiable
            // stream; explicit-seed mode with the reproducible SeededDiceSource.
            // The counting wrapper lets each play query carry its roll index (fair
            // mode only — seeded matches have no commitment to verify against).
            bool fairMode = record.DiceKey is not null;
            var counting = new CountingDiceSource(
                fairMode ? new VerifiableDiceSource(record.DiceKey!) : new SeededDiceSource(record.Seed));
            Func<int>? rollsProduced = fairMode ? () => counting.RollsProduced : null;

            // Time-control matches run on one shared Fischer clock (a per-seat
            // pool pair) instead of the flat per-decision timeout; the clock is
            // match runtime, scoped to this run. Every settlement is journaled
            // as per-decision arbitration evidence.
            MatchClock? clock = record.TimeControl is null
                ? null
                : new MatchClock(
                    record.TimeControl, _time,
                    settlement => record.Journal?.RecordClockDecision(settlement));

            var runner = new MatchRunner(counting);
            var participantOne = MatchParticipant.From(
                new RemoteEngineAgent(
                    sessionOne.Connection, MatchSeat.One, _options.DecisionTimeout, rollsProduced, clock));
            var participantTwo = MatchParticipant.From(
                new RemoteEngineAgent(
                    sessionTwo.Connection, MatchSeat.Two, _options.DecisionTimeout, rollsProduced, clock));

            // The live feed and the journal are sibling consumers of the same
            // observer flow — both non-blocking and non-throwing by construction.
            IMatchObserver observer = record.Journal is { } journal
                ? new CompositeMatchObserver(record.Live, journal)
                : record.Live;

            var result = await runner.RunMatchAsync(
                participantOne, participantTwo, record.MatchLength, record.MaxGames,
                observer: observer, cancellationToken: matchCts.Token);

            record.Result = result;
            record.SeatOneScore = result.SeatOneScore;
            record.SeatTwoScore = result.SeatTwoScore;
            record.Winner = result.Winner switch
            {
                MatchSeat.One => record.EngineOne,
                MatchSeat.Two => record.EngineTwo,
                _ => null,
            };
            record.Status = MatchStatus.Completed;
        }
        catch (AgentContractViolationException ex)
        {
            record.RecordForfeit(
                seat: ex.Seat == MatchSeat.One ? 1 : 2, ForfeitCause.ContractViolation, ex.Message);
        }
        catch (EngineTimeoutException ex)
        {
            record.RecordForfeit(SeatOfEngine(record, ex.EngineName), ForfeitCause.Timeout, ex.Message);
        }
        catch (EngineFlagFallException ex)
        {
            record.RecordForfeit(SeatOfEngine(record, ex.EngineName), ForfeitCause.FlagFall, ex.Message);
        }
        catch (EngineDisconnectedException ex)
        {
            record.RecordForfeit(SeatOfEngine(record, ex.EngineName), ForfeitCause.Disconnect, ex.Message);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disconnectedSeat) != 0)
        {
            int seat = Volatile.Read(ref disconnectedSeat);
            string name = seat == 1 ? record.EngineOne : record.EngineTwo;
            record.RecordForfeit(seat, ForfeitCause.Disconnect, $"Engine '{name}' disconnected mid-match.");
        }
        catch (OperationCanceledException)
        {
            record.Status = MatchStatus.Aborted;
            record.Detail = "The server stopped the match.";
        }
        catch (Exception ex)
        {
            record.Status = MatchStatus.Faulted;
            record.Detail = "Unexpected server error; see the server log.";
            _logger.LogError(ex, "Match {MatchId} faulted.", record.MatchId);
        }
        finally
        {
            Volatile.Write(ref matchDone, true);
            sessionOne.Connection.LateReplyDiscarded -= lateReplyOne;
            sessionTwo.Connection.LateReplyDiscarded -= lateReplyTwo;
            record.EndedAtUtc = _time.GetUtcNow();

            // The single terminal live-feed event, for every outcome: the
            // substrate emits none on abort (the stream just stops), so the
            // host emits it here — after the catch blocks have folded the final
            // status/winner/forfeit into the record, so the event carries the
            // truth (a forfeit reads "forfeited", not "running").
            record.Live.MarkTerminal(record.ToSummary());

            // The journal's terminal event mirrors it, then the journal drains
            // and closes. Awaiting the flush here delays nothing that matters:
            // the match is over, and the busy flags release after this method
            // regardless. A process killed mid-drain folds honestly as
            // Interrupted on the next start.
            try
            {
                if (record.Journal is { } terminalJournal)
                {
                    terminalJournal.RecordTerminal(record);
                    await terminalJournal.CompleteAsync();
                }
            }
            finally
            {
                // Unconditionally: a gate left pending would hang the audit
                // read, which is strictly worse than the short file it
                // protects against.
                record.MarkJournalSettled();
            }

            // Aborted/Faulted have no wire vocabulary in v1 (PROTOCOL.md §7
            // defines matchComplete/gamesCapReached/forfeit) — those matches
            // end silently rather than lying about the reason.
            if (record.Status is MatchStatus.Completed or MatchStatus.Forfeited)
            {
                await NotifyEndedAsync(record, sessionOne, seat: 1);
                await NotifyEndedAsync(record, sessionTwo, seat: 2);
            }
            if (record.Status == MatchStatus.Forfeited)
            {
                var offender = record.ForfeitedBy == record.EngineOne ? sessionOne : sessionTwo;
                await offender.Connection.CloseAsync("forfeit");
            }

            _logger.LogInformation(
                "Match {MatchId} ({EngineOne} vs {EngineTwo}, length {MatchLength}, seed {Seed}): {Status}. {Detail}",
                record.MatchId, record.EngineOne, record.EngineTwo, record.MatchLength, record.Seed,
                record.Status, record.Detail ?? string.Empty);
        }
    }

    private static int SeatOfEngine(MatchRecord record, string engineName) =>
        engineName == record.EngineOne ? 1 : 2;

    private async Task NotifyStartedAsync(
        MatchRecord record, EngineSession session, string opponent, CancellationToken cancellationToken)
    {
        // Fair mode: publish the commitment + algorithm before the first roll.
        // Explicit-seed matches carry neither (both null ⇒ omitted on the wire).
        // A time control is announced here so engines can budget the whole
        // match; flat-regime matches omit it.
        await session.Connection.SendAsync(
            new MatchStartedMessage
            {
                MatchId = record.MatchId,
                Opponent = opponent,
                MatchLength = record.MatchLength,
                MaxGames = record.MaxGames,
                DiceCommitment = record.Commitment?.ToHex(),
                DiceAlgorithm = record.DiceKey is null ? null : VerifiableDice.AlgorithmId,
                TimeControl = record.TimeControl is null
                    ? null
                    : new WireTimeControl
                    {
                        InitialSeconds = record.TimeControl.InitialSeconds,
                        IncrementSeconds = record.TimeControl.IncrementSeconds,
                    },
            },
            cancellationToken);
    }

    private async Task NotifyEndedAsync(MatchRecord record, EngineSession session, int seat)
    {
        // Points as attested by a completed run; a forfeit decides the match
        // itself, and v1 reports 0–0 for it (partial scores are lost with the
        // runner's throw — flagged limitation).
        int ownPoints = (seat == 1 ? record.SeatOneScore : record.SeatTwoScore) ?? 0;
        int opponentPoints = (seat == 1 ? record.SeatTwoScore : record.SeatOneScore) ?? 0;
        string ownName = seat == 1 ? record.EngineOne : record.EngineTwo;
        try
        {
            await session.Connection.SendAsync(
                new MatchEndedMessage
                {
                    MatchId = record.MatchId,
                    Reason = record.Status switch
                    {
                        MatchStatus.Forfeited => MatchEndReason.Forfeit,
                        MatchStatus.Completed when record.Winner is null => MatchEndReason.GamesCapReached,
                        _ => MatchEndReason.MatchComplete,
                    },
                    YourPoints = ownPoints,
                    OpponentPoints = opponentPoints,
                    YouWon = record.Winner is null ? null : record.Winner == ownName,
                    ForfeitedBy = record.ForfeitedBy is null
                        ? null
                        : record.ForfeitedBy == ownName ? ForfeitSide.You : ForfeitSide.Opponent,
                    Detail = record.Detail,

                    // Fair mode: reveal the key so either party re-derives and
                    // audits every roll that occurred — including on a forfeit,
                    // which still reveals the (partial) stream that was played.
                    // Omitted for explicit-seed matches.
                    DiceKey = record.DiceKey?.ToHex(),
                },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(
                ex, "Could not notify engine {EngineName} that match {MatchId} ended (it likely disconnected).",
                session.Name, record.MatchId);
        }
    }
}
