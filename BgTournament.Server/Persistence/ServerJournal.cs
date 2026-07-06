using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The server-session journal: the durable record of engine lifecycle at the
/// server level (connects, disconnects, handshake rejections), one segment
/// file per process run under <c>server/</c> in the journal store.
///
/// <para>Registered once as a singleton and as the hosted service that opens
/// the segment: <see cref="StartAsync"/> runs before Kestrel accepts
/// connections, so the <see cref="ServerStartedEvent"/> header precedes any
/// engine event; <see cref="StopAsync"/> appends the graceful
/// <see cref="ServerStoppedEvent"/> and drains — a segment without one is a
/// crash, evidenced by absence.</para>
///
/// <para>Same containment discipline as <see cref="MatchJournal"/>: record
/// methods are non-blocking appends off the connection-handling paths, a
/// mapping bug logs loudly and stops the journal, and a broken disk never
/// touches a handshake (the <see cref="JournalWriter{TEvent}"/> pump owns all
/// I/O). Evidence-only: nothing rehydrates from these files — sessions are
/// ephemeral, and the registry stays purely live state.</para>
/// </summary>
internal sealed class ServerJournal : IHostedService
{
    private readonly IJournalStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerJournal> _logger;
    private JournalWriter<ServerJournalEvent>? _writer;
    private bool _faulted;

    public ServerJournal(IJournalStore store, TimeProvider time, ILogger<ServerJournal> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Open this session's segment and journal the header.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        string segmentId = Guid.NewGuid().ToString("N");
        _writer = new JournalWriter<ServerJournalEvent>(
            () => _store.CreateJournal(JournalKind.Server, segmentId),
            JournalCodec.Serialize, _logger, $"server session {segmentId}");
        Append(() => new ServerStartedEvent(_time.GetUtcNow(), JournalCodec.ServerSchemaVersion));
        return Task.CompletedTask;
    }

    /// <summary>Journal the graceful-stop marker, then drain and close the segment.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_writer is { } writer)
        {
            Append(() => new ServerStoppedEvent(_time.GetUtcNow()));
            await writer.CompleteAsync();
        }
    }

    /// <summary>Journal an engine's successful registration.</summary>
    public void RecordEngineConnected(EngineSession session) =>
        Append(() => JournalMapping.ToConnectedEvent(session, _time.GetUtcNow()));

    /// <summary>Journal a registered engine's connection end.</summary>
    public void RecordEngineDisconnected(EngineSession session) =>
        Append(() => JournalMapping.ToDisconnectedEvent(session, _time.GetUtcNow()));

    /// <summary>
    /// Journal a handshake rejection — the same reason string the wire's
    /// <c>rejected</c> message carries, plus the claimed engine name when one
    /// was readable.
    /// </summary>
    public void RecordHandshakeRejected(string reason, string? engineName) =>
        Append(() => JournalMapping.ToRejectedEvent(reason, engineName, _time.GetUtcNow()));

    /// <summary>
    /// Map-and-enqueue with the journal containment: a mapping bug is logged
    /// and stops the journal instead of disturbing connection handling.
    /// </summary>
    private void Append(Func<ServerJournalEvent> map)
    {
        if (_faulted || _writer is not { } writer)
        {
            return;
        }

        try
        {
            writer.Append(map());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faulted = true;
            _logger.LogError(
                ex,
                "The server journal failed to map an event and has stopped writing; "
                    + "connection handling is unaffected.");
        }
    }
}
