using System.Threading.Channels;
using BgGame_Lib;
using BgTournament.Api;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server;

/// <summary>
/// The live per-move cache and broadcast hub for one hosted match: an
/// <see cref="IMatchObserver"/> written from the runner's flow and read
/// concurrently by SSE subscribers of <c>GET /matches/{matchId}/live</c>.
///
/// <para><b>Non-disruptive by construction.</b> The substrate invokes observer
/// callbacks synchronously on the match loop and fails the whole run if one
/// throws — so every callback here does only fast, in-memory work under a
/// short, essentially uncontended lock (the writer is the single match flow;
/// readers are rare HTTP joins) and enqueues to lock-free channels; it never
/// awaits, does I/O, or blocks. A projection bug cannot take the match down
/// with it: callbacks are wrapped so any throw is logged and
/// <em>faults the feed loudly</em> — subscriber streams are closed rather than
/// left silently stale — while the match plays on.</para>
///
/// <para><b>Retention and replay.</b> The completed <see cref="GameRecord"/>s
/// collected here are the retained games for terminal matches, so a match
/// interrupted by forfeit/fault still serves the games that finished before the
/// break. Per-entry and per-game projection is single-sourced in
/// <see cref="ReplayProjection"/>, shared with the settled-replay endpoint.</para>
///
/// <para><b>Terminal.</b> The substrate emits no terminal callback when a run
/// aborts (the stream simply stops), so the one terminal event is emitted by
/// the host: <see cref="MatchService"/> calls <see cref="MarkTerminal"/> once
/// the outcome is folded into the record — the single terminal path for every
/// outcome (completed / forfeited / aborted / faulted).</para>
/// </summary>
internal sealed class LiveMatch : IMatchObserver
{
    private readonly string _matchId;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly List<Channel<LiveMatchEvent>> _subscribers = new();
    private readonly List<GameRecord> _completedGames = new();
    private readonly List<GameEntry> _currentGameEntries = new();

    /// <summary>
    /// The in-flight game's substrate transcript — raw entries in their native
    /// (on-roll) frames — or null between games. Distinct from
    /// <see cref="_currentGameEntries"/> (seat-One-frame API projections for the
    /// live snapshot): this is the untouched substrate record an interrupted
    /// match's <c>.MAT</c> export carries as its trailing partial game.
    /// </summary>
    private Transcript? _partialTranscript;
    private int _gameNumber;
    private int _seatOneScore;
    private int _seatTwoScore;
    private bool _isCrawford;

    /// <summary>Set on a clean terminal; a faulted feed closes with none.</summary>
    private MatchSummary? _terminalSummary;

    /// <summary>Terminal or faulted: no further live events; new subscribers get snapshot then close.</summary>
    private bool _closed;

    public LiveMatch(string matchId, ILogger logger)
    {
        _matchId = matchId;
        _logger = logger;
    }

    /// <summary>
    /// The games completed so far, as a stable snapshot — the retained replay
    /// source for a terminal match (the full set for a completed one; the
    /// pre-interruption games for a forfeited/faulted one).
    /// </summary>
    public IReadOnlyList<GameRecord> CompletedGames
    {
        get
        {
            lock (_lock)
            {
                return _completedGames.ToArray();
            }
        }
    }

    /// <summary>
    /// The in-flight game's transcript — the entries recorded since the current
    /// game started, with no terminating result — or null when no game is in
    /// progress (between games, or before the first, or after a clean match
    /// end). The trailing partial an interrupted match's <c>.MAT</c> export
    /// carries; the finished games travel in <see cref="CompletedGames"/>. A
    /// defensive snapshot taken under the lock; null (never an empty transcript)
    /// when nothing has been recorded, matching the exporter's "no entries ⇒ no
    /// game block" contract.
    /// </summary>
    public Transcript? PartialTranscript
    {
        get
        {
            lock (_lock)
            {
                if (_partialTranscript is not { Entries.Count: > 0 })
                {
                    return null;
                }

                var snapshot = new Transcript();
                foreach (var entry in _partialTranscript.Entries)
                {
                    snapshot.Append(entry);
                }

                return snapshot;
            }
        }
    }

    /// <inheritdoc/>
    public void OnGameStarted(GameStartContext context) => SafelyObserve(() =>
    {
        lock (_lock)
        {
            // The substrate is the single source of the game's frame-free
            // context — entering scores (seat-absolute) and the Crawford flag —
            // so the feed holds them verbatim rather than re-folding a running
            // tally of its own.
            _gameNumber = context.GameNumber;
            _seatOneScore = context.SeatOneScore;
            _seatTwoScore = context.SeatTwoScore;
            _isCrawford = context.IsCrawford;
            _currentGameEntries.Clear();

            // A fresh game is in progress: start accumulating its raw transcript
            // for a possible partial export. Retained until the game ends
            // (cleared on OnGameEnded) so it holds exactly the in-flight game.
            _partialTranscript = new Transcript();
            Broadcast(new LiveGameStartedEvent(
                context.GameNumber, context.SeatOneScore, context.SeatTwoScore, context.IsCrawford));
        }
    });

