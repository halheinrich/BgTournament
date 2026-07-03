using BgTournament.Protocol;

namespace BgTournament.Server;

/// <summary>
/// The query seam <see cref="RemoteEngineAgent"/> speaks through: one
/// correlated request/response exchange at a time with a named engine.
/// Implemented over the live WebSocket by <see cref="EngineConnection"/>;
/// faked in adapter behavioral tests.
/// </summary>
internal interface IEngineChannel
{
    /// <summary>The engine's registered name, for attribution in failures.</summary>
    string EngineName { get; }

    /// <summary>
    /// Send one query (built by <paramref name="queryFactory"/> from the
    /// assigned request id) and await its reply.
    /// </summary>
    /// <exception cref="EngineProtocolViolationException">
    /// The engine answered with a malformed frame, a reply of the wrong type,
    /// or a reply correlating to no outstanding query.
    /// </exception>
    /// <exception cref="EngineDisconnectedException">The connection ended before the reply.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired; the outstanding query is
    /// abandoned (a late reply is discarded, per PROTOCOL.md §8).
    /// </exception>
    Task<TReply> QueryAsync<TReply>(
        Func<string, QueryMessage> queryFactory,
        CancellationToken cancellationToken)
        where TReply : ReplyMessage;
}
