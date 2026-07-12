using System.Net.WebSockets;
using System.Text.Json;
using BgTournament.Protocol;
using BgTournament.Server.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BgTournament.Server;

/// <summary>
/// The <c>/engine</c> WebSocket endpoint: accept, run the hello handshake
/// (PROTOCOL.md §3) — including the roster gate (§3.1: a presented
/// <c>engineKey</c> is always validated; a Registered-policy server refuses
/// keyless hellos) — register the engine, then hold the connection open for
/// its lifetime. Every rejection sends a named <c>rejected</c> message before
/// closing, so engine authors see why — and journals the same reason to the
/// server journal, so the arbitration record sees it too (one funnel, one
/// vocabulary). Registrations and disconnects are journaled symmetrically.
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
        var roster = context.RequestServices.GetRequiredService<RosterService>();
        var serverJournal = context.RequestServices.GetRequiredService<ServerJournal>();
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("BgTournament.Server.EngineSocketEndpoint");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await RunConnectionAsync(
            socket, registry, options, roster, serverJournal, logger, context.RequestAborted);
    }

    private static async Task RunConnectionAsync(
        WebSocket socket,
        EngineRegistry registry,
        TournamentOptions options,
        RosterService roster,
        ServerJournal serverJournal,
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
                    return; // closed before saying hello — not a rejection, nothing to journal
                default:
                    await RejectAsync(
                        channel, socket, serverJournal,
                        $"Expected hello as the first message, got {first.GetType().Name}.");
                    return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RejectAsync(
                channel, socket, serverJournal,
                $"No hello within the handshake timeout ({options.HandshakeTimeoutSeconds:0.##} s).");
            return;
        }
        catch (JsonException)
        {
            await RejectAsync(
                channel, socket, serverJournal,
                "The first frame was not a well-formed protocol message; expected hello.");
            return;
        }

        // From here the hello parsed — rejections carry the claimed name when
        // it is usable as evidence (non-blank).
        string? claimedName = string.IsNullOrWhiteSpace(hello.EngineName) ? null : hello.EngineName;

        if (hello.ProtocolVersion != WireProtocol.Version)
        {
            await RejectAsync(
                channel, socket, serverJournal,
                $"protocol version {hello.ProtocolVersion} not supported; this server speaks {WireProtocol.Version}",
                claimedName);
            return;
        }

        if (claimedName is null)
        {
            await RejectAsync(channel, socket, serverJournal, "engineName must be non-empty.");
            return;
        }

        // Registration (PROTOCOL.md §3.1). A presented engineKey is always
        // validated — enforcing or not, an unrecognized key must fail loudly,
        // never silently degrade to an anonymous connection. The key resolves
        // the roster identity; the claimed name must match it (the key
        // authenticates the name, it does not rename the engine). Only under
        // the Registered policy is a keyless hello itself refused.
        if (hello.EngineKey is { } engineKey)
        {
            if (!roster.TryResolveKey(engineKey, out var registered))
            {
                await RejectAsync(
                    channel, socket, serverJournal,
                    roster.List().Count == 0
                        ? "This server has no engine roster; omit engineKey."
                        : "The presented engineKey is not recognized (rotated, or never issued by this server).",
                    claimedName);
                return;
            }

            if (!registered.Active)
            {
                await RejectAsync(
                    channel, socket, serverJournal,
                    "The registered engine this key belongs to is deactivated.", claimedName);
                return;
            }

            if (!string.Equals(registered.Name, claimedName, StringComparison.Ordinal))
            {
                await RejectAsync(
                    channel, socket, serverJournal,
                    "engineName does not match the engine this engineKey is registered to.", claimedName);
                return;
            }
        }
        else if (options.EnginePolicy == EnginePolicy.Registered)
        {
            await RejectAsync(
                channel, socket, serverJournal,
                "This server admits registered engines only; present your engineKey in the hello.",
                claimedName);
            return;
        }

        var connection = new EngineConnection(socket, hello.EngineName, logger);
        var session = new EngineSession(hello.EngineName, hello.EngineVersion, hello.Author, connection);
        if (!registry.TryRegister(session))
        {
            await RejectAsync(
                channel, socket, serverJournal,
                $"An engine named '{hello.EngineName}' is already connected.", claimedName);
            return;
        }

        serverJournal.RecordEngineConnected(session);
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
            serverJournal.RecordEngineDisconnected(session);
            logger.LogInformation("Engine {EngineName} disconnected.", session.Name);
        }
    }

    /// <summary>
    /// The one rejection funnel: the reason goes to the peer as a
    /// <c>rejected</c> message and to the server journal verbatim — the wire
    /// and the record never diverge.
    /// </summary>
    private static async Task RejectAsync(
        ProtocolSocket channel, WebSocket socket, ServerJournal serverJournal, string reason,
        string? engineName = null)
    {
        serverJournal.RecordHandshakeRejected(reason, engineName);
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
