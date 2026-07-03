using System.Net.WebSockets;
using System.Text.Json;
using BgTournament.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BgTournament.Server;

/// <summary>
/// The <c>/engine</c> WebSocket endpoint: accept, run the hello handshake
/// (PROTOCOL.md §3), register the engine, then hold the connection open for
/// its lifetime. Every rejection sends a named <c>rejected</c> message before
/// closing, so engine authors see why.
/// </summary>
internal static class EngineSocketEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("This endpoint speaks WebSocket (PROTOCOL.md §1).");
            return;
        }

        var registry = context.RequestServices.GetRequiredService<EngineRegistry>();
        var options = context.RequestServices.GetRequiredService<IOptions<TournamentOptions>>().Value;
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("BgTournament.Server.EngineSocketEndpoint");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await RunConnectionAsync(socket, registry, options, logger, context.RequestAborted);
    }

    private static async Task RunConnectionAsync(
        WebSocket socket,
        EngineRegistry registry,
        TournamentOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var channel = new ProtocolSocket(socket);

        HelloMessage hello;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(options.HandshakeTimeout);
            var first = await channel.ReceiveAsync(handshakeTimeout.Token);
            switch (first)
            {
                case HelloMessage received:
                    hello = received;
                    break;
                case null:
                    return; // closed before saying hello
                default:
                    await RejectAsync(channel, socket, $"Expected hello as the first message, got {first.GetType().Name}.");
                    return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RejectAsync(channel, socket, $"No hello within the handshake timeout ({options.HandshakeTimeoutSeconds:0.##} s).");
            return;
        }
        catch (JsonException)
        {
            await RejectAsync(channel, socket, "The first frame was not a well-formed protocol message; expected hello.");
            return;
        }

        if (hello.ProtocolVersion != WireProtocol.Version)
        {
            await RejectAsync(
                channel, socket,
                $"protocol version {hello.ProtocolVersion} not supported; this server speaks {WireProtocol.Version}");
            return;
        }

        if (string.IsNullOrWhiteSpace(hello.EngineName))
        {
            await RejectAsync(channel, socket, "engineName must be non-empty.");
            return;
        }

        var connection = new EngineConnection(socket, hello.EngineName, logger);
        var session = new EngineSession(hello.EngineName, hello.EngineVersion, hello.Author, connection);
        if (!registry.TryRegister(session))
        {
            await RejectAsync(channel, socket, $"An engine named '{hello.EngineName}' is already connected.");
            return;
        }

        try
        {
            await channel.SendAsync(
                new WelcomeMessage { ProtocolVersion = WireProtocol.Version }, cancellationToken);
            logger.LogInformation(
                "Engine {EngineName} registered (version {EngineVersion}, author {Author}).",
                session.Name, session.Version ?? "-", session.Author ?? "-");
            await connection.RunReceiveLoopAsync(cancellationToken);
        }
        finally
        {
            registry.Remove(session);
            logger.LogInformation("Engine {EngineName} disconnected.", session.Name);
        }
    }

    private static async Task RejectAsync(ProtocolSocket channel, WebSocket socket, string reason)
    {
        try
        {
            await channel.SendAsync(new RejectedMessage { Reason = reason }, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "rejected", CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The peer vanished mid-rejection; nothing left to tell it.
            _ = ex;
        }
    }
}
