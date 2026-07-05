using BgGame_Lib;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// One hosted match's write-through journal consumer — the durable sibling of
/// <c>LiveMatch</c> on the same observer flow. The runner's callbacks and the
/// host's lifecycle calls each map to a journal event (via
/// <see cref="JournalMapping"/>, timestamped through the DI
/// <see cref="TimeProvider"/>) and enqueue it on the background
/// <see cref="JournalWriter{TEvent}"/>.
///
/// <para><b>Same discipline as the live feed, same reason.</b> The substrate
/// invokes observer callbacks synchronously on the match loop and fails the
/// run if one throws — so every path here does only fast in-memory mapping
/// and a non-blocking channel write; it never awaits, does I/O, or throws
/// uncontained. A mapping bug is logged loudly and stops the journal (the
/// file folds as interrupted next start); the match plays on.</para>
///
/// <para>Unlike the live feed, the terminating
/// <see cref="GameEndedTranscriptEntry"/> <em>is</em> journaled: the audit
/// record keeps the full transcript, and rehydration rebuilds each game's
/// record from it.</para>
/// </summary>
internal sealed class MatchJournal : IMatchObserver
{
    private readonly JournalWriter<MatchJournalEvent> _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _matchId;
    private bool _faulted;

    private MatchJournal(
        JournalWriter<MatchJournalEvent> writer, TimeProvider time, ILogger logger, string matchId)
    {
        _writer = writer;
        _time = time;
        _logger = logger;
        _matchId = matchId;
    }

    /// <summary>Open the journal for a new hosted match (the sink opens lazily on the write pump).</summary>
    public static MatchJournal Create(
        IJournalStore store, string matchId, TimeProvider time, ILogger logger) =>
        new(
            new JournalWriter<MatchJournalEvent>(
                () => store.CreateJournal(JournalKind.Match, matchId),
                JournalCodec.Serialize, logger, $"match {matchId}"),
            time, logger, matchId);

    /// <summary>Journal the header: the match's identity and configuration. Always the first event.</summary>
    public void RecordCreated(MatchRecord record) =>
        Append(() => JournalMapping.ToCreatedEvent(record, _time.GetUtcNow()));

    /// <summary>Journal that the wire lifecycle is beginning.</summary>
    public void RecordStarted() =>
        Append(() => new MatchStartedEvent(_time.GetUtcNow()));

    /// <inheritdoc/>
    public void OnGameStarted(GameStartContext context) =>
        Append(() => JournalMapping.ToEvent(context, _time.GetUtcNow()));

    /// <inheritdoc/>
    public void OnEntryRecorded(TranscriptEntry entry) =>
        Append(() => JournalMapping.ToEvent(entry, _time.GetUtcNow()));

    /// <inheritdoc/>
    /// <remarks>The folded game record is derivable from the journaled entries — nothing to write.</remarks>
    public void OnGameEnded(int gameNumber, GameRecord record)
    {
    }

    /// <inheritdoc/>
    /// <remarks>The host journals the terminal outcome for every ending via <see cref="RecordTerminal"/>.</remarks>
    public void OnMatchEnded(MatchResult result)
    {
    }

    /// <summary>
    /// Journal the terminal outcome folded into <paramref name="record"/> —
    /// the journal's last event, written by the host after the catch blocks
    /// have settled status/winner/forfeit (so it carries the truth).
    /// </summary>
    public void RecordTerminal(MatchRecord record) =>
        Append(() => JournalMapping.ToTerminalEvent(record, _time.GetUtcNow()));

    /// <summary>Drain and close the journal; see <see cref="JournalWriter{TEvent}.CompleteAsync"/>.</summary>
    public Task CompleteAsync() => _writer.CompleteAsync();

    /// <summary>
    /// Map-and-enqueue with the observer containment: a mapping bug is logged
    /// and stops the journal instead of killing the match run.
    /// </summary>
    private void Append(Func<MatchJournalEvent> map)
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
                "The journal for match {MatchId} failed to map an event and has stopped writing; "
                    + "the match itself is unaffected.",
                _matchId);
        }
    }
}
