using System.Net;
using System.Net.WebSockets;
using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Protocol;
using Microsoft.Extensions.Logging;

namespace BgTournament.EngineClient;

/// <summary>
/// The .NET engine-side of the wire protocol: connect, handshake, then serve
/// decision queries by delegating to a local <see cref="IPlayAgent"/> /
/// <see cref="ICubeAgent"/> pair — any in-proc agent becomes a remote engine.
/// The SDK is a convenience for .NET engines; the contract is PROTOCOL.md.
///
/// <para>No perspective work happens here: every query state is already in
/// this engine's frame (the unified frame rule), so it maps straight onto the
/// local agent contract via <see cref="WireMapping"/>.</para>
///
/// <para>An exception thrown by a local agent propagates out of the serve
/// loop and drops the connection — which the server correctly treats as a
/// forfeit by this engine.</para>
/// </summary>
public sealed class EngineClient
{
    private readonly EngineIdentity _identity;
    private readonly IPlayAgent _playAgent;
    private readonly ICubeAgent _cubeAgent;
    private readonly ILogger<EngineClient>? _logger;
    private readonly Action<DiceVerificationReport>? _onDiceVerified;
    private readonly string? _engineKey;

    /// <summary>Create a client serving queries from the given local agents.</summary>
    /// <param name="identity">The engine's handshake identity.</param>
    /// <param name="playAgent">The local play policy.</param>
    /// <param name="cubeAgent">The local cube policy.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="onDiceVerified">
    /// Optional fair-dice hook: after each fair-mode match ends, the client
    /// verifies the revealed key against the commitment and the rolls it observed
    /// (see <see cref="DiceVerification"/>) and delivers the report here. Pass
    /// nothing to opt out. Never invoked for explicit-seed matches (no commitment),
    /// and a failed verification is <em>reported</em>, not thrown — the policy on a
    /// cheating server belongs to the consumer, not the serve loop.
    /// </param>
    /// <param name="engineKey">
    /// Optional registration key (PROTOCOL.md §3.1), issued show-once when the
    /// engine was registered on the server's roster; sent on the hello.
    /// Required by a server enforcing registration; validated by any server it
    /// is presented to, so the identity's name must be the registered name.
    /// Deliberately a separate parameter, not part of
    /// <see cref="EngineIdentity"/> — the identity is loggable metadata, the
    /// key is a secret. Omit it (the default) for a hello byte-identical to
    /// the pre-registration wire.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public EngineClient(
        EngineIdentity identity,
        IPlayAgent playAgent,
        ICubeAgent cubeAgent,
        ILogger<EngineClient>? logger = null,
        Action<DiceVerificationReport>? onDiceVerified = null,
        string? engineKey = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(playAgent);
        ArgumentNullException.ThrowIfNull(cubeAgent);
        _identity = identity;
        _playAgent = playAgent;
        _cubeAgent = cubeAgent;
        _logger = logger;
        _onDiceVerified = onDiceVerified;
        _engineKey = engineKey;
    }

    /// <summary>
    /// Connect to a server's engine endpoint (a <c>ws://</c> or <c>wss://</c>
    /// URI) and serve until the server closes the connection or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="serverUri"/> is null.</exception>
    /// <exception cref="HandshakeRejectedException">The server rejected the handshake.</exception>
    /// <exception cref="ProtocolViolationException">The server violated the protocol.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task RunAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(serverUri, cancellationToken).ConfigureAwait(false);
        await ServeAsync(socket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handshake and serve over an already-open WebSocket (any transport that
    /// yields one — in-proc test servers included). Returns when the server
    /// closes the connection.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="socket"/> is null.</exception>
    /// <exception cref="HandshakeRejectedException">The server rejected the handshake.</exception>
    /// <exception cref="ProtocolViolationException">The server violated the protocol.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task ServeAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var channel = new ProtocolSocket(socket);

        await channel.SendAsync(
            new HelloMessage
            {
                ProtocolVersion = WireProtocol.Version,
                EngineName = _identity.Name,
                EngineVersion = _identity.Version,
                Author = _identity.Author,
                EngineKey = _engineKey,
            },
            cancellationToken).ConfigureAwait(false);

        var first = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        switch (first)
        {
            case WelcomeMessage welcome:
                _logger?.LogInformation(
                    "Engine {EngineName} registered (protocol version {ProtocolVersion}).",
                    _identity.Name, welcome.ProtocolVersion);
                break;
            case RejectedMessage rejected:
                throw new HandshakeRejectedException(rejected.Reason);
            case null:
                throw new ProtocolViolationException("The server closed the connection before completing the handshake.");
            default:
                throw new ProtocolViolationException(
                    $"Expected welcome or rejected, got {first.GetType().Name}.");
        }

        // Accumulates each fair-mode match's dice observations for verification at
        // match end; harmlessly inert when no fair-dice hook is wired.
        var diceAudit = new DiceAuditRecorder();

        while (true)
        {
            var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                _logger?.LogInformation("Engine {EngineName}: server closed the connection.", _identity.Name);
                return;
            }

            switch (message)
            {
                case PlayQueryMessage query:
                {
                    diceAudit.OnPlayQuery(query);
                    var play = await _playAgent
                        .ChoosePlayAsync(query.State.ToGameState(), query.Die1, query.Die2, cancellationToken)
                        .ConfigureAwait(false);
                    await channel.SendAsync(
                        new PlayReplyMessage { RequestId = query.RequestId, Moves = play.ToWireMoves() },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case CubeOfferQueryMessage query:
                {
                    var action = await _cubeAgent
                        .ChooseOfferAsync(query.State.ToGameState(), cancellationToken)
                        .ConfigureAwait(false);
                    await channel.SendAsync(
                        new CubeOfferReplyMessage
                        {
                            RequestId = query.RequestId,
                            Action = action switch
                            {
                                CubeAction.NoDouble => CubeOfferAction.NoDouble,
                                CubeAction.Double => CubeOfferAction.Double,
                                _ => throw new InvalidOperationException(
                                    $"The local cube agent answered an offer query with {action}; legal values are NoDouble and Double."),
                            },
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case CubeResponseQueryMessage query:
                {
                    var action = await _cubeAgent
                        .ChooseResponseAsync(query.State.ToGameState(), cancellationToken)
                        .ConfigureAwait(false);
                    await channel.SendAsync(
                        new CubeResponseReplyMessage
                        {
                            RequestId = query.RequestId,
                            Action = action switch
                            {
                                CubeAction.Take => CubeResponseAction.Take,
                                CubeAction.Pass => CubeResponseAction.Pass,
                                _ => throw new InvalidOperationException(
                                    $"The local cube agent answered a response query with {action}; legal values are Take and Pass."),
                            },
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case MatchStartedMessage started:
                    diceAudit.OnMatchStarted(started);
                    _logger?.LogInformation(
                        "Engine {EngineName}: match {MatchId} started against {Opponent} (length {MatchLength}).",
                        _identity.Name, started.MatchId, started.Opponent, started.MatchLength);
                    break;

                case MatchEndedMessage ended:
                    _logger?.LogInformation(
                        "Engine {EngineName}: match {MatchId} ended ({Reason}) {YourPoints}–{OpponentPoints}.",
                        _identity.Name, ended.MatchId, ended.Reason, ended.YourPoints, ended.OpponentPoints);
                    DeliverDiceReport(diceAudit.OnMatchEnded(ended));
                    break;

                default:
                    throw new ProtocolViolationException(
                        $"Unexpected message from server: {message.GetType().Name}.");
            }
        }
    }

    // Deliver a fair-dice verification report to the consumer's hook. The
    // callback is guarded: a throwing (or slow) consumer must not drop an
    // otherwise-healthy engine session over an observational audit.
    private void DeliverDiceReport(DiceVerificationReport? report)
    {
        if (report is null || _onDiceVerified is null)
        {
            return;
        }

        if (!report.Verified)
        {
            _logger?.LogWarning(
                "Engine {EngineName}: dice verification did not pass ({Outcome}): {Detail}",
                _identity.Name, report.Outcome, report.Detail);
        }

        try
        {
            _onDiceVerified(report);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Engine {EngineName}: the dice-verification hook threw; ignoring.", _identity.Name);
        }
    }
}
