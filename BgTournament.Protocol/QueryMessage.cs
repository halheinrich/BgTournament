using System.Text.Json.Serialization;

namespace BgTournament.Protocol;

/// <summary>
/// Base for server → engine decision queries. At most one query is outstanding
/// per engine at any time; the engine answers with the matching reply type,
/// echoing <see cref="RequestId"/> verbatim (see PROTOCOL.md).
/// </summary>
public abstract record QueryMessage : ProtocolMessage
{
    /// <summary>
    /// Opaque server-generated correlation id, unique per connection. The
    /// engine treats it as an opaque string and echoes it verbatim in the
    /// reply.
    /// </summary>
    [JsonPropertyOrder(-10)]
    public required string RequestId { get; init; }
}
