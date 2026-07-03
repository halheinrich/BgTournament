using System.Text.Json.Serialization;

namespace BgTournament.Protocol;

/// <summary>
/// Base for engine → server replies to decision queries. A reply is valid only
/// while its query is outstanding; a reply whose <see cref="RequestId"/>
/// matches no outstanding query is a protocol violation (see PROTOCOL.md).
/// </summary>
public abstract record ReplyMessage : ProtocolMessage
{
    /// <summary>The queried <see cref="QueryMessage.RequestId"/>, echoed verbatim.</summary>
    [JsonPropertyOrder(-10)]
    public required string RequestId { get; init; }
}
