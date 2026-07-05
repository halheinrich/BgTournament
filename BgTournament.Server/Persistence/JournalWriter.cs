using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// One journal's write pump: an unbounded channel fed by non-blocking
/// <see cref="Append"/> calls and drained by a single background task that
/// serializes each event to a JSONL line and flushes it — one line, one flush
/// (the settled durability discipline: an event survives a process crash the
/// moment it is written). The sink is opened lazily on the pump, so no caller
/// ever waits on disk I/O — the match loop least of all.
///
/// <para><b>Contained failure.</b> A sink or write error is logged loudly
/// once, the journal stops persisting (subsequent events are drained and
/// dropped), and the owner is never thrown at: a broken disk must not take a
/// running match down. The in-memory record stays authoritative for the
/// session; the truncated journal folds honestly (as interrupted) on the next
/// start.</para>
/// </summary>
/// <typeparam name="TEvent">The journal's polymorphic event base.</typeparam>
internal sealed class JournalWriter<TEvent>
    where TEvent : class
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<TEvent> _events = Channel.CreateUnbounded<TEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Task _pump;

    /// <param name="openSink">Opens the journal's byte sink; invoked once, on the pump (never on a caller).</param>
    /// <param name="serialize">The codec path serializing one event to one JSONL line.</param>
    /// <param name="logger">Failure reporting.</param>
    /// <param name="journalName">Names this journal in failure logs (e.g. "match abc123").</param>
    public JournalWriter(
        Func<Stream> openSink, Func<TEvent, string> serialize, ILogger logger, string journalName)
    {
        _pump = Task.Run(() => PumpAsync(openSink, serialize, logger, journalName));
    }

    /// <summary>
    /// Enqueue one event. Never blocks, awaits, or throws — safe from an
    /// observer callback on the match loop.
    /// </summary>
    public void Append(TEvent journalEvent) => _events.Writer.TryWrite(journalEvent);

    /// <summary>
    /// No further events: drain the pump and close the sink. Idempotent.
    /// Callers await this only after the match/tournament is over, so the
    /// final flush delays nothing that matters.
    /// </summary>
    public Task CompleteAsync()
    {
        _events.Writer.TryComplete();
        return _pump;
    }

    private async Task PumpAsync(
        Func<Stream> openSink, Func<TEvent, string> serialize, ILogger logger, string journalName)
    {
        var reader = _events.Reader;
        try
        {
            await using var sink = openSink();
            await using var writer = new StreamWriter(sink, Utf8NoBom) { NewLine = "\n" };
            await foreach (var journalEvent in reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(serialize(journalEvent));

                // Flush per event: out of the process (and its buffers) the
                // moment it happened. StreamWriter.Flush flushes the sink too.
                await writer.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(
                ex,
                "The journal for {JournalName} failed to persist and has stopped writing; "
                    + "the in-memory record remains authoritative for this session, and the "
                    + "truncated journal will fold as interrupted on the next start.",
                journalName);

            // Keep draining so CompleteAsync never hangs; events are dropped.
            await foreach (var _ in reader.ReadAllAsync())
            {
            }
        }
    }
}
