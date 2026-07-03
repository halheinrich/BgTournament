using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Protocol;

namespace BgTournament.Server;

/// <summary>
/// Adapts one connected engine onto the substrate's agent contracts, so
/// <see cref="MatchRunner"/> stays transport-blind. Each decision is one
/// correlated query over the engine's channel, under the per-decision
/// timeout.
///
/// <para>Failure taxonomy (the forfeit hooks): protocol violations and
/// out-of-range replies become the substrate's typed
/// <see cref="AgentContractViolationException"/> (via its public
/// message-carrying constructors, built for exactly this) with the
/// decision-appropriate kind; timeouts become
/// <see cref="EngineTimeoutException"/>; disconnects pass through as
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

    public RemoteEngineAgent(IEngineChannel channel, MatchSeat seat, TimeSpan decisionTimeout)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _seat = seat;
        _decisionTimeout = decisionTimeout;
    }

    /// <inheritdoc/>
    public async ValueTask<Play> ChoosePlayAsync(
        GameState state, int die1, int die2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wireState = state.Snapshot().ToWireState();
        var reply = await QueryAsync<PlayReplyMessage>(
            id => new PlayQueryMessage { RequestId = id, State = wireState, Die1 = die1, Die2 = die2 },
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
        var reply = await QueryAsync<CubeOfferReplyMessage>(
            id => new CubeOfferQueryMessage { RequestId = id, State = wireState },
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
        var reply = await QueryAsync<CubeResponseReplyMessage>(
            id => new CubeResponseQueryMessage { RequestId = id, State = wireState },
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

    private async Task<TReply> QueryAsync<TReply>(
        Func<string, QueryMessage> queryFactory,
        string decision,
        AgentContractViolationKind violationKind,
        CancellationToken cancellationToken)
        where TReply : ReplyMessage
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_decisionTimeout);
        try
        {
            return await _channel.QueryAsync<TReply>(queryFactory, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineTimeoutException(_channel.EngineName, decision, _decisionTimeout);
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
