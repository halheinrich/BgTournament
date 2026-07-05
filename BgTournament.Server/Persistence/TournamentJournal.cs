using BgTournament.Api;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// One hosted tournament's write-through journal: creation, the per-match
/// linkage, each result fold, and the terminal outcome — written from the
/// orchestration loop with the same containment discipline as
/// <see cref="MatchJournal"/> (a journal failure is logged and never thrown
/// at the loop). Per-decision detail lives in each match's own journal.
/// </summary>
internal sealed class TournamentJournal
{
    private readonly JournalWriter<TournamentJournalEvent> _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _tournamentId;
    private bool _faulted;

    private TournamentJournal(
        JournalWriter<TournamentJournalEvent> writer, TimeProvider time, ILogger logger,
        string tournamentId)
    {
        _writer = writer;
        _time = time;
        _logger = logger;
        _tournamentId = tournamentId;
    }

    /// <summary>Open the journal for a new hosted tournament (the sink opens lazily on the write pump).</summary>
    public static TournamentJournal Create(
        IJournalStore store, string tournamentId, TimeProvider time, ILogger logger) =>
        new(
            new JournalWriter<TournamentJournalEvent>(
                () => store.CreateJournal(JournalKind.Tournament, tournamentId),
                JournalCodec.Serialize, logger, $"tournament {tournamentId}"),
            time, logger, tournamentId);

    /// <summary>Journal the header: the tournament's identity and configuration. Always the first event.</summary>
    public void RecordCreated(TournamentRecord record) =>
        Append(() => JournalMapping.ToCreatedEvent(record, _time.GetUtcNow()));

    /// <summary>Journal the schedule-index → match-record linkage as a scheduled match is created.</summary>
    public void RecordMatchStarted(int matchIndex, string matchId) =>
        Append(() => new TournamentMatchStartedEvent(_time.GetUtcNow(), matchIndex, matchId));

    /// <summary>Journal one result fold.</summary>
    public void RecordResult(int matchIndex, string winner) =>
        Append(() => new TournamentResultEvent(_time.GetUtcNow(), matchIndex, winner));

    /// <summary>Journal the terminal outcome — the journal's last event.</summary>
    public void RecordTerminal(TournamentStatus status, string? detail) =>
        Append(() => JournalMapping.ToTerminalEvent(status, detail, _time.GetUtcNow()));

    /// <summary>Drain and close the journal; see <see cref="JournalWriter{TEvent}.CompleteAsync"/>.</summary>
    public Task CompleteAsync() => _writer.CompleteAsync();

    private void Append(Func<TournamentJournalEvent> map)
    {
        if (_faulted)
        {
            return;
        }

        try
        {
            _writer.Append(map());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faulted = true;
            _logger.LogError(
                ex,
                "The journal for tournament {TournamentId} failed to map an event and has stopped "
                    + "writing; the tournament itself is unaffected.",
                _tournamentId);
        }
    }
}
