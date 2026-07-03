using System.Collections.Concurrent;
using BgGame_Lib;
using BgTournament.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BgTournament.Server;

/// <summary>How a hosted match concluded (or that it has not yet).</summary>
internal enum TournamentMatchStatus
{
    /// <summary>Still being played.</summary>
    Running,

    /// <summary>Played to a natural end — a winner, or the games cap.</summary>
    Completed,

    /// <summary>Ended by a forfeit: contract violation, timeout, or disconnect.</summary>
    Forfeited,

    /// <summary>Stopped by the server (shutdown), no side at fault.</summary>
    Aborted,

    /// <summary>An unexpected server-side error; see the detail and logs.</summary>
    Faulted,
}

/// <summary>One hosted match: identity, configuration (seed always recorded — auditability), and outcome.</summary>
internal sealed class TournamentMatchRecord
{
    public required string MatchId { get; init; }

    public required string EngineOne { get; init; }

    public required string EngineTwo { get; init; }

    public required int MatchLength { get; init; }

    public int? MaxGames { get; init; }

    /// <summary>The dice seed. Recorded even when server-chosen, so any match can be re-rolled.</summary>
    public required int Seed { get; init; }

    public TournamentMatchStatus Status { get; set; } = TournamentMatchStatus.Running;

    /// <summary>Winning engine's name; null while running or when there is no winner.</summary>
    public string? Winner { get; set; }

    public int? SeatOneScore { get; set; }

    public int? SeatTwoScore { get; set; }

    /// <summary>Forfeiting engine's name; null unless <see cref="Status"/> is Forfeited.</summary>
    public string? ForfeitedBy { get; set; }

    /// <summary>Human-readable outcome detail (forfeit cause, abort reason).</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// The substrate's full result — per-game records and transcripts —
    /// retained in memory for completed matches. Absent on forfeit: the
    /// runner's throw discards partial state (known v1 limitation, flagged
    /// for the umbrella). Export formats are a later arc.
    /// </summary>
    public MatchResult? Result { get; set; }
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
/// notifications, and record retention.
/// </summary>
internal sealed class MatchService
{
    private readonly EngineRegistry _registry;
    private readonly TournamentOptions _options;
    private readonly ILogger<MatchService> _logger;
    private readonly CancellationToken _serverStopping;
    private readonly ConcurrentDictionary<string, TournamentMatchRecord> _records = new();

    public MatchService(
        EngineRegistry registry,
        IOptions<TournamentOptions> options,
        ILogger<MatchService> logger,
        IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
        _serverStopping = lifetime.ApplicationStopping;
    }

    /// <summary>Fetch a match record by id.</summary>
    public bool TryGetRecord(string matchId, out TournamentMatchRecord record) =>
        _records.TryGetValue(matchId, out record!);

    /// <summary>
    /// Validate, claim both engines, and start the match in the background.
    /// Returns the running record, or the reason it could not start.
    /// </summary>
    public (TournamentMatchRecord? Record, StartMatchError Error, string? ErrorDetail) StartMatch(
        string engineOne, string engineTwo, int matchLength, int? seed, int? maxGames)
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

        var record = new TournamentMatchRecord
        {
            MatchId = Guid.NewGuid().ToString("N"),
            EngineOne = sessionOne.Name,
            EngineTwo = sessionTwo.Name,
            MatchLength = matchLength,
            MaxGames = maxGames,
            Seed = seed ?? Random.Shared.Next(),
        };
        _records[record.MatchId] = record;

        _ = Task.Run(() => RunMatchAsync(record, sessionOne, sessionTwo), CancellationToken.None);
        return (record, StartMatchError.None, null);
    }

    private async Task RunMatchAsync(
        TournamentMatchRecord record, EngineSession sessionOne, EngineSession sessionTwo)
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

