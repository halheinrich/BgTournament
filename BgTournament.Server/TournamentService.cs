using System.Collections.Concurrent;
using BgTournament.Api;
using BgTournament.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server;

/// <summary>Why a tournament could not be started.</summary>
internal enum StartTournamentError
{
    /// <summary>No error — started.</summary>
    None,

    /// <summary>A named participant is not connected.</summary>
    UnknownEngine,

    /// <summary>A named participant is already in a match or tournament.</summary>
    EngineBusy,

    /// <summary>The tournament configuration is invalid (participants or format).</summary>
    InvalidConfiguration,
}

/// <summary>
/// One hosted tournament: the execution-blind domain aggregate plus the
/// hosting state the domain deliberately does not know — status, per-match
/// record ids, and failure detail.
/// </summary>
internal sealed class TournamentRecord
{
    public TournamentRecord(string tournamentId, Tournament tournament)
    {
        TournamentId = tournamentId;
        Tournament = tournament;
        MatchIds = new string?[tournament.Schedule.Count];
    }

    public string TournamentId { get; }

    /// <summary>
    /// The domain aggregate (schedule, results, standings). Not thread-safe —
    /// every mutation and read goes through <see cref="Gate"/>.
    /// </summary>
    public Tournament Tournament { get; }

    /// <summary>Hosted-match record ids aligned with the schedule; null until that match is reached.</summary>
    public string?[] MatchIds { get; }

    public TournamentStatus Status { get; set; } = TournamentStatus.Running;

    /// <summary>Human-readable outcome detail (abort or fault reason).</summary>
    public string? Detail { get; set; }

    /// <summary>Serializes the orchestration loop's mutations against admin-surface reads.</summary>
    public object Gate { get; } = new();
}

/// <summary>
/// Hosts round-robin tournaments over the match machinery
/// <see cref="MatchService"/> already owns. The domain library plans the
/// schedule and folds the standings; this class owns execution: it claims
/// every participant for the tournament's duration (so nothing can poach an
/// engine between rounds), plays the schedule strictly in order, converts
/// each hosted match's outcome into a domain result — a forfeit is simply a
/// win for the non-offender — and forfeits without play against a
/// participant that is no longer connected when its match comes up.
/// Tournaments are invisible on the wire: engines just see the usual
/// per-match lifecycle.
/// </summary>
internal sealed class TournamentService
{
    private readonly EngineRegistry _registry;
    private readonly MatchService _matches;
    private readonly ILogger<TournamentService> _logger;
    private readonly CancellationToken _serverStopping;
    private readonly ConcurrentDictionary<string, TournamentRecord> _records = new();

    public TournamentService(
        EngineRegistry registry,
        MatchService matches,
        ILogger<TournamentService> logger,
        IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _matches = matches;
        _logger = logger;
        _serverStopping = lifetime.ApplicationStopping;
    }

    /// <summary>
    /// Validate, claim every participant, and start the tournament in the
    /// background. Returns the running record, or the reason it could not
    /// start.
    /// </summary>
    public (TournamentRecord? Record, StartTournamentError Error, string? ErrorDetail) StartTournament(
        IReadOnlyList<string>? participants, int matchLength, int matchesPerPairing, int? seed)
    {
        // The domain library is the single home of participant and format
        // validation; its rejections map to InvalidConfiguration verbatim.
        Tournament tournament;
        try
        {
            tournament = new Tournament(
                participants ?? [],
                new TournamentFormat(matchLength, matchesPerPairing),
                seed ?? Random.Shared.Next());
        }
        catch (ArgumentException ex)
        {
            return (null, StartTournamentError.InvalidConfiguration, ex.Message);
        }

        var sessions = new EngineSession[tournament.Participants.Count];
        for (int i = 0; i < sessions.Length; i++)
        {
            if (!_registry.TryGet(tournament.Participants[i], out var session))
            {
                return (
                    null, StartTournamentError.UnknownEngine,
                    $"No engine named '{tournament.Participants[i]}' is connected.");
            }

            sessions[i] = session;
        }

        // Claim every participant for the whole tournament (rolled back on
        // failure). Between-round idleness is deliberate v1 simplicity: the
        // claim is what keeps one-in-flight-query-per-engine an invariant.
        for (int i = 0; i < sessions.Length; i++)
        {
            if (!sessions[i].TryEnterMatch())
            {
                for (int j = 0; j < i; j++)
                {
                    sessions[j].ExitMatch();
                }

                return (
                    null, StartTournamentError.EngineBusy,
                    $"Engine '{sessions[i].Name}' is already in a match.");
            }
        }

        var record = new TournamentRecord(Guid.NewGuid().ToString("N"), tournament);
        _records[record.TournamentId] = record;

        _ = Task.Run(() => RunTournamentAsync(record, sessions), CancellationToken.None);
        return (record, StartTournamentError.None, null);
    }

    /// <summary>Project a tournament by id onto its admin summary.</summary>
    public bool TryGetSummary(string tournamentId, out TournamentSummary summary)
    {
        if (!_records.TryGetValue(tournamentId, out var record))
        {
            summary = null!;
            return false;
        }

        summary = Summarize(record);
        return true;
    }

