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

    /// <summary>
    /// Time-control matches only: your remaining pool in seconds as of the
    /// moment this query was issued — this decision's own thinking time is not
    /// yet debited (see PROTOCOL.md §10). The server's measurement is
    /// authoritative, and network latency is on your clock. Omitted when the
    /// match has no time control.
    /// </summary>
    [JsonPropertyOrder(90)]
    public double? YourTimeRemainingSeconds { get; init; }

    /// <summary>
    /// Time-control matches only: your opponent's remaining pool in seconds as
    /// of the moment this query was issued. Omitted when the match has no time
    /// control.
    /// </summary>
    [JsonPropertyOrder(91)]
    public double? OpponentTimeRemainingSeconds { get; init; }
}