        try
        {
            await NotifyStartedAsync(record, sessionOne, opponent: sessionTwo.Name, matchCts.Token);
            await NotifyStartedAsync(record, sessionTwo, opponent: sessionOne.Name, matchCts.Token);

            var runner = new MatchRunner(new SeededDiceSource(record.Seed));
            var participantOne = MatchParticipant.From(
                new RemoteEngineAgent(sessionOne.Connection, MatchSeat.One, _options.DecisionTimeout));
            var participantTwo = MatchParticipant.From(
                new RemoteEngineAgent(sessionTwo.Connection, MatchSeat.Two, _options.DecisionTimeout));

            var result = await runner.RunMatchAsync(
                participantOne, participantTwo, record.MatchLength, record.MaxGames, matchCts.Token);

            record.Result = result;
            record.SeatOneScore = result.SeatOneScore;
            record.SeatTwoScore = result.SeatTwoScore;
            record.Winner = result.Winner switch
            {
                MatchSeat.One => record.EngineOne,
                MatchSeat.Two => record.EngineTwo,
                _ => null,
            };
            record.Status = TournamentMatchStatus.Completed;
        }
        catch (AgentContractViolationException ex)
        {
            RecordForfeit(record, seat: ex.Seat == MatchSeat.One ? 1 : 2, ex.Message);
        }
        catch (EngineTimeoutException ex)
        {
            RecordForfeit(record, SeatOfEngine(record, ex.EngineName), ex.Message);
        }
        catch (EngineDisconnectedException ex)
        {
            RecordForfeit(record, SeatOfEngine(record, ex.EngineName), ex.Message);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disconnectedSeat) != 0)
        {
            int seat = Volatile.Read(ref disconnectedSeat);
            string name = seat == 1 ? record.EngineOne : record.EngineTwo;
            RecordForfeit(record, seat, $"Engine '{name}' disconnected mid-match.");
        }
        catch (OperationCanceledException)
        {
            record.Status = TournamentMatchStatus.Aborted;
            record.Detail = "The server stopped the match.";
        }
        catch (Exception ex)
        {
            record.Status = TournamentMatchStatus.Faulted;
            record.Detail = "Unexpected server error; see the server log.";
            _logger.LogError(ex, "Match {MatchId} faulted.", record.MatchId);
        }
        finally
        {
            Volatile.Write(ref matchDone, true);

            // Aborted/Faulted have no wire vocabulary in v1 (PROTOCOL.md §7
            // defines matchComplete/gamesCapReached/forfeit) — those matches
            // end silently rather than lying about the reason.
            if (record.Status is TournamentMatchStatus.Completed or TournamentMatchStatus.Forfeited)
            {
                await NotifyEndedAsync(record, sessionOne, seat: 1);
                await NotifyEndedAsync(record, sessionTwo, seat: 2);
            }
            if (record.Status == TournamentMatchStatus.Forfeited)
            {
                var offender = record.ForfeitedBy == record.EngineOne ? sessionOne : sessionTwo;
                await offender.Connection.CloseAsync("forfeit");
            }

            sessionOne.ExitMatch();
            sessionTwo.ExitMatch();
            _logger.LogInformation(
                "Match {MatchId} ({EngineOne} vs {EngineTwo}, length {MatchLength}, seed {Seed}): {Status}. {Detail}",
                record.MatchId, record.EngineOne, record.EngineTwo, record.MatchLength, record.Seed,
                record.Status, record.Detail ?? string.Empty);
        }
    }

    private static int SeatOfEngine(TournamentMatchRecord record, string engineName) =>
        engineName == record.EngineOne ? 1 : 2;

    private static void RecordForfeit(TournamentMatchRecord record, int seat, string detail)
    {
        record.Status = TournamentMatchStatus.Forfeited;
        record.ForfeitedBy = seat == 1 ? record.EngineOne : record.EngineTwo;
        record.Winner = seat == 1 ? record.EngineTwo : record.EngineOne;
        record.Detail = detail;
    }

    private async Task NotifyStartedAsync(
        TournamentMatchRecord record, EngineSession session, string opponent, CancellationToken cancellationToken)
    {
        await session.Connection.SendAsync(
            new MatchStartedMessage
            {
                MatchId = record.MatchId,
                Opponent = opponent,
                MatchLength = record.MatchLength,
                MaxGames = record.MaxGames,
            },
            cancellationToken);
    }

    private async Task NotifyEndedAsync(TournamentMatchRecord record, EngineSession session, int seat)
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
                        TournamentMatchStatus.Forfeited => MatchEndReason.Forfeit,
                        TournamentMatchStatus.Completed when record.Winner is null => MatchEndReason.GamesCapReached,
                        _ => MatchEndReason.MatchComplete,
                    },
                    YourPoints = ownPoints,
                    OpponentPoints = opponentPoints,
                    YouWon = record.Winner is null ? null : record.Winner == ownName,
                    ForfeitedBy = record.ForfeitedBy is null
                        ? null
                        : record.ForfeitedBy == ownName ? ForfeitSide.You : ForfeitSide.Opponent,
                    Detail = record.Detail,
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