    /// <inheritdoc/>
    public void OnEntryRecorded(TranscriptEntry entry) => SafelyObserve(() =>
    {
        // The terminating game-end is not a contract entry; it arrives as the
        // completed game's finalState via OnGameEnded.
        if (entry is GameEndedTranscriptEntry)
        {
            return;
        }

        var projected = ReplayProjection.ProjectEntry(entry);   // pure, outside the lock
        lock (_lock)
        {
            _currentGameEntries.Add(projected);

            // Also retain the untouched substrate entry (native frame, no
            // projection) so an interrupted match can export its partial game.
            // Null-guarded only defensively: entries never arrive between games.
            _partialTranscript?.Append(entry);
            Broadcast(new LiveEntryEvent(projected));
        }
    });

    /// <inheritdoc/>
    public void OnGameEnded(int gameNumber, GameRecord record) => SafelyObserve(() =>
    {
        var replay = ReplayProjection.ProjectGame(record, gameNumber);   // pure, outside the lock
        lock (_lock)
        {
            // Scores are not re-folded here: the next OnGameStarted carries the
            // substrate's own entering scores, and the snapshot describes the
            // current game (its entering scores, held since it started). The
            // completed game's final scores travel in this event's replay.
            _completedGames.Add(record);

            // This game is now a completed record, no longer in flight: drop the
            // partial so a between-games interruption exports no phantom trailing
            // game (the finished game is already in _completedGames).
            _partialTranscript = null;
            Broadcast(new LiveGameEndedEvent(replay));
        }
    });

    /// <inheritdoc/>
    /// <remarks>
    /// The terminal live event is emitted by the host via
    /// <see cref="MarkTerminal"/> (the single path for every outcome), not from
    /// here — a clean completion is one branch of that, an abort has no callback
    /// at all.
    /// </remarks>
    public void OnMatchEnded(MatchResult result)
    {
    }

    /// <summary>
    /// Emit the terminal event carrying the final record and close the feed.
    /// Idempotent, and the one place a terminal event originates — called by
    /// <see cref="MatchService"/> once the outcome is folded into the record.
    /// </summary>
    /// <param name="summary">The final match record.</param>
    public void MarkTerminal(MatchSummary summary)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _terminalSummary = summary;
            _closed = true;
            Broadcast(new LiveTerminalEvent(summary));
            CloseAllLocked();
        }
    }

    /// <summary>
    /// Register a new subscriber. It receives a join-in-progress snapshot
    /// immediately; if the match is already terminal it additionally receives
    /// the terminal event and its stream ends at once — the single already-done
    /// code path. Registration and the snapshot are taken atomically under the
    /// lock, so no event is missed or duplicated across the join.
    /// </summary>
    public LiveSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<LiveMatchEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_lock)
        {
            channel.Writer.TryWrite(SnapshotLocked());
            if (_closed)
            {
                if (_terminalSummary is { } summary)
                {
                    channel.Writer.TryWrite(new LiveTerminalEvent(summary));
                }

                channel.Writer.TryComplete();   // faulted feeds close with no terminal event — a loud EOF
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        return new LiveSubscription(this, channel);
    }

    internal void RemoveSubscriber(Channel<LiveMatchEvent> channel)
    {
        lock (_lock)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    private LiveSnapshotEvent SnapshotLocked() =>
        new(_gameNumber, _seatOneScore, _seatTwoScore, _isCrawford, _currentGameEntries.ToArray());

    /// <summary>Fan one event out to every current subscriber. Caller holds the lock; never blocks.</summary>
    private void Broadcast(LiveMatchEvent evt)
    {
        foreach (var channel in _subscribers)
        {
            channel.Writer.TryWrite(evt);
        }
    }

    private void CloseAllLocked()
    {
        foreach (var channel in _subscribers)
        {
            channel.Writer.TryComplete();
        }

        _subscribers.Clear();
    }

    /// <summary>
    /// Run an observer callback, containing any failure: the substrate would
    /// kill the match if a callback threw, so a projection bug here is logged
    /// and turned into a loud feed fault (subscriber streams closed) instead.
    /// </summary>
    private void SafelyObserve(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(
                ex,
                "Live feed for match {MatchId} faulted while projecting an observer callback; "
                    + "closing subscriber streams. The match itself is unaffected.",
                _matchId);

            lock (_lock)
            {
                if (_closed)
                {
                    return;
                }

                // No terminal summary: subscribers see the stream end with no
                // terminal event — a loud EOF, never a silently stale feed.
                _closed = true;
                CloseAllLocked();
            }
        }
    }
}

/// <summary>
/// One SSE subscription to a <see cref="LiveMatch"/>: the event reader plus the
/// cleanup that removes it when the stream ends (client disconnect or terminal).
/// </summary>
internal sealed class LiveSubscription
{
    private readonly LiveMatch _match;
    private readonly Channel<LiveMatchEvent> _channel;

    internal LiveSubscription(LiveMatch match, Channel<LiveMatchEvent> channel)
    {
        _match = match;
        _channel = channel;
    }

    /// <summary>The ordered event stream: the join snapshot, then live increments, then terminal.</summary>
    public ChannelReader<LiveMatchEvent> Reader => _channel.Reader;

    /// <summary>Detach this subscriber; idempotent, safe to call from the stream's finally.</summary>
    public void Unsubscribe() => _match.RemoveSubscriber(_channel);
}
