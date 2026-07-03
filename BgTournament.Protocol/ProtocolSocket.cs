using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace BgTournament.Protocol;

/// <summary>
/// Message-level view of a WebSocket speaking this protocol. Encodes the
/// framing rule of PROTOCOL.md §1 — one complete UTF-8 text frame is one
/// <see cref="ProtocolMessage"/>, at most <see cref="MaxMessageBytes"/> — in
/// one place, for both the server and the client SDK.
///
/// <para>Wraps but does not own the socket: the caller controls connection
/// lifetime, close, and disposal. Not safe for concurrent receives or
/// concurrent sends; one receive and one send may run concurrently (the
/// underlying WebSocket contract).</para>
/// </summary>
public sealed class ProtocolSocket
{
    /// <summary>Maximum accepted message size in bytes (PROTOCOL.md §1).</summary>
    public const int MaxMessageBytes = 64 * 1024;

    private readonly WebSocket _socket;
    private readonly byte[] _receiveBuffer = new byte[8 * 1024];

    /// <summary>Wrap an open WebSocket. The caller retains ownership.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="socket"/> is null.</exception>
    public ProtocolSocket(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <summary>Send one message as a single complete text frame.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] bytes = Encoding.UTF8.GetBytes(WireProtocol.Serialize(message));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Receive the next complete message, or null when the peer closes the
    /// connection. Transport failures (abrupt disconnects) surface as the
    /// socket's own <see cref="WebSocketException"/>.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The frame is not a well-formed protocol message.</exception>
    /// <exception cref="ProtocolViolationException">The peer sent a binary frame or exceeded <see cref="MaxMessageBytes"/>.</exception>
    public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var accumulated = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                throw new ProtocolViolationException(
                    "Binary frames are not part of the protocol; messages are UTF-8 text (PROTOCOL.md §1).");
            }

            if (accumulated.Length + result.Count > MaxMessageBytes)
            {
                throw new ProtocolViolationException(
                    $"Message exceeds the {MaxMessageBytes}-byte limit (PROTOCOL.md §1).");
            }

            accumulated.Write(_receiveBuffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                string json = Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
                return WireProtocol.Deserialize(json);
            }
        }
    }
}
