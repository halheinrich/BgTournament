using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Protocol;

namespace BgTournament.Server;

/// <summary>
/// Adapts one connected engine onto the substrate's agent contracts, so
/// <see cref="MatchRunner"/> stays transport-blind. Each decision is one
/// correlated query over the engine's channel, under exactly one of the two
/// timing regimes: the flat per-decision timeout (default), or — when the
/// match has a <see cref="MatchClock"/> — the engine's Fischer pool, which
/// <em>replaces</em> the flat timeout and stamps both players' remaining time
/// onto every query.
///
/// <para>Failure taxonomy (the forfeit hooks): protocol violations and
/// out-of-range replies become the substrate's typed
/// <see cref="AgentContractViolationException"/> (via its public
/// message-carrying constructors, built for exactly this) with the
/// decision-appropriate kind; flat-timeout expiries become
/// <see cref="EngineTimeoutException"/> and emptied pools
/// <see cref="EngineFlagFallException"/>; disconnects pass through as
/// <see cref="EngineDisconnectedException"/>. External cancellation
/// propagates as <see cref="OperationCanceledException"/>, untranslated.</para>
///
/// <para>An illegal-but-well-formed play is deliberately <em>not</em> policed
/// here: the unresolved play is returned as-is and the runner — the single
/// legality authority — raises the violation itself.</para>
/// </summary>
internal sealed class RemoteEngineAgent : IPlayAgent, ICubeAgent
{
    private readonly IEngineChannel _channel;
    private readonly MatchSeat _seat;
    private readonly TimeSpan _decisionTimeout;
    private readonly Func<int>? _rollsProduced;
    private readonly MatchClock? _clock;

    /// <param name="channel">The connected engine's query channel.</param>
    /// <param name="seat">Which seat this engine occupies (for forfeit attribution).</param>
    /// <param name="decisionTimeout">
    /// Flat regime only: per-decision timeout before a query times out. Ignored
    /// when <paramref name="clock"/> is present — the pool replaces it.
    /// </param>
    /// <param name="rollsProduced">
    /// Fair mode only: reads the match's running roll count, so each play query
    /// can be stamped with its roll's stream index (<c>rollIndex = rollsProduced()
    /// - 1</c>). Null in explicit-seed mode — no commitment, so the index has
    /// nothing to verify against and is omitted.
    /// </param>
    /// <param name="clock">
    /// Time-control matches only: the match's Fischer clock. This seat's pool
    /// is debited around every query, an emptied pool flags (forfeit), and both
    /// players' remaining time rides each query. Null in the flat regime.
    /// </param>
    public RemoteEngineAgent(
        IEngineChannel channel, MatchSeat seat, TimeSpan decisionTimeout, Func<int>? rollsProduced = null,
        MatchClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _seat = seat;
        _decisionTimeout = decisionTimeout;
        _rollsProduced = rollsProduced;
        _clock = clock;
    }

    /// <inheritdoc/>
    public async ValueTask<Play> ChoosePlayAsync(
        GameState state, int die1, int die2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wireState = state.Snapshot().ToWireState();

        // Fair mode: this roll's 0-based stream index. The runner takes exactly
        // one roll immediately before this query, so the last-produced roll is
        // the one being played (see CountingDiceSource).
        int? rollIndex = _rollsProduced is null ? null : _rollsProduced() - 1;

        var (yourClock, opponentClock) = ClockStamp();
        var reply = await QueryAsync<PlayReplyMessage>(
            id => new PlayQueryMessage
            {
                RequestId = id, State = wireState, Die1 = die1, Die2 = die2, RollIndex = rollIndex,
                YourTimeRemainingSeconds = yourClock, OpponentTimeRemainingSeconds = opponentClock,
            },
            "play",
            AgentContractViolationKind.IllegalPlay,
            cancellationToken).ConfigureAwait(false);

        Play unresolved;
        try
        {
            unresolved = reply.Moves.ToUnresolvedPlay();
        }
        catch (ArgumentException ex)
        {
            throw new AgentContractViolationException(
                AgentContractViolationKind.IllegalPlay,
                _seat,
                $"Engine '{_channel.EngineName}' replied with out-of-range moves: {ex.Message}",
                ex);
        }

        // Canonicalize (hit encoding restored); an unmatched play goes to the
        // runner verbatim so the legality verdict stays single-sourced there.
        return PlayResolver.Resolve(state.Board, die1, die2, unresolved) ?? unresolved;
    }