    /// <summary>A point-in-time admin projection: status, standings, and the per-match ledger.</summary>
    public TournamentSummary Summarize(TournamentRecord record)
    {
        lock (record.Gate)
        {
            var tournament = record.Tournament;
            var standings = tournament.ComputeStandings().Select(ApiMapping.ToStandingEntry).ToArray();
            var matches = tournament.Schedule
                .Select(scheduled =>
                {
                    string? matchId = record.MatchIds[scheduled.Index];
                    MatchStatus? status = null;
                    string? winner = null;
                    if (matchId is not null && _matches.TryGetRecord(matchId, out var match))
                    {
                        status = match.Status;
                        winner = match.Winner;
                    }

                    return new TournamentMatchEntry(
                        scheduled.Index, scheduled.SeatOne, scheduled.SeatTwo, scheduled.Seed,
                        matchId, status, winner);
                })
                .ToArray();

            return new TournamentSummary(
                record.TournamentId,
                tournament.Participants,
                tournament.Format.MatchLength,
                tournament.Format.MatchesPerPairing,
                tournament.Seed,
                record.Status,
                tournament.Winner,
                record.Detail,
                standings,
                matches);
        }
    }

    private async Task RunTournamentAsync(TournamentRecord record, EngineSession[] sessions)
    {
        var tournament = record.Tournament;
        var sessionsByName = sessions.ToDictionary(session => session.Name, StringComparer.Ordinal);
        try
        {
            foreach (var scheduled in tournament.Schedule)
            {
                if (_serverStopping.IsCancellationRequested)
                {
                    Halt(record, TournamentStatus.Aborted, "The server stopped the tournament.");
                    return;
                }

                // A participant seen disconnected here forfeits without play.
                // The check is best-effort, not load-bearing: a connection
                // that is closing but not yet observed closed fails its first
                // send inside the match run, which surfaces as an ordinary
                // disconnect forfeit — either path attributes the same loss.
                var sessionOne = sessionsByName[scheduled.SeatOne];
                var sessionTwo = sessionsByName[scheduled.SeatTwo];
                bool oneConnected = !sessionOne.Connection.Closed.IsCompleted;
                bool twoConnected = !sessionTwo.Connection.Closed.IsCompleted;

                if (!oneConnected && !twoConnected)
                {
                    Halt(
                        record, TournamentStatus.Faulted,
                        $"Both '{scheduled.SeatOne}' and '{scheduled.SeatTwo}' are disconnected — "
                        + $"match {scheduled.Index} can be forfeited to neither side.");
                    return;
                }

                var match = _matches.CreateHostedMatch(
                    scheduled.SeatOne, scheduled.SeatTwo, tournament.Format.MatchLength, scheduled.Seed);
                if (!oneConnected || !twoConnected)
                {
                    // Forfeit without play: no matchStarted was ever sent, so
                    // the wire stays silent — the record still tells the story.
                    string offender = oneConnected ? scheduled.SeatTwo : scheduled.SeatOne;
                    match.RecordForfeit(
                        seat: oneConnected ? 2 : 1,
                        $"Engine '{offender}' was not connected when its tournament match came up.");
                }
                else
                {
                    await _matches.RunHostedMatchAsync(match, sessionOne, sessionTwo);
                }

                lock (record.Gate)
                {
                    record.MatchIds[scheduled.Index] = match.MatchId;
                }

                switch (match.Status)
                {
                    case MatchStatus.Completed when match.Winner is not null:
                    case MatchStatus.Forfeited:
                        lock (record.Gate)
                        {
                            tournament.RecordResult(scheduled.Index, match.Winner!);
                        }

                        break;
                    case MatchStatus.Aborted:
                        Halt(
                            record, TournamentStatus.Aborted,
                            $"Match {match.MatchId} was aborted; the tournament cannot continue.");
                        return;
                    default:
                        // Faulted — or Completed without a winner, which a
                        // tournament match cannot produce (length ≥ 1, no
                        // games cap) and is treated as a fault if it ever does.
                        Halt(
                            record, TournamentStatus.Faulted,
                            $"Match {match.MatchId} ended without a winner ({match.Status}); "
                            + "the tournament cannot continue.");
                        return;
                }
            }

            lock (record.Gate)
            {
                record.Status = TournamentStatus.Completed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tournament {TournamentId} faulted.", record.TournamentId);
            Halt(record, TournamentStatus.Faulted, "Unexpected server error; see the server log.");
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.ExitMatch();
            }

            _logger.LogInformation(
                "Tournament {TournamentId} ({Participants}; length {MatchLength} × {MatchesPerPairing} per pairing, "
                + "seed {Seed}): {Status}. Winner: {Winner}. {Detail}",
                record.TournamentId, string.Join(", ", tournament.Participants),
                tournament.Format.MatchLength, tournament.Format.MatchesPerPairing, tournament.Seed,
                record.Status, tournament.Winner ?? "—", record.Detail ?? string.Empty);
        }
    }

    private static void Halt(TournamentRecord record, TournamentStatus status, string detail)
    {
        lock (record.Gate)
        {
            record.Status = status;
            record.Detail = detail;
        }
    }
}
