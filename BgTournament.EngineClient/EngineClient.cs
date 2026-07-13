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
///
/// <para>Clock surfacing (PROTOCOL.md §10): an agent that implements
/// <see cref="IClockAwarePlayAgent"/> / <see cref="IClockAwareCubeAgent"/>
/// hears each match's time control announced up front (null = unclocked) and
/// receives a <see cref="ClockReading"/> with every decision in a clocked
/// match. Detection is by type, per role — plain agents are served exactly as
/// before, and the constructor is unchanged.</para>
/// </summary>
public sealed class EngineClient
{
    private readonly EngineIdentity _identity;
    private readonly IPlayAgent _playAgent;
    private readonly ICubeAgent _cubeAgent;
    private readonly IClockAwarePlayAgent? _clockAwarePlayAgent;
    private readonly IClockAwareCubeAgent? _clockAwareCubeAgent;
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
        _clockAwarePlayAgent = playAgent as IClockAwarePlayAgent;
        _clockAwareCubeAgent = cubeAgent as IClockAwareCubeAgent;
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
                    var clock = ClockOf(query);
                    var play = _clockAwarePlayAgent is not null && clock is not null
                        ? await _clockAwarePlayAgent
                            .ChoosePlayAsync(query.State.ToGameState(), query.Die1, query.Die2, clock, cancellationToken)
                            .ConfigureAwait(false)
                        : await _playAgent
                            .ChoosePlayAsync(query.State.ToGameState(), query.Die1, query.Die2, cancellationToken)
                            .ConfigureAwait(false);
                    await channel.SendAsync(
                        new PlayReplyMessage { RequestId = query.RequestId, Moves = play.ToWireMoves() },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case CubeOfferQueryMessage query:
                {
                    var clock = ClockOf(query);
                    var action = _clockAwareCubeAgent is not null && clock is not null
                        ? await _clockAwareCubeAgent
                            .ChooseOfferAsync(query.State.ToGameState(), clock, cancellationToken)
                            .ConfigureAwait(false)
                        : await _cubeAgent
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
                    var clock = ClockOf(query);
                    var action = _clockAwareCubeAgent is not null && clock is not null
                        ? await _clockAwareCubeAgent
                            .ChooseResponseAsync(query.State.ToGameState(), clock, cancellationToken)
                            .ConfigureAwait(false)
                        : await _cubeAgent
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
                    AnnounceTimeControl(started.TimeControl);
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

    // Deliver the match-start clock announcement to any clock-aware agent —
    // always, clocked or not: null affirmatively says "this match is
    // unclocked" (the flat regime's per-decision timeout is server-side
    // configuration that never rides the wire, so null is the honest ceiling
    // of what can be said). One announcement per match per distinct agent
    // object: one object serving both roles hears it once.
    private void AnnounceTimeControl(WireTimeControl? announced)
    {
        if (_clockAwarePlayAgent is null && _clockAwareCubeAgent is null)
        {
            return;
        }

        MatchTimeControl? control = announced is null
            ? null
            : new MatchTimeControl(
                TimeSpan.FromSeconds(announced.InitialSeconds),
                TimeSpan.FromSeconds(announced.IncrementSeconds));

        _clockAwarePlayAgent?.OnTimeControlAnnounced(control);
        if (_clockAwareCubeAgent is not null && !ReferenceEquals(_clockAwareCubeAgent, _clockAwarePlayAgent))
        {
            _clockAwareCubeAgent.OnTimeControlAnnounced(control);
        }
    }

    // The query's clock reading, or null in the flat regime (whose queries
    // carry no pool fields — nothing is delivered, and no sentinel is ever
    // fabricated). Both pools must be present together: a query carrying only
    // one is out of contract, and the decided policy is to treat it as
    // unclocked rather than invent the missing half. Logged at Debug so a
    // clocked session's pools are visible with no agent opt-in (e.g. the
    // reference bot at --verbosity debug).
    private ClockReading? ClockOf(QueryMessage query)
    {
        if (query.YourTimeRemainingSeconds is not double yours ||
            query.OpponentTimeRemainingSeconds is not double opponent)
        {
            return null;
        }

        _logger?.LogDebug(
            "Engine {EngineName}: clock at query issuance — you {YourSeconds}s, opponent {OpponentSeconds}s.",
            _identity.Name, yours, opponent);
        return new ClockReading(TimeSpan.FromSeconds(yours), TimeSpan.FromSeconds(opponent));
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