    /// <inheritdoc/>
    public async ValueTask<CubeAction> ChooseOfferAsync(
        GameState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wireState = state.Snapshot().ToWireState();
        var (yourClock, opponentClock) = ClockStamp();
        var reply = await QueryAsync<CubeOfferReplyMessage>(
            id => new CubeOfferQueryMessage
            {
                RequestId = id, State = wireState,
                YourTimeRemainingSeconds = yourClock, OpponentTimeRemainingSeconds = opponentClock,
            },
            "cube-offer",
            AgentContractViolationKind.IllegalCubeOffer,
            cancellationToken).ConfigureAwait(false);
        return reply.Action switch
        {
            CubeOfferAction.NoDouble => CubeAction.NoDouble,
            CubeOfferAction.Double => CubeAction.Double,
            _ => throw new AgentContractViolationException(
                AgentContractViolationKind.IllegalCubeOffer,
                _seat,
                $"Engine '{_channel.EngineName}' replied with an undefined offer action ({reply.Action})."),
        };
    }

    /// <inheritdoc/>
    public async ValueTask<CubeAction> ChooseResponseAsync(
        GameState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wireState = state.Snapshot().ToWireState();
        var (yourClock, opponentClock) = ClockStamp();
        var reply = await QueryAsync<CubeResponseReplyMessage>(
            id => new CubeResponseQueryMessage
            {
                RequestId = id, State = wireState,
                YourTimeRemainingSeconds = yourClock, OpponentTimeRemainingSeconds = opponentClock,
            },
            "cube-response",
            AgentContractViolationKind.IllegalCubeResponse,
            cancellationToken).ConfigureAwait(false);
        return reply.Action switch
        {
            CubeResponseAction.Take => CubeAction.Take,
            CubeResponseAction.Pass => CubeAction.Pass,
            _ => throw new AgentContractViolationException(
                AgentContractViolationKind.IllegalCubeResponse,
                _seat,
                $"Engine '{_channel.EngineName}' replied with an undefined response action ({reply.Action})."),
        };
    }

    /// <summary>
    /// Clocked matches: both pools in seconds as of query issuance — this
    /// decision's own thinking time is not yet debited. <c>(null, null)</c> in
    /// the flat regime, which omits the fields on the wire.
    /// </summary>
    private (double? Your, double? Opponent) ClockStamp()
    {
        if (_clock is null)
        {
            return (null, null);
        }

        var opponent = _seat == MatchSeat.One ? MatchSeat.Two : MatchSeat.One;
        return (_clock.Remaining(_seat).TotalSeconds, _clock.Remaining(opponent).TotalSeconds);
    }

    private async Task<TReply> QueryAsync<TReply>(
        Func<string, QueryMessage> queryFactory,
        string decision,
        AgentContractViolationKind violationKind,
        CancellationToken cancellationToken)
        where TReply : ReplyMessage
    {
        if (_clock is null)
        {
            // Flat regime: the per-decision timeout is the only limit.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_decisionTimeout);
            return await ExchangeAsync<TReply>(
                queryFactory, decision, violationKind, timeout.Token, cancellationToken,
                expired: () => new EngineTimeoutException(_channel.EngineName, decision, _decisionTimeout))
                .ConfigureAwait(false);
        }

        // Clocked regime: the seat's remaining pool is the only limit — the
        // flat timeout deliberately does not apply, so a large pool can be
        // spent on one long think. Settlement (the debit) happens exactly once
        // when the decision scope is disposed; only an answered decision earns
        // the increment, so a flag fall or violation forfeits without credit.
        using var clockedDecision = _clock.StartDecision(_seat);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, clockedDecision.FlagToken);
        var reply = await ExchangeAsync<TReply>(
            queryFactory, decision, violationKind, linked.Token, cancellationToken,
            expired: () => new EngineFlagFallException(_channel.EngineName, decision, _clock.Control))
            .ConfigureAwait(false);
        clockedDecision.MarkAnswered();
        return reply;
    }

    /// <summary>
    /// One wire exchange under an already-armed timing token, translating the
    /// regime-independent failures: an expiry (the timing token fired without
    /// external cancellation) becomes the regime's exception via
    /// <paramref name="expired"/>; a protocol violation becomes the substrate's
    /// typed contract violation.
    /// </summary>
    private async Task<TReply> ExchangeAsync<TReply>(
        Func<string, QueryMessage> queryFactory,
        string decision,
        AgentContractViolationKind violationKind,
        CancellationToken queryToken,
        CancellationToken externalToken,
        Func<Exception> expired)
        where TReply : ReplyMessage
    {
        try
        {
            return await _channel.QueryAsync<TReply>(queryFactory, queryToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!externalToken.IsCancellationRequested)
        {
            throw expired();
        }
        catch (EngineProtocolViolationException ex)
        {
            throw new AgentContractViolationException(
                violationKind,
                _seat,
                $"Engine '{_channel.EngineName}' broke protocol answering a {decision} query: {ex.Message}",
                ex);
        }
    }
}
