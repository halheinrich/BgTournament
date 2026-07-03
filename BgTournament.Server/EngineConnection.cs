using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using BgTournament.Protocol;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server;

/// <summary>
/// The server's live view of one connected engine: a dedicated receive loop,
/// strictly one in-flight query correlated by request id, and serialized
/// sends (queries and notifications share the socket).
///
/// <para>Failure surfaces are deliberate: a malformed frame or wrong reply
/// while a query is pending fails that query with
/// <see cref="EngineProtocolViolationException"/> (one-in-flight makes the
/// offending frame attributable to the query); connection loss fails it with
/// <see cref="EngineDisconnectedException"/>; caller cancellation abandons the
/// query and tolerates its late reply once (PROTOCOL.md §8's benign race).
/// Any violation also closes the connection — after malformed traffic the
/// stream is not trustworthy.</para>
/// </summary>
internal sealed class EngineConnection : IEngineChannel
{
    private readonly WebSocket _socket;
    private readonly ProtocolSocket _channel;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _requestCounter;
    private PendingQuery? _pending;
    private string? _lastAbandonedRequestId;
    private bool _isClosed;

    public EngineConnection(WebSocket socket, string engineName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineName);
        ArgumentNullException.ThrowIfNull(logger);
        _socket = socket;
        _channel = new ProtocolSocket(socket);
        EngineName = engineName;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string EngineName { get; }

    /// <summary>
    /// Completes when the connection is done — peer close, transport failure,
    /// or violation close. Match hosts watch this to forfeit a disconnecting
    /// engine proactively, even while its opponent is deciding.
    /// </summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Receive until the connection ends. Run exactly once, by the endpoint
    /// handler that accepted the socket; the handler's request lifetime spans
    /// this loop.
    /// </summary>
    public async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var message = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    FailPending(new EngineDisconnectedException(EngineName, "the engine closed the connection."));
                    return;
                }

                if (!TryDispatch(message))
                {
                    return;
                }
            }
        }
        catch (JsonException ex)
        {
            // One-in-flight discipline: a malformed frame while a query is
            // pending is that query's answer, gone wrong.
            FailPending(new EngineProtocolViolationException("The engine sent a malformed frame.", ex));
            await AbortAsync().ConfigureAwait(false);
        }
        catch (ProtocolViolationException ex)
        {
            FailPending(new EngineProtocolViolationException(ex.Message, ex));
            await AbortAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            FailPending(new EngineDisconnectedException(EngineName, "the connection dropped."));
        }
        finally
        {
            lock (_gate)
            {
                _isClosed = true;
            }

            FailPending(new EngineDisconnectedException(EngineName, "the connection is closed."));
            _closed.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public async Task<TReply> QueryAsync<TReply>(
        Func<string, QueryMessage> queryFactory,
        CancellationToken cancellationToken)
        where TReply : ReplyMessage
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        string requestId = $"q-{Interlocked.Increment(ref _requestCounter)}";
        var pending = new PendingQuery(requestId);
        lock (_gate)
        {
            if (_isClosed)
            {
                throw new EngineDisconnectedException(EngineName, "the connection is closed.");
            }

            if (_pending is not null)
            {
                throw new InvalidOperationException(
                    $"A query ({_pending.RequestId}) is already outstanding for engine '{EngineName}' — the protocol is one in-flight query per engine.");
            }

            _pending = pending;
        }

        try
        {
            await SendAsync(queryFactory(requestId), cancellationToken).ConfigureAwait(false);
            using var abandonOnCancel = cancellationToken.Register(() => Abandon(pending, cancellationToken));
            var reply = await pending.Reply.Task.ConfigureAwait(false);
            if (reply is TReply typed)
            {
                return typed;
            }

            var violation = new EngineProtocolViolationException(
                $"Expected a {typeof(TReply).Name} for {requestId}, got {reply.GetType().Name}.");
            await AbortAsync().ConfigureAwait(false);
            throw violation;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    /// <summary>
    /// Send a notification (no reply expected). Sends are serialized with
    /// queries. A transport-level send failure surfaces as
    /// <see cref="EngineDisconnectedException"/> — a connection that cannot be
    /// written to is over for match purposes, even if the receive loop has not
    /// observed the close yet (e.g. moments after a forfeit close).
    /// </summary>
    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new EngineDisconnectedException(EngineName, "the connection could not be written to.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Close the connection gracefully (e.g. after a forfeit). Sends the
    /// close frame without waiting for the peer's acknowledgement — a
    /// misbehaving engine may never send one, and the caller must not hang on
    /// it. Safe to call when already closed.
    /// </summary>
    public async Task CloseAsync(string reason)
    {
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Closing connection to engine {EngineName} after it already ended.", EngineName);
        }
    }

    /// <summary>Dispatch one received message; false ends the receive loop (violation close).</summary>
    private bool TryDispatch(ProtocolMessage message)
    {
        if (message is ReplyMessage reply)
        {
            PendingQuery? completed = null;
            bool discard = false;
            lock (_gate)
            {
                if (_pending is not null && _pending.RequestId == reply.RequestId)
                {
                    completed = _pending;
                }
                else if (_lastAbandonedRequestId == reply.RequestId)
                {
                    // The benign race of PROTOCOL.md §8: the match moved on
                    // while this reply was in flight. Tolerated once.
                    _lastAbandonedRequestId = null;
                    discard = true;
                }
            }

            if (completed is not null)
            {
                completed.Reply.TrySetResult(reply);
                return true;
            }

            if (discard)
            {
                _logger.LogDebug(
                    "Discarded a late reply ({RequestId}) from engine {EngineName} to an abandoned query.",
                    reply.RequestId, EngineName);
                return true;
            }

            FailPending(new EngineProtocolViolationException(
                $"The engine sent a reply correlating to no outstanding query ({reply.RequestId})."));
        }
        else
        {
            FailPending(new EngineProtocolViolationException(
                $"The engine sent an unsolicited {message.GetType().Name}."));
        }

        _ = AbortAsync();
        return false;
    }

    private void Abandon(PendingQuery pending, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
                _lastAbandonedRequestId = pending.RequestId;
            }
        }

        pending.Reply.TrySetCanceled(cancellationToken);
    }

    private void FailPending(Exception failure)
    {
        PendingQuery? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Reply.TrySetException(failure);
    }

    private async Task AbortAsync()
    {
        try
        {
            await _socket.CloseAsync(
                    WebSocketCloseStatus.ProtocolError, "protocol violation", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Abort of engine {EngineName} raced its own close.", EngineName);
        }
    }

    private sealed class PendingQuery
    {
        public PendingQuery(string requestId)
        {
            RequestId = requestId;
        }

        public string RequestId { get; }

        public TaskCompletionSource<ReplyMessage> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
